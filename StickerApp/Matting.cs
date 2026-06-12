using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

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
            new(1024, ImageNetMean, ImageNetStd, "BiRefNet-general-epoch_244.onnx"),
        _ => new(1024, HalfMean, UnitStd, model + ".onnx"),  // isnet-style guess
    };

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

    public Matting(string model = DefaultModel)
    {
        _model = model;
        _spec = SpecFor(model);
        if (!ModelFileExists(model))
            DownloadModel(model);

        var options = new SessionOptions();
        try
        {
            options.AppendExecutionProvider_DML(0);
        }
        catch
        {
            // No DX12 adapter / DirectML unavailable — CPU is fine, just slower.
        }
        _session = new InferenceSession(ModelPathFor(model), options);
        _inputName = _session.InputMetadata.Keys.First();
    }

    private static void DownloadModel(string model)
    {
        Directory.CreateDirectory(ModelDir);
        string url = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/"
                     + SpecFor(model).FileName;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        byte[] bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        File.WriteAllBytes(ModelPathFor(model), bytes);
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

    /// <summary>Returns the path of a matted PNG for <paramref name="source"/>, using the cache when possible.</summary>
    public string RemoveBackground(string source, bool force = false)
    {
        string cached = CachePath(source);
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

    private static (byte[] Pixels, int W, int H) LoadBgra(string path)
    {
        var frame = BitmapFrame.Create(new Uri(path),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var pixels = new byte[w * h * 4];
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
