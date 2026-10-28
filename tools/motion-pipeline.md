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

## 0.2.2 sleeping breath

`node tools/touchup-sleep.cjs` rebuilds only sleeping breathing. The normalized image_gen master and complete recipe are in `assets/strawberry-cat/keyframes-v4/sleep`. It requests 1024 RIFE phases and samples only the first quarter, producing a restrained six-pixel head/shoulder rise on a 512-pixel canvas. The 3.6-second loop retains a 240 Hz timeline and the original sleeping endpoint.

Very small RIFE phases can contain a nonzero neural reconstruction residual. In this case the first inferred phase visibly differed from the exact source, despite both keyframe mapping checks passing. `sleep-endpoint.cjs` uses the existing **FFmpeg 8.1.1** floating-point `premultiply`, `blend`, and `unpremultiply` filters with a smoothstep weight only over phases 1–15; phase 0 stays exact and phase 16 onward remains unchanged. The correction operates on subpixel endpoint displacement, not a dissolve between the complete sleeping and inhaling poses. FFmpeg must be available in PATH for offline rebuilding; the delivered app does not depend on it.

`node tools/qa-sleep-breathing.cjs` checks the original-rest-to-first-inferred-frame transition separately from the repeat boundary, alongside two-cycle 120 Hz sampling, grounded paws, head/decoration displacement, alpha validity and unchanged non-sleep assets. The default comparison source is the preserved 0.2.1 publish folder; its optional second argument can point to an extracted 0.2.1 portable release.

## 0.3.1 V9 click reaction (current workflow)

The frozen runtime has automatic **blink/yawn** (4:1), and equal-probability **rise/wink** click reactions. The 1500 ms Wink uses the exact V9 artwork: 440 ms closing, 350 ms held expression, 330 ms reopening and 380 ms recovery. No new gaze motion is authored. Its recipe is `reference/idle-v9-fluid-selection.json`; approved normalized-key hashes are in `reference/idle-v9-approved-keys.json`.

`npm run assets:idle` stages a Wink candidate from the saved V9 sources. The two-pass RIFE pipeline subdivides each original pose interval into eight smaller intervals; `wink-endpoint.cjs` removes endpoint residual without fading out real motion. Monotone timing is sampled by measured eyelid travel. `npm run assets:idle:install` imports the staged result after checking the original keys and idle endpoints, and verifies that all other clips remain unchanged. This rebuild needs the offline RIFE tool, Node/sharp and FFmpeg; compiling or running the saved release needs none of them.

`npm run assets:idle:check` verifies `reference/releases/frozen-0.3.1.json`. `npm run assets:idle:preview` regenerates the standalone manifest-driven preview, with idle and click actions in separate groups. Its HTML and referenced runtime PNGs work without a server. The `artifacts` directory contains disposable inference/diagnostic caches only; no historical cache is required to begin rebuilding.

The older `assets:prepare` and v7 builder commands below reconstruct historical releases and are not the 0.3.1 publication pipeline. To reproduce the frozen app, use `tools/build.ps1 -Test -Publish` with the committed runtime PNGs.

## 0.3.0 frozen actions (archived workflow)

The approved idle pool is now **blink, yawn, wink**, with weights 4:1:1. The active manifest has no happy clip or happy metadata. The v7 builder and source selection only accept yawn/wink and explicitly remove a stale happy clip when packing. Their approved PNGs, key poses and timings remain unchanged from preview.3; the ordinary blink is unchanged as well. QA checks this three-action pool and the stable `0.3.0` revision.

At that freeze, `assets:idle`, `assets:idle:check` and `assets:idle:preview` selected the v7 workflow. `keyframes-v7/recipe.json` preserves those two generated-action recipes; `recipe-preview.3.json` preserves the previous three-action recipe as history. Earlier candidate artwork and preview snapshots, including happy, are kept only as production history and are not active playback entries.

## 0.3.0-preview.3 complete idle gestures (archived design)

`npm run assets:idle` runs `build-idle-v7.cjs`; `assets:idle:check` runs `qa-idle-v7.cjs`. The full `assets:prepare` pipeline also invokes v7 after rebuilding the original motion clips. `assets:idle:preview` reuses the manifest-driven HTML/GIF generator. Commands in the archived section below describe preview.2 only.

