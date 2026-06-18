using StickerApp;
using Xunit;

namespace StickerApp.Tests;

public class MattingTests
{
    // --- cache key (shared verbatim with prototype/sticker.py) ---

    [Fact]
    public void CacheFileName_matches_a_known_anchor()
    {
        // Regression anchor: sha1("C:\img\photo.png|1700000000000000000|isnet-general-use")
        // truncated to 16 hex chars. Locks the algorithm so an accidental change to the
        // key format (which would silently invalidate every cached cutout, and break
        // interop with the Python prototype) fails the build.
        string name = Matting.CacheFileName(
            @"C:\img\photo.png", 1700000000000000000L, "isnet-general-use");
        Assert.Equal("photo.cac8b674537f6a1b.png", name);
    }

    [Fact]
    public void CacheFileName_keeps_the_source_stem_and_png_extension()
    {
        string name = Matting.CacheFileName(@"C:\pics\Sunset Beach.JPEG", 123L, "u2net_human_seg");
        Assert.StartsWith("Sunset Beach.", name);
        Assert.EndsWith(".png", name);
    }

    [Fact]
    public void CacheFileName_is_deterministic()
    {
        Assert.Equal(
            Matting.CacheFileName(@"C:\a.png", 5L, "isnet-general-use"),
            Matting.CacheFileName(@"C:\a.png", 5L, "isnet-general-use"));
    }

    [Theory]
    [InlineData(@"C:\a.png", 5L, "u2net_human_seg")]   // different model
    [InlineData(@"C:\a.png", 6L, "isnet-general-use")] // different mtime
    [InlineData(@"C:\b.png", 5L, "isnet-general-use")] // different path (also different stem)
    public void CacheFileName_changes_when_any_input_changes(string path, long mtime, string model)
    {
        string baseline = Matting.CacheFileName(@"C:\a.png", 5L, "isnet-general-use");
        Assert.NotEqual(baseline, Matting.CacheFileName(path, mtime, model));
    }

    // --- bilinear sample mapping (the core of pre/post-process resize) ---

    [Fact]
    public void Sample_is_identity_when_axes_match()
    {
        // dst == src: pixel centers line up, so frac is 0 and lo is the index itself.
        var (lo, hi, frac) = Matting.Sample(0, 4, 4);
        Assert.Equal(0, lo);
        Assert.Equal(1, hi);
        Assert.Equal(0f, frac, 5);
    }

    [Fact]
    public void Sample_clamps_at_the_far_edge()
    {
        var (lo, hi, frac) = Matting.Sample(3, 4, 4);
        Assert.Equal(3, lo);
        Assert.Equal(3, hi);   // hi can't exceed srcLen-1
        Assert.Equal(0f, frac, 5);
    }

    [Fact]
    public void Sample_downscales_with_a_midpoint_fraction()
    {
        // Mapping dst index 1 of a 2-wide axis onto a 4-wide source.
        var (lo, hi, frac) = Matting.Sample(1, 4, 2);
        Assert.Equal(2, lo);
        Assert.Equal(3, hi);
        Assert.Equal(0.5f, frac, 5);
    }

    [Fact]
    public void Sample_clamps_a_negative_source_position_to_zero()
    {
        // Upscaling: dst index 0 maps to a source position left of center (negative),
        // which clamps to lo=0 with frac=0.
        var (lo, hi, frac) = Matting.Sample(0, 2, 4);
        Assert.Equal(0, lo);
        Assert.Equal(1, hi);
        Assert.Equal(0f, frac, 5);
    }

    // --- mask resize (the non-square upscale that turns model output into alpha) ---

    [Fact]
    public void ResizeMask_preserves_orientation_on_a_non_square_upscale()
    {
        // 2x2 source that increases left->right and is constant top->bottom. A y/x
        // transpose in the resize would instead make the output vary top->bottom, so
        // this non-square fixture is exactly what guards the flat-index Infer rewrite.
        int side = 2;
        float[] small = { 0f, 1f,    // row y=0: (x=0)=0, (x=1)=1
                          0f, 1f };  // row y=1: same
        // Upscale to 3x2 with identity normalization (min=0, range=1 => byte = v*255).
        byte[] mask = Matting.ResizeMaskToBytes(small, side, w: 3, h: 2, min: 0f, range: 1f);
        Assert.Equal(new byte[] { 0, 127, 255,
                                  0, 127, 255 }, mask);
    }

    [Fact]
    public void ResizeMask_applies_min_max_normalization()
    {
        // side==w==h==2 => identity resize, so corners map straight through. min=0.2,
        // range=0.5 sends 0.2 -> 0 and 0.7 -> 255.
        int side = 2;
        float[] small = { 0.2f, 0.7f, 0.2f, 0.7f };
        byte[] mask = Matting.ResizeMaskToBytes(small, side, w: 2, h: 2, min: 0.2f, range: 0.5f);
        Assert.Equal(0, mask[0]);     // small[0] = 0.2
        Assert.Equal(255, mask[1]);   // small[1] = 0.7
    }
}
