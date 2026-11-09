"""Fit an anatomical 2D skeleton to frozen RGBA clips; store exact raster residuals.

Source files are opened read-only. Run using .tools/rig-python/Scripts/python.exe.
The integer inverse skinning kernel is mirrored by LegacyRenderer.cs.
"""
import argparse
import hashlib
import json
from pathlib import Path
import time
import zlib
import brotli

import cv2
import numpy as np

W = H = 512
Q = 65536
NAMES = ['idle', 'blink', 'fall_asleep', 'sleep_loop', 'wake', 'rise', 'hold', 'settle', 'yawn', 'wink', 'meow']
# parent, center x/y, elliptical influence radii x/y. Three strongest influences
# are retained per vertex; the raster is a dense skinned mesh, not a frame atlas.
BONES = [
    ('torso', -1, 357, 399, 112, 80),
    ('head', 0, 232, 327, 106, 87),
    ('ear_left', 1, 127, 260, 38, 54),
    ('ear_right', 1, 327, 271, 42, 55),
    ('eye_left', 1, 164, 329, 38, 38),
    ('eye_right', 1, 304, 344, 39, 38),
    ('muzzle', 1, 231, 372, 43, 42),
    ('tail', 0, 444, 405, 44, 63),
    ('paw_left', 0, 160, 445, 51, 38),
    ('paw_right', 0, 270, 448, 53, 37),
]
IDENTITY = np.array([Q, 0, 0, 0, Q, 0], np.int64)
YY, XX = np.mgrid[:H, :W]


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read_rgba(path):
    image = cv2.imread(str(path), cv2.IMREAD_UNCHANGED)
    if image is None or image.shape != (H, W, 4):
        raise ValueError(f'Expected 512x512 RGBA PNG: {path}')
    return np.ascontiguousarray(image[:, :, [2, 1, 0, 3]])


def influences():
    priors = []
    for name, parent, cx, cy, rx, ry in BONES:
        radius = ((XX - cx) / rx) ** 2 + ((YY - cy) / ry) ** 2
        priors.append(1 / (1 + radius) ** 4)
    prior = np.stack(priors, axis=2)
    indices = np.argsort(prior, axis=2)[:, :, -3:][:, :, ::-1]
    weights = np.take_along_axis(prior, indices, axis=2)
    weights = weights / weights.sum(axis=2, keepdims=True) * 256
    weights = np.floor(weights).astype(np.uint16)
    weights[:, :, 0] += 256 - weights.sum(axis=2).astype(np.uint16)
    return indices.astype(np.uint8), weights, prior


INDICES, WEIGHTS, PRIOR = influences()


def fit_bones(previous, current):
    # Track source correspondence backwards, because inverse skinning avoids
    # raster holes. Texture/alpha detail not explained by bones is residual.
    def gray(image):
        premult = (image[:, :, :3].astype(np.float32) * (image[:, :, 3:4] / 255)).astype(np.uint8)
        return cv2.resize(cv2.cvtColor(premult, cv2.COLOR_RGB2GRAY), (256, 256), interpolation=cv2.INTER_AREA)
    now = gray(current)
    before = gray(previous)
    flow = cv2.calcOpticalFlowFarneback(now, before, None, .5, 4, 21, 4, 7, 1.5, 0)
    flow = cv2.resize(flow, (W, H), interpolation=cv2.INTER_LINEAR) * 2
    # Fit one affine joint transform per anatomical region. A regularizer
    # stabilizes invisible/textureless bones toward zero displacement (identity).
    edge = np.hypot(cv2.Sobel(now, cv2.CV_32F, 1, 0), cv2.Sobel(now, cv2.CV_32F, 0, 1))
    edge = cv2.resize(edge, (W, H))
    confidence = (current[:, :, 3] > 64) * (.15 + np.minimum(edge / 80, 1))
    matrices = []
    errors = []
    for bone, (name, parent, cx, cy, rx, ry) in enumerate(BONES):
        weight = (PRIOR[:, :, bone] * confidence)[::4, ::4].reshape(-1)
        design = np.stack(((XX[::4, ::4] - cx) / rx, (YY[::4, ::4] - cy) / ry,
                           np.ones_like(XX[::4, ::4])), axis=2).reshape(-1, 3)
        observed = flow[::4, ::4].reshape(-1, 2)
        normal = design.T @ (weight[:, None] * design)
        regularizer = max(.01, float(weight.sum()) * .015)
        fitted = np.linalg.solve(normal + np.diag([regularizer, regularizer, regularizer * .02]),
                                 design.T @ (weight[:, None] * observed))
        ax, bx = np.clip(fitted[0, 0] / rx, -.12, .12), np.clip(fitted[1, 0] / ry, -.12, .12)
        ay, by = np.clip(fitted[0, 1] / rx, -.12, .12), np.clip(fitted[1, 1] / ry, -.12, .12)
        matrix = [1 + ax, bx, fitted[2, 0] - ax * cx - bx * cy,
                  ay, 1 + by, fitted[2, 1] - ay * cx - by * cy]
        matrices.append(np.floor(np.array(matrix) * Q + .5).astype(np.int64))
        errors.append(float(np.sqrt(np.sum(weight[:, None] * (design @ fitted - observed) ** 2) / max(1, weight.sum()))))
    return np.stack(matrices), errors


