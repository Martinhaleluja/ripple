using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Ripple.UI;

/// <summary>
/// Ripple's bubble lettering, drawn from scratch rather than taken from a font, so there
/// is no licence to carry. Each letter is a few thick round-ended strokes, inflated and
/// shaded like a glossy blue bubble. Being vector, it stays sharp at any size.
/// </summary>
public static class LogoArt
{
    private const double Fat = 27;

    private static readonly Color Deep = Color.FromRgb(0x06, 0x3F, 0xA8);
    private static readonly Color Mid = Color.FromRgb(0x14, 0x7D, 0xF5);
    private static readonly Color Bright = Color.FromRgb(0x6C, 0xC6, 0xFF);

    // Each stroke is path text in a letter's own box; the baseline is y = 100.
    private static readonly (string[] Strokes, double Width, double[]? Dot)[] Letters =
    {
        // R
        (new[]
        {
            "M 14,14 L 14,100",
            "M 14,14 L 38,14 C 72,14 72,60 38,60 L 14,60",
            "M 36,60 L 64,100",
        }, 88, null),
        // i
        (new[] { "M 14,48 L 14,100" }, 36, new[] { 14d, 14d, 15.5d }),
        // p
        (new[]
        {
            "M 14,48 L 14,134",
            "M 14,60 C 22,42 66,38 66,74 C 66,110 22,106 14,88",
        }, 80, null),
        // p
        (new[]
        {
            "M 14,48 L 14,134",
            "M 14,60 C 22,42 66,38 66,74 C 66,110 22,106 14,88",
        }, 80, null),
        // l
        (new[] { "M 14,10 L 14,100" }, 34, null),
        // e
        (new[]
        {
            "M 10,74 L 70,74 C 70,20 10,20 10,74 C 10,116 50,120 70,108",
        }, 82, null),
    };

    /// <summary>The whole word, for the window and the large app icon.</summary>
    public static DrawingImage Word()
    {
        var image = new DrawingImage(DrawLetters(0, Letters.Length));
        image.Freeze();
        return image;
    }

    /// <summary>The bubble R alone: at tray size, the whole word can't be read.</summary>
    public static DrawingImage Initial()
    {
        var image = new DrawingImage(DrawLetters(0, 1));
        image.Freeze();
        return image;
    }

