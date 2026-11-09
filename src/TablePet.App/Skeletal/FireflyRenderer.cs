using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace TablePet.App.Skeletal;

/// <summary>The approved v3 articulated paw, shoulder, head and binocular rig.
/// Only immutable attachment textures and sampled bone/eye parameters are stored.
/// Rendering is thread-safe; output uses straight-alpha RGBA, like the frozen art.</summary>
public sealed class FireflyRenderer
{
    private const int W = 512, Size = W * W * 4;
    private readonly byte[] _base, _paw, _cleared, _exposed, _arm, _support, _mouth;
    private readonly float[] _mouthMask, _sourceDark, _targetDark;
    private readonly double[,] _contours = new double[512, 2];
    private readonly double[] _bodyWeights = new double[W * W];
    private readonly Eye[] _eyes;
    private readonly Slot[] _slots;
    private readonly Sprite[] _sprites;
    private readonly Dictionary<string, (int Index, bool Overlay)> _files = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> Files => _files.Keys;
    public bool Contains(string file) => _files.ContainsKey(file);
    public (int Width, int Height) Dimensions(string file) => _files[file].Overlay ? (640, 640) : (512, 512);
    public byte[] Render(string file) { var entry = _files[file]; return entry.Overlay ? RenderOverlay(entry.Index) : RenderCat(entry.Index); }
    /// <summary>Immutable RGBA and floating-point attachment arrays, excluding
    /// object overhead, sampled pose tables, and temporary per-render pixels.</summary>
    public long ResidentTextureBytes { get; }