def skin(previous, matrices):
    sx = np.zeros((H, W), dtype=np.int64)
    sy = np.zeros((H, W), dtype=np.int64)
    for slot in range(3):
        matrix = matrices[INDICES[:, :, slot]]
        weight = WEIGHTS[:, :, slot]
        sx += (matrix[:, :, 0] * XX + matrix[:, :, 1] * YY + matrix[:, :, 2]) * weight
        sy += (matrix[:, :, 3] * XX + matrix[:, :, 4] * YY + matrix[:, :, 5]) * weight
    # 16.16 matrix * 8-bit normalized bone weights -> 8-bit subpixels.
    sx = (sx + 32768) >> 16
    sy = (sy + 32768) >> 16
    x0, y0 = sx >> 8, sy >> 8
    fx, fy = sx & 255, sy & 255
    result = np.zeros((H, W, 4), dtype=np.int64)
    for dx, dy, factor in [(0, 0, (256-fx)*(256-fy)), (1, 0, fx*(256-fy)),
                           (0, 1, (256-fx)*fy), (1, 1, fx*fy)]:
        px, py = x0 + dx, y0 + dy
        valid = (px >= 0) & (px < W) & (py >= 0) & (py < H)
        sample = previous[np.clip(py, 0, H-1), np.clip(px, 0, W-1)]
        result += sample.astype(np.int64) * (factor * valid)[:, :, None]
    return ((result + 32768) >> 16).astype(np.uint8)


def planar(data):
    return data.transpose(2, 0, 1).copy().tobytes()


def encode_image(image, horizontal=False):
    # Reversible modulo-256 color decorrelation; never changes output pixels.
    converted = image.copy()
    converted[:, :, 0] -= converted[:, :, 1]
    converted[:, :, 2] -= converted[:, :, 1]
    if horizontal:
        converted[:, 1:] -= converted[:, :-1].copy()
    return planar(converted)