Complete transparent image_gen poses are selected in `assets/strawberry-cat/sources/idle-v7/selection.json`. Prompts and rejected-candidate decisions are saved in `reference/idle-v7-{happy,wink,yawn}-prompts.json`. `normalize-idle-v7.cjs` registers the complete artwork using only the two front-paw regions; it does not align the nose, mask facial changes into the old body, draw eyes, or warp a pose. Near-transparent generated noise is cleared at alpha≤3, matching the existing dual-matte recovery rule. Original pixels with alpha>3 are preserved through that cleanup.

Yawn lasts 4400 ms and adds a small head/chest lift. Happy lasts 1600 ms and adds cheek/shoulder movement without deliberate head tilt. Wink lasts 1400 ms (360 ms closing, 600 ms holding, 440 ms reopening), with a restrained head/shoulder inclination and a relaxed companion eye. All three use RIFE 4.6 temporal TTA, factor 64, a 240 Hz timeline and the existing three-phase FFmpeg endpoint correction. Complete RGBA contours move together, and both action boundaries use the exact original idle PNG.

The frozen baseline lives in `reference/idle-v7/baseline`, so cloning the source does not require a previous machine's ignored `artifacts` directory. Registration and packing verify all eight approved clips and their 1216 PNG hashes before changing the active manifest. Outputs use source fingerprints under `frames/v7`; inference caches and diagnostic reports remain in `artifacts/idle-v7`. Use `node tools/build-idle-v7.cjs --normalize-only` to inspect keys or `--stage-only` to prepare a candidate manifest before activation.

`qa-idle-v7.cjs` checks protected hashes, exact idle endpoints, duration, transparency, clipping, paw stability and non-facial movement, and exports full-body and eye close-up contact sheets. Visual review must separately inspect eye taper, happy head direction, companion-eye response, highlight transitions and unintended silhouette inflation. Numeric checks do not certify appealing motion. Runtime/build dependencies remain unchanged; the app uses saved PNGs offline.

## 0.3.0-preview.2 idle expressions (archived workflow)

`npm run assets:idle` adds yawn, happy and wink while preserving the approved eight clips. Original yawn outputs and prompts remain in `sources/idle-v5` and `reference/idle-v5-prompts.json`. Revised happy/wink outputs, including rejected candidates, are in `sources/idle-v6`; complete prompts and selection records are in `reference/idle-v6-happy-prompts.json` and `reference/idle-v6-wink-prompts.json`. The existing Sharp registration is reused by `normalize-idle-v6.cjs`, with an eyes-only matte that keeps the original nose/mouth, ears, body, paws, tail and decoration fixed. Final registered keys and the full recipe are in `keyframes-v6`.

`build-idle-v5.cjs` uses the existing RIFE 4.6 dual-matte interpolation at factor 64 and samples a 240 Hz timeline. Yawn retains its original keys with every time knot doubled (4400 ms). Happy lasts 1500 ms (380 ms closing, 650 ms holding, 470 ms reopening). Wink lasts 1060 ms (180 ms closing, 600 ms holding, 280 ms reopening). Both eyes respond in the wink. The first and last slots use the exact original idle PNG. Standard FFmpeg float-alpha correction remains limited to the three inferred phases next to each exact key; fixed pixels are restored afterward. The click-to-react return drops consecutive duplicate expression frames so the long hold does not consume the brief return, while the source action's deliberate hold remains intact.

`npm run assets:idle:check` compares all original PNGs against the fixed `v0.2.2` Git tag and checks alpha, idle boundaries, fixed landmarks, protected nose/mouth, yawn half-speed timing, wink hold/companion-eye response and 120 Hz frame changes. Numeric checks flag defects for visual review; they do not establish artistic quality. `npm run assets:idle:preview` builds the independent local HTML page and a four-panel 50 fps GIF, verifying its encoded duration. Current RIFE work and QA reports stay under `artifacts/idle-v6*`; prior cache files can be reused by source fingerprint. The runtime needs only the saved PNGs and manifest, without image generation or interpolation dependencies.
