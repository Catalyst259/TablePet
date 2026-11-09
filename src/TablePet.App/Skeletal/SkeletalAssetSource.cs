using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TablePet.App.Services;

namespace TablePet.App.Skeletal;

/// <summary>Reconstructs the original artwork on demand; the playback clock still owns all timing.</summary>
internal sealed class SkeletalAssetSource
{
    private readonly LegacyRenderer _legacy;
    private readonly WorkRenderer? _work;
    private readonly FireflyRenderer? _firefly;
    private readonly Dictionary<string, (int Width, int Height)> _dimensions = new(StringComparer.Ordinal);

    private SkeletalAssetSource(string root)
    {
        using var pack = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "skeletal", "pack.json")));
        if (pack.RootElement.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("Unsupported skeletal pack version.");
        _legacy = new(root);
        // A damaged optional scene must not disable the required daily motions.
        try { _work = new(root); }
        catch (Exception error) { ErrorLogger.Write("工作骨骼素材无法加载，已跳过工作片段。", error); }
        try { _firefly = new(root); }
        catch (Exception error) { ErrorLogger.Write("萤火虫骨骼素材无法加载，已跳过萤火虫片段。", error); }
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var data = manifest.RootElement;
        var canvas = data.GetProperty("canvas");
        foreach (var clip in data.GetProperty("clips").EnumerateObject())
            Register(clip.Value, canvas);
        if (data.TryGetProperty("decorations", out var decorations))
            foreach (var channel in decorations.EnumerateObject())
                Register(channel.Value, channel.Value.GetProperty("canvas"));
    }

    private void Register(JsonElement clip, JsonElement canvas)
    {
        var width = canvas.GetProperty("width").GetInt32();
        var height = canvas.GetProperty("height").GetInt32();
        if (width is < 1 or > 4096 || height is < 1 or > 4096)
            throw new InvalidDataException("Invalid skeletal canvas dimensions.");
        foreach (var frame in clip.GetProperty("frames").EnumerateArray())
        {
            var file = frame.GetProperty("file").GetString()!;
            if (_dimensions.TryGetValue(file, out var previous) && previous != (width, height))
                throw new InvalidDataException("Conflicting skeletal frame dimensions.");
            _dimensions[file] = (width, height);
        }
    }

    internal static SkeletalAssetSource? OpenIfPresent(string root) =>
        File.Exists(Path.Combine(root, "skeletal", "pack.json")) ? new(root) : null;

    internal IReadOnlyCollection<string> WarmupFiles => _legacy.WarmupFiles;

    public bool Contains(string file) => _legacy.Contains(file) || _work?.Contains(file) == true || _firefly?.Contains(file) == true;

    public AssetCatalog.CachedFrame Decode(string file)
    {
        var rgba = _legacy.Contains(file) ? _legacy.Render(file)
            : _work?.Contains(file) == true ? _work.Render(file)
            : _firefly?.Contains(file) == true ? _firefly.Render(file) : throw new KeyNotFoundException(file);
        var (width, height) = _dimensions[file];
        if (rgba.Length != checked(width * height * 4))
            throw new InvalidDataException("Skeletal renderer returned the wrong canvas.");
        // All providers return an owned buffer. Swizzle it in place instead of
        // allocating another full canvas for each displayed pose.
        var alpha = new byte[rgba.Length / 4];
        for (var i = 0; i < alpha.Length; i++)
        {
            var p = i * 4;
            (rgba[p], rgba[p + 2]) = (rgba[p + 2], rgba[p]);
            alpha[i] = rgba[p + 3];
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, rgba, width * 4);
        bitmap.Freeze();
        return new(bitmap, alpha);
    }
}
