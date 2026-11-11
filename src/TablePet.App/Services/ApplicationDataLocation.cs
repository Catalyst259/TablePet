using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TablePet.App.Services;

/// <summary>Uses the same normalized directory for storage and single-instance signals.</summary>
internal sealed record ApplicationDataLocation(string DirectoryPath, string InstanceSuffix)
{
    // The public executable is TablePet.exe; existing data stays here across the rename.
    internal static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopPet.Skeletal");

    internal static ApplicationDataLocation Resolve(string? directory)
    {
        var defaultDirectory = Normalize(DefaultDirectory);
        var resolved = directory is null ? defaultDirectory : Normalize(directory);
        // Preserve the installed application's signal names, including when its directory is explicit.
        var suffix = StringComparer.OrdinalIgnoreCase.Equals(resolved, defaultDirectory) ? ""
            : "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resolved.ToUpperInvariant())))[..16];
        return new(resolved, suffix);
    }

    private static string Normalize(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
}
