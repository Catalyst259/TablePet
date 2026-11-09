"""Recompress an existing lossless skeletal archive without changing decoded data.

The source archive is read once and never modified. A separately named output
directory is mandatory, so an application can continue reading the source pack.
"""
import argparse
from concurrent.futures import ProcessPoolExecutor
import copy
import hashlib
import json
from pathlib import Path
import time

import brotli


def sha(data):
    return hashlib.sha256(data).hexdigest()


def recompress(task):
    compressed, digest, length, quality = task
    raw = brotli.decompress(compressed)
    if len(raw) != length or sha(raw) != digest:
        raise ValueError('Source block is corrupt')
    candidate = brotli.compress(raw, quality=quality, lgwin=20)
    selected = candidate if len(candidate) < len(compressed) else compressed
    if brotli.decompress(selected) != raw:
        raise ValueError('Recompression changed decoded bytes')
    return selected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--quality', type=int, default=11, choices=range(12))
    parser.add_argument('--workers', type=int, default=2)
    parser.add_argument('--benchmark', type=int, default=0, help='Only time this many spread-out blocks; do not write outputs')
    args = parser.parse_args()
    source, output = args.source.resolve(), args.output.resolve()
    if source == output or source in output.parents or output in source.parents:
        raise ValueError('Output must be a separate, nonoverlapping directory')
    if not 1 <= args.workers <= 8:
        raise ValueError('Workers must be in 1..8')
    metadata = json.loads((source / 'legacy.json').read_text(encoding='utf-8'))
    archive = (source / metadata['archive']).read_bytes()
    if sha(archive) != metadata['archiveSha256']:
        raise ValueError('Source archive integrity check failed')
    result = copy.deepcopy(metadata)
    blocks = [result['binding']] + [frame['block'] for frame in result['frames'] if 'block' in frame]
    if args.benchmark:
        blocks = [blocks[i * (len(blocks) - 1) // max(1, args.benchmark - 1)] for i in range(min(args.benchmark, len(blocks)))]
    tasks = [(archive[b['offset']:b['offset'] + b['length']], b['sha256'], b['rawLength'], args.quality) for b in blocks]
    started = time.monotonic()
    pieces = []
    old_size = sum(block['length'] for block in blocks)
    offset = 0
    with ProcessPoolExecutor(max_workers=args.workers) as pool:
        for i, (block, data) in enumerate(zip(blocks, pool.map(recompress, tasks))):
            block['offset'], block['length'] = offset, len(data)
            pieces.append(data)
            offset += len(data)
            if i % 50 == 0:
                print(json.dumps({'blocks': i + 1, 'total': len(blocks), 'bytes': offset, 'seconds': round(time.monotonic() - started, 2)}), flush=True)
    report = {'blocks': len(blocks), 'originalBytes': old_size, 'recompressedBytes': offset,
              'savedBytes': old_size - offset, 'quality': args.quality, 'workers': args.workers,
              'elapsedSeconds': round(time.monotonic() - started, 2), 'allDecodedBlocksExact': True}
    print(json.dumps(report, indent=2), flush=True)
    if args.benchmark:
        return
    output.mkdir(parents=True, exist_ok=True)
    packed = b''.join(pieces)
    result['archiveSha256'] = sha(packed)
    result['compressionQuality'] = args.quality
    (output / 'legacy.bin').write_bytes(packed)
    (output / 'legacy.json').write_text(json.dumps(result, separators=(',', ':')) + '\n', encoding='utf-8')
    verification = json.loads((source / 'verification.json').read_text(encoding='utf-8'))
    seen = set()
    for check in verification.get('clips', []):
        count = 0
        for slot in result['clips'][check['clip']]['frames']:
            frame_id = slot['id']
            if frame_id in seen:
                continue
            seen.add(frame_id)
            frame = result['frames'][frame_id]
            count += frame.get('block', {}).get('length', 0)
        check['archiveBytes'] = count
    verification.update(archiveBytes=len(packed), metadataBytes=(output / 'legacy.json').stat().st_size, recompression=report)
    (output / 'verification.json').write_text(json.dumps(verification, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
