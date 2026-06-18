using StickerApp;
using Xunit;

namespace StickerApp.Tests;

public class ModelsTests
{
    [Fact]
    public void Default_resolves_to_a_known_model_with_matching_id()
    {
        var info = Models.For(Models.Default);
        Assert.Equal(Models.Default, info.Id);
        Assert.Equal("isnet-general-use", Models.Default);
    }

    [Fact]
    public void Pickable_models_are_known_and_labelled()
    {
        Assert.NotEmpty(Models.Pickable);
        foreach (var info in Models.Pickable)
        {
            // Pickable entries must be real registry entries (round-trip by id)…
            Assert.Equal(info.Id, Models.For(info.Id).Id);
            // …and carry a menu label (unlisted internal models have an empty label).
            Assert.False(string.IsNullOrWhiteSpace(info.MenuLabel));
        }
    }

    [Fact]
    public void Pickable_includes_the_three_documented_models()
    {
        var ids = Pickable_ids();
        Assert.Contains("isnet-general-use", ids);
        Assert.Contains("u2net_human_seg", ids);
        Assert.Contains("birefnet-general", ids);
    }

    [Fact]
    public void Birefnet_is_heavy_sigmoid_and_large()
    {
        var b = Models.For("birefnet-general");
        Assert.True(b.IsHeavy);
        Assert.True(b.Sigmoid);
        Assert.Equal(900, b.ApproxMb);
        Assert.True(Models.IsHeavy("birefnet-general"));
    }

    [Fact]
    public void Light_models_are_not_heavy_and_not_sigmoid()
    {
        foreach (var id in new[] { "isnet-general-use", "u2net_human_seg", "u2net", "silueta" })
        {
            var m = Models.For(id);
            Assert.False(m.IsHeavy, id);
            Assert.False(m.Sigmoid, id);
            Assert.Equal(180, m.ApproxMb);
        }
        Assert.False(Models.IsHeavy("isnet-general-use"));
    }

    [Fact]
    public void Every_known_model_loads_from_an_onnx_file()
    {
        foreach (var info in Models.Pickable)
            Assert.EndsWith(".onnx", info.FileName);
    }

    [Fact]
    public void Unknown_model_gets_an_isnet_style_guess_with_no_hash()
    {
        var info = Models.For("totally-made-up-model");
        Assert.Equal("totally-made-up-model", info.Id);
        Assert.Equal("totally-made-up-model.onnx", info.FileName);
        Assert.False(info.IsHeavy);
        Assert.False(info.Sigmoid);
        Assert.Equal(1024, info.Size);
        Assert.Null(info.Sha256);   // no pinned hash → download accepted without a check
    }

    [Fact]
    public void Unknown_birefnet_prefixed_model_is_treated_as_heavy_and_sigmoid()
    {
        // Mirrors the old per-call StartsWith("birefnet") heuristics.
        var info = Models.For("birefnet-portrait-custom");
        Assert.True(info.IsHeavy);
        Assert.True(info.Sigmoid);
    }

    [Fact]
    public void Shipped_models_have_a_pinned_sha256()
    {
        // The three models Sticker can download are integrity-checked.
        foreach (var id in new[] { "isnet-general-use", "u2net_human_seg", "birefnet-general" })
        {
            var hash = Models.For(id).Sha256;
            Assert.False(string.IsNullOrEmpty(hash));
            Assert.Equal(64, hash!.Length);   // SHA-256 hex
        }
    }

    private static System.Collections.Generic.List<string> Pickable_ids()
    {
        var ids = new System.Collections.Generic.List<string>();
        foreach (var m in Models.Pickable)
            ids.Add(m.Id);
        return ids;
    }
}
