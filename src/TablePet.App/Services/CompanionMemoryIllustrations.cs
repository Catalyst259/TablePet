using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using TablePet.Core;

namespace TablePet.App.Services;

/// <summary>Optional, cached pictures keyed by the existing memory IDs; no saved-state changes are needed.</summary>
internal sealed class CompanionMemoryIllustrations
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Dictionary<string, BitmapSource?> _cache = new(StringComparer.Ordinal);

    public static CompanionMemoryIllustrations Default { get; } = new(
        Path.Combine(AppContext.BaseDirectory, "Assets", "strawberry-cat", "memories"));

    internal CompanionMemoryIllustrations(string directory) => _directory = directory;

    public BitmapSource? Find(string memoryId)
    {
        // Only the bundled catalog can name a file; saved or future unknown IDs cannot supply paths.
        if (CompanionContent.Default.FindMemory(memoryId) is null) return null;
        lock (_gate)
        {
            if (_cache.TryGetValue(memoryId, out var cached)) return cached;
            var image = Load(Path.Combine(_directory, memoryId + ".png"));
            _cache[memoryId] = image;
            return image;
        }
    }

    private static BitmapSource? Load(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // 80 DIP thumbnails remain crisp at high DPI without retaining the full generated image.
            image.DecodePixelWidth = 320;
            image.StreamSource = input;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            NotSupportedException or FileFormatException or ArgumentException or COMException)
        {
            // A missing or damaged decorative image must never prevent reading a memory.
            return null;
        }
    }
}