    public FireflyRenderer(string assetRoot)
    {
        var root = Path.Combine(assetRoot, "skeletal", "firefly");
        var jsonPath = Path.Combine(root, "firefly.json");
        if (new FileInfo(jsonPath).Length > 8 * 1024 * 1024) throw new InvalidDataException("Oversized firefly description.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
        var json = doc.RootElement;
        if (json.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("Unsupported firefly rig.");
        foreach (var entry in json.GetProperty("files").EnumerateObject())
        {
            var index = entry.Value.GetProperty("index").GetInt32();
            if (index is < 0 or >= 600) throw new InvalidDataException("Invalid firefly frame reference.");
            _files.Add(entry.Name, (index, entry.Value.GetProperty("overlay").GetBoolean()));
        }
        var textureBytes = json.GetProperty("textureBytes").GetInt32();
        if (textureBytes is <= 0 or > 64 * 1024 * 1024) throw new InvalidDataException("Invalid firefly texture budget.");
        foreach (var entry in json.GetProperty("buffers").EnumerateObject())
        {
            var offset = entry.Value.GetProperty("offset").GetInt32(); var length = entry.Value.GetProperty("length").GetInt32();
            var type = entry.Value.GetProperty("type").GetString();
            if (offset < 0 || length < 0 || (long)offset + length > textureBytes || type is not ("u8" or "f32") || (type == "f32" && length % 4 != 0))
                throw new InvalidDataException("Invalid firefly attachment range.");
        }
        using var compressed = File.OpenRead(Path.Combine(root, "textures.bin.z"));
        using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
        var raw = new byte[textureBytes]; inflate.ReadExactly(raw);
        if (inflate.ReadByte() != -1 || !Convert.ToHexStringLower(SHA256.HashData(raw)).Equals(json.GetProperty("textureSha256").GetString(), StringComparison.Ordinal))
            throw new InvalidDataException("Firefly attachment integrity check failed.");
        ResidentTextureBytes = textureBytes;
        byte[] Bytes(JsonElement name)
        {
            var b = json.GetProperty("buffers").GetProperty(name.GetString()!);
            return raw.AsSpan(b.GetProperty("offset").GetInt32(), b.GetProperty("length").GetInt32()).ToArray();
        }
        float[] Floats(JsonElement name)
        {
            var b = Bytes(name); var f = new float[b.Length / 4]; Buffer.BlockCopy(b, 0, f, 0, b.Length);
            if (f.Any(n => !float.IsFinite(n))) throw new InvalidDataException("Nonfinite firefly attachment.");
            return f;
        }
        var body = json.GetProperty("body");
        _base = Bytes(body.GetProperty("base")); _paw = Bytes(body.GetProperty("paw"));
        _cleared = Bytes(body.GetProperty("cleared")); _exposed = Bytes(body.GetProperty("exposedUnderbody"));
        _arm = Bytes(body.GetProperty("arm")); _support = Bytes(body.GetProperty("supportMask"));
        _mouth = Bytes(body.GetProperty("mouthTexture")); _mouthMask = Floats(body.GetProperty("mouthMask"));
        _sourceDark = Floats(body.GetProperty("sourceDark")); _targetDark = Floats(body.GetProperty("targetDark"));
        var contour = body.GetProperty("contours");
        for (var y = 393; y < 489; y++) { _contours[y, 0] = contour[y].GetProperty("source").GetDouble(); _contours[y, 1] = contour[y].GetProperty("target").GetDouble(); }
        for (var y = 0; y < W; y++) for (var x = 0; x < W; x++) _bodyWeights[y * W + x] = (1 - Smooth((y - 385) / 54.0)) * (1 - Smooth((x - 401) / 28.0));
        _eyes = json.GetProperty("eyes").EnumerateArray().Select(e => new Eye
        {
            Frame = ReadFrame(e.GetProperty("baseFrame")), Layer = Floats(e.GetProperty("layer")), Mask = Floats(e.GetProperty("mask")), Background = Floats(e.GetProperty("background")),
            Highlights = e.GetProperty("highlights").EnumerateArray().Select(h => new Highlight
            {
                Width = h.GetProperty("width").GetInt32(), Height = h.GetProperty("height").GetInt32(), Center = Array(h.GetProperty("sourceCenter")),
                UpWidth = h.GetProperty("upWidth").GetInt32(), UpHeight = h.GetProperty("upHeight").GetInt32(), UpCenter = Array(h.GetProperty("upSourceCenter")),
                Patch = Floats(h.GetProperty("patch")), UpPatch = Floats(h.GetProperty("upPatch"))
            }).ToArray()
        }).ToArray();
        _sprites = json.GetProperty("sprites").EnumerateArray().Select(s => new Sprite(s.GetProperty("width").GetInt32(), s.GetProperty("height").GetInt32(), Bytes(s.GetProperty("data")))).ToArray();
        _slots = json.GetProperty("frames").EnumerateArray().Select(s =>
        {
            var b = s.GetProperty("body"); var fly = s.GetProperty("fly");
            return new Slot(b.GetProperty("prepare").GetDouble(), b.GetProperty("paw").GetDouble(), b.GetProperty("mouth").GetDouble(), s.GetProperty("engagement").GetDouble(),
                s.GetProperty("eyeFrames").EnumerateArray().Select(ReadFrame).ToArray(),
                s.GetProperty("poses").EnumerateArray().Select(p => new Pose(Array(p.GetProperty("center")), Array(p.GetProperty("Pinv")),
                    p.GetProperty("highlights").EnumerateArray().Select(h => new HighlightPose(Array(h.GetProperty("center")), Array(h.GetProperty("Hinv")), h.GetProperty("upBlend").GetDouble())).ToArray())).ToArray(),
                fly.ValueKind == JsonValueKind.Null ? null : new Fly(fly.GetProperty("sprite").GetInt32(), fly.GetProperty("left").GetInt32(), fly.GetProperty("top").GetInt32(), fly.GetProperty("opacity").GetDouble(), fly.GetProperty("pulse").GetDouble(), fly.GetProperty("behind").GetBoolean()));
        }).ToArray();
        ValidateDimensions();
    }

    private void ValidateDimensions()
    {
        if (_slots.Length != 600 || _eyes.Length != 2 || _sprites.Length is < 1 or > 600 || _files.Count is < 1 or > 1200 ||
            new[] { _base, _paw, _cleared, _exposed, _arm, _support, _mouth }.Any(a => a.Length != Size) ||
            new[] { _mouthMask, _sourceDark, _targetDark }.Any(a => a.Length != W * W)) throw new InvalidDataException("Invalid firefly rig dimensions.");
        foreach (var eye in _eyes)
        {
            ValidateEyeFrame(eye.Frame);
            if (eye.Layer.Length != 192 * 192 * 4 || eye.Mask.Length != W * W || eye.Background.Length != W * W * 3 || eye.Highlights.Length != 2) throw new InvalidDataException("Invalid firefly eye attachments.");
            foreach (var h in eye.Highlights)
                if (h.Width is < 1 or > W || h.Height is < 1 or > W || h.UpWidth is < 1 or > W || h.UpHeight is < 1 or > W ||
                    h.Patch.Length != h.Width * h.Height * 4 || h.UpPatch.Length != h.UpWidth * h.UpHeight * 4 || h.Center.Length != 2 || h.UpCenter.Length != 2)
                    throw new InvalidDataException("Invalid firefly highlight attachment.");
        }
        foreach (var sprite in _sprites) if (sprite.Width is < 1 or > 640 || sprite.Height is < 1 or > 640 || sprite.Data.Length != sprite.Width * sprite.Height * 4) throw new InvalidDataException("Invalid firefly sprite attachment.");
        foreach (var slot in _slots)
        {
            if (!Unit(slot.Prepare) || !Unit(slot.Paw) || !Unit(slot.Mouth) || !Unit(slot.Engagement) || slot.EyeFrames.Length != 2 || slot.Poses.Length != 2) throw new InvalidDataException("Invalid firefly pose.");
            foreach (var frame in slot.EyeFrames) ValidateEyeFrame(frame);
            foreach (var pose in slot.Poses)
            {
                if (pose.Center.Length != 2 || pose.Pinv.Length != 4 || pose.Highlights.Length != 2) throw new InvalidDataException("Invalid pupil pose.");
                foreach (var h in pose.Highlights) if (h.Center.Length != 2 || h.Hinv.Length != 4 || !Unit(h.UpBlend)) throw new InvalidDataException("Invalid highlight pose.");
            }
            if (slot.Fly is { } f && (f.Sprite < 0 || f.Sprite >= _sprites.Length || !Unit(f.Opacity) || !Unit(f.Pulse) || Math.Abs((long)f.Left) > 1280 || Math.Abs((long)f.Top) > 1280)) throw new InvalidDataException("Invalid flying attachment pose.");
        }
    }
    private static bool Unit(double n) => double.IsFinite(n) && n >= 0 && n <= 1;
    private static void ValidateEyeFrame(EyeFrame f)
    {
        if (!double.IsFinite(f.Cx) || !double.IsFinite(f.Cy) || !double.IsFinite(f.Rx) || !double.IsFinite(f.Ry) || f.Cx < 0 || f.Cx >= W || f.Cy < 0 || f.Cy >= W || f.Rx is < 1 or > 100 || f.Ry is < 1 or > 100) throw new InvalidDataException("Invalid eye bone frame.");
    }

    public byte[] RenderCat(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index); ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, 600);
        var s = _slots[index];
        if (s.Prepare == 0 && s.Paw == 0 && s.Mouth == 0) return RenderEyes(_base, s);
        var under = MorphUnderbody(Smooth(s.Prepare));
        if (s.Mouth > 0) for (var i = 0; i < _mouthMask.Length; i++) if (_mouthMask[i] != 0) MixAt(under, i * 4, under, _mouth, _mouthMask[i] * s.Mouth);
        var torso = BodyWarp(under, s.Prepare);
        var paw = TransformPaw(s.Paw, out var cx, out var cy, out var theta);
        for (var p = 0; p < Size; p += 4) if (_support[p + 3] != 0) MixAt(torso, p, torso, _base, _support[p + 3] / 255.0);
        if (s.Paw > .001)
        {
            var arm = ArmWarp(cx, cy, theta, s.Paw);
            for (var p = 0; p < Size; p += 4) arm[p + 3] = Round(arm[p + 3] * torso[p + 3] / 255.0);
            Composite(torso, arm);
        }
        var result = RenderEyes(torso, s); Composite(result, paw); return result;
    }

