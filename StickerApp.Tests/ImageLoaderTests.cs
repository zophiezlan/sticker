using System.Windows.Media;
using System.Windows.Media.Imaging;
using StickerApp;
using Xunit;

namespace StickerApp.Tests;

public class ImageLoaderTests
{
    // 2x2 fixture, pixels tagged by their blue channel:
    //   A B      A=10 B=20
    //   C D      C=30 D=40
    private static BitmapSource Fixture()
    {
        byte[] px =
        {
            10, 0, 0, 255,  20, 0, 0, 255,
            30, 0, 0, 255,  40, 0, 0, 255,
        };
        var bmp = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, px, 8);
        bmp.Freeze();
        return bmp;
    }

    private static byte[] BlueChannel(BitmapSource src)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var px = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(px, bgra.PixelWidth * 4, 0);
        return Enumerable.Range(0, px.Length / 4).Select(i => px[i * 4]).ToArray();
    }

    // Expected upright layout (row-major) for each EXIF orientation, per the EXIF spec
    // (and PIL's ImageOps.exif_transpose): 6 = rotate 90° CW, 8 = 90° CCW, 5 = transpose,
    // 7 = transverse.
    [Theory]
    [InlineData(1, new byte[] { 10, 20, 30, 40 })]
    [InlineData(2, new byte[] { 20, 10, 40, 30 })]
    [InlineData(3, new byte[] { 40, 30, 20, 10 })]
    [InlineData(4, new byte[] { 30, 40, 10, 20 })]
    [InlineData(5, new byte[] { 10, 30, 20, 40 })]
    [InlineData(6, new byte[] { 30, 10, 40, 20 })]
    [InlineData(7, new byte[] { 40, 20, 30, 10 })]
    [InlineData(8, new byte[] { 20, 40, 10, 30 })]
    public void ApplyOrientation_produces_the_upright_layout(int orientation, byte[] expected)
    {
        var upright = ImageLoader.ApplyOrientation(Fixture(), orientation);
        Assert.Equal(expected, BlueChannel(upright));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(8, true)]
    public void SwapsAxes_only_for_quarter_turns(int orientation, bool swaps) =>
        Assert.Equal(swaps, ImageLoader.SwapsAxes(orientation));
}
