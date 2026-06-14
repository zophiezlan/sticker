using System.Collections.Concurrent;
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
    public const string DefaultModel = "isnet-general-use";

    /// <summary>Per-model preprocessing recipe, mirroring rembg's sessions.</summary>
    private sealed record ModelSpec(int Size, float[] Mean, float[] Std, string FileName);

    private static readonly float[] ImageNetMean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] ImageNetStd = { 0.229f, 0.224f, 0.225f };
    private static readonly float[] HalfMean = { 0.5f, 0.5f, 0.5f };
    private static readonly float[] UnitStd = { 1f, 1f, 1f };

    private static ModelSpec SpecFor(string model) => model switch
    {
        "isnet-general-use" or "isnet-anime" =>
            new(1024, HalfMean, UnitStd, model + ".onnx"),
        "u2net" or "u2netp" or "u2net_human_seg" or "silueta" =>
            new(320, ImageNetMean, ImageNetStd, model + ".onnx"),
        "birefnet-general" =>
            new(BirefnetSize(), ImageNetMean, ImageNetStd, "BiRefNet-general-epoch_244.onnx"),
        _ => new(1024, HalfMean, UnitStd, model + ".onnx"),  // isnet-style guess
    };

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

    private static readonly string ModelDir =
        Environment.GetEnvironmentVariable("U2NET_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".u2net");

    private static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sticker_cache");

    private static string ModelPathFor(string model) =>
        Path.Combine(ModelDir, SpecFor(model).FileName);

    public static bool ModelFileExists(string model) => File.Exists(ModelPathFor(model));

    private readonly string _model;
    private readonly ModelSpec _spec;
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
        _spec = SpecFor(model);
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
        Directory.CreateDirectory(ModelDir);
        string url = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/"
                     + SpecFor(model).FileName;
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
            File.Move(tmp, dest, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Mirror of the Python prototype's cache key: sha1(path|mtime_ns|model)[:16].</summary>
    public string CachePath(string source)
    {
        string full = Path.GetFullPath(source);
        long mtimeNs = (File.GetLastWriteTimeUtc(full) - DateTime.UnixEpoch).Ticks * 100;
        string key = $"{full}|{mtimeNs}|{_model}";
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))
                      [..16].ToLowerInvariant();
        return Path.Combine(CacheDir, $"{Path.GetFileNameWithoutExtension(full)}.{hash}.png");
    }

    // One lock object per distinct output path. Without this, two concurrent
    // re-mattes of the same image+model both pass the cache check, both run
    // inference, and both try to write the same PNG (wasted work + a file-in-use
    // race). Locking per cache path serializes only those; different images or
    // models still run in parallel. The map is tiny (one object per result seen).
    private static readonly ConcurrentDictionary<string, object> _cacheLocks = new();

    /// <summary>Returns the path of a matted PNG for <paramref name="source"/>, using the cache when possible.</summary>
    public string RemoveBackground(string source, bool force = false)
    {
        string cached = CachePath(source);
        if (File.Exists(cached) && !force)
            return cached;

        lock (_cacheLocks.GetOrAdd(cached, _ => new object()))
        {
            // Re-check inside the lock: another thread may have produced it while
            // we waited (only matters for the non-forced path).
            if (File.Exists(cached) && !force)
                return cached;

            Directory.CreateDirectory(CacheDir);

            var (pixels, w, h) = LoadBgra(source);
            byte[] mask = Infer(pixels, w, h);

            // alpha = originalAlpha * mask
            for (int i = 0; i < w * h; i++)
                pixels[i * 4 + 3] = (byte)(pixels[i * 4 + 3] * mask[i] / 255);

            SavePng(pixels, w, h, cached);
            return cached;
        }
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
        if (!Directory.Exists(CacheDir))
            return (0, 0);
        foreach (var path in Directory.EnumerateFiles(CacheDir, "*.png", SearchOption.TopDirectoryOnly))
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
        int side = _spec.Size;

        // --- preprocess: bilinear resize, RGB, (x/255 - mean) / std ---
        var tensor = new DenseTensor<float>(new[] { 1, 3, side, side });
        for (int y = 0; y < side; y++)
        {
            (int y0, int y1, float fy) = Sample(y, h, side);
            for (int x = 0; x < side; x++)
            {
                (int x0, int x1, float fx) = Sample(x, w, side);
                for (int c = 0; c < 3; c++)
                {
                    int b = 2 - c;  // tensor is RGB, pixels are BGRA
                    float v00 = pixels[(y0 * w + x0) * 4 + b];
                    float v01 = pixels[(y0 * w + x1) * 4 + b];
                    float v10 = pixels[(y1 * w + x0) * 4 + b];
                    float v11 = pixels[(y1 * w + x1) * 4 + b];
                    float v = v00 * (1 - fx) * (1 - fy) + v01 * fx * (1 - fy)
                            + v10 * (1 - fx) * fy + v11 * fx * fy;
                    tensor[0, c, y, x] = (v / 255f - _spec.Mean[c]) / _spec.Std[c];
                }
            }
        }

        // --- inference ---
        using var results = _session.Run(
            new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        var output = results.First().AsTensor<float>();

        // --- postprocess: (sigmoid for logit-output models, then) min-max normalize ---
        bool sigmoid = _model.StartsWith("birefnet");
        var small = new float[side * side];
        float min = float.MaxValue, max = float.MinValue;
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                float v = output[0, 0, y, x];
                if (sigmoid)
                    v = 1f / (1f + MathF.Exp(-v));
                small[y * side + x] = v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        float range = Math.Max(max - min, 1e-6f);

        // --- bilinear resize mask back to original resolution ---
        var mask = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            (int y0, int y1, float fy) = Sample(y, side, h);
            for (int x = 0; x < w; x++)
            {
                (int x0, int x1, float fx) = Sample(x, side, w);
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
    private static (int Lo, int Hi, float Frac) Sample(int i, int srcLen, int dstLen)
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
