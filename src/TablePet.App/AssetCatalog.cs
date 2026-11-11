using System.IO;
using System.Buffers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TablePet.App.Services;
using TablePet.Core;
using TablePet.App.Skeletal;

namespace TablePet.App;

/// <summary>Frozen WPF images in a bounded cache, with background look-ahead for dense animations.</summary>
public sealed class AssetCatalog
{
    private static readonly string[] RequiredClips = ["idle", "blink", "fall_asleep", "sleep_loop", "wake", "rise", "hold", "settle"];
    private readonly string _root;
    private readonly Func<string, int, CachedFrame> _decode;
    private SkeletalAssetSource? _skeletal;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new();
    private readonly LinkedList<CacheEntry> _leastRecentlyUsed = new();
    private readonly HashSet<string> _entryFrames = new();
    private readonly Dictionary<string, ClipFrameIndex> _clipFrameIndices = new();
    private readonly Dictionary<string, long> _frameBytes = new();
    private readonly Dictionary<string, int> _frameHeights = new();
    private readonly Dictionary<string, (int Width, int Height)> _frameDimensions = new();
    private readonly HashSet<string> _decorationFrames = new();
    private HashSet<string> _playbackFrames = new();
    private Queue<string> _lookAhead = new();
    private int _generation, _workerGeneration = -1, _decodedHeight;
    private int _workerCount;
    private readonly HashSet<string> _inFlight = new();
    private long _cachedBytes, _synchronousCacheMisses;
    private volatile bool _hasFrameFallback;
    public long CacheBudgetBytes { get; }
    public long CachedBytes { get { lock (_cacheLock) return _cachedBytes; } }
    public int CachedFrameCount { get { lock (_cacheLock) return _cache.Count; } }
    public long SynchronousCacheMisses => Interlocked.Read(ref _synchronousCacheMisses);
    public IReadOnlyDictionary<string, AnimationClip> Clips { get; private set; } = null!;
    public IReadOnlyDictionary<string, AnimationDecoration> Decorations { get; private set; } =
        new Dictionary<string, AnimationDecoration>();
    public IReadOnlyList<string> AllFrameFiles { get; }
    public IReadOnlyList<IdleAction> IdleActions { get; private set; } = [new("blink", 2)];
    public double CanvasWidth { get; private set; } = 512;
    public double CanvasHeight { get; private set; } = 512;
    public bool IsFallback { get; private set; }
    public bool HasOptionalActionFallback { get; private set; }
    public bool HasFrameFallback => _hasFrameFallback;
    private (double X, double Y, double Width, double Height)? _hitRegion;
    public AssetCatalog(string root, long cacheBudgetBytes = 128L * 1024 * 1024)
        : this(root, cacheBudgetBytes, Decode) { }

    internal AssetCatalog(string root, long cacheBudgetBytes, Func<string, int, CachedFrame> decode)
    {
        if (cacheBudgetBytes < 8L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(cacheBudgetBytes));
        _root = root;
        _decode = decode;
        CacheBudgetBytes = cacheBudgetBytes;
        try
        {
            _skeletal = SkeletalAssetSource.OpenIfPresent(root);
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
                    clips.Add(property.Name, ReadClip(property.Name, property.Value, ReadSampleFps(manifest, property.Name)));
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
            // Meow is optional for older packs, but its one-shot reaction must
            // join the exact resting pose on both sides without a looping mouth.
            if (clips.TryGetValue("meow", out var meow) && (meow.Loop || meow.Frames.Count < 2 ||
                meow.Frames[0].File != clips["idle"].Frames[0].File || meow.Frames[^1].File != clips["idle"].Frames[0].File))
            {
                clips.Remove("meow");
                HasOptionalActionFallback = true;
                ErrorLogger.Write("可选动作无法加载，已跳过：meow（需要首尾待机姿势的单次动作）");
            }
            LoadFireflyDecoration(manifest, clips);
            ValidateWorkClips(clips);
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
            // Legacy happy/wink entries are retired rather than broken assets.
            // Current automatic choices (including the complete rise sequence
            // and paired firefly clip) are validated by IdleAction.Available.
            HasOptionalActionFallback |= requested.Any(action => action.Clip is not ("happy" or "wink")
                && !IdleActions.Contains(action));
        }
        catch (Exception error)
        {
            ErrorLogger.Write("素材清单无法加载，使用原型静态图。", error);
            IsFallback = true;
            _skeletal = null;
            Decorations = new Dictionary<string, AnimationDecoration>();
            _hitRegion = null;
            CanvasWidth = 1208; CanvasHeight = 1255;
            Clips = RequiredClips
                .ToDictionary(n => n, n => new AnimationClip(n, [new("fallback.png", 300)], n == "sleep_loop"));
        }
        // An eased opening can repeat one image across several timeline slots.
        // Warm three different poses so those repeats do not exhaust the head start.
        var allClips = Clips.Values.Concat(Decorations.Values.Select(decoration => decoration.Clip)).ToArray();
        AllFrameFiles = allClips.SelectMany(clip => clip.Frames).Select(frame => frame.File).Distinct().ToArray();
        foreach (var clip in allClips)
            foreach (var file in clip.Frames.Select(frame => frame.File).Distinct().Take(3)) _entryFrames.Add(file);
        // Reverse motion can enter a distant skeletal dependency chain shortly
        // after the three ordinary entry poses. Warm that exact pose once in
        // the existing image budget, without pinning the renderer's source LRU.
        if (_skeletal is not null) _entryFrames.UnionWith(_skeletal.WarmupFiles.Where(AllFrameFiles.Contains));
        void RegisterFrames(AnimationClip clip, double width, double height)
        {
            var bytes = (long)Math.Ceiling(width) * (long)Math.Ceiling(height) * 5;
            foreach (var file in clip.Frames.Select(frame => frame.File).Distinct())
            {
                _frameBytes[file] = Math.Max(_frameBytes.GetValueOrDefault(file), bytes);
                _frameHeights[file] = Math.Max(_frameHeights.GetValueOrDefault(file), (int)height);
                _frameDimensions[file] = ((int)width, (int)height);
            }
        }
        foreach (var clip in Clips.Values) RegisterFrames(clip, CanvasWidth, CanvasHeight);
        foreach (var decoration in Decorations.Values)
        {
            RegisterFrames(decoration.Clip, decoration.CanvasWidth, decoration.CanvasHeight);
            _decorationFrames.UnionWith(decoration.Clip.Frames.Select(frame => frame.File));
        }
        foreach (var clip in Clips.Values)
        {
            var channels = PlaybackChannels(clip);
            var frames = Enumerable.Range(0, clip.Frames.Count)
                .SelectMany(index => channels.Select(channel => (channel.Frames[index].File, Index: index)))
                .DistinctBy(item => item.File).ToArray();
            var workingSetBytes = _entryFrames.Union(frames.Select(frame => frame.File)).Sum(EstimatedBytes);
            _clipFrameIndices.Add(clip.Name, new ClipFrameIndex(clip,
                frames.Select(frame => frame.File).ToArray(), frames.Select(frame => frame.Index).ToArray(),
                workingSetBytes));
        }
    }

    private void ValidateWorkClips(Dictionary<string, AnimationClip> clips)
    {
        string[] names = ["work_enter", "work_loop", "work_exit", "work_props"];
        if (!names.Any(clips.ContainsKey)) return;
        var valid = names.All(clips.ContainsKey);
        if (valid)
        {
            var enter = clips["work_enter"]; var loop = clips["work_loop"];
            var exit = clips["work_exit"]; var props = clips["work_props"];
            var idle = clips["idle"].Frames[0].File;
            valid = !enter.Loop && !exit.Loop && loop.Loop && props.Loop
                && enter.DurationMs == 400 && exit.DurationMs == 400 && loop.DurationMs == 9600
                && enter.Frames.Count >= 2 && exit.Frames.Count >= 2 && loop.Frames.Count > 1 && props.Frames.Count == 1
                && enter.Frames[0].File == idle && exit.Frames[^1].File == idle
                && enter.Frames[^1].File == loop.Frames[0].File && exit.Frames[0].File == loop.Frames[0].File;
        }
        if (valid) return;
        foreach (var name in names) clips.Remove(name);
        HasOptionalActionFallback = true;
        ErrorLogger.Write("工作模式素材不完整或进出端点不一致，已保留日常陪伴素材。");
    }

