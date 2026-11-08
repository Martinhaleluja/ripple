using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Rectangle = System.Drawing.Rectangle;

namespace Ripple.Core;

/// <summary>
/// Finds and prepares the picture under the water. Ripple only ever reads the Windows
/// wallpaper: choosing a picture here never changes what Windows itself has set.
/// </summary>
internal static class WallpaperSource
{
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
        uint GetMonitorDevicePathCount();
        Native.RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
    }

    [ComImport, Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")]
    private class DesktopWallpaperClass
    {
    }

    /// <summary>What Windows itself is showing, for the preview and for the water.</summary>
    public sealed record WindowsWallpaper(string? Path, WallpaperFit Fit, bool Span, Color Background);

    public static WindowsWallpaper ReadWindows(Native.RECT monitor)
    {
        string? path = null;
        var fit = WallpaperFit.Fill;
        var span = false;
        var background = Color.FromArgb(unchecked((int)(0xFF000000 | Swap(Native.GetSysColor(Native.COLOR_DESKTOP)))));

        try
        {
            var wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
            try
            {
                var count = wallpaper.GetMonitorDevicePathCount();
                for (uint i = 0; i < count; i++)
                {
                    var id = wallpaper.GetMonitorDevicePathAt(i);
                    Native.RECT rect;
                    try
                    {
                        rect = wallpaper.GetMonitorRECT(id);
                    }
                    catch (COMException)
                    {
                        continue; // A monitor Windows knows of but is not showing.
                    }

                    if (rect.Left == monitor.Left && rect.Top == monitor.Top)
                    {
                        path = wallpaper.GetWallpaper(id);
                        break;
                    }
                }

                switch (wallpaper.GetPosition())
                {
                    case 0: fit = WallpaperFit.Centre; break;
                    case 2: fit = WallpaperFit.Stretch; break;
                    case 3: fit = WallpaperFit.Fit; break;
                    case 5: span = true; break;
                    default: fit = WallpaperFit.Fill; break;
                }

                background = Color.FromArgb(unchecked((int)(0xFF000000 | Swap(wallpaper.GetBackgroundColor()))));
            }
            finally
            {
                Marshal.ReleaseComObject(wallpaper);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            Log.Info($"Desktop wallpaper service not available: {exception.Message}");
        }

        if (!Usable(path))
        {
            var buffer = new StringBuilder(1024);
            if (Native.SystemParametersInfo(Native.SPI_GETDESKWALLPAPER, (uint)buffer.Capacity, buffer, 0))
            {
                path = buffer.ToString();
            }
        }

        if (!Usable(path))
        {
            // Slideshows and cloud pictures keep their current frame here.
            var transcoded = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\TranscodedWallpaper");
            path = File.Exists(transcoded) ? transcoded : null;
        }

        return new WindowsWallpaper(Usable(path) ? path : null, fit, span, background);
    }

    private static bool Usable(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private static uint Swap(uint colorRef) =>
        ((colorRef & 0xFF) << 16) | (colorRef & 0xFF00) | ((colorRef >> 16) & 0xFF);

    /// <summary>
    /// Draws the picture for one monitor at that monitor's physical size. Falls back to
    /// the solid background if the picture can't be read.
    /// </summary>
    public static Bitmap Compose(string? path, WallpaperFit fit, bool span, Color background, Native.RECT monitor, Native.RECT everyMonitor, Dictionary<string, Bitmap> decoded)
    {
        var target = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(target);
        graphics.Clear(background);

        if (path is null)
        {
            return target;
        }

        Bitmap picture;
        try
        {
            if (!decoded.TryGetValue(path, out picture!))
            {
                picture = Decode(path);
                decoded[path] = picture;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Info($"Wallpaper could not be read ({System.IO.Path.GetFileName(path)}): {exception.Message}");
            return target;
        }

        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;

        var area = span
            ? new Rectangle(everyMonitor.Left - monitor.Left, everyMonitor.Top - monitor.Top, everyMonitor.Width, everyMonitor.Height)
            : new Rectangle(0, 0, monitor.Width, monitor.Height);

        var place = PlaceIn(picture.Width, picture.Height, area, span ? WallpaperFit.Fill : fit);
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY); // Stops a pale fringe along the picture's edge.
        graphics.DrawImage(picture, place, 0, 0, picture.Width, picture.Height, GraphicsUnit.Pixel, attributes);
        return target;
    }

    private static Rectangle PlaceIn(int width, int height, Rectangle area, WallpaperFit fit)
    {
        double scale;
        switch (fit)
        {
            case WallpaperFit.Stretch:
                return area;
            case WallpaperFit.Centre:
                scale = 1;
                break;
            case WallpaperFit.Fit:
                scale = Math.Min(area.Width / (double)width, area.Height / (double)height);
                break;
            default:
                scale = Math.Max(area.Width / (double)width, area.Height / (double)height);
                break;
        }

        var w = (int)Math.Round(width * scale);
        var h = (int)Math.Round(height * scale);
        return new Rectangle(area.X + ((area.Width - w) / 2), area.Y + ((area.Height - h) / 2), w, h);
    }

    /// <summary>Reads any picture Windows has a codec for, and honours phone-camera rotation.</summary>
    private static Bitmap Decode(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];

        BitmapSource source = frame;
        var angle = 0;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort orientation)
            {
                angle = orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
            }
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // No readable rotation: show it as stored.
        }

        if (angle != 0)
        {
            source = new TransformedBitmap(source, new RotateTransform(angle));
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var bitmap = new Bitmap(converted.PixelWidth, converted.PixelHeight, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            converted.CopyPixels(System.Windows.Int32Rect.Empty, data.Scan0, data.Stride * bitmap.Height, data.Stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }
}