    private static Drawing DrawLetters(int first, int count)
    {
        var group = new DrawingGroup();
        var x = 0d;
        var overlap = -4d;

        // Letters overlap a little so the word reads as one squashy shape.
        var bodies = new List<(Geometry Body, TranslateTransform Shift, string[] Strokes, double[]? Dot)>();
        for (var i = first; i < first + count; i++)
        {
            var (strokes, width, dot) = Letters[i];
            var shift = new TranslateTransform(x, 0);
            bodies.Add((Body(strokes, dot), shift, strokes, dot));
            x += width + overlap;
        }

        // Soft outline first, behind every letter, so overlaps stay crisp.
        foreach (var (body, shift, _, _) in bodies)
        {
            var outline = new PathGeometry();
            outline.AddGeometry(body);
            var wide = outline.GetWidenedPathGeometry(new Pen(Brushes.Black, 11) { LineJoin = PenLineJoin.Round });
            var combined = Geometry.Combine(wide, body, GeometryCombineMode.Union, null);
            combined.Transform = shift;
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(Deep), null, combined));
        }

        foreach (var (body, shift, strokes, dot) in bodies)
        {
            group.Children.Add(Letter(body, shift, strokes, dot));
        }

        group.Freeze();
        return group;
    }

    private static Geometry Body(string[] strokes, double[]? dot)
    {
        Geometry shape = Geometry.Empty;
        foreach (var stroke in strokes)
        {
            var widened = Geometry.Parse(stroke).GetWidenedPathGeometry(new Pen(Brushes.Black, Fat)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            });
            shape = Geometry.Combine(shape, widened, GeometryCombineMode.Union, null);
        }

        if (dot is not null)
        {
            shape = Geometry.Combine(shape, new EllipseGeometry(new Point(dot[0], dot[1]), dot[2], dot[2]), GeometryCombineMode.Union, null);
        }

        return shape;
    }

    private static Drawing Letter(Geometry body, Transform shift, string[] strokes, double[]? dot)
    {
        var letter = new DrawingGroup { Transform = shift, ClipGeometry = body };

        // The body: lighter at the top, deep at the bottom, like a filled bubble.
        var fill = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Bright, 0),
            new GradientStop(Mid, 0.5),
            new GradientStop(Color.FromRgb(0x0A, 0x56, 0xD6), 1),
        }, new Point(0.5, 0), new Point(0.5, 1));
        letter.Children.Add(new GeometryDrawing(fill, null, body));

        // A darker rim along the bottom edge rounds it off.
        var under = new TranslateTransform(0, -7);
        var rim = Geometry.Combine(body, Clone(body, under), GeometryCombineMode.Exclude, null);
        letter.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x55, 0x03, 0x2E, 0x8C)), null, rim));

        // The glossy streak: the same strokes, thin and pale, nudged up and to the left.
        var shine = new Pen(new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)), 6.5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        foreach (var stroke in strokes)
        {
            var line = Geometry.Parse(stroke).Clone();
            line.Transform = new TranslateTransform(-4.5, -6.5);
            letter.Children.Add(new GeometryDrawing(null, shine, Shorten(line)));
        }

        if (dot is not null)
        {
            letter.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)), null,
                new EllipseGeometry(new Point(dot[0] - 4.5, dot[1] - 6), 4.2, 4.2)));
        }

        return letter;
    }

    private static Geometry Clone(Geometry geometry, Transform transform)
    {
        var copy = geometry.Clone();
        copy.Transform = transform;
        return copy;
    }

    /// <summary>Only the first part of each stroke shines; a full-length streak looks like a wire.</summary>
    private static Geometry Shorten(Geometry line)
    {
        var flat = line.GetFlattenedPathGeometry(0.5, ToleranceType.Absolute);
        var result = new PathGeometry();
        foreach (var figure in flat.Figures)
        {
            var points = new List<Point> { figure.StartPoint };
            foreach (var segment in figure.Segments)
            {
                if (segment is PolyLineSegment poly)
                {
                    points.AddRange(poly.Points);
                }
                else if (segment is LineSegment single)
                {
                    points.Add(single.Point);
                }
            }

            var total = 0d;
            for (var i = 1; i < points.Count; i++)
            {
                total += (points[i] - points[i - 1]).Length;
            }

            var from = total * 0.12;
            var to = total * 0.5;
            var travelled = 0d;
            var kept = new List<Point>();
            for (var i = 1; i < points.Count; i++)
            {
                var length = (points[i] - points[i - 1]).Length;
                if (travelled + length >= from && travelled <= to)
                {
                    kept.Add(points[i]);
                }

                travelled += length;
            }

            if (kept.Count == 0 && points.Count > 1)
            {
                kept.Add(points[0] + ((points[^1] - points[0]) * 0.12));
                kept.Add(points[0] + ((points[^1] - points[0]) * 0.5));
            }
            else if (kept.Count == 1 && points.Count > 1)
            {
                kept.Add(kept[0]);
            }

            var start = points.Count == 2 ? points[0] + ((points[1] - points[0]) * 0.12) : kept[0];
            var end = points.Count == 2 ? points[0] + ((points[1] - points[0]) * 0.5) : kept[^1];
            var outFigure = new PathFigure { StartPoint = start, IsClosed = false };
            if (points.Count == 2)
            {
                outFigure.Segments.Add(new LineSegment(end, true));
            }
            else
            {
                outFigure.Segments.Add(new PolyLineSegment(kept, true));
            }

            result.Figures.Add(outFigure);
        }

        return result;
    }

    /// <summary>Renders a logo drawing to pixels, centred in a square or fitted to a box.</summary>
    public static BitmapSource Render(DrawingImage art, int width, int height, double margin = 0.06)
    {
        var bounds = ((Drawing)art.Drawing).Bounds;
        var usableW = width * (1 - (margin * 2));
        var usableH = height * (1 - (margin * 2));
        var scale = Math.Min(usableW / bounds.Width, usableH / bounds.Height);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform((width - (bounds.Width * scale)) / 2, (height - (bounds.Height * scale)) / 2));
            context.PushTransform(new ScaleTransform(scale, scale));
            context.PushTransform(new TranslateTransform(-bounds.Left, -bounds.Top));
            context.DrawDrawing(art.Drawing);
            context.Pop();
            context.Pop();
            context.Pop();
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>The icon for the tray, at the size Windows asks for.</summary>
    public static System.Drawing.Icon TrayIcon(int size)
    {
        var pixels = Render(Initial(), size, size, 0.02);
        var stride = size * 4;
        var buffer = new byte[stride * size];
        pixels.CopyPixels(buffer, stride, 0);
        using var bitmap = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, size, size), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        System.Runtime.InteropServices.Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        bitmap.UnlockBits(data);

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)icon.Clone();
        }
        finally
        {
            Core.NativeIcons.Destroy(handle);
        }
    }

    /// <summary>Writes Ripple.ico and the logo pictures. Run with --write-assets when the art changes.</summary>
    public static void WriteAssets(string folder)
    {
        Directory.CreateDirectory(folder);
        SavePng(Render(Word(), 1200, 520, 0.03), System.IO.Path.Combine(folder, "logo-word.png"));

        var frames = new List<byte[]>();
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        foreach (var size in sizes)
        {
            // Small sizes show the bubble R. From 128 up, the whole word fits and reads.
            var art = size >= 128 ? Word() : Initial();
            var margin = size >= 128 ? 0.03 : 0.02;
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Render(art, size, size, margin)));
            encoder.Save(stream);
            frames.Add(stream.ToArray());
        }

        using var file = new FileStream(System.IO.Path.Combine(folder, "Ripple.ico"), FileMode.Create);
        using var writer = new BinaryWriter(file);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Count);
        var offset = 6 + (16 * frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }

        foreach (var frame in frames)
        {
            writer.Write(frame);
        }
    }

    private static void SavePng(BitmapSource source, string path)
    {
        using var stream = new FileStream(path, FileMode.Create);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(stream);
    }
}
