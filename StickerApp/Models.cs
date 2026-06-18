namespace StickerApp;

/// <summary>
/// Everything Sticker needs to know about one matting model, gathered in a
/// single descriptor. These facts used to be scattered across
/// <c>Matting.SpecFor</c> (file name + preprocessing), <c>Matting.ExpectedSha256</c>,
/// <c>App.ModelSizeMb</c>, <c>App.IsHeavyModel</c>, the sigmoid check in
/// <c>Matting.Infer</c>, and the hardcoded right-click menu items — six copies of
/// the same knowledge that had to be kept in lockstep. Keeping them together here
/// stops them drifting apart.
/// </summary>
/// <param name="Id">The model id (also the <c>--model</c> / <c>STICKER_MODEL</c> value).</param>
/// <param name="FileName">The <c>.onnx</c> file in <c>~/.u2net</c> this model loads from.</param>
/// <param name="MenuLabel">Label for the right-click "Matte:" picker (empty if not pickable).</param>
/// <param name="ApproxMb">Rough on-disk size, used only for the download notice.</param>
/// <param name="IsHeavy">Runs at high resolution and commits gigabytes; dropped from the
/// warm-session cache as soon as a run finishes (see App.GetMatting / EvictMatting).</param>
/// <param name="Sigmoid">Output is logits and needs a sigmoid before min-max normalization.</param>
/// <param name="Size">Square inference resolution (heavy models resolve this dynamically — see
/// <c>Matting</c>'s <c>STICKER_BIREFNET_SIZE</c> handling).</param>
/// <param name="Mean">Per-channel preprocessing mean (RGB), mirroring rembg's sessions.</param>
/// <param name="Std">Per-channel preprocessing std (RGB).</param>
/// <param name="Sha256">Pinned hash of the known-good download, or null to skip the check
/// (e.g. a custom <c>--model</c> with no shipped hash).</param>
internal sealed record ModelInfo(
    string Id,
    string FileName,
    string MenuLabel,
    int ApproxMb,
    bool IsHeavy,
    bool Sigmoid,
    int Size,
    float[] Mean,
    float[] Std,
    string? Sha256);

/// <summary>The known matting models and how to look one up by id.</summary>
internal static class Models
{
    /// <summary>Default model for new stickers when neither --model nor STICKER_MODEL is set.</summary>
    public const string Default = "isnet-general-use";

    // Preprocessing recipes, mirroring rembg's per-model sessions.
    private static readonly float[] ImageNetMean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] ImageNetStd = { 0.229f, 0.224f, 0.225f };
    private static readonly float[] HalfMean = { 0.5f, 0.5f, 0.5f };
    private static readonly float[] UnitStd = { 1f, 1f, 1f };

    // Known models, keyed by id. SHA-256 values were computed from the known-good
    // rembg release downloads (https://github.com/danielgatis/rembg). Models with a
    // null hash are accepted without an integrity check.
    private static readonly Dictionary<string, ModelInfo> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["isnet-general-use"] = new("isnet-general-use", "isnet-general-use.onnx",
            "Matte: ISNet — general (default)", 180, IsHeavy: false, Sigmoid: false,
            1024, HalfMean, UnitStd, "60920e99c45464f2ba57bee2ad08c919a52bbf852739e96947fbb4358c0d964a"),
        ["isnet-anime"] = new("isnet-anime", "isnet-anime.onnx",
            "", 180, IsHeavy: false, Sigmoid: false, 1024, HalfMean, UnitStd, null),
        ["u2net"] = new("u2net", "u2net.onnx",
            "", 180, IsHeavy: false, Sigmoid: false, 320, ImageNetMean, ImageNetStd, null),
        ["u2netp"] = new("u2netp", "u2netp.onnx",
            "", 180, IsHeavy: false, Sigmoid: false, 320, ImageNetMean, ImageNetStd, null),
        ["u2net_human_seg"] = new("u2net_human_seg", "u2net_human_seg.onnx",
            "Matte: U2Net — people & portraits", 180, IsHeavy: false, Sigmoid: false,
            320, ImageNetMean, ImageNetStd, "01eb6a29a5c4d8edb30b56adad9bb3a2a0535338e480724a213e0acfd2d1c73c"),
        ["silueta"] = new("silueta", "silueta.onnx",
            "", 180, IsHeavy: false, Sigmoid: false, 320, ImageNetMean, ImageNetStd, null),
        ["birefnet-general"] = new("birefnet-general", "BiRefNet-general-epoch_244.onnx",
            "Matte: BiRefNet — best (slow)", 900, IsHeavy: true, Sigmoid: true,
            1024, ImageNetMean, ImageNetStd, "58f621f00f5d756097615970a88a791584600dcf7c45b18a0a6267535a1ebd3c"),
    };

    /// <summary>Models offered in the sticker's right-click "Matte:" picker, in menu order.</summary>
    public static readonly IReadOnlyList<ModelInfo> Pickable = new[]
    {
        Known["isnet-general-use"],
        Known["u2net_human_seg"],
        Known["birefnet-general"],
    };

    /// <summary>
    /// Descriptor for <paramref name="id"/>. An unknown id (a custom <c>--model</c>)
    /// gets an isnet-style guess: <c>id.onnx</c> at 1024², half-mean/unit-std, no hash
    /// check. A custom id beginning with "birefnet" (case-insensitive) is treated as a
    /// heavy, sigmoid model. This unifies what used to be a scatter of per-call checks
    /// with inconsistent casing (some <c>StartsWith("birefnet")</c>, one
    /// OrdinalIgnoreCase); a side effect is that such a custom id now also honors
    /// STICKER_BIREFNET_SIZE (it ran at a fixed 1024 before). The three shipped models
    /// are listed in <see cref="Known"/>, so none of this affects them.
    /// </summary>
    public static ModelInfo For(string id)
    {
        if (Known.TryGetValue(id, out var info))
            return info;
        bool birefnet = id.StartsWith("birefnet", StringComparison.OrdinalIgnoreCase);
        return new ModelInfo(id, id + ".onnx", id, 180,
            IsHeavy: birefnet, Sigmoid: birefnet, 1024, HalfMean, UnitStd, Sha256: null);
    }

    public static bool IsHeavy(string id) => For(id).IsHeavy;
}
