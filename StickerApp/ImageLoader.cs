using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StickerApp;

/// <summary>
/// Orientation-aware image loading shared by matting and display.
/// WPF's decoders ignore the EXIF Orientation tag, so a portrait phone photo
/// (stored landscape + "rotate 90°" tag) would otherwise be matted and shown
/// sideways. Everything that reads a source image goes through here so the
/// cutout, the "Show original" view and the saved PNG all agree.
/// </summary>
internal static class ImageLoader
{
    /// <summary>
    /// Read the EXIF orientation (1–8) and the raw, pre-rotation pixel size from the
    /// file header without decoding pixels. Any failure (no metadata, a format whose
    /// codec doesn't expose it, unreadable file) reports orientation 1 and size 0×0.
    /// </summary>
    public static (int Orientation, int RawWidth, int RawHeight) Probe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(fs,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (OrientationOf(frame), frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return (1, 0, 0);
        }
    }

    /// <summary>EXIF orientation of <paramref name="path"/>, or 1 when absent/unreadable.</summary>
    public static int ReadOrientation(string path) => Probe(path).Orientation;

    private static int OrientationOf(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata md)
                return 1;
            // The photo-metadata policy name covers JPEG/TIFF (and other WIC codecs
            // that expose EXIF); the raw JPEG APP1 path is a fallback for codecs that
            // don't map the policy.
            foreach (var query in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}" })
            {
                try
                {
                    if (md.GetQuery(query) is { } v)
                    {
                        int o = Convert.ToInt32(v);
                        if (o is >= 1 and <= 8)
                            return o;
                    }
                }
                catch { /* query unsupported by this codec — try the next */ }
            }
        }
        catch { /* no metadata for this format (e.g. BMP) */ }
        return 1;
    }

    /// <summary>
    /// Return <paramref name="src"/> rotated/mirrored so it displays upright for the
    /// given EXIF orientation. Orientation 1 (or unknown) returns the source as-is.
    /// TransformGroup applies its children in order: 5 = transpose (rotate 90° CW,
    /// then mirror), 7 = transverse (rotate 90° CCW, then mirror).
    /// </summary>
    public static BitmapSource ApplyOrientation(BitmapSource src, int orientation)
    {
        Transform? t = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        if (t is null)
            return src;
        var oriented = new TransformedBitmap(src, t);
        oriented.Freeze();
        return oriented;
    }

    /// <summary>True when the orientation swaps width and height (the 90°/270° cases).</summary>
    public static bool SwapsAxes(int orientation) => orientation is >= 5 and <= 8;

    /// <summary>
    /// Load <paramref name="path"/> upright, for display. When <paramref name="maxSide"/>
    /// is positive and the image is larger, it is decoded straight to that long-side
    /// size (a 48 MP photo would otherwise pin ~190 MB per sticker while being shown
    /// at a fraction of that). Returns the image plus the *full-resolution* upright
    /// size, which is what sticker scale is measured against — so a downscaled decode
    /// doesn't change saved sizes in session.json.
    /// </summary>
    public static (BitmapSource Image, int FullWidth, int FullHeight) Load(
        string path, int maxSide = 0, bool ignoreCache = false)
    {
        var (orientation, rawW, rawH) = Probe(path);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (ignoreCache)
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        if (maxSide > 0 && rawW > 0 && rawH > 0 && Math.Max(rawW, rawH) > maxSide)
        {
            // Set one axis only: WPF then preserves the aspect ratio.
            if (rawW >= rawH)
                bmp.DecodePixelWidth = maxSide;
            else
                bmp.DecodePixelHeight = maxSide;
        }
        bmp.EndInit();
        bmp.Freeze();

        if (rawW <= 0 || rawH <= 0)
            (rawW, rawH) = (bmp.PixelWidth, bmp.PixelHeight);   // probe failed; decoded size is full size
        var image = ApplyOrientation(bmp, orientation);
        return SwapsAxes(orientation) ? (image, rawH, rawW) : (image, rawW, rawH);
    }
}
