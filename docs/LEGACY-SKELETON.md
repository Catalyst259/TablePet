# Legacy motion: skeletal prediction with lossless correction

The frozen release's older movements were produced by RIFE, which changes texture and alpha as well as pose. A small skeleton cannot represent all those new pixels exactly. This implementation therefore stores genuine skeletal motion **and** explicit lossless raster corrections. It is not a claim that these movements can be represented by bones alone.

## Representation

`tools/compile-legacy.py` reads the frozen source repository without writing to it. It processes the eleven selected legacy clips: idle, blink, fall_asleep, sleep_loop, wake, rise, hold, settle, yawn, wink and meow. The selected current manifest, including each exact original filename and slot duration, is the reference. No candidate art is promoted.

Ten named bones cover torso, head, left/right ear, left/right eye, muzzle, tail and left/right paw. The bind mesh has 512×512 vertices, with the three strongest anatomical elliptical influences at each vertex. Integer weights sum to 256. Parent relationships describe the anatomy; the fitted animation stores world-space affine matrices per bone, so a local bone fit can account for expression deformation independently of the torso.

For each new pose, backward optical flow estimates correspondence to its reference pose. Weighted regularized least squares fits an affine transform per bone. The runtime performs inverse linear-blend skinning, using 16.16 matrix coefficients, 8-bit subpixel coordinates and an integer bilinear sampler. Python and C# use the same rounding and zero-outside-canvas rules. This is actual bone-driven geometry: changing a matrix changes the predicted pose.

The compiler subtracts the predicted RGBA from the frozen target modulo 256. Its residual is stored only in the changed rectangle, decorrelated reversibly as R−G, G, B−G, A and Brotli-compressed. Residuals include alpha and even RGB under zero alpha; final output is byte-for-byte identical, not merely visually similar. The `RenderPrediction(file)` review API omits the current residual while using the exact reference pose, making the contribution of bones and correction directly inspectable.

At most twelve dependent skinned poses follow an independent checkpoint. The final pack has 80 checkpoint textures and 628 skinned residual poses, covering 708 original PNG paths. A checkpoint is explicitly a raster pose; it is not counted as a successful skeletal approximation. Shared filenames and phase reuse remain shared, including the shared idle/asleep/hold endpoints and reverse wake motion.

Fourteen cross-clip junctions and nine cold entry poses were promoted from the initial 57 checkpoints after real-time playback exposed repeated dependency reconstruction in reverse wake and cold fall-asleep motion. The conversion adds only 787,800 compressed bytes and leaves every decoded pixel and timeline slot unchanged. Each dynamic clip's fourth distinct pose (the first beyond the three WPF-warmed images) is independently decodable. The original fitted bones for these promoted poses remain reviewable in `reference/skeletal/checkpoint-promotions.json`. This avoids reserving unrelated entry poses in the 24 MiB dynamic LRU, which reduced its useful working set during diagnosis.

## Measured first build

The initial Brotli quality-9 build used 129,520,749 bytes of original referenced PNGs and produced a 64,898,638-byte archive plus 541,560-byte metadata, a 49.5% reduction for this legacy subset. These are the initial codec measurements, not a claim about a later quality-11 recompression or the complete application.

| Archive component | Compressed bytes | Count |
|---|---:|---:|
| Skeleton vertex binding | 309040 | 1 |
| Independent checkpoint textures | 7709062 | 57 |
| Skeletally predicted pose residuals | 56880536 | 651 |

Thus residuals account for 87.6% of the initial archive. The mean alpha-weighted visible RGB prediction error before correction was 2.10 intensity levels out of 255. That mean is not a worst-case guarantee and is not sufficient to call bone-only output equivalent. After correction, every RGBA byte matches. Per-pose prediction errors and fitted matrices are inspectable in `assets/strawberry-cat/skeletal/legacy/legacy.json`.

## Runtime memory and performance

The final quality-11 archive, including the 23 seek/entry checkpoints, is 56,266,293 bytes, plus 530,921 bytes of metadata (56.15% smaller than this legacy subset's PNGs). It comprises 246,406 bytes of binding, 9,309,576 bytes of checkpoints and 46,710,311 bytes of residuals. Residuals therefore remain 83.02% of this subset's archive. The full package size and final playback measurements are recorded separately in `reference/skeletal/`; the quality-9 table above is preserved as the initial baseline.

`LegacyRenderer` keeps the compressed archive on disk and reads individual blocks at their recorded offsets. The decoded pose LRU is bounded to 24 MiB; the fixed vertex binding occupies 3 MiB. Temporary decode buffers, metadata and caller-owned output arrays are additional allocations. The renderer serializes cache/seek operations with a lock so two background prefetch workers cannot corrupt dependencies. Every returned raster is a fresh array, preventing callers from mutating cached poses.

A conservative transparent-canvas optimization bounds all bone displacements over the canvas corners. It skips only pixels that provably cannot sample a nonzero source byte, including hidden RGB. Two disjoint row partitions evaluate the integer skin in parallel without changing output ordering or allocating another canvas. These optimizations passed the full raw hash comparison again.

The historical quality-9 Release probe measured 708 sequential renders in 6.85 seconds, median 7.97 ms and p95 9.87 ms per pose. Random-seek p95 was 105.9 ms because a cold seek may reconstruct up to twelve predecessors. These are initial isolated CPU decode measurements, not final playback or display-frame-rate guarantees. Final reports are `reference/skeletal/legacy-runtime-verification.json` and `playback-skeletal-60hz.json`; the application's entry-frame warming and background prefetch remain necessary.

## Verification and reproduction

The Python compiler performs a compressed round trip and an exact RGBA reconstruction check for every pose. The independently implemented C# renderer additionally validates archive, block and final pose SHA-256 values. The probe checks all 708 poses, shuffled seeks, two-worker access, cache bounds and mutation isolation. Final results are in `reference/skeletal/legacy-runtime-verification.json`; source-side build statistics are in `assets/strawberry-cat/skeletal/legacy/verification.json`.

Build-time dependencies are pinned in `tools/compile-legacy-requirements.txt`. They are not needed by the distributed app.

```powershell
npm run assets:compile -- C:/Users/33776/Documents/ChatGPT/TablePet

dotnet run --project tools/compile-legacy-probe/LegacyProbe.csproj -c Release -- `
  assets/strawberry-cat reference/skeletal/legacy-runtime-verification.json
```

The compiler rejects an output directory inside the frozen source repository. The runtime does not require that repository, Python, OpenCV, Brotli's Python package or the original PNG sequence.
