using System.IO;
using System.Buffers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TablePet.App.Services;
using TablePet.Core;

namespace TablePet.App;

/// <summary>Frozen WPF images in a bounded cache, with background look-ahead for dense animations.</summary>
public sealed class AssetCatalog
{
    private static readonly string[] RequiredClips = ["idle", "blink", "fall_asleep", "sleep_loop", "wake", "rise", "hold", "settle"];
    private readonly string _root;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new();
    private readonly LinkedList<CacheEntry> _leastRecentlyUsed = new();
    private readonly HashSet<string> _entryFrames = new();
    private readonly Dictionary<string, ClipFrameIndex> _clipFrameIndices = new();
    private HashSet<string> _playbackFrames = new();
    private Queue<string> _lookAhead = new();
    private int _generation, _workerGeneration = -1, _decodedHeight;
    private long _cachedBytes, _synchronousCacheMisses;
    private volatile bool _hasFrameFallback;
    public long CacheBudgetBytes { get; }
    public long CachedBytes { get { lock (_cacheLock) return _cachedBytes; } }
    public int CachedFrameCount { get { lock (_cacheLock) return _cache.Count; } }
    public long SynchronousCacheMisses => Interlocked.Read(ref _synchronousCacheMisses);
    public IReadOnlyDictionary<string, AnimationClip> Clips { get; private set; } = null!;
    public IReadOnlyList<IdleAction> IdleActions { get; private set; } = [new("blink", 4)];
    public double CanvasWidth { get; private set; } = 512;
    public double CanvasHeight { get; private set; } = 512;
    public bool IsFallback { get; private set; }
    public bool HasOptionalActionFallback { get; private set; }
    public bool HasFrameFallback => _hasFrameFallback;
    private (double X, double Y, double Width, double Height)? _hitRegion;
    public AssetCatalog(string root, long cacheBudgetBytes = 128L * 1024 * 1024)
    {
        if (cacheBudgetBytes < 8L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(cacheBudgetBytes));
        _root = root;
        CacheBudgetBytes = cacheBudgetBytes;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
            var manifest = document.RootElement;
            CanvasWidth = manifest.GetProperty("canvas").GetProperty("width").GetDouble();
            CanvasHeight = manifest.GetProperty("canvas").GetProperty("height").GetDouble();
            if (!double.IsFinite(CanvasWidth) || !double.IsFinite(CanvasHeight) || CanvasWidth <= 0 || CanvasHeight <= 0)
                throw new InvalidDataException("Invalid canvas.");
            if (manifest.TryGetProperty("hitRegion", out var region))
            {
                var x = region.GetProperty("x").GetDouble(); var y = region.GetProperty("y").GetDouble();
                var width = region.GetProperty("width").GetDouble(); var height = region.GetProperty("height").GetDouble();
                if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)
                    || x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > CanvasWidth || y + height > CanvasHeight)
                    throw new InvalidDataException("Invalid interaction region.");
                _hitRegion = (x, y, width, height);
            }
            var clips = new Dictionary<string, AnimationClip>();
            foreach (var property in manifest.GetProperty("clips").EnumerateObject())
            {
                try
                {
                    var frames = property.Value.GetProperty("frames").EnumerateArray().Select(f =>
                        new AnimationFrame(f.GetProperty("file").GetString()!, f.GetProperty("durationMs").GetInt32())).ToArray();
                    foreach (var frame in frames)
                        if (!File.Exists(Resolve(frame.File))) throw new FileNotFoundException(frame.File);
                    var clip = new AnimationClip(property.Name, frames,
                        property.Value.TryGetProperty("loop", out var loop) && loop.GetBoolean());
                    clip.EnsureValid();
                    clips.Add(property.Name, clip);
                }
                catch (Exception error) when (!RequiredClips.Contains(property.Name))
                {
                    // An optional expression must not disable the pack's working basic actions.
                    HasOptionalActionFallback = true;
                    ErrorLogger.Write($"可选动作无法加载，已跳过：{property.Name}", error);
                }
            }
            foreach (var required in RequiredClips)
                if (!clips.ContainsKey(required)) throw new InvalidDataException($"Missing clip: {required}");
            Clips = clips;
            var requested = new List<IdleAction>();
            if (manifest.TryGetProperty("idleActions", out var actions))
            {
                if (actions.ValueKind != JsonValueKind.Array) HasOptionalActionFallback = true;
                else
                    foreach (var action in actions.EnumerateArray())
                    {
                        if (action.ValueKind == JsonValueKind.Object
                            && action.TryGetProperty("clip", out var name) && name.ValueKind == JsonValueKind.String
                            && (!action.TryGetProperty("weight", out var weight) || weight.ValueKind == JsonValueKind.Number && weight.TryGetInt32(out _)))
                            requested.Add(new IdleAction(name.GetString()!, weight.ValueKind == JsonValueKind.Undefined ? 1 : weight.GetInt32()));
                        else HasOptionalActionFallback = true;
                    }
            }
            IdleActions = IdleAction.Available(Clips, requested);
            // These legacy entries are intentionally retired from the idle pool,
            // rather than broken assets. Wink remains available as a click reaction.
            HasOptionalActionFallback |= requested.Any(action => action.Clip is not ("happy" or "wink")
                && !IdleActions.Contains(action));
        }
        catch (Exception error)
        {
            ErrorLogger.Write("素材清单无法加载，使用原型静态图。", error);
            IsFallback = true;
            _hitRegion = null;
            CanvasWidth = 1208; CanvasHeight = 1255;
            Clips = RequiredClips
                .ToDictionary(n => n, n => new AnimationClip(n, [new("fallback.png", 300)], n == "sleep_loop"));
        }
        // An eased opening can repeat one image across several timeline slots.
        // Warm three different poses so those repeats do not exhaust the head start.
        var distinctFrames = Clips.Values.ToDictionary(clip => clip.Name, clip => clip.Frames
            .Select((frame, index) => (frame.File, Index: index)).DistinctBy(item => item.File).ToArray());
        foreach (var frames in distinctFrames.Values)
            foreach (var frame in frames.Take(3)) _entryFrames.Add(frame.File);
        foreach (var clip in Clips.Values)
        {
            var frames = distinctFrames[clip.Name];
            _clipFrameIndices.Add(clip.Name, new ClipFrameIndex(clip.Frames,
                frames.Select(frame => frame.File).ToArray(), frames.Select(frame => frame.Index).ToArray(),
                _entryFrames.Count + frames.Count(frame => !_entryFrames.Contains(frame.File))));
        }
    }

    private string Resolve(string file)
    {
        var path = Path.GetFullPath(Path.Combine(_root, file));
        if (!path.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Asset path escapes its character folder.");
        return path;
    }

    public void CacheForSize(double heightDip, double dpiScale)
    {
        // Decode native pixels once. WIC's per-PNG resize was far more expensive than
        // decoding; WPF scales the image during presentation at the requested DIP size.
        var desired = (int)CanvasHeight;
        lock (_cacheLock)
        {
            if (_cache.Count > 0 && desired == _decodedHeight) return;
            _cache.Clear(); _leastRecentlyUsed.Clear(); _lookAhead.Clear(); _playbackFrames.Clear();
            _cachedBytes = 0; _decodedHeight = desired; _generation++;
        }
        // Every state can start immediately. Small packs retain the former all-preloaded
        // behavior; dense packs prefetch their remaining frames as playback approaches.
        var files = Clips.Values.SelectMany(c => c.Frames).Select(f => f.File).Distinct().ToArray();
        var estimatedBytes = (long)Math.Ceiling(desired * CanvasWidth / CanvasHeight) * desired * 5 * files.Length;
        foreach (var file in estimatedBytes <= CacheBudgetBytes ? files : _entryFrames.ToArray()) GetFrame(file, countMiss: false);
    }
    public BitmapSource Image(string file) => GetFrame(file, countMiss: true).Image;

    /// <summary>Refresh a short look-ahead queue; cancelled/obsolete clips cannot fill the cache.</summary>
    public void PreparePlayback(AnimationClip clip, int currentIndex)
    {
        int generation, height;
        lock (_cacheLock)
        {
            if (_decodedHeight == 0) return;
            var upcoming = new List<string>();
            var protectedFrames = new HashSet<string> { clip.Frames[currentIndex].File };
            var duration = 0;
            var frameBytes = Math.Max(1L, (long)Math.Ceiling(_decodedHeight * CanvasWidth / CanvasHeight) * _decodedHeight * 5);
            if (_clipFrameIndices.TryGetValue(clip.Name, out var complete)
                && ReferenceEquals(clip.Frames, complete.Source)
                && complete.WorkingSetFrames <= CacheBudgetBytes / frameBytes)
            {
                // Compact clips fit alongside all entry poses. Queue their distinct
                // images beyond long holds too, and retain them through reopening.
                // This index belongs only to fixed catalog clips; temporary return
                // clips use the bounded look-ahead below and are never retained.
                protectedFrames.UnionWith(complete.Files);
                var next = Array.BinarySearch(complete.FirstIndices, currentIndex);
                next = next >= 0 ? next + 1 : ~next;
                for (var offset = 0; offset < complete.Files.Length; offset++)
                {
                    var file = complete.Files[(next + offset) % complete.Files.Length];
                    if (!_cache.ContainsKey(file)) upcoming.Add(file);
                }
            }
            else
            {
                var lookAheadCount = (int)Math.Clamp(CacheBudgetBytes / frameBytes - _entryFrames.Count - 2, 1, 64);
                for (var offset = 1; offset <= lookAheadCount && duration < 250; offset++)
                {
                    var index = currentIndex + offset;
                    if (index >= clip.Frames.Count && !clip.Loop) break;
                    var frame = clip.Frames[index % clip.Frames.Count];
                    duration += frame.DurationMs;
                    protectedFrames.Add(frame.File);
                    if (!_cache.ContainsKey(frame.File)) upcoming.Add(frame.File);
                }
            }
            _playbackFrames = protectedFrames;
            _lookAhead = new Queue<string>(upcoming.Distinct());
            if (_lookAhead.Count == 0 || _workerGeneration == _generation) return;
            generation = _generation; height = _decodedHeight;
            _workerGeneration = generation;
        }
        _ = Task.Run(() => Prefetch(generation, height));
    }

    /// <summary>Stop obsolete work without discarding useful decoded frames.</summary>
    public void StopPrefetch()
    {
        lock (_cacheLock) { _lookAhead.Clear(); _playbackFrames.Clear(); }
    }

    private void Prefetch(int generation, int height)
    {
        while (true)
        {
            string file;
            lock (_cacheLock)
            {
                if (generation != _generation) return;
                if (!_lookAhead.TryDequeue(out file!)) { _workerGeneration = -1; return; }
                if (_cache.ContainsKey(file)) continue;
            }
            CachedFrame decoded;
            try { decoded = DecodeWithFallback(file, height); }
            catch (Exception error)
            {
                // A missing fallback must not surface as an unobserved background exception.
                ErrorLogger.Write($"动画预读失败：{file}", error);
                continue;
            }
            lock (_cacheLock)
            {
                if (generation != _generation) return;
                Insert(file, decoded);
            }
        }
    }

    private CachedFrame GetFrame(string file, bool countMiss)
    {
        int height, generation;
        lock (_cacheLock)
        {
            if (TryGetCached(file, out var cached)) return cached;
            if (_decodedHeight == 0) throw new InvalidOperationException("Call CacheForSize before displaying images.");
            height = _decodedHeight; generation = _generation;
        }
        if (countMiss) Interlocked.Increment(ref _synchronousCacheMisses);
        var frame = DecodeWithFallback(file, height);
        lock (_cacheLock)
        {
            if (generation == _generation) Insert(file, frame);
        }
        return frame;
    }

    private bool TryGetCached(string file, out CachedFrame frame)
    {
        if (_cache.TryGetValue(file, out var node))
        {
            _leastRecentlyUsed.Remove(node); _leastRecentlyUsed.AddLast(node);
            frame = node.Value.Frame; return true;
        }
        frame = null!; return false;
    }

    private void Insert(string file, CachedFrame frame)
    {
        if (_cache.ContainsKey(file)) return;
        var bytes = (long)frame.Image.PixelWidth * frame.Image.PixelHeight * 5;
        var node = _leastRecentlyUsed.First;
        while (_cachedBytes + bytes > CacheBudgetBytes && node is not null)
        {
            var next = node.Next;
            if (!_entryFrames.Contains(node.Value.File) && !_playbackFrames.Contains(node.Value.File))
            {
                _cache.Remove(node.Value.File); _leastRecentlyUsed.Remove(node);
                _cachedBytes -= node.Value.Bytes;
            }
            node = next;
        }
        // A caller may choose a tiny budget or a pack with many distinct clip starts.
        // The budget still wins over pinning in that case.
        while (_cachedBytes + bytes > CacheBudgetBytes && _leastRecentlyUsed.First is { } oldest)
        {
            _cache.Remove(oldest.Value.File); _leastRecentlyUsed.RemoveFirst();
            _cachedBytes -= oldest.Value.Bytes;
        }
        _cache[file] = _leastRecentlyUsed.AddLast(new CacheEntry(file, frame, bytes));
        _cachedBytes += bytes;
    }

    private CachedFrame DecodeWithFallback(string file, int height)
    {
        try { return Decode(Resolve(file), height); }
        catch (Exception error)
        {
            _hasFrameFallback = true;
            ErrorLogger.Write($"帧加载失败：{file}", error);
            return Decode(Path.Combine(_root, "fallback.png"), height);
        }
    }
    public bool Hit(string file, double x, double y)
    {
        CachedFrame frame;
        lock (_cacheLock)
            if (!TryGetCached(file, out frame) || x < 0 || y < 0 || x >= 1 || y >= 1) return false;
        if (_hitRegion is { } region && (x * CanvasWidth < region.X || y * CanvasHeight < region.Y
            || x * CanvasWidth >= region.X + region.Width || y * CanvasHeight >= region.Y + region.Height)) return false;
        var px = (int)(x * frame.Image.PixelWidth); var py = (int)(y * frame.Image.PixelHeight);
        return frame.Alpha[py * frame.Image.PixelWidth + px] >= 96;
    }
    private static CachedFrame Decode(string path, int height)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        using var input = File.OpenRead(path);
        bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze();
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0); converted.Freeze();
        var alpha = new byte[converted.PixelWidth * converted.PixelHeight];
        var pixels = ArrayPool<byte>.Shared.Rent(alpha.Length * 4);
        try
        {
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            for (var pixel = 0; pixel < alpha.Length; pixel++) alpha[pixel] = pixels[pixel * 4 + 3];
        }
        finally { ArrayPool<byte>.Shared.Return(pixels); }
        return new(bitmap, alpha);
    }
    private sealed record CachedFrame(BitmapSource Image, byte[] Alpha);
    private sealed record CacheEntry(string File, CachedFrame Frame, long Bytes);
    private sealed record ClipFrameIndex(IReadOnlyList<AnimationFrame> Source, string[] Files, int[] FirstIndices, int WorkingSetFrames);
}
