using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StartupManager.Gui;

/// <summary>
/// A neutral "application placeholder" icon used when a queue item's executable has
/// no usable icon (missing file, unparseable command, or shell extraction failure).
///
/// Drawn as vectors with WPF and rendered once into a frozen bitmap, so it stays
/// crisp at any size and needs no image assets or NuGet packages. This deliberately
/// replaces the blank white document icon that SHGetFileInfo returns via
/// SHGFI_USEFILEATTRIBUTES for missing/unknown file types.
/// </summary>
internal static class DefaultAppIcon
{
    private static readonly Lazy<ImageSource> Instance = new(Create, isThreadSafe: true);

    /// <summary>Shared, frozen image source. Never null.</summary>
    public static ImageSource Image => Instance.Value;

    private static ImageSource Create()
    {
        const int size = 64;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double s = size;

            // Rounded blue tile — same palette as the application icon, so the
            // placeholder reads as part of the app rather than a random file type.
            var bg = new LinearGradientBrush(
                Color.FromRgb(0x6B, 0x86, 0xFF),
                Color.FromRgb(0x3A, 0x57, 0xE0),
                new Point(0, 0), new Point(0, 1));
            dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, s, s), s * 0.22, s * 0.22);

            // Soft top sheen so the tile reads as a raised surface.
            var sheen = new LinearGradientBrush(
                Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
                new Point(0, 0), new Point(0, 0.6));
            dc.DrawRoundedRectangle(sheen, null, new Rect(0, 0, s, s), s * 0.22, s * 0.22);

            // White "application window" outline with a title-bar divider: a
            // generic but meaningful app glyph, clearly not a blank document.
            var pen = new Pen(new SolidColorBrush(Colors.White), s * 0.075)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };

            double x = s * 0.25, y = s * 0.27, w = s * 0.50, h = s * 0.46;
            dc.DrawRoundedRectangle(null, pen, new Rect(x, y, w, h), s * 0.055, s * 0.055);
            dc.DrawLine(pen, new Point(x, y + s * 0.15), new Point(x + w, y + s * 0.15));
        }

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }
}
