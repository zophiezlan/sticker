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
}

public static class Session
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sticker_cache");

    private static readonly string FilePath = Path.Combine(Dir, "session.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(IEnumerable<StickerState> states)
    {
        try
        {
            Directory.CreateDirectory(Dir);
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
            if (!File.Exists(FilePath))
                return new();
            return JsonSerializer.Deserialize<List<StickerState>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception)
        {
            return new();
        }
    }
}
