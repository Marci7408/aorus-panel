using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AorusPanel;

internal readonly record struct ImageFraming(bool Fill, double Zoom, double X, double Y)
{
    public const double MinZoom = 0.25;
    public const double MaxZoom = 4;

    public RectangleF Bounds(int sourceWidth, int sourceHeight)
    {
        double horizontal = PanelMedia.Width / (double)sourceWidth;
        double vertical = PanelMedia.Height / (double)sourceHeight;
        double scale = (Fill ? Math.Max(horizontal, vertical) : Math.Min(horizontal, vertical)) * Zoom;
        float width = (float)(sourceWidth * scale);
        float height = (float)(sourceHeight * scale);
        return new RectangleF((PanelMedia.Width - width) / 2 + (float)X,
            (PanelMedia.Height - height) / 2 + (float)Y, width, height);
    }

    public ImageFraming Constrain(int sourceWidth, int sourceHeight)
    {
        RectangleF bounds = Bounds(sourceWidth, sourceHeight);
        // Leave part of the image reachable even if it is dragged past an edge.
        double maxX = (PanelMedia.Width + bounds.Width) / 2 - Math.Min(16, bounds.Width / 2);
        double maxY = (PanelMedia.Height + bounds.Height) / 2 - Math.Min(16, bounds.Height / 2);
        return this with { X = Math.Clamp(X, -maxX, maxX), Y = Math.Clamp(Y, -maxY, maxY) };
    }
}

internal sealed class PanelImage
{
    private readonly byte[] original;
    public int Width { get; }
    public int Height { get; }
    public byte Delay { get; }
    public IReadOnlyList<BitmapSource> Preview { get; }
    public PanelMode Mode => Preview.Count > 1 ? PanelMode.Gif : PanelMode.Image;

    private PanelImage(byte[] original, int width, int height, byte delay, List<BitmapSource> preview)
    {
        this.original = original;
        Width = width;
        Height = height;
        Delay = delay;
        Preview = preview;
    }

    public static PanelImage Load(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 64L * 1024 * 1024)
            throw new InvalidDataException("Select an image under 64 MB.");
        byte[] original = new byte[(int)file.Length];
        file.ReadExactly(original);
        using var stream = new MemoryStream(original, writable: false);
        using Image source = Image.FromStream(stream);
        if (source.Width > 8192 || source.Height > 8192)
            throw new InvalidDataException("Select an image no larger than 8192 pixels per side.");

        int frameCount = ImageFormat.Gif.Equals(source.RawFormat) ? source.GetFrameCount(FrameDimension.Time) : 1;
        if (frameCount is < 1 or > PanelMedia.MaxFrames)
            throw new InvalidDataException($"Choose a GIF with 1 to {PanelMedia.MaxFrames} frames.");

        byte delay = 0;
        if (frameCount > 1)
        {
            byte[]? times = source.PropertyIdList.Contains(0x5100) ? source.GetPropertyItem(0x5100)?.Value : null;
            // The panel plays every frame at a single interval.
            uint centiseconds = times is { Length: >= 4 } ? BitConverter.ToUInt32(times) : 10;
            delay = (byte)(centiseconds == 0 ? 100 : Math.Clamp(centiseconds, 2u, 25u) * 10);
        }

        double maxSide = Math.Min(1280, Math.Sqrt(12_000_000d / frameCount));
        double scale = Math.Min(1, maxSide / Math.Max(source.Width, source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var preview = new List<BitmapSource>(frameCount);
        using var canvas = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(canvas);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        for (int i = 0; i < frameCount; i++)
        {
            if (frameCount > 1)
                source.SelectActiveFrame(FrameDimension.Time, i);
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.DrawImage(source, 0, 0, width, height);
            preview.Add(PreviewFrame(canvas));
        }

        return new PanelImage(original, source.Width, source.Height, delay, preview);
    }

    public PanelMedia Render(ImageFraming framing)
    {
        using var stream = new MemoryStream(original, writable: false);
        using Image source = Image.FromStream(stream);
        return PanelMedia.FromImage(source, Preview.Count, Delay, framing);
    }

    private static BitmapSource PreviewFrame(Bitmap canvas)
    {
        var area = new Rectangle(0, 0, canvas.Width, canvas.Height);
        var frame = canvas.LockBits(area, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            int stride = area.Width * 4;
            byte[] bytes = new byte[stride * area.Height];
            for (int y = 0; y < area.Height; y++)
                Marshal.Copy(frame.Scan0 + y * frame.Stride, bytes, y * stride, stride);

            var bitmap = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Pbgra32, null, bytes, stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            canvas.UnlockBits(frame);
        }
    }
}
