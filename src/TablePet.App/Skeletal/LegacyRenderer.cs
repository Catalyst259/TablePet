using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace TablePet.App.Skeletal;

/// <summary>
/// Ten anatomical affine bones drive a dense weighted 2D skin. Lossless local
/// residuals preserve RIFE texture and alpha that no small skeleton can express.
/// Dependencies are checkpointed; the decoded LRU is capped at 24 MiB. Archive
/// bytes remain on disk. The lock makes recursive seeks and prefetch thread-safe.
/// </summary>
public sealed class LegacyRenderer
{
    public const int Width = 512;
    public const int Height = 512;
    private const int PixelBytes = Width * Height * 4;
    private const int CacheLimit = 24;
    private static readonly ParallelOptions SkinParallelism = new() { MaxDegreeOfParallelism = Math.Min(2, Environment.ProcessorCount) };
    private readonly string _archivePath;
    private readonly long _archiveLength;
    private readonly Frame[] _frames;
    private readonly Dictionary<string, Frame> _byFile;
    private readonly ushort[] _binding;
    private readonly int _boneCount;
    private readonly object _gate = new();
    private readonly Dictionary<int, (byte[] Pixels, LinkedListNode<int> Node)> _cache = new();
    private readonly LinkedList<int> _recent = new();
    private readonly HashSet<int> _entrySeeds = new();

    public IReadOnlyCollection<string> Files => _byFile.Keys;
    public int CachedBytes { get { lock (_gate) return _cache.Count * PixelBytes; } }
    public IReadOnlyList<string> WarmupFiles { get; private set; } = [];

