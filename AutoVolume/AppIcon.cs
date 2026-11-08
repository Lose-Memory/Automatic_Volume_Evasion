using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AutoVolume;

/// <summary>Draws the app icon in code: a speaker with a downward arrow on a blue rounded square.</summary>
internal static class AppIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Bitmap Render(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size;

        using (var background = new SolidBrush(Color.FromArgb(0x00, 0x67, 0xC0)))
        using (var rounded = RoundedRect(0, 0, s, s, s * 0.22f))
            g.FillPath(background, rounded);

        using var white = new SolidBrush(Color.White);

        using (var body = new GraphicsPath())
        {
            body.AddPolygon(
            [
                new PointF(s * 0.17f, s * 0.41f),
                new PointF(s * 0.30f, s * 0.41f),
                new PointF(s * 0.47f, s * 0.25f),
                new PointF(s * 0.47f, s * 0.75f),
                new PointF(s * 0.30f, s * 0.59f),
                new PointF(s * 0.17f, s * 0.59f),
            ]);
            g.FillPath(white, body);
        }

        using (var stem = new GraphicsPath())
        {
            stem.AddPolygon(
            [
                new PointF(s * 0.655f, s * 0.24f),
                new PointF(s * 0.775f, s * 0.24f),
                new PointF(s * 0.775f, s * 0.50f),
                new PointF(s * 0.655f, s * 0.50f),
            ]);
            g.FillPath(white, stem);
        }

        using (var head = new GraphicsPath())
        {
            head.AddPolygon(
            [
                new PointF(s * 0.56f, s * 0.48f),
                new PointF(s * 0.87f, s * 0.48f),
                new PointF(s * 0.715f, s * 0.76f),
            ]);
            g.FillPath(white, head);
        }

        return bitmap;
    }

    public static Icon CreateIcon(int size = 32)
    {
        using var bitmap = Render(size);
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public static System.Windows.Media.ImageSource CreateImageSource(int size = 64)
    {
        using var icon = CreateIcon(size);
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    private static GraphicsPath RoundedRect(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}