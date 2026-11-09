using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StartupManager.Gui;

/// <summary>
/// Draws the application icon (a rounded blue tile with a white power glyph) and
/// packs it into a multi-resolution .ico. Vector-drawn with WPF so no imaging
/// package is required and every size stays crisp.
/// </summary>
internal static class IconFactory
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    public static void Generate(string icoPath, string? previewPath = null)
    {
        var frames = Sizes.Select(s => (Size: s, Data: RenderPng(s))).ToList();

        if (previewPath is not null)
            File.WriteAllBytes(previewPath, frames.First(f => f.Size == 256).Data);

        var dir = Path.GetDirectoryName(icoPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = File.Create(icoPath);
        using var w = new BinaryWriter(fs);

        // ICONDIR
        w.Write((ushort)0);              // reserved
        w.Write((ushort)1);              // type = icon
        w.Write((ushort)frames.Count);   // image count

        // ICONDIRENTRY per image (PNG-compressed frames, Vista+)
        var offset = 6 + 16 * frames.Count;
        foreach (var (size, data) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); // width  (0 => 256)
            w.Write((byte)(size >= 256 ? 0 : size)); // height (0 => 256)
            w.Write((byte)0);                        // palette count
            w.Write((byte)0);                        // reserved
            w.Write((ushort)1);                      // colour planes
            w.Write((ushort)32);                     // bits per pixel
            w.Write(data.Length);                    // bytes in resource
            w.Write(offset);                         // offset from file start
            offset += data.Length;
        }

        foreach (var (_, data) in frames) w.Write(data);
    }

    private static byte[] RenderPng(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double s = size;

            // rounded tile with a vertical blue gradient
            var bg = new LinearGradientBrush(
                Color.FromRgb(0x6B, 0x86, 0xFF),
                Color.FromRgb(0x3A, 0x57, 0xE0),
                new Point(0, 0), new Point(0, 1));
            dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, s, s), s * 0.22, s * 0.22);

            // soft top highlight so the tile reads as a raised surface
            var sheen = new LinearGradientBrush(
                Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
                new Point(0, 0), new Point(0, 0.55));
            dc.DrawRoundedRectangle(sheen, null, new Rect(0, 0, s, s), s * 0.22, s * 0.22);

            // power glyph: ring with a gap at the top + a vertical stem
            double cx = s * 0.5, cy = s * 0.545, r = s * 0.245;
            var stroke = new Pen(new SolidColorBrush(Colors.White), s * 0.098)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(Polar(cx, cy, r, -55), false, false);
                g.ArcTo(Polar(cx, cy, r, 235), new Size(r, r), 0,
                    isLargeArc: true, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, stroke, geo);

            dc.DrawLine(stroke,
                new Point(cx, cy - r * 1.06),
                new Point(cx, cy - r * 0.02));
        }

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static Point Polar(double cx, double cy, double r, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }
}