    /// <param name="assetRoot">The strawberry-cat asset directory.</param>
    public LegacyRenderer(string assetRoot)
    {
        var directory = Path.Combine(assetRoot, "skeletal", "legacy");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var schema = JsonSerializer.Deserialize<Schema>(File.ReadAllText(Path.Combine(directory, "legacy.json")), options)
            ?? throw new InvalidDataException("Missing legacy skeleton metadata.");
        if (schema.Version != 1 || schema.Width != Width || schema.Height != Height || schema.Format != "RGBA8"
            || schema.Compression != "brotli" || schema.Archive != "legacy.bin" || schema.InfluencesPerVertex != 3
            || schema.Prediction != "anatomical-affine-inverse-skinning-fixedpoint-v1"
            || schema.ImageEncoding != "planar-r-minus-g-g-b-minus-g-a;checkpoint-horizontal-sub"
            || schema.MaxDependencyDepth is < 1 or > 24 || schema.Bones.Length is < 1 or > 256)
            throw new InvalidDataException("Unsupported legacy skeleton format.");
        _boneCount = schema.Bones.Length;
        _archivePath = Path.Combine(directory, schema.Archive);
        using (var archive = File.OpenRead(_archivePath))
        {
            _archiveLength = archive.Length;
            if (!HashMatches(SHA256.HashData(archive), schema.ArchiveSha256))
                throw new InvalidDataException("Legacy skeleton archive checksum mismatch.");
        }
        var binding = Inflate(schema.Binding, Width * Height * 3 * 4);
        _binding = new ushort[binding.Length / 2];
        for (var i = 0; i < _binding.Length; i++)
            _binding[i] = BinaryPrimitives.ReadUInt16LittleEndian(binding.AsSpan(i * 2, 2));
        for (var i = 0; i < _binding.Length; i += 6)
            if (_binding[i] >= _boneCount || _binding[i + 2] >= _boneCount || _binding[i + 4] >= _boneCount
                || _binding[i + 1] + _binding[i + 3] + _binding[i + 5] != 256)
                throw new InvalidDataException("Invalid skeleton vertex binding.");
        _frames = schema.Frames;
        _byFile = new Dictionary<string, Frame>(StringComparer.Ordinal);
        for (var i = 0; i < _frames.Length; i++)
        {
            var frame = _frames[i];
            if (frame.Id != i || string.IsNullOrEmpty(frame.File) || !_byFile.TryAdd(frame.File, frame)
                || frame.Depth < 0 || frame.Depth > schema.MaxDependencyDepth)
                throw new InvalidDataException("Invalid legacy pose identity.");
            if (frame.Kind == "checkpoint")
            {
                if (frame.Parent != -1 || frame.Depth != 0) throw new InvalidDataException("Invalid skeleton checkpoint.");
                Validate(frame.Block, PixelBytes);
            }
            else
            {
                if (frame.Parent < 0 || frame.Parent >= i) throw new InvalidDataException("Skeleton dependency is not acyclic.");
                if (frame.Kind == "alias")
                {
                    if (frame.Depth != _frames[frame.Parent].Depth) throw new InvalidDataException("Invalid alias depth.");
                }
                else if (frame.Kind == "skinned")
                {
                    if (frame.Depth != _frames[frame.Parent].Depth + 1 || frame.InverseBones?.Length != _boneCount
                        || frame.InverseBones.Any(m => m.Length != 6 || m.Any(v => v is < -67108864 or > 67108864)))
                        throw new InvalidDataException("Invalid animated bone matrices.");
                    var rect = frame.Rect ?? throw new InvalidDataException("Missing skeleton residual rectangle.");
                    if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0
                        || rect.X > Width - rect.Width || rect.Y > Height - rect.Height)
                        throw new InvalidDataException("Skeleton residual exceeds canvas.");
                    Validate(frame.Block, checked(rect.Width * rect.Height * 4));
                }
                else throw new InvalidDataException("Unknown legacy attachment kind.");
            }
        }
        // Identify an early cross-chain pose for the WPF cache to warm in
        // addition to its three normal entry images. Keep the entire source
        // cache dynamic: even a small pin can disrupt reverse dependency reuse.
        var seedBudget = Math.Min(4, Math.Max(0, CacheLimit - schema.MaxDependencyDepth));
        if (schema.Clips is not null)
        {
            foreach (var clip in schema.Clips.Values)
            {
                if (clip.Frames.Any(slot => slot.Id < 0 || slot.Id >= _frames.Length || slot.DurationMs <= 0))
                    throw new InvalidDataException("Invalid skeletal clip seed reference.");
            }
            foreach (var clip in schema.Clips.Values)
            {
                var elapsed = 0; var previous = -1; var distinct = 0;
                foreach (var slot in clip.Frames)
                {
                    if (elapsed >= 250 || _entrySeeds.Count >= seedBudget) break;
                    elapsed += slot.DurationMs;
                    if (slot.Id == previous) continue;
                    var frame = _frames[slot.Id];
                    distinct++;
                    if (distinct > 3 && frame.Kind == "skinned" && frame.Depth >= 3 && frame.Parent != previous)
                    {
                        _entrySeeds.Add(slot.Id);
                        break;
                    }
                    previous = slot.Id;
                }
            }
        }
        WarmupFiles = _entrySeeds.Select(id => _frames[id].File).ToArray();
    }

    public bool Contains(string file) => _byFile.ContainsKey(file);

    /// <summary>Returns fresh straight RGBA bytes, including every original alpha byte.</summary>
    public byte[] Render(string file)
    {
        if (!_byFile.TryGetValue(file, out var frame)) throw new KeyNotFoundException(file);
        lock (_gate) return (byte[])Decode(frame.Id).Clone();
    }

    /// <summary>
    /// Review the fitted bone prediction before this pose's residual. Its input
    /// reference pose is reconstructed exactly; this is not an error metric for
    /// indefinitely accumulating uncorrected skeleton motion.
    /// </summary>
    public byte[] RenderPrediction(string file)
    {
        if (!_byFile.TryGetValue(file, out var frame)) throw new KeyNotFoundException(file);
        lock (_gate)
            return frame.Kind == "skinned" ? Skin(Decode(frame.Parent), frame.InverseBones!) : (byte[])Decode(frame.Id).Clone();
    }

    private byte[] Decode(int id)
    {
        if (_cache.TryGetValue(id, out var cached))
        {
            _recent.Remove(cached.Node);
            _recent.AddFirst(cached.Node);
            return cached.Pixels;
        }
        var frame = _frames[id];
        byte[] pixels;
        if (frame.Kind == "checkpoint") pixels = DecodeImage(Inflate(frame.Block!, PixelBytes), Width, Height, horizontal: true);
        else if (frame.Kind == "alias") pixels = Decode(frame.Parent);
        else
        {
            pixels = Skin(Decode(frame.Parent), frame.InverseBones!);
            var rect = frame.Rect!;
            var correction = DecodeImage(Inflate(frame.Block!, rect.Width * rect.Height * 4), rect.Width, rect.Height, horizontal: false);
            for (var y = 0; y < rect.Height; y++)
            {
                var from = y * rect.Width * 4;
                var to = ((rect.Y + y) * Width + rect.X) * 4;
                for (var x = 0; x < rect.Width * 4; x++) pixels[to + x] = unchecked((byte)(pixels[to + x] + correction[from + x]));
            }
        }
        if (!HashMatches(SHA256.HashData(pixels), frame.Sha256))
            throw new InvalidDataException("Reconstructed skeleton pose checksum mismatch: " + frame.File);
        _cache.Add(id, (pixels, _recent.AddFirst(id)));
        while (_cache.Count > CacheLimit)
        {
            var last = _recent.Last!;
            _cache.Remove(last.Value);
            _recent.Remove(last);
        }
        return pixels;
    }

    private byte[] Skin(byte[] source, int[][] matrices)
    {
        var result = new byte[PixelBytes];
        // Ignore only provably empty canvas. Any convex blend of the bones'
        // affine displacement is bounded by their extrema at canvas corners.
        // Include RGB even under zero alpha: the codec preserves those bytes.
        var minX = Width; var minY = Height; var maxX = -1; var maxY = -1;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var p = (y * Width + x) * 4;
            if ((source[p] | source[p + 1] | source[p + 2] | source[p + 3]) == 0) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        if (maxX < 0) return result;
        long displacementX = 0, displacementY = 0;
        foreach (var m in matrices)
        foreach (var y in new[] { 0, Height - 1 })
        foreach (var x in new[] { 0, Width - 1 })
        {
            displacementX = Math.Max(displacementX, Math.Abs(((long)m[0] - 65536) * x + (long)m[1] * y + m[2]));
            displacementY = Math.Max(displacementY, Math.Abs((long)m[3] * x + ((long)m[4] - 65536) * y + m[5]));
        }
        var marginX = (int)((displacementX + 65535) / 65536) + 2;
        var marginY = (int)((displacementY + 65535) / 65536) + 2;
        minX = Math.Max(0, minX - marginX); maxX = Math.Min(Width - 1, maxX + marginX);
        minY = Math.Max(0, minY - marginY); maxY = Math.Min(Height - 1, maxY + marginY);
        Parallel.For(minY, maxY + 1, SkinParallelism, y =>
        {
        for (var x = minX; x <= maxX; x++)
        {
            var vertex = y * Width + x;
            long sx = 0, sy = 0;
            var binding = vertex * 6;
            for (var influence = 0; influence < 3; influence++)
            {
                var m = matrices[_binding[binding + influence * 2]];
                var weight = _binding[binding + influence * 2 + 1];
                sx += ((long)m[0] * x + (long)m[1] * y + m[2]) * weight;
                sy += ((long)m[3] * x + (long)m[4] * y + m[5]) * weight;
            }
            var fx8 = (int)((sx + 32768) >> 16);
            var fy8 = (int)((sy + 32768) >> 16);
            var x0 = fx8 >> 8;
            var y0 = fy8 >> 8;
            var fx = fx8 & 255;
            var fy = fy8 & 255;
            var w00 = (256 - fx) * (256 - fy);
            var w10 = fx * (256 - fy);
            var w01 = (256 - fx) * fy;
            var w11 = fx * fy;
            var p00 = x0 >= 0 && x0 < Width && y0 >= 0 && y0 < Height ? (y0 * Width + x0) * 4 : -1;
            var p10 = x0 + 1 >= 0 && x0 + 1 < Width && y0 >= 0 && y0 < Height ? (y0 * Width + x0 + 1) * 4 : -1;
            var p01 = x0 >= 0 && x0 < Width && y0 + 1 >= 0 && y0 + 1 < Height ? ((y0 + 1) * Width + x0) * 4 : -1;
            var p11 = x0 + 1 >= 0 && x0 + 1 < Width && y0 + 1 >= 0 && y0 + 1 < Height ? ((y0 + 1) * Width + x0 + 1) * 4 : -1;
            for (var channel = 0; channel < 4; channel++)
            {
                var value = (p00 < 0 ? 0 : source[p00 + channel] * w00)
                    + (p10 < 0 ? 0 : source[p10 + channel] * w10)
                    + (p01 < 0 ? 0 : source[p01 + channel] * w01)
                    + (p11 < 0 ? 0 : source[p11 + channel] * w11);
                result[vertex * 4 + channel] = (byte)((value + 32768) >> 16);
            }
        }
        });
        return result;
    }

    private static byte[] DecodeImage(byte[] planar, int width, int height, bool horizontal)
    {
        var result = new byte[planar.Length];
        var plane = width * height;
        for (var c = 0; c < 4; c++)
        for (var y = 0; y < height; y++)
        {
            byte previous = 0;
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var value = planar[c * plane + i];
                if (horizontal) value = unchecked((byte)(value + previous));
                result[i * 4 + c] = value;
                previous = value;
            }
        }
        for (var i = 0; i < result.Length; i += 4)
        {
            result[i] = unchecked((byte)(result[i] + result[i + 1]));
            result[i + 2] = unchecked((byte)(result[i + 2] + result[i + 1]));
        }
        return result;
    }

    private void Validate(Block? block, int expected)
    {
        if (block is null || block.RawLength != expected || block.Offset < 0 || block.Length <= 0
            || block.Length > _archiveLength || block.Offset > _archiveLength - block.Length)
            throw new InvalidDataException("Skeleton block is out of bounds.");
    }

    private byte[] Inflate(Block block, int expected)
    {
        Validate(block, expected);
        using var stream = File.OpenRead(_archivePath);
        stream.Position = block.Offset;
        var compressed = new byte[block.Length];
        stream.ReadExactly(compressed);
        using var memory = new MemoryStream(compressed, writable: false);
        using var decoder = new BrotliStream(memory, CompressionMode.Decompress);
        var result = new byte[expected];
        decoder.ReadExactly(result);
        if (decoder.ReadByte() != -1 || !HashMatches(SHA256.HashData(result), block.Sha256))
            throw new InvalidDataException("Skeleton block failed checksum or length validation.");
        return result;
    }

    private static bool HashMatches(byte[] digest, string expected) =>
        string.Equals(Convert.ToHexString(digest), expected, StringComparison.OrdinalIgnoreCase);

    private sealed record Schema(int Version, int Width, int Height, string Format, string Compression,
        string Prediction, string ImageEncoding, string Archive, string ArchiveSha256, Block Binding,
        int InfluencesPerVertex, int MaxDependencyDepth, Bone[] Bones, Frame[] Frames, Dictionary<string, Clip>? Clips);
    private sealed record Bone(string Name, int Parent, double[] Center, double[] Radius);
    private sealed record Block(long Offset, int Length, int RawLength, string Sha256);
    private sealed record Rectangle(int X, int Y, int Width, int Height);
    private sealed record Frame(int Id, string File, string Kind, int Parent, int Depth, string Sha256,
        int[][]? InverseBones, Rectangle? Rect, Block? Block);
    private sealed record Clip(bool Loop, Slot[] Frames);
    private sealed record Slot(int Id, int DurationMs);
}
