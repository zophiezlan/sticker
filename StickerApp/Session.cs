using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StickerApp;

/// <summary>
/// One sticker's saved state. Field names and types deliberately match the
/// Python prototype's session.json so the two apps can restore each other's
/// sessions (x/y stay integers — PyQt's move() rejects floats).
/// </summary>
public sealed class StickerState
{
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("no_matte")] public bool NoMatte { get; set; }
    [JsonPropertyName("x")] public long X { get; set; }
    [JsonPropertyName("y")] public long Y { get; set; }
    [JsonPropertyName("scale")] public double Scale { get; set; } = 1.0;
    [JsonPropertyName("rotation")] public double Rotation { get; set; }
    [JsonPropertyName("flipped")] public bool Flipped { get; set; }
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 1.0;
    [JsonPropertyName("on_top")] public bool OnTop { get; set; } = true;
    [JsonPropertyName("pinned")] public bool Pinned { get; set; }  // extra key; Python ignores it
    [JsonPropertyName("model")] public string? Model { get; set; } // extra key; Python ignores it
}

public static class Session
{
    private static readonly string FilePath = StickerPaths.SessionFile;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(IEnumerable<StickerState> states)
    {
        try
        {
            Directory.CreateDirectory(StickerPaths.CacheDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(states.ToList(), Options));
        }
        catch (Exception)
        {
            // Persistence is best-effort; never take a sticker down over it.
        }
    }

    public static List<StickerState> Load()
    {
        try
        {
            return File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath)) : new();
        }
        catch (Exception)
        {
            // Unreadable/corrupt file — start fresh rather than crash.
            return new();
        }
    }

    /// <summary>
    /// Parse the session JSON tolerantly. The file is a flat array shared verbatim
    /// with the Python prototype, so we can't add a version envelope without breaking
    /// it — instead, accept the array (or a future {"stickers":[…]} object, for
    /// forward-compat), deserialize each entry on its own, and skip any that are
    /// malformed or have no source. A hand-edit, partial write, or future field change
    /// then loses one sticker, not the whole session. Pure (string in, list out) so it
    /// can be unit-tested without touching the real session file.
    /// </summary>
    internal static List<StickerState> Parse(string json)
    {
        var result = new List<StickerState>();
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement array =
            root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("stickers", out var s) ? s
            : default;
        if (array.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var el in array.EnumerateArray())
        {
            try
            {
                var state = el.Deserialize<StickerState>();
                if (state is not null && !string.IsNullOrWhiteSpace(state.Source))
                    result.Add(state);
            }
            catch (JsonException)
            {
                // Skip this entry; keep the rest of the session.
            }
        }
        return result;
    }
}
