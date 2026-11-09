using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace TablePet.App.Skeletal;

/// <summary>
/// A root cat attachment, a local face expression attachment and a separate foreground.
/// WorkScenePresentation retains the authored root-bone breathing and prop opacity.
/// Face corrections preserve the frozen release's RIFE pixels, including alpha exactly.
/// Immutable inputs and fresh results allow concurrent background rendering.
/// </summary>
public sealed class WorkRenderer
{
    private readonly byte[] _archive;
    private readonly byte[] _base;
    private readonly Dictionary<string, Frame> _frames;
    public const int Width = 512;
    public const int Height = 512;
    public IReadOnlyCollection<string> Files => _frames.Keys;

    /// <param name="assetRoot">The strawberry-cat asset directory.</param>
    public WorkRenderer(string assetRoot)
    {
        var directory = Path.Combine(assetRoot, "skeletal", "work");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var schema = JsonSerializer.Deserialize<Schema>(File.ReadAllText(Path.Combine(directory, "work.json")), options)
            ?? throw new InvalidDataException("Missing work skeleton metadata.");
        if (schema.Version != 1 || schema.Width != Width || schema.Height != Height || schema.Format != "RGBA8"
            || schema.Compression != "zlib" || schema.Archive != "work.bin")
            throw new InvalidDataException("Unsupported work skeleton format.");
        _archive = File.ReadAllBytes(Path.Combine(directory, schema.Archive));
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(_archive)), schema.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Work skeleton archive checksum mismatch.");
        _base = Inflate(schema.BaseTexture, Width * Height * 4);
        _frames = new Dictionary<string, Frame>(StringComparer.Ordinal);
        foreach (var frame in schema.Frames)
        {
            if (string.IsNullOrWhiteSpace(frame.File) || !_frames.TryAdd(frame.File, frame))
                throw new InvalidDataException("Empty or duplicate work frame binding.");
            if (frame.Kind == "expression")
            {
                var rect = frame.Rect ?? throw new InvalidDataException("Expression rectangle missing.");
                if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0
                    || rect.X > Width - rect.Width || rect.Y > Height - rect.Height)
                    throw new InvalidDataException("Work expression rectangle exceeds canvas.");
                Validate(frame.Block, checked(rect.Width * rect.Height * 4));
            }
            else if (frame.Kind == "texture") Validate(frame.Block, Width * Height * 4);
            else if (frame.Kind != "base") throw new InvalidDataException("Unknown work attachment kind.");
        }
    }

    public bool Contains(string file) => _frames.ContainsKey(file);

    /// <summary>Returns straight RGBA bytes at the authored 512px canvas.</summary>
    public byte[] Render(string file)
    {
        if (!_frames.TryGetValue(file, out var frame)) throw new KeyNotFoundException(file);
        if (frame.Kind == "texture") return Inflate(frame.Block!, Width * Height * 4);
        var pixels = (byte[])_base.Clone();
        if (frame.Kind == "base") return pixels;
        var rect = frame.Rect!;
        var patch = Inflate(frame.Block!, rect.Width * rect.Height * 4);
        for (var y = 0; y < rect.Height; y++)
        {
            var source = y * rect.Width * 4;
            var target = ((rect.Y + y) * Width + rect.X) * 4;
            for (var x = 0; x < rect.Width * 4; x++)
                pixels[target + x] = unchecked((byte)(pixels[target + x] + patch[source + x]));
        }
        return pixels;
    }

    private void Validate(Block? block, int expected)
    {
        if (block is null || block.RawLength != expected || block.Offset < 0 || block.Length <= 0
            || block.Length > _archive.Length || block.Offset > _archive.Length - block.Length)
            throw new InvalidDataException("Work attachment block is out of bounds.");
    }

    private byte[] Inflate(Block block, int expected)
    {
        Validate(block, expected);
        using var input = new MemoryStream(_archive, block.Offset, block.Length, writable: false);
        using var decoder = new ZLibStream(input, CompressionMode.Decompress);
        var bytes = new byte[expected];
        decoder.ReadExactly(bytes);
        if (decoder.ReadByte() != -1) throw new InvalidDataException("Work attachment has excess decoded bytes.");
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), block.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Work attachment checksum mismatch.");
        return bytes;
    }

    private sealed record Schema(int Version, int Width, int Height, string Format, string Compression,
        string Archive, string ArchiveSha256, Block BaseTexture, Frame[] Frames);
    private sealed record Block(int Offset, int Length, int RawLength, string Sha256);
    private sealed record Rectangle(int X, int Y, int Width, int Height);
    private sealed record Frame(string File, string Kind, Rectangle? Rect, Block? Block);
}