def decode_image(raw, height, width, horizontal=False):
    converted = np.frombuffer(raw, np.uint8).reshape(4, height, width).transpose(1, 2, 0).copy()
    if horizontal:
        converted = np.cumsum(converted, axis=1, dtype=np.uint64).astype(np.uint8)
    converted[:, :, 0] += converted[:, :, 1]
    converted[:, :, 2] += converted[:, :, 1]
    return converted


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path(__file__).resolve().parents[1] / 'assets/strawberry-cat/skeletal/legacy')
    parser.add_argument('--limit', type=int, default=0, help='Study only; limit new frames per clip')
    parser.add_argument('--checkpoint-depth', type=int, default=12)
    args = parser.parse_args()
    source = args.source.resolve()
    output = args.output.resolve()
    if source == output or source in output.parents:
        raise ValueError('Output must not be inside the frozen source repository.')
    root = source / 'assets/strawberry-cat'
    manifest_bytes = (root / 'manifest.json').read_bytes()
    manifest = json.loads(manifest_bytes)
    output.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    archive = bytearray()

    def store(raw):
        compressed = brotli.compress(raw, quality=9, lgwin=20)
        assert brotli.decompress(compressed) == raw
        block = {'offset': len(archive), 'length': len(compressed), 'rawLength': len(raw), 'sha256': digest(raw)}
        archive.extend(compressed)
        return block

    binding = np.concatenate((INDICES[:, :, :, None].astype(np.uint16), WEIGHTS[:, :, :, None]), axis=3)
    # [boneIndex, weight] each uint16 little-endian, three entries per vertex.
    binding_block = store(binding.astype('<u2').tobytes())
    frames = []
    by_file = {}
    by_hash = {}
    timings = {}
    checks = []
    total_original = 0
    for name in NAMES:
        clip = manifest['clips'][name]
        previous_file = None
        new_count = 0
        clip_bytes = len(archive)
        clip_original = 0
        for timeline_index, slot in enumerate(clip['frames']):
            file = slot['file']
            if file in by_file:
                previous_file = file
                continue
            current = read_rgba(root / file)
            raw_hash = digest(current.tobytes())
            original_size = (root / file).stat().st_size
            total_original += original_size
            clip_original += original_size
            frame = {'id': len(frames), 'file': file, 'sha256': raw_hash}
            if raw_hash in by_hash:
                frame.update(kind='alias', parent=by_hash[raw_hash], depth=frames[by_hash[raw_hash]]['depth'])
            elif previous_file is None or by_file[previous_file]['depth'] >= args.checkpoint_depth:
                block = store(encode_image(current, horizontal=True))
                frame.update(kind='checkpoint', parent=-1, depth=0, block=block)
                decoded = decode_image(brotli.decompress(archive[block['offset']:block['offset']+block['length']]), H, W, horizontal=True)
                assert np.array_equal(decoded, current)
            else:
                previous = read_rgba(root / previous_file)
                matrices, errors = fit_bones(previous, current)
                predicted = skin(previous, matrices)
                residual = (current.astype(np.int16) - predicted.astype(np.int16)).astype(np.uint8)
                changed = np.any(residual != 0, axis=2)
                ys, xs = np.nonzero(changed)
                rect = {'x': int(xs.min()), 'y': int(ys.min()), 'width': int(xs.max()-xs.min()+1), 'height': int(ys.max()-ys.min()+1)} if len(xs) else {'x': 0, 'y': 0, 'width': 1, 'height': 1}
                x, y, w, h = rect.values()
                block = store(encode_image(residual[y:y+h, x:x+w]))
                correction = decode_image(brotli.decompress(archive[block['offset']:block['offset']+block['length']]), h, w)
                restored = predicted.copy()
                restored[y:y+h, x:x+w] += correction
                assert np.array_equal(restored, current), file
                alpha = np.maximum(previous[:, :, 3], current[:, :, 3]) / 255
                error = np.abs(current[:, :, :3].astype(np.int16) - predicted[:, :, :3].astype(np.int16))
                frame.update(kind='skinned', parent=by_file[previous_file]['id'], depth=by_file[previous_file]['depth']+1,
                             inverseBones=matrices.tolist(), rect=rect, block=block,
                             fitRmsPx=round(float(np.mean(errors)), 5),
                             predictionMae=round(float(np.sum(error * alpha[:, :, None]) / max(1, 3 * alpha.sum())), 5))
            frames.append(frame)
            by_file[file] = frame
            by_hash.setdefault(raw_hash, frame['id'])
            previous_file = file
            new_count += 1
            if new_count % 25 == 0:
                print(f'{name}: {new_count} new poses, {len(archive)/1048576:.2f} MiB, {time.monotonic()-started:.1f}s', flush=True)
            if args.limit and new_count >= args.limit:
                break
        timings[name] = {'loop': clip['loop'], 'frames': [{'id': by_file[f['file']]['id'], 'durationMs': f['durationMs']}
                                                        for f in clip['frames'] if f['file'] in by_file]}
        checks.append({'clip': name, 'newPoses': new_count, 'originalBytes': clip_original, 'archiveBytes': len(archive)-clip_bytes})
        print(json.dumps(checks[-1]), flush=True)
    metadata = {'version': 1, 'width': W, 'height': H, 'format': 'RGBA8', 'compression': 'brotli',
                'imageEncoding': 'planar-r-minus-g-g-b-minus-g-a;checkpoint-horizontal-sub',
                'prediction': 'anatomical-affine-inverse-skinning-fixedpoint-v1', 'archive': 'legacy.bin',
                'archiveSha256': digest(archive), 'binding': binding_block, 'influencesPerVertex': 3,
                'maxDependencyDepth': args.checkpoint_depth, 'sourceManifestSha256': digest(manifest_bytes),
                'bones': [{'name': b[0], 'parent': b[1], 'center': list(b[2:4]), 'radius': list(b[4:])} for b in BONES],
                'frames': frames, 'clips': timings}
    (output / 'legacy.bin').write_bytes(archive)
    (output / 'legacy.json').write_text(json.dumps(metadata, separators=(',', ':'))+'\n', encoding='utf-8')
    report = {'complete': not args.limit, 'allDecodedRgbaExact': True, 'poses': len(frames),
              'originalPngBytes': total_original, 'archiveBytes': len(archive),
              'metadataBytes': (output / 'legacy.json').stat().st_size,
              'elapsedSeconds': round(time.monotonic()-started, 2), 'clips': checks,
              'kinds': {k: sum(f['kind'] == k for f in frames) for k in ['checkpoint', 'skinned', 'alias']},
              'maxDependencyDepth': max(f['depth'] for f in frames)}
    (output / 'verification.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2), flush=True)


if __name__ == '__main__':
    main()
