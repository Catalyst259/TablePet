"""Promote cross-clip junctions and first cold-entry poses to seek checkpoints.

Run after compile-legacy.py and optional quality-11 recompression. The output is
an independent staged asset root; never overwrite an archive being rendered.
"""
import argparse
from collections import OrderedDict
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import time

import brotli
import numpy as np

spec = importlib.util.spec_from_file_location('legacy_compiler', Path(__file__).with_name('compile-legacy.py'))
codec = importlib.util.module_from_spec(spec)
spec.loader.exec_module(codec)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main():
    repo = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser()
    parser.add_argument('--input', type=Path, default=repo / 'assets/strawberry-cat/skeletal/legacy')
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=repo / '.tools/legacy-seek-optimized')
    args = parser.parse_args()
    source = args.source.resolve()
    output = args.output.resolve()
    if source == output or source in output.parents:
        raise ValueError('Output must not be inside the frozen source repository.')
    destination = output / 'skeletal/legacy'
    if destination.resolve() == args.input.resolve():
        raise ValueError('Use a separate output; live archives must never be rewritten in place.')
    started = time.monotonic()
    original = json.loads((args.input / 'legacy.json').read_bytes())
    previous_archive = (args.input / original['archive']).read_bytes()
    assert sha(previous_archive) == original['archiveSha256']
    next_metadata = copy.deepcopy(original)
    frames = next_metadata['frames']
    usage, reasons = {}, {}
    for name, clip in original['clips'].items():
        for slot in clip['frames']:
            usage.setdefault(slot['id'], set()).add(name)
    for name, clip in original['clips'].items():
        previous = -1
        for slot in clip['frames']:
            identity = slot['id']
            if identity == previous:
                continue
            frame = frames[identity]
            if len(usage[identity]) > 1 and frame['kind'] == 'skinned' and frame['parent'] != previous:
                reasons.setdefault(identity, []).append(name)
            previous = identity
    # AssetCatalog preloads three distinct images per clip. The fourth is the
    # first pose a cold background decoder must produce before its deadline.
    # Give that pose an independent checkpoint, rather than reconstructing the
    # three source predecessors that may have left the separate RGBA LRU.
    for name, clip in original['clips'].items():
        entry = list(dict.fromkeys(slot['id'] for slot in clip['frames']))[:4]
        if len(entry) == 4 and frames[entry[3]]['kind'] == 'skinned':
            reasons.setdefault(entry[3], []).append('fourth-distinct cold entry: ' + name)
    if len(reasons) > 24:
        raise ValueError(f'Refusing broad conversion: {len(reasons)} junctions; inspect the input graph first.')
    print('Promoting junction/entry checkpoints:', len(reasons), list(reasons), flush=True)
    archive = bytearray()

    def append(raw, compressed=None):
        if compressed is None:
            compressed = brotli.compress(raw, quality=11, lgwin=20)
        assert brotli.decompress(compressed) == raw
        block = {'offset': len(archive), 'length': len(compressed), 'rawLength': len(raw), 'sha256': sha(raw)}
        archive.extend(compressed)
        return block

    def copy_block(block):
        compressed = previous_archive[block['offset']:block['offset']+block['length']]
        raw = brotli.decompress(compressed)
        assert len(raw) == block['rawLength'] and sha(raw) == block['sha256']
        return append(raw, compressed)

    next_metadata['binding'] = copy_block(original['binding'])
    promotions = []
    for frame in frames:
        identity = frame['id']
        if identity in reasons:
            pixels = codec.read_rgba(source / 'assets/strawberry-cat' / frame['file'])
            assert sha(pixels.tobytes()) == frame['sha256'], frame['file']
            promotions.append({'id': identity, 'file': frame['file'], 'clips': sorted(usage[identity]),
                               'checkpointReasons': reasons[identity], 'previousBonePose': copy.deepcopy(frame)})
            block = append(codec.encode_image(pixels, horizontal=True))
            frame.clear()
            frame.update(id=identity, file=promotions[-1]['file'], kind='checkpoint', parent=-1, depth=0,
                         sha256=sha(pixels.tobytes()), block=block)
        elif frame.get('block'):
            frame['block'] = copy_block(frame['block'])
        if frame['kind'] == 'checkpoint':
            frame['depth'] = 0
        else:
            frame['depth'] = frames[frame['parent']]['depth'] + (frame['kind'] == 'skinned')
    next_metadata['archiveSha256'] = sha(archive)
    assert next_metadata['clips'] == original['clips']
    assert [f['sha256'] for f in frames] == [f['sha256'] for f in original['frames']]
    assert max(f['depth'] for f in frames) <= original['maxDependencyDepth']
    destination.mkdir(parents=True, exist_ok=True)
    (destination / 'legacy.bin').write_bytes(archive)
    (destination / 'legacy.json').write_text(json.dumps(next_metadata, separators=(',', ':'))+'\n', encoding='utf-8')

    # Independently reconstruct every pose through the updated graph, including
    # descendants whose predecessor depth changed. The reference is original PNG.
    cached = OrderedDict()

    def reconstruct(identity):
        if identity in cached:
            cached.move_to_end(identity)
            return cached[identity]
        f = frames[identity]
        block = f.get('block')
        raw = None if block is None else brotli.decompress(archive[block['offset']:block['offset']+block['length']])
        if f['kind'] == 'checkpoint':
            pixels = codec.decode_image(raw, 512, 512, horizontal=True)
        elif f['kind'] == 'alias':
            pixels = reconstruct(f['parent'])
        else:
            pixels = codec.skin(reconstruct(f['parent']), np.array(f['inverseBones'], dtype=np.int64))
            r = f['rect']
            correction = codec.decode_image(raw, r['height'], r['width'])
            pixels[r['y']:r['y']+r['height'], r['x']:r['x']+r['width']] += correction
        assert sha(pixels.tobytes()) == f['sha256'], f['file']
        cached[identity] = pixels
        while len(cached) > 24:
            cached.popitem(last=False)
        return pixels

    for f in frames:
        actual = reconstruct(f['id'])
        expected = codec.read_rgba(source / 'assets/strawberry-cat' / f['file'])
        assert np.array_equal(actual, expected), f['file']
        if f['id'] % 150 == 0:
            print('Verified RGBA', f['id'], '/', len(frames), flush=True)
    previous_verification = json.loads((args.input / 'verification.json').read_bytes())
    verification = copy.deepcopy(previous_verification)
    verification.update(archiveBytes=len(archive), metadataBytes=(destination/'legacy.json').stat().st_size,
                        kinds={kind: sum(f['kind'] == kind for f in frames) for kind in ['checkpoint', 'skinned', 'alias']},
                        maxDependencyDepth=max(f['depth'] for f in frames))
    accounted = set()
    for clip in verification['clips']:
        ids = set(s['id'] for s in original['clips'][clip['clip']]['frames']) - accounted
        accounted.update(ids)
        before = sum(f.get('block', {}).get('length', 0) for f in original['frames'] if f['id'] in ids)
        after = sum(f.get('block', {}).get('length', 0) for f in frames if f['id'] in ids)
        clip['archiveBytes'] += after - before
    verification['seekOptimization'] = {'promotedPoses': len(reasons), 'archiveBytesBefore': len(previous_archive),
                                        'archiveBytesAfter': len(archive), 'allPosesComparedToOriginalPng': True, 'checkedPoses': len(frames),
                                        'timelineUnchanged': True, 'elapsedSeconds': round(time.monotonic()-started, 3)}
    (destination/'verification.json').write_text(json.dumps(verification, indent=2)+'\n', encoding='utf-8')
    (output/'checkpoint-promotions.json').write_text(json.dumps({'version': 1, 'sourceArchiveSha256': original['archiveSha256'],
                 'resultArchiveSha256': next_metadata['archiveSha256'], 'promotions': promotions,
                 'verification': verification['seekOptimization']}, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(verification['seekOptimization'], indent=2), flush=True)


if __name__ == '__main__':
    main()