    private static int? ReadSampleFps(JsonElement manifest, string name)
    {
        if (!manifest.TryGetProperty("sampling", out var sampling) || sampling.ValueKind != JsonValueKind.Object) return null;
        var target = default(JsonElement);
        if (sampling.TryGetProperty("clipOverrides", out var overrides) && overrides.ValueKind == JsonValueKind.Object
            && overrides.TryGetProperty(name, out var clipOverride) && clipOverride.ValueKind == JsonValueKind.Object)
            clipOverride.TryGetProperty("targetFps", out target);
        if (target.ValueKind == JsonValueKind.Undefined) sampling.TryGetProperty("targetFps", out target);
        return target.ValueKind == JsonValueKind.Number && target.TryGetInt32(out var fps) && fps is > 0 and <= 1000 ? fps : null;
    }

    private AnimationClip ReadClip(string name, JsonElement definition, int? sampleFps = null)
    {
        var frames = definition.GetProperty("frames").EnumerateArray().Select(frame => new AnimationFrame(
            frame.GetProperty("file").GetString()!, frame.GetProperty("durationMs").GetInt32())).ToArray();
        var clip = new AnimationClip(name, frames,
            definition.TryGetProperty("loop", out var loop) && loop.GetBoolean(), sampleFps);
        clip.EnsureValid();
        foreach (var frame in frames)
        {
            var path = Resolve(frame.File); // Validate virtual identifiers just like physical paths.
            if (_skeletal?.Contains(frame.File) != true && !File.Exists(path)) throw new FileNotFoundException(frame.File);
        }
        return clip;
    }

