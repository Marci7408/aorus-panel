using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AorusPanel;

internal sealed record PanelMedia(PanelMode Mode, ushort Frames, byte Delay, byte[] Data,
    IReadOnlyList<BitmapSource> Preview)
{
    public const int Width = 320;
    public const int Height = 170;
    private const int MaxBytes = 16 * 1024 * 1024;
    internal const int MaxFrames = 150;
    private const int Pixels = Width * Height;

    internal static PanelMedia FromImage(Image source, int frameCount, byte delay, ImageFraming framing)
    {
        var frames = new List<ushort[]>(frameCount);
        using var canvas = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(canvas);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        RectangleF target = framing.Bounds(source.Width, source.Height);
        for (int i = 0; i < frameCount; i++)
        {
            if (frameCount > 1)
                source.SelectActiveFrame(FrameDimension.Time, i);
            graphics.Clear(System.Drawing.Color.Black);
            graphics.DrawImage(source, target);
            frames.Add(ReadRgb565(canvas));
        }

        return Encode(frameCount > 1 ? PanelMode.Gif : PanelMode.Image, delay, frames, []);
    }

    public static PanelMedia FromText(string text, System.Drawing.Color ink, System.Drawing.Color background)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("Enter the text you want on the LCD.");
        using var canvas = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(canvas);
        graphics.Clear(background);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(ink);
        using var layout = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var bounds = new RectangleF(14, 10, Width - 28, Height - 20);
        for (int size = 54; size >= 8; size -= 2)
        {
            using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            graphics.MeasureString(text, font, bounds.Size, layout, out int characters, out _);
            if (characters < text.Length && size > 8)
                continue;
            graphics.DrawString(text, font, brush, bounds, layout);
            break;
        }

        ushort[] pixels = ReadRgb565(canvas);
        BitmapSource rendered = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr565, null, pixels, Width * 2);
        rendered.Freeze();
        return Encode(PanelMode.Text, 0, [pixels], [rendered]);
    }

    private static ushort[] ReadRgb565(Bitmap canvas)
    {
        var pixels = new ushort[Pixels];
        var frame = canvas.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            byte[] row = new byte[Width * 3];
            for (int y = 0; y < Height; y++)
            {
                Marshal.Copy(frame.Scan0 + y * frame.Stride, row, 0, row.Length);
                for (int x = 0; x < Width; x++)
                {
                    int offset = x * 3;
                    pixels[y * Width + x] = (ushort)(((row[offset + 2] >> 3) << 11) |
                        ((row[offset + 1] >> 2) << 5) | (row[offset] >> 3));
                }
            }
        }
        finally
        {
            canvas.UnlockBits(frame);
        }

        return pixels;
    }

    private static PanelMedia Encode(PanelMode mode, byte delay, List<ushort[]> frames, List<BitmapSource> preview)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)frames.Count);
        output.SetLength(2 + frames.Count * 10);
        int next = (int)output.Length;
        for (int index = 0; index < frames.Count; index++)
        {
            output.Position = next;
            ushort[] frame = frames[index];
            bool compressed = mode == PanelMode.Gif;
            if (compressed)
                WriteRle(writer, frame);
            else
                foreach (ushort pixel in frame)
                    writer.Write(pixel);
            next = (int)output.Position;

            output.Position = 2 + index * 10;
            writer.Write(next - 1);
            writer.Write((ushort)Width);
            writer.Write((ushort)Height);
            writer.Write((ushort)(compressed ? 3 : 1));
        }

        if (next > MaxBytes)
            throw new InvalidDataException("The processed GIF exceeds the panel’s 16 MB limit.");
        return new PanelMedia(mode, (ushort)frames.Count, delay, output.ToArray(), preview);
    }

    private static void WriteRle(BinaryWriter writer, ushort[] pixels)
    {
        int start = 0;
        while (start < pixels.Length)
        {
            int end = start + 1;
            int limit = Math.Min(start + 32767, pixels.Length);
            while (end < limit && pixels[end] == pixels[start])
                end++;

            if (end - start >= 3)
            {
                writer.Write((ushort)(0x8000 | (end - start)));
                writer.Write(pixels[start]);
            }
            else
            {
                while (end < limit && (end + 2 >= pixels.Length ||
                    pixels[end] != pixels[end + 1] || pixels[end] != pixels[end + 2]))
                    end++;
                writer.Write((ushort)(end - start));
                for (int i = start; i < end; i++)
                    writer.Write(pixels[i]);
            }
            start = end;
        }
    }

    public void Stage(string path)
    {
        using var writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        writer.Write((byte)Mode);
        writer.Write(Delay);
        writer.Write(Data);
    }

    public static PanelMedia ReadStaged(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 16 or > MaxBytes + 2)
            throw new InvalidDataException("Invalid upload size.");
        using var reader = new BinaryReader(file);
        var mode = (PanelMode)reader.ReadByte();
        byte delay = reader.ReadByte();
        byte[] data = reader.ReadBytes((int)file.Length - 2);

        using var content = new BinaryReader(new MemoryStream(data, writable: false));
        ushort frames = content.ReadUInt16();
        bool gif = mode == PanelMode.Gif;
        if ((!gif && (mode is not (PanelMode.Text or PanelMode.Image) || frames != 1 || delay != 0)) ||
            (gif && (frames is < 2 or > MaxFrames || delay < 20)))
            throw new InvalidDataException("Invalid upload mode, frame count or timing.");

        int next = 2 + frames * 10;
        for (int i = 0; i < frames; i++)
        {
            content.BaseStream.Position = 2 + i * 10;
            int end = content.ReadInt32();
            if (content.ReadUInt16() != Width || content.ReadUInt16() != Height ||
                content.ReadUInt16() != (gif ? 3 : 1) || end < next || end >= data.Length)
                throw new InvalidDataException("Invalid frame dimensions or offsets.");

            if (gif)
            {
                content.BaseStream.Position = next;
                int pixels = 0;
                while (content.BaseStream.Position <= end)
                {
                    ushort run = content.ReadUInt16();
                    int length = run & 0x7fff;
                    if (length == 0)
                        throw new InvalidDataException("Empty GIF run.");
                    pixels += length;
                    content.BaseStream.Position += (run & 0x8000) != 0 ? 2 : length * 2;
                    if (pixels > Pixels)
                        throw new InvalidDataException("Oversized GIF frame.");
                }
                if (pixels != Pixels || content.BaseStream.Position != end + 1)
                    throw new InvalidDataException("Truncated GIF frame.");
            }
            else if (end + 1 - next != Pixels * 2)
                throw new InvalidDataException("Invalid still frame length.");

            next = end + 1;
        }

        if (next != data.Length)
            throw new InvalidDataException("Unexpected data after the last frame.");
        return new PanelMedia(mode, frames, delay, data, []);
    }
}
