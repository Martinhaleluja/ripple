using System;
using System.IO;

namespace Ripple.Core;

/// <summary>A small text log at %LOCALAPPDATA%\Ripple\ripple.log, fresh each run.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Prepare();

    public static string PathOnDisk => FilePath;

    public static void Info(string message)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // A log must never take the water down with it.
            }
        }
    }

    private static string Prepare()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ripple");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "ripple.log");
        try { File.WriteAllText(file, ""); } catch (IOException) { }
        return file;
    }
}
