using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace StickerApp;

/// <summary>Human-friendly sticker names for menus (pure, so it's unit-testable).</summary>
internal static class StickerNames
{
    // Clipboard captures are saved as clip-yyyyMMdd-HHmmss-fff-<guid>.png (see App.PasteAsSticker).
    private static readonly Regex ClipName = new(@"^clip-(\d{8})-(\d{6})", RegexOptions.CultureInvariant);

    /// <summary>
    /// "Pasted image (2:45 pm)" for a clipboard capture taken today, "Pasted image
    /// (16 Nov)" for an older one, otherwise the file name.
    /// </summary>
    public static string Display(string path, DateTime now)
    {
        string name = Path.GetFileName(path);
        var m = ClipName.Match(name);
        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var taken))
        {
            string when = taken.Date == now.Date
                ? taken.ToString("t", CultureInfo.CurrentCulture)
                : taken.ToString("d MMM", CultureInfo.CurrentCulture);
            return $"Pasted image ({when})";
        }
        return name;
    }
}