    public byte[] RenderOverlay(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index); ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, 600);
        var output = new byte[640 * 640 * 4]; var f = _slots[index].Fly;
        if (f is null) return output;
        var sprite = _sprites[f.Sprite]; var cat = f.Behind ? RenderCat(index) : null;
        for (var y = Math.Max(0, f.Top); y < Math.Min(640, f.Top + sprite.Height); y++)
            for (var x = Math.Max(0, f.Left); x < Math.Min(640, f.Left + sprite.Width); x++)
            {
                var sp = ((y - f.Top) * sprite.Width + x - f.Left) * 4; var dp = (y * 640 + x) * 4;
                var alpha = Round(sprite.Data[sp + 3] * f.Opacity * f.Pulse);
                if (cat is not null && x >= 64 && x < 576 && y >= 100 && y < 612) alpha = Round(alpha * (255 - cat[((y - 100) * W + x - 64) * 4 + 3]) / 255.0);
                if (alpha != 0) { output[dp] = sprite.Data[sp]; output[dp + 1] = sprite.Data[sp + 1]; output[dp + 2] = sprite.Data[sp + 2]; output[dp + 3] = alpha; }
            }
        return output;
    }

    private byte[] BodyWarp(byte[] source, double prepare)
    {
        if (prepare == 0) return (byte[])source.Clone();
        var output = new byte[Size]; var theta = -.028 * prepare; var tx = -2.5 * prepare; var ty = -4 * prepare;
        var headCos = Math.Cos(theta); var headSin = Math.Sin(theta);
        // Most head pixels share one rigid bone transform. Only the feathered
        // shoulder/tail boundary needs per-pixel trigonometry; planted pixels
        // below and right of that boundary are contiguous unchanged spans.
        Buffer.BlockCopy(source, 439 * W * 4, output, 439 * W * 4, (W - 439) * W * 4);
        for (var y = 0; y < 439; y++)
        {
            Buffer.BlockCopy(source, (y * W + 429) * 4, output, (y * W + 429) * 4, (W - 429) * 4);
            for (var x = 0; x < 429; x++)
            {
            var p = (y * W + x) * 4; var w = _bodyWeights[y * W + x];
            if (w == 0) { Buffer.BlockCopy(source, p, output, p, 4); continue; }
            var angle = theta * w; var c = w == 1 ? headCos : Math.Cos(angle); var s = w == 1 ? headSin : Math.Sin(angle); var dx = x - 232 - tx * w; var dy = y - 400 - ty * w;
            Pixel(source, 232 + c * dx + s * dy, 400 - s * dx + c * dy, output, p);
            }
        }
        return output;
    }

    private byte[] TransformPaw(double amount, out double cx, out double cy, out double theta)
    {
        cx = 160 - 55 * amount - 3 * Math.Sin(Math.PI * amount); cy = 440 - 50 * amount - 3 * Math.Sin(Math.PI * amount); theta = Math.PI / 3 * amount;
        var output = new byte[Size]; var c = Math.Cos(theta); var s = Math.Sin(theta);
        for (var y = 315; y < 490; y++) for (var x = 55; x < 225; x++) { var dx = x - cx; var dy = y - cy; Pixel(_paw, 160 + c * dx + s * dy, 440 - s * dx + c * dy, output, (y * W + x) * 4); }
        return output;
    }

    private byte[] ArmWarp(double cx, double cy, double theta, double amount)
    {
        var wx = cx - Math.Sin(theta) * -35; var wy = cy + Math.Cos(theta) * -35;
        var sl = Hypot(-43, -42); var tx = wx - 170; var ty = wy - 425; var tl = Math.Max(8, Hypot(tx, ty));
        var sc = -43 / sl; var ss = -42 / sl; var tc = tx / tl; var ts = ty / tl; var widthScale = .94 + .06 * amount; var exposed = Smooth((tl - 22) / 12);
        var output = new byte[Size]; if (exposed == 0) return output;
        for (var y = 345; y < 479; y++) for (var x = 65; x < 218; x++)
        {
            var dx = x - 170; var dy = y - 425; var along = (dx * tc + dy * ts) * sl / tl; var across = (-dx * ts + dy * tc) / widthScale; var p = (y * W + x) * 4;
            Pixel(_arm, 171 + sc * along - ss * across, 426 + ss * along + sc * across, output, p); output[p + 3] = Round(output[p + 3] * exposed);
        }
        return output;
    }

    private byte[] MorphUnderbody(double amount)
    {
        if (amount <= 0) return (byte[])_cleared.Clone(); if (amount >= 1) return (byte[])_exposed.Clone();
        var output = (byte[])_cleared.Clone(); var a = new byte[4]; var b = new byte[4];
        for (var y = 393; y < 489; y++)
        {
            var from = _contours[y, 0]; var to = _contours[y, 1]; var edge = from + (to - from) * amount;
            for (var x = 50; x < 237; x++)
            {
                var p = (y * W + x) * 4; var u = (x - edge) / (237 - edge); var sx = from + u * (237 - from); var tx = to + u * (237 - to);
                System.Array.Clear(a); System.Array.Clear(b); Pixel(_cleared, sx, y, a, 0); Pixel(_exposed, tx, y, b, 0);
                var aa = a[3] * (1 - amount); var ba = b[3] * amount; var alpha = aa + ba; output[p + 3] = Round(alpha);
                if (alpha > .001) for (var c = 0; c < 3; c++) output[p + c] = Round((a[c] * aa + b[c] * ba) / alpha); else System.Array.Clear(output, p, 4);
                var line = Smooth((y - 402) / 7.0) * (1 - Smooth((y - 458) / 8.0)) * (1 - Smooth((x - 123) / 13.0));
                if (line > 0 && alpha > 1)
                {
                    var distance = FieldAt(_sourceDark, sx, y) * (1 - amount) + FieldAt(_targetDark, tx, y) * amount; var ink = Smooth(distance + .5);
                    for (var c = 0; c < 3; c++)
                    {
                        var paperColor = c == 0 ? 255 : c == 1 ? 254 : 251; var black = c == 0 ? 23 : c == 1 ? 10 : 7;
                        var paperA = a[c] > 190 ? a[c] : paperColor; var paperB = b[c] > 190 ? b[c] : paperColor;
                        var paper = (paperA * aa + paperB * ba) / alpha; var paint = paper * (1 - ink) + black * ink;
                        output[p + c] = Round(output[p + c] * (1 - line) + paint * line);
                    }
                }
            }
        }
        return output;
    }

    private byte[] RenderEyes(byte[] source, Slot slot)
    {
        var output = (byte[])source.Clone(); if (slot.Engagement < 1e-9) return output;
        var frames = slot.EyeFrames;
        var headRoll = Math.Clamp(Math.Atan2(frames[1].Cy - frames[0].Cy, frames[1].Cx - frames[0].Cx) - Math.Atan2(_eyes[1].Frame.Cy - _eyes[0].Frame.Cy, _eyes[1].Frame.Cx - _eyes[0].Frame.Cx), -.22, .22);
        for (var e = 0; e < 2; e++)
        {
            var eye = _eyes[e]; var b = eye.Frame; var f = frames[e]; var pose = slot.Poses[e];
            // Body's analytic frames keep the original ellipse axis convention.
            var cos = Math.Cos(headRoll); var sin = Math.Sin(headRoll); var sx = f.Rx / b.Rx; var sy = f.Ry / b.Ry;
            var a00 = cos * sx; var a01 = -sin * sy; var a10 = sin * sx; var a11 = cos * sy; var det = a00 * a11 - a01 * a10;
            var ai00 = a11 / det; var ai01 = -a01 / det; var ai10 = -a10 / det; var ai11 = a00 / det;
            var startup = Smooth(slot.Engagement / .10); var radius = (int)Math.Ceiling(Math.Max(f.Rx, f.Ry) + 9);
            for (var y = Math.Max(0, (int)Math.Floor(f.Cy - radius)); y <= Math.Min(511, (int)Math.Ceiling(f.Cy + radius)); y++)
                for (var x = Math.Max(0, (int)Math.Floor(f.Cx - radius)); x <= Math.Min(511, (int)Math.Ceiling(f.Cx + radius)); x++)
                {
                    var dx = ai00 * (x - f.Cx) + ai01 * (y - f.Cy); var dy = ai10 * (x - f.Cx) + ai11 * (y - f.Cy); var cx = b.Cx + dx; var cy = b.Cy + dy;
                    var weight = Sample(eye.Mask, W, W, cx, cy, 0, 1) * startup; if (weight < 1e-6) continue;
                    var ux = pose.Pinv[0] * (cx - pose.Center[0]) + pose.Pinv[1] * (cy - pose.Center[1]); var uy = pose.Pinv[2] * (cx - pose.Center[0]) + pose.Pinv[3] * (cy - pose.Center[1]);
                    var tx = (ux / 2.5 + .5) * 191; var ty = (uy / 2.5 + .5) * 191; var alpha = Sample(eye.Layer, 192, 192, tx, ty, 3, 4);
                    var r = Sample(eye.Layer, 192, 192, tx, ty, 0, 4) + Sample(eye.Background, W, W, cx, cy, 0, 3) * (1 - alpha);
                    var g = Sample(eye.Layer, 192, 192, tx, ty, 1, 4) + Sample(eye.Background, W, W, cx, cy, 1, 3) * (1 - alpha);
                    var blue = Sample(eye.Layer, 192, 192, tx, ty, 2, 4) + Sample(eye.Background, W, W, cx, cy, 2, 3) * (1 - alpha);
                    for (var j = 0; j < eye.Highlights.Length; j++)
                    {
                        var h = eye.Highlights[j]; var hp = pose.Highlights[j];
                        var qx = hp.Hinv[0] * (ux - hp.Center[0]) + hp.Hinv[1] * (uy - hp.Center[1]); var qy = hp.Hinv[2] * (ux - hp.Center[0]) + hp.Hinv[3] * (uy - hp.Center[1]);
                        var normal = Sample(h.Patch, h.Width, h.Height, h.Center[0] + qx * h.Width, h.Center[1] + qy * h.Height, 3, 4);
                        var up = Sample(h.UpPatch, h.UpWidth, h.UpHeight, h.UpCenter[0] + qx * h.UpWidth, h.UpCenter[1] + qy * h.UpHeight, 3, 4);
                        var ha = (normal + (up - normal) * hp.UpBlend) * alpha;
                        r = r * (1 - ha) + 255 * ha; g = g * (1 - ha) + 255 * ha; blue = blue * (1 - ha) + 255 * ha;
                    }
                    var p = (y * W + x) * 4;
                    output[p] = Round(Math.Clamp(source[p] * (1 - weight) + r * weight, 0, 255));
                    output[p + 1] = Round(Math.Clamp(source[p + 1] * (1 - weight) + g * weight, 0, 255));
                    output[p + 2] = Round(Math.Clamp(source[p + 2] * (1 - weight) + blue * weight, 0, 255));
                }
        }
        return output;
    }

    private static void Pixel(byte[] raw, double x, double y, byte[] output, int p)
    {
        if (x < 0 || y < 0 || x >= 511 || y >= 511) return;
        var ix = (int)Math.Floor(x); var iy = (int)Math.Floor(y); var u = x - ix; var v = y - iy; var q = (iy * W + ix) * 4;
        var w0 = (1 - u) * (1 - v) * raw[q + 3]; var w1 = u * (1 - v) * raw[q + 7]; var w2 = (1 - u) * v * raw[q + W * 4 + 3]; var w3 = u * v * raw[q + W * 4 + 7];
        // Keep the exact accumulation order used by JavaScript's loop.
        var a = ((w0 + w1) + w2) + w3; output[p + 3] = Round(a);
        if (a > .001) for (var c = 0; c < 3; c++) output[p + c] = Round((((w0 * raw[q + c] + w1 * raw[q + 4 + c]) + w2 * raw[q + W * 4 + c]) + w3 * raw[q + W * 4 + 4 + c]) / a);
    }
    private static void MixAt(byte[] output, int p, byte[] a, byte[] b, double w)
    {
        if (w == 0) { Buffer.BlockCopy(a, p, output, p, 4); return; } if (w == 1) { Buffer.BlockCopy(b, p, output, p, 4); return; }
        var aa = a[p + 3] * (1 - w); var ba = b[p + 3] * w; var alpha = aa + ba; output[p + 3] = Round(alpha);
        if (alpha > .001) for (var c = 0; c < 3; c++) output[p + c] = Round((a[p + c] * aa + b[p + c] * ba) / alpha);
    }
    private static void Composite(byte[] background, byte[] foreground)
    {
        for (var p = 0; p < background.Length; p += 4)
        {
            var a = foreground[p + 3] / 255.0; if (a == 0) continue; var b = background[p + 3] / 255.0; var alpha = a + b * (1 - a);
            for (var c = 0; c < 3; c++) background[p + c] = Round((foreground[p + c] * a + background[p + c] * b * (1 - a)) / alpha);
            background[p + 3] = Round(alpha * 255);
        }
    }
    private static double FieldAt(float[] f, double x, double y) => x < 0 || x >= 511 || y < 0 || y >= 511 ? -20 : Sample(f, W, W, x, y, 0, 1);
    private static double Sample(float[] src, int w, int h, double x, double y, int c, int channels)
    {
        if (x < 0 || x >= w - 1 || y < 0 || y >= h - 1) return 0;
        var ix = (int)Math.Floor(x); var iy = (int)Math.Floor(y); var u = x - ix; var v = y - iy; var p = (iy * w + ix) * channels + c;
        return src[p] * (1 - u) * (1 - v) + src[p + channels] * u * (1 - v) + src[p + w * channels] * (1 - u) * v + src[p + (w + 1) * channels] * u * v;
    }
    private static byte Round(double n) => unchecked((byte)(long)Math.Floor(n + .5));
    private static double Smooth(double n) { n = Math.Clamp(n, 0, 1); return n * n * (3 - 2 * n); }
    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);
    private static double[] Array(JsonElement e)
    {
        var result = e.EnumerateArray().Select(n => n.GetDouble()).ToArray();
        if (result.Any(n => !double.IsFinite(n) || Math.Abs(n) > 1000000)) throw new InvalidDataException("Invalid firefly matrix.");
        return result;
    }
    private static EyeFrame ReadFrame(JsonElement e) => new(e.GetProperty("cx").GetDouble(), e.GetProperty("cy").GetDouble(), e.GetProperty("rx").GetDouble(), e.GetProperty("ry").GetDouble());
    private sealed record EyeFrame(double Cx, double Cy, double Rx, double Ry);
    private sealed record Slot(double Prepare, double Paw, double Mouth, double Engagement, EyeFrame[] EyeFrames, Pose[] Poses, Fly? Fly);
    private sealed record Pose(double[] Center, double[] Pinv, HighlightPose[] Highlights);
    private sealed record HighlightPose(double[] Center, double[] Hinv, double UpBlend);
    private sealed record Fly(int Sprite, int Left, int Top, double Opacity, double Pulse, bool Behind);
    private sealed record Sprite(int Width, int Height, byte[] Data);
    private sealed class Eye { public EyeFrame Frame = null!; public float[] Layer = [], Mask = [], Background = []; public Highlight[] Highlights = []; }
    private sealed class Highlight { public int Width, Height, UpWidth, UpHeight; public double[] Center = [], UpCenter = []; public float[] Patch = [], UpPatch = []; }
}
