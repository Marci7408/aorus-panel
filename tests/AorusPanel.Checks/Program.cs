using System;
using System.Buffers.Binary;
using System.Drawing;
using System.IO;
using AorusPanel;

class Checks
{
    [STAThread]
    static void Main()
    {
        DirectoryInfo folder = Directory.CreateTempSubdirectory("AorusPanelChecks-");
        try { Test(folder.FullName); }
        finally { folder.Delete(recursive: true); }
    }

    private static void Test(string folder)
    {
        string png = Path.Combine(folder, "framing-sample.png");
        using (var bitmap = new Bitmap(320, 170))
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Blue);
            graphics.FillRectangle(Brushes.Red, 0, 0, 160, 170);
            bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        }

        var source = PanelImage.Load(png);
        Require(source.Width == 320 && source.Height == 170 && source.Preview.Count == 1, "Source dimensions or preview");
        var fit = new ImageFraming(false, 1, 0, 0);
        var centered = source.Render(fit);
        Require(Pixel(centered, 80, 85) == 0xf800 && Pixel(centered, 240, 85) == 0x001f, "Fit RGB565");
        var shifted = source.Render(fit with { X = 48, Y = 22 });
        Require(Pixel(shifted, 20, 85) == 0 && Pixel(shifted, 155, 85) == 0xf800 &&
            Pixel(shifted, 260, 85) == 0x001f && Pixel(shifted, 80, 10) == 0, "Manual pan");
        var zoomed = source.Render(fit with { Zoom = 2, X = 48, Y = 22 });
        Require(Pixel(zoomed, 20, 10) == 0xf800 && Pixel(zoomed, 185, 85) == 0xf800 &&
            Pixel(zoomed, 240, 85) == 0x001f, "Pan and zoom");
        var zoomedOut = source.Render(fit with { Zoom = .5 });
        Require(Pixel(zoomedOut, 60, 85) == 0 && Pixel(zoomedOut, 120, 85) == 0xf800 &&
            Pixel(zoomedOut, 200, 85) == 0x001f, "Manual zoom out");
        var fitPortrait = fit.Bounds(170, 320);
        var fillPortrait = (fit with { Fill = true }).Bounds(170, 320);
        Require(fitPortrait.Height == 170 && fitPortrait.X > 0 &&
            fillPortrait.Width == 320 && fillPortrait.Y < 0, "Fit and fill preserve aspect");
        var clamped = (fit with { X = 1e6, Y = -1e6 }).Constrain(320, 170).Bounds(320, 170);
        Require(clamped.Left == 304 && clamped.Bottom == 16, "Pan preserves a way to recover the image");

        // A generated three-frame, solid-color GIF with an 80 ms first frame.
        string gifPath = Path.Combine(folder, "animation.gif");
        File.WriteAllBytes(gifPath, Convert.FromBase64String(
            "R0lGODlhQAAoAIEAAPAUKAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACAAAACwAAAAAQAAoAAAIVAABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFixgzatzIsaPHjyBDihxJsqTJkyhTqlzJsqXLlzBjypxJs6bNmzhz6tzJs6fPn0CDCh1KtKjRowQDAgAh+QQBEAABACwAAAAAQAAoAIEUqvAAAAAAAAAAAAAIVAABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFixgzatzIsaPHjyBDihxJsqTJkyhTqlzJsqXLlzBjypxJs6bNmzhz6tzJs6fPn0CDCh1KtKjRowQDAgAh+QQBCAABACwAAAAAQAAoAIEU8FAAAAAAAAAAAAAIVAABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFixgzatzIsaPHjyBDihxJsqTJkyhTqlzJsqXLlzBjypxJs6bNmzhz6tzJs6fPn0CDCh1KtKjRowQDAgA7"));
        var gif = PanelImage.Load(gifPath);
        Require(gif.Preview.Count == 3 && gif.Delay == 80, "Animated preview");
        var upload = gif.Render(new ImageFraming(true, 1.5, -24, 14));
        string staged = Path.Combine(folder, "upload.bin");
        try
        {
            upload.Stage(staged);
            var recovered = PanelMedia.ReadStaged(staged);
            Require(recovered.Mode == PanelMode.Gif && recovered.Frames == 3 &&
                recovered.Data.AsSpan().SequenceEqual(upload.Data), "Cropped GIF upload encoding");
        }
        finally
        {
            File.Delete(staged);
        }
        Require(PanelMedia.FromText("RTX 3070", Color.White, Color.Black).Preview.Count == 1, "Text preview regression");
        Console.WriteLine("Passed fit, fill, pan, zoom, RGB565 color placement, bounds, cropped GIF encoding, and text preview.");
    }

    static ushort Pixel(PanelMedia media, int x, int y) =>
        BinaryPrimitives.ReadUInt16LittleEndian(media.Data.AsSpan(12 + (y * PanelMedia.Width + x) * 2));

    static void Require(bool condition, string test)
    {
        if (!condition)
            throw new Exception(test);
    }
}
