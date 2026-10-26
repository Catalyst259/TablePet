using System;
using System.IO;
using System.Text;

namespace TablePet.App.Services;

/// <summary>A bounded local log. Never logs pointer movements or individual animation frames.</summary>
public static class ErrorLogger
{
    private static readonly object Gate = new();
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopPet", "TablePet.log");

    public static void Write(string message, Exception? exception = null)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024)
                    File.Move(LogPath, LogPath + ".previous", overwrite: true);
                var line = $"{DateTimeOffset.Now:O} {message}";
                if (exception is not null) line += $"{Environment.NewLine}{exception}";
                File.AppendAllText(LogPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never prevent the companion from starting or exiting.
            }
        }
    }
}
