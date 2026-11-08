# Approved firefly v3 runtime assets

The approved ten-second, 60 fps preview is exported as two synchronized lossless PNG tracks. The cat remains a 512 × 512 image. The insect uses a separate transparent 640 × 640 canvas, positioned at `(-64, -100)` relative to the cat canvas. Both tracks have exactly 600 slots and last 10,000 ms.

## Rebuild and verify

From the repository root, with the locked Node dependencies installed:

```powershell
node tools/build-firefly.cjs
node tools/build-firefly.cjs --verify
```

The builder reads only tracked source files under `assets/strawberry-cat/sources/firefly-v3`, the existing `assets/strawberry-cat/frames/fall_asleep-00.png`, and this approved script archive. It never reads the ignored `artifacts` directory. The archived `approved/render.cjs` records the preview compositor for provenance; the runtime exporter reproduces its fly transform and the same body → eyes → original-paw composition.

`approved/` contains the unchanged approved body, eye, geometry, choreography and compositor scripts. The executable copies in the asset sources differ only in input paths. The builder checks that restriction before exporting. The calibrated eye geometry is kept as input rather than refitted during each build.

The shared original idle file is part of the input inventory. Every source, output PNG and decoded RGBA buffer is recorded by SHA-256. A source change invalidates verification and requires a deliberate rebuild.

## Runtime handoff

`../firefly-v3-runtime.json` contains:

- `cat.frames` and `overlay.frames`: `{file, durationMs}` arrays, with paths relative to `assets/strawberry-cat`, ready to adapt into the application manifest.
- `overlay.width`, `height`, `offsetX`, `offsetY` and `clickThrough`: decoration-window geometry and interaction requirements.
- `slots`: synchronized indices and start times for both tracks.
- `stats`: unique image counts, shared-image slots, encoded byte size and rear-occlusion coverage.

For slot `i`, the duration is `round((i + 1) × 1000 / 60) - round(i × 1000 / 60)`. No animation timing, body motion, eye geometry, pupil treatment, sprite angle or glow pulse has been changed.

Every raw frame is hashed before encoding. Identical cat frames reference the same file, and the first and last cat slots reference `frames/fall_asleep-00.png` directly. All fully transparent insect slots reference one transparent image. Unique frames use PNG compression level 9 with adaptive filtering and no palette quantization. Each PNG is decoded and compared byte-for-byte to its source RGBA buffer.

During the rear-emergence segment, insect alpha is multiplied by `(1 - cat alpha)` using the same frame's cat pixels. This pre-occlusion makes a front, click-through decoration window hide the insect behind the cat. Fully opaque cat pixels remove the rear insect completely; partially transparent antialiased pixels attenuate it continuously. The overlay contains only the insect, never a second copy of the cat.

## Verification records

- `source-inventory.json`: required input paths, hashes and source fingerprint.
- `frame-pool.json`: unique encoded files, dimensions, byte counts and encoded/raw hashes.
- `verification.json`: synchronized timing, endpoints, archive integrity and lossless roundtrip results.

The copied body rig also writes its small diagnostic mask and forearm images under its own source directory when initialized. These diagnostics do not participate in frame playback or source fingerprints.