    private void LoadFireflyDecoration(JsonElement manifest, Dictionary<string, AnimationClip> clips)
    {
        var hasClip = manifest.GetProperty("clips").TryGetProperty("firefly", out _);
        var hasDecorations = manifest.TryGetProperty("decorations", out var definitions);
        var definition = default(JsonElement);
        var hasDecoration = hasDecorations && definitions.ValueKind == JsonValueKind.Object
            && definitions.TryGetProperty("firefly", out definition);
        if (!hasClip && !hasDecoration) return; // Existing packs do not require this optional pair.
        try
        {
            if (!clips.TryGetValue("firefly", out var clip) || !hasDecoration)
                throw new InvalidDataException("Firefly requires both character and decoration channels.");
            var rest = clips["idle"].Frames[0].File;
            if (clip.Loop || clip.Frames.Count < 2 || clip.Frames[0].File != rest || clip.Frames[^1].File != rest)
                throw new InvalidDataException("Firefly must start and end on the resting frame.");
            var width = definition.GetProperty("canvas").GetProperty("width").GetDouble();
            var height = definition.GetProperty("canvas").GetProperty("height").GetDouble();
            var x = definition.GetProperty("offset").GetProperty("x").GetDouble();
            var y = definition.GetProperty("offset").GetProperty("y").GetDouble();
            if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(x) || !double.IsFinite(y)
                || width is < 1 or > 4096 || height is < 1 or > 4096
                || width != Math.Truncate(width) || height != Math.Truncate(height)
                || Math.Abs(x) > 4096 || Math.Abs(y) > 4096
                || x >= CanvasWidth || y >= CanvasHeight || x + width <= 0 || y + height <= 0
                || (width * height + CanvasWidth * CanvasHeight) * 5 > CacheBudgetBytes)
                throw new InvalidDataException("Invalid decoration geometry or a frame pair larger than the cache budget.");
            var overlay = ReadClip("firefly-decoration", definition, clip.SampleFps);
            if (overlay.Loop || overlay.Frames.Count != clip.Frames.Count
                || !overlay.Frames.Select(frame => frame.DurationMs).SequenceEqual(clip.Frames.Select(frame => frame.DurationMs)))
                throw new InvalidDataException("Decoration slots must match the character timeline exactly.");
            if ((width != CanvasWidth || height != CanvasHeight)
                && overlay.Frames.Select(frame => frame.File).Intersect(clips.Values.SelectMany(source => source.Frames).Select(frame => frame.File)).Any())
                throw new InvalidDataException("The same image cannot declare different native dimensions in two channels.");
            Decorations = new Dictionary<string, AnimationDecoration>
            {
                ["firefly"] = new(width, height, x, y, overlay)
            };
        }
        catch (Exception error)
        {
            clips.Remove("firefly");
            HasOptionalActionFallback = true;
            ErrorLogger.Write("可选动作无法加载，已跳过：firefly（角色与萤火虫图层必须完整且同步）", error);
        }
    }

    private AnimationClip[] PlaybackChannels(AnimationClip clip) =>
        Clips.TryGetValue(clip.Name, out var fixedClip) && ReferenceEquals(clip, fixedClip)
        && Decorations.TryGetValue(clip.Name, out var decoration) ? [clip, decoration.Clip] : [clip];

    private long EstimatedBytes(string file) => _frameBytes.GetValueOrDefault(file,
        Math.Max(1L, (long)Math.Ceiling(CanvasWidth) * (long)Math.Ceiling(CanvasHeight) * 5));

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
            _cache.Clear(); _leastRecentlyUsed.Clear(); _lookAhead.Clear(); _playbackFrames.Clear(); _inFlight.Clear();
            _workerCount = 0; _workerGeneration = -1;
            _cachedBytes = 0; _decodedHeight = desired; _generation++;
        }
        // Every state can start immediately. Small packs retain the former all-preloaded
        // behavior; dense packs prefetch their remaining frames as playback approaches.
        var estimatedBytes = AllFrameFiles.Sum(EstimatedBytes);
        foreach (var file in estimatedBytes <= CacheBudgetBytes ? AllFrameFiles : _entryFrames.ToArray()) GetFrame(file, countMiss: false);
    }
    public BitmapSource Image(string file) => GetFrame(file, countMiss: true).Image;

    /// <summary>Refresh a short look-ahead queue; cancelled/obsolete clips cannot fill the cache.</summary>
    public void PreparePlayback(AnimationClip clip, int currentIndex)
    {
        int generation, height, workersToStart;
        lock (_cacheLock)
        {
            if (_decodedHeight == 0) return;
            var channels = PlaybackChannels(clip);
            var upcoming = new List<string>();
            var protectedFrames = channels.Select(channel => channel.Frames[currentIndex].File).ToHashSet();
            upcoming.AddRange(protectedFrames.Where(file => !_cache.ContainsKey(file)));
            var duration = 0;
            if (_clipFrameIndices.TryGetValue(clip.Name, out var complete)
                && (ReferenceEquals(clip, complete.Source)
                    // Existing loop playback copies a clip only to override Loop.
                    // Retain its old fixed-frame cache behavior; decorated clips
                    // require the original clip object before binding either channel.
                    || !Decorations.ContainsKey(clip.Name) && ReferenceEquals(clip.Frames, complete.Source.Frames))
                && complete.WorkingSetBytes <= CacheBudgetBytes)
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
                var reserved = _entryFrames.Union(protectedFrames).ToHashSet();
                var reservedBytes = reserved.Sum(EstimatedBytes);
                // Leave room for one demand-loaded image without evicting either
                // channel's current frame or its protected upcoming frames.
                var limit = CacheBudgetBytes - protectedFrames.Max(EstimatedBytes);
                for (var offset = 1; offset <= 64 && duration < 250; offset++)
                {
                    var index = currentIndex + offset;
                    if (index >= clip.Frames.Count && !clip.Loop) break;
                    index %= clip.Frames.Count;
                    var files = channels.Select(channel => channel.Frames[index].File).Distinct().ToArray();
                    var extraBytes = files.Where(file => !reserved.Contains(file)).Sum(EstimatedBytes);
                    if (reservedBytes + extraBytes > limit) break;
                    reservedBytes += extraBytes;
                    reserved.UnionWith(files);
                    duration += clip.Frames[index].DurationMs;
                    protectedFrames.UnionWith(files);
                    upcoming.AddRange(files.Where(file => !_cache.ContainsKey(file)));
                }
            }
            _playbackFrames = protectedFrames;
            _lookAhead = new Queue<string>(upcoming.Distinct().Where(file => !_inFlight.Contains(file)));
            // Two decoders keep dense 240Hz clips ahead without expanding the cache
            // or look-ahead window. Refreshes never requeue a frame already decoding.
            workersToStart = Math.Min(2 - _workerCount, _lookAhead.Count);
            if (workersToStart == 0) return;
            generation = _generation; height = _decodedHeight;
            _workerGeneration = generation;
            _workerCount += workersToStart;
        }
        for (var worker = 0; worker < workersToStart; worker++)
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
                if (!_lookAhead.TryDequeue(out file!))
                {
                    // Queue refresh and worker retirement share the same lock, so a
                    // newly populated queue cannot be left without an active worker.
                    _workerCount--;
                    if (_workerCount == 0) _workerGeneration = -1;
                    return;
                }
                if (_cache.ContainsKey(file) || !_inFlight.Add(file)) continue;
            }
            CachedFrame? decoded = null;
            try { decoded = DecodeWithFallback(file, height); }
            catch (Exception error)
            {
                // A missing fallback must not surface as an unobserved background exception.
                ErrorLogger.Write($"动画预读失败：{file}", error);
            }
            lock (_cacheLock)
            {
                // An obsolete decoder must not remove a newer generation's ownership
                // of the same file, or insert frames after playback was cancelled.
                if (generation != _generation) return;
                _inFlight.Remove(file);
                if (decoded is not null && (_playbackFrames.Contains(file) || _entryFrames.Contains(file)))
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
        try
        {
            var decoded = _skeletal?.Contains(file) == true
                ? _skeletal.Decode(file)
                : _decode(Resolve(file), _frameHeights.GetValueOrDefault(file, height));
            if (!IsFallback && _frameDimensions.TryGetValue(file, out var expected)
                && (decoded.Image.PixelWidth != expected.Width || decoded.Image.PixelHeight != expected.Height))
                throw new InvalidDataException($"Frame dimensions do not match its declared canvas: {file}");
            return decoded;
        }
        catch (Exception error)
        {
            _hasFrameFallback = true;
            ErrorLogger.Write($"帧加载失败：{file}", error);
            if (_decorationFrames.Contains(file))
            {
                // Decorative failures must never display the character fallback
                // over the enlarged transparent window. Keep the channel invisible.
                var size = _frameDimensions[file];
                var alpha = new byte[size.Width * size.Height];
                var transparent = BitmapSource.Create(size.Width, size.Height, 96, 96,
                    PixelFormats.Bgra32, null, new byte[alpha.Length * 4], size.Width * 4);
                transparent.Freeze();
                return new(transparent, alpha);
            }
            return _decode(Path.Combine(_root, "fallback.png"), height);
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
    internal sealed record CachedFrame(BitmapSource Image, byte[] Alpha);
    private sealed record CacheEntry(string File, CachedFrame Frame, long Bytes);
    private sealed record ClipFrameIndex(AnimationClip Source, string[] Files, int[] FirstIndices, long WorkingSetBytes);
}
