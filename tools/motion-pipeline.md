# Offline motion interpolation

`motion-interpolate.cjs` uses the existing **RIFE 4.6 / ncnn Vulkan** model. It does not implement an optical-flow model or generate poses. The desktop app only consumes the resulting PNG files; it does not need a GPU model, Node.js, or network access.

## Dependencies

- Node.js 22.15+ / 24 (the setup script uses built-in ZIP CRC32 support).
- Sharp 0.35.5, already declared by the repository. Use `npm install`, or the app's bundled `NODE_PATH`.
- Official RIFE executable/model, installed by `node tools/motion-setup-rife.cjs` into `.tools/motion-rife`.
- The downloaded program is MIT licensed; its LICENSE and README are retained beside the executable. `provenance.json` records the exact official source URL, ZIP CRC32 checks, and each extracted file's SHA-256. No publisher SHA-256 was supplied by this historical release; recorded hashes are reproducibility evidence, not an independent signature.

Setup requests only the necessary archive byte ranges, about 12 MiB compressed instead of the full 412 MiB release. Existing complete members are verified and reused. Failed ranges are cached for a retry. An optional `RIFE_DOWNLOAD_IPV4` can select a GitHub CDN address when local DNS routing is unreliable; TLS verification remains enabled. This machine successfully used `185.199.111.133` during setup.

## Use

Create a job JSON with absolute input filenames and a fresh output directory:

```json
{
  "frames": ["C:/project/keys/00.png", "C:/project/keys/01.png"],
  "outputDirectory": "C:/project/artifacts/motion-example",
  "factor": 64,
  "gpu": 0,
  "temporalTta": true
}
```

Then run `node tools/motion-interpolate.cjs job.json`, or import its `interpolate(config)` function. The factor is an integer from 2 through 1024. All input frames must share canvas size, alignment, and **equal phase spacing**; duration/easing is assigned later by the packaging tool. Output files are `00000.png`, `00001.png`, etc. Source keyframes are copied exactly at every factor-th position.

On this computer GPU 0 is the RTX 5070 Laptop GPU, GPU 1 is the integrated Intel GPU. The first real-asset check generated 41 usable transparent frames from 6 keys at factor 8 with temporal TTA in 7.3 seconds, including both matte passes and packaging. Timing depends on sequence and system load.

## Time and alpha correctness

The upstream 20221029 `src/main.cpp` lines 713–718 use `fx = i * inputCount / outputCount`. Therefore the wrapper requests `N * factor` outputs and discards the repeated final-pose tail, retaining `(N - 1) * factor + 1`. Before reinserting exact PNGs, it asserts that **every original keyframe's decoded black-matte RGB matches the corresponding RIFE output pixel for pixel**. A mapping failure stops the job rather than introducing periodic jumps.

RIFE is RGB-only. The wrapper interpolates the same keys against black and white, recovers alpha from their difference, unpremultiplies the black pass, and removes alpha values 0–3. This is not a linear dissolve. Both pale and dark backgrounds must still be visually reviewed: inconsistent inferred motion between matte passes can produce fringe artifacts. Original endpoints preserve their original alpha exactly.

`interpolation-report.json` records input count, output count, dimensions, factor, timing and all keyframe mapping assertions. Working RGB passes and GPU logs remain in `work/` for inspection. Use a fresh output directory for another job so older PNGs cannot contaminate counts.

## Quality limits and rejected fallback

Large semantic changes need more generated keys. For example, interpolate open eyes through the existing half-closed and near-closed eye poses before fully closed eyes. A direct open-to-closed interpolation can produce an unnatural black eyelid shape.

An explicit `engine: "ffmpeg"` diagnostic fallback is retained for comparison. FFmpeg 8.1.1 `minterpolate` was tested on the original assets and produced visible head-decoration ghosts. **It was rejected for final pet animation**. It is not selected automatically, including when RIFE is missing or fails.

Research/smoke evidence is under `artifacts/motion-rife-smoke`: `rise-v2-contact.png` (pale/dark alpha checks), `rise-v2/interpolation-report.json`, and the rejected `ffmpeg-contact.png` comparison.
