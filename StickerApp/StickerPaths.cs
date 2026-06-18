using System.IO;

namespace StickerApp;

/// <summary>
/// Single source of truth for the per-user folders Sticker reads and writes.
/// These locations are a compatibility contract with the Python prototype
/// (<c>~/.u2net</c> for models, <c>~/.sticker_cache</c> for mattes + session), so
/// they're defined once here rather than re-derived at each call site. Resolved
/// at type init — the backing env var (U2NET_HOME) isn't expected to change mid-run.
/// </summary>
internal static class StickerPaths
{
    private static readonly string Home =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Downloaded ONNX models. Overridable via U2NET_HOME (rembg's historical name).</summary>
    public static readonly string ModelDir =
        Environment.GetEnvironmentVariable("U2NET_HOME") ?? Path.Combine(Home, ".u2net");

    /// <summary>Holds cached cutout PNGs, session.json, and the clipboard/ capture subfolder.</summary>
    public static readonly string CacheDir = Path.Combine(Home, ".sticker_cache");

    /// <summary>Throwaway PNGs captured from "Paste as sticker" (pruned by App.PruneClipboardCaptures).</summary>
    public static readonly string ClipboardDir = Path.Combine(CacheDir, "clipboard");

    /// <summary>Open-sticker positions/sizes that <c>--resume</c> restores.</summary>
    public static readonly string SessionFile = Path.Combine(CacheDir, "session.json");
}
