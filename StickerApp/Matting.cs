using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Vortice.DXGI;

namespace StickerApp;

/// <summary>
/// Background removal via ONNX Runtime (DirectML GPU, CPU fallback).
/// Shares the model folder (~/.u2net) and matte cache (~/.sticker_cache)
/// with the Python prototype, including its cache-key scheme.
/// </summary>
public sealed class Matting : IDisposable
{
    public const string DefaultModel = Models.Default;

    /// <summary>
    /// BiRefNet runs at 1024² by default, which is very memory-hungry — and
    /// DirectML's memory planning can overflow VRAM well before the card is
    /// "full". Peak memory scales ~quadratically with the side, so dropping the
    /// resolution is the strongest lever to make it fit. STICKER_BIREFNET_SIZE
    /// (e.g. 768 or 512) trades some edge fidelity for a smaller footprint;
    /// snapped to a multiple of 32 and clamped. Note: if this model's ONNX was
    /// exported with a fixed 1024 input, a non-1024 value will fail with a shape
    /// error — that just means this lever isn't available for that export.
    /// </summary>
    private static int BirefnetSize()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("STICKER_BIREFNET_SIZE"), out int s))
            return Math.Clamp(s / 32 * 32, 256, 1024);
        return 1024;
    }

    private static string ModelPathFor(string model) =>
        Path.Combine(StickerPaths.ModelDir, Models.For(model).FileName);

    public static bool ModelFileExists(string model) => File.Exists(ModelPathFor(model));

    private readonly string _model;
    private readonly ModelInfo _info;
    private readonly int _side;
    private readonly InferenceSession _session;
    private readonly string _inputName;

    /// <summary>True if this session deliberately skipped the GPU and runs on CPU.</summary>
    public bool IsCpu { get; }

    /// <summary>
    /// Heavy models (BiRefNet) can exhaust GPU VRAM and fail mid-graph. This
    /// classifies such failures so callers can offer a CPU retry. ONNX Runtime
    /// surfaces out-of-memory in several forms depending on which allocator hit
    /// the wall, so we match on substrings from each rather than a node name:
    ///   • BFCArena    → "BFCArena::AllocateRawInternal Failed to allocate memory…"
    ///   • DML commit  → "DmlCommittedResourceAllocator… 8007000E Not enough memory…"
    /// 8007000E is the Windows E_OUTOFMEMORY HRESULT.
    /// </summary>
    public static bool IsGpuMemoryError(Exception ex)
    {
        string m = ex.Message;
        string[] needles =
        {
            "Failed to allocate", "AllocateRawInternal", "BFCArena",   // BFC arena form (inference)
            "Not enough memory", "CommittedResourceAllocator", "8007000E", // DML commit form
            "bad allocation", "bad_alloc",                             // std::bad_alloc at model load
            "out of memory", "E_OUTOFMEMORY",                          // generic
        };
        return needles.Any(n => m.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Choose the DirectML adapter (a DXGI adapter index). STICKER_DML_DEVICE
    /// overrides everything; otherwise enumerate adapters and pick the one with
    /// the most dedicated VRAM, skipping software (WARP) adapters. This avoids
    /// the hybrid-graphics trap where DXGI adapter 0 is a low-memory iGPU —
    /// heavy models would OOM on it while the real GPU sits idle. Any failure
    /// falls back to 0 (DirectML's own default), so this can only help.
    /// </summary>
    private static int PickDmlDevice()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("STICKER_DML_DEVICE"), out int forced)
            && forced >= 0)
            return forced;

        try
        {
            if (DXGI.CreateDXGIFactory1<IDXGIFactory1>(out var factory).Failure || factory is null)
                return 0;
            using (factory)
            {
                int best = 0;
                ulong bestVram = 0;
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
                {
                    using (adapter)
                    {
                        var desc = adapter.Description1;
                        if ((desc.Flags & AdapterFlags.Software) != 0)
                            continue;                          // skip the WARP software adapter
                        ulong vram = (ulong)desc.DedicatedVideoMemory;
                        if (vram > bestVram) { bestVram = vram; best = (int)i; }
                    }
                }
                return best;
            }
        }
        catch
        {
            return 0;   // DXGI unavailable — let DirectML use its default adapter
        }
    }

    public Matting(string model = DefaultModel, bool forceCpu = false)
    {
        _model = model;
        _info = Models.For(model);
        // BiRefNet (the only heavy model) is the one whose input resolution is
        // tunable via STICKER_BIREFNET_SIZE; everything else runs at its fixed size.
        _side = _info.IsHeavy ? BirefnetSize() : _info.Size;
        if (!ModelFileExists(model))
            DownloadModel(model);

        // Env var lets users on low-VRAM machines opt out of the GPU entirely.
        IsCpu = forceCpu
            || Environment.GetEnvironmentVariable("STICKER_FORCE_CPU") == "1";

        var options = new SessionOptions();
        if (!IsCpu)
        {
            try
            {
                options.AppendExecutionProvider_DML(PickDmlDevice());
            }
            catch
            {
                // No DX12 adapter / DirectML unavailable — CPU is fine, just slower.
                IsCpu = true;
            }
        }
        _session = new InferenceSession(ModelPathFor(model), options);
        _inputName = _session.InputMetadata.Keys.First();
    }

    /// <summary>
    /// Raised while a model downloads: (model, bytesRead, totalBytes-or-null).
    /// Fires on a background thread — handlers must marshal to the UI themselves.
    /// </summary>
    public static event Action<string, long, long?>? DownloadProgress;

    private static void DownloadModel(string model)
    {
        Directory.CreateDirectory(StickerPaths.ModelDir);
        var info = Models.For(model);
        string url = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/"
                     + info.FileName;
        string dest = ModelPathFor(model);
        string tmp = dest + ".part";

        // Stream to a temp file and only rename into place once fully written.
        // A dropped connection or the 15-min timeout then leaves a stray .part
        // (cleaned up below) rather than a truncated .onnx at the real path —
        // which would otherwise crash on load every launch until manually
        // deleted. ModelFileExists checks `dest`, so a failure simply retries.
        // Streaming (vs. GetByteArrayAsync) also lets us report progress.
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                                 .GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;

            using (var src = resp.Content.ReadAsStream())
            using (var dst = File.Create(tmp))
            {
                byte[] buf = new byte[81920];
                long read = 0;
                int n;
                DownloadProgress?.Invoke(model, 0, total);
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    read += n;
                    DownloadProgress?.Invoke(model, read, total);
                }
            }

            // Integrity gate: verify the freshly written .part before promoting it
            // to the real path. Models are executable compute graphs fed to ONNX
            // Runtime, so a corrupted or substituted download must never load. On
            // mismatch we throw — the catch below deletes the .part, so a bad
            // download simply retries next time rather than being left to crash on
            // load. Hash is streamed so a ~900 MB model isn't slurped into one byte[].
            // A model with no pinned hash (e.g. a custom --model) is accepted as-is.
            if (info.Sha256 is { } expected)
            {
                string actual;
                using (var check = File.OpenRead(tmp))
                    actual = Convert.ToHexString(SHA256.HashData(check)).ToLowerInvariant();
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Downloaded model '{info.FileName}' failed its integrity "
                        + $"check (expected {expected[..12]}…, got {actual[..12]}…); not installed.");
            }
            File.Move(tmp, dest, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Mirror of the Python prototype's cache key: sha1(path|mtime_ns|model)[:16].</summary>
    public static string CachePathFor(string source, string model)
    {
        string full = Path.GetFullPath(source);
        long mtimeNs = (File.GetLastWriteTimeUtc(full) - DateTime.UnixEpoch).Ticks * 100;
        return Path.Combine(StickerPaths.CacheDir, CacheFileName(full, mtimeNs, model));
    }

    /// <summary>
    /// The cache file name for a resolved path + mtime + model:
    /// <c>{stem}.{sha1(path|mtime_ns|model)[:16]}.png</c>. Pure (no I/O), split out from
    /// <see cref="CachePathFor"/> so it can be unit-tested and cross-checked against the
    /// Python prototype's identical scheme (prototype/sticker.py). NOTE the cross-language
    /// hazard isn't the formula but the mtime unit: .NET ticks×100 gives 100 ns precision
    /// while Python's st_mtime_ns is 1 ns — equal whole-nanosecond values still match, but
    /// a filesystem exposing sub-100 ns mtimes could diverge.
    /// </summary>
    internal static string CacheFileName(string fullPath, long mtimeNs, string model)
    {
        string key = $"{fullPath}|{mtimeNs}|{model}";
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))
                      [..16].ToLowerInvariant();
        return $"{Path.GetFileNameWithoutExtension(fullPath)}.{hash}.png";
    }

    /// <summary>
    /// True if a matte for <paramref name="source"/> with <paramref name="model"/>
    /// is already on disk — i.e. it can be shown instantly, without loading the
    /// model or running inference. Lets a restored sticker keep its original
    /// (possibly heavy) model only when doing so costs nothing.
    /// </summary>
    public static bool CachedMatteExists(string source, string model)
    {
        try { return File.Exists(CachePathFor(source, model)); }
        catch { return false; }   // odd/invalid path — treat as not cached
    }

    /// <summary>Returns the path of a matted PNG for <paramref name="source"/>, using the cache when possible.</summary>
    public string RemoveBackground(string source, bool force = false)
    {
        // No self-locking here: single-writer safety rests on App._matteGate
        // (a SemaphoreSlim(1,1)) serializing the only two callers — App.OpenStickers
        // and App.RematteAsync — so exactly one RemoveBackground runs process-wide
        // at a time and no two mattes ever race the same output path. A future
        // caller that ran this OFF the gate would lose that guarantee, so keep all
        // matting behind _matteGate.
        string cached = CachePathFor(source, _model);
        if (File.Exists(cached) && !force)
            return cached;

        Directory.CreateDirectory(StickerPaths.CacheDir);

        var (pixels, w, h) = LoadBgra(source);
        byte[] mask = Infer(pixels, w, h);

        // alpha = originalAlpha * mask
        for (int i = 0; i < w * h; i++)
            pixels[i * 4 + 3] = (byte)(pixels[i * 4 + 3] * mask[i] / 255);

        SavePng(pixels, w, h, cached);
        return cached;
    }

    /// <summary>
    /// Delete cached matte PNGs (per-image/per-model results). Leaves session.json
    /// and the clipboard/ capture folder intact (top-level only), and doesn't touch
    /// downloaded models in ~/.u2net. Returns (filesDeleted, bytesFreed).
    /// </summary>
    public static (int Files, long Bytes) ClearMatteCache()
    {
        int files = 0;
        long bytes = 0;
        if (!Directory.Exists(StickerPaths.CacheDir))
            return (0, 0);
        foreach (var path in Directory.EnumerateFiles(StickerPaths.CacheDir, "*.png", SearchOption.TopDirectoryOnly))
        {
            try
            {
                long len = new FileInfo(path).Length;
                File.Delete(path);
                files++;
                bytes += len;
            }
            catch { /* skip anything locked/removed mid-scan */ }
        }
        return (files, bytes);
    }

    private static (byte[] Pixels, int W, int H) LoadBgra(string path)
    {
        var frame = BitmapFrame.Create(new Uri(path),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;

        // Guard against degenerate or pathological dimensions before allocating.
        // `w * h * 4` is a 32-bit multiply: past ~23k² it silently overflows to a
        // wrong (even negative) size, and even short of that a multi-hundred-MB
        // buffer just OOM-crashes. Cap at ~100 MP (≈400 MB BGRA) and fail with a
        // clear message instead. Matting itself runs at ≤1024², so this only
        // limits the source we'll load, not output quality.
        if (w <= 0 || h <= 0 || (long)w * h > 100_000_000)
            throw new InvalidOperationException(
                $"Image is an unsupported size ({w}×{h}). Try one under ~100 megapixels.");

        var pixels = new byte[(long)w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        return (pixels, w, h);
    }

    /// <summary>Runs the model; returns a per-pixel alpha mask at the original resolution.</summary>
    private byte[] Infer(byte[] pixels, int w, int h)
    {
        int side = _side;

        // --- preprocess: bilinear resize, RGB, (x/255 - mean) / std ---
        // The x-axis sample mapping is the same for every row, so memoize it once
        // rather than recomputing Sample() per column per row. Values are written
        // straight to the tensor's contiguous NCHW buffer at c*plane + y*side + x —
        // exactly the offset tensor[0,c,y,x] resolves to — skipping the multi-index
        // indexer on this ~3M-element hot path. The bilinear expression is left
        // byte-for-byte unchanged (float multiply isn't associative).
        var tensor = new DenseTensor<float>(new[] { 1, 3, side, side });
        Span<float> dst = tensor.Buffer.Span;
        int plane = side * side;

        var sx0 = new int[side];
        var sx1 = new int[side];
        var sfx = new float[side];
        for (int x = 0; x < side; x++)
            (sx0[x], sx1[x], sfx[x]) = Sample(x, w, side);

        for (int y = 0; y < side; y++)
        {
            (int y0, int y1, float fy) = Sample(y, h, side);
            for (int x = 0; x < side; x++)
            {
                int x0 = sx0[x], x1 = sx1[x];
                float fx = sfx[x];
                for (int c = 0; c < 3; c++)
                {
                    int b = 2 - c;  // tensor is RGB, pixels are BGRA
                    float v00 = pixels[(y0 * w + x0) * 4 + b];
                    float v01 = pixels[(y0 * w + x1) * 4 + b];
                    float v10 = pixels[(y1 * w + x0) * 4 + b];
                    float v11 = pixels[(y1 * w + x1) * 4 + b];
                    float v = v00 * (1 - fx) * (1 - fy) + v01 * fx * (1 - fy)
                            + v10 * (1 - fx) * fy + v11 * fx * fy;
                    dst[c * plane + y * side + x] = (v / 255f - _info.Mean[c]) / _info.Std[c];
                }
            }
        }

        // --- inference ---
        using var results = _session.Run(
            new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });

        // --- postprocess: (sigmoid for logit-output models, then) min-max normalize ---
        // The output is single-channel, contiguous NCHW [1,1,side,side], so a linear
        // walk over its backing buffer matches output[0,0,y,x] index-for-index.
        var output = (DenseTensor<float>)results.First().AsTensor<float>();
        ReadOnlySpan<float> outBuf = output.Buffer.Span;
        bool sigmoid = _info.Sigmoid;
        var small = new float[side * side];
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < small.Length; i++)
        {
            float v = outBuf[i];
            if (sigmoid)
                v = 1f / (1f + MathF.Exp(-v));
            small[i] = v;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        float range = Math.Max(max - min, 1e-6f);

        // --- bilinear resize mask back to original resolution, min-max normalized ---
        return ResizeMaskToBytes(small, side, w, h, min, range);
    }

    /// <summary>
    /// Bilinearly resize the <paramref name="side"/>×<paramref name="side"/>
    /// <paramref name="small"/> mask up to <paramref name="w"/>×<paramref name="h"/>,
    /// min-max normalizing (<c>(v-min)/range</c>) to bytes. Split out of
    /// <see cref="Infer"/> — which needs a live ONNX session — so the resize geometry
    /// can be unit-tested directly; in particular a y/x transpose is invisible on the
    /// square preprocess path but would corrupt this non-square upscale (the common
    /// real-world case). The x-axis mapping is loop-invariant so it's memoized once.
    /// </summary>
    internal static byte[] ResizeMaskToBytes(float[] small, int side, int w, int h, float min, float range)
    {
        var mask = new byte[w * h];
        var mx0 = new int[w];
        var mx1 = new int[w];
        var mfx = new float[w];
        for (int x = 0; x < w; x++)
            (mx0[x], mx1[x], mfx[x]) = Sample(x, side, w);

        for (int y = 0; y < h; y++)
        {
            (int y0, int y1, float fy) = Sample(y, side, h);
            for (int x = 0; x < w; x++)
            {
                int x0 = mx0[x], x1 = mx1[x];
                float fx = mfx[x];
                float v = small[y0 * side + x0] * (1 - fx) * (1 - fy)
                        + small[y0 * side + x1] * fx * (1 - fy)
                        + small[y1 * side + x0] * (1 - fx) * fy
                        + small[y1 * side + x1] * fx * fy;
                mask[y * w + x] = (byte)Math.Clamp((v - min) / range * 255f, 0f, 255f);
            }
        }
        return mask;
    }

    /// <summary>Bilinear sample positions mapping index i of a dstLen-sized axis onto a srcLen-sized source.</summary>
    internal static (int Lo, int Hi, float Frac) Sample(int i, int srcLen, int dstLen)
    {
        float s = (i + 0.5f) * srcLen / dstLen - 0.5f;
        int lo = Math.Clamp((int)MathF.Floor(s), 0, srcLen - 1);
        int hi = Math.Min(lo + 1, srcLen - 1);
        float frac = Math.Clamp(s - lo, 0f, 1f);
        return (lo, hi, frac);
    }

    private static void SavePng(byte[] pixels, int w, int h, string dest)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(dest);
        encoder.Save(fs);
    }

    public void Dispose() => _session.Dispose();
}
