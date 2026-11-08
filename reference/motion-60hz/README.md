# Reproducible 0.3.4 / 60 fps runtime

`source-manifest-0.3.3.json` is the complete 0.3.3 runtime before conversion, including the approved firefly character and decoration tracks. `source-png-inventory.json` records every PNG referenced by that source, its encoded byte count and SHA-256. The converter pins both archive hashes. This is a conversion archive, not a new release freeze or a new visual approval.

From a source checkout with the original assets present:

```powershell
node tools/resample-motion-60hz.cjs --self-test
node tools/resample-motion-60hz.cjs --write
node tools/resample-motion-60hz.cjs --verify
```

The write mode accepts only the archived 0.3.3 manifest or its exact reproducible 0.3.4 result. It rejects unexpected/newer manifests and concurrent edits. Verify mode is read-only. Neither mode generates, re-encodes, deletes or rewrites PNGs; all 2,211 source-referenced PNG hashes are checked, including images omitted from the runtime manifest.

## Timing and action policy

For the eleven legacy clips, slot starts use `round(n × 1000 / 60)`. The frame visible at that original timestamp is selected. The final end is clamped to the original duration, and the final original pose is retained. This preserves exact durations, first/last images and loop flags. The 375 ms blink has 23 slots with an 8 ms final slot. Static `idle` and `hold` remain one 1,000 ms slot each.

The firefly character's 600 slots and its decoration's 600 slots are copied unchanged, including all file paths, durations and canvas/offset metadata. Each track still lasts exactly 10,000 ms. The converter does not resample them.

The manifest declares revision 0.3.4, schema version 9 and runtime sampling at 60 fps. The idle pool is blink ×2, yawn ×1, rise ×1 and firefly ×1. Rise remains the controller-managed rise → hold → settle sequence. Single clicks choose wink or meow with equal probability; double clicks use meow. Behavior execution remains the responsibility of the Core controller.

Old generation details are retained as authoring provenance and in the source archive. The current statistics describe the selected 60 fps files. No old visual-approval statement is presented as approval of newly interpolated artwork, since no new artwork is generated.

## Conversion result

| Measure | Before | After |
| --- | ---: | ---: |
| Character slots | 2,507 | 1,555 |
| Decoration slots | 600 | 600 |
| Total slots | 3,107 | 2,155 |
| Runtime-referenced PNGs | 2,211 | 1,644 |
| Runtime-referenced PNG bytes | 319,043,975 | 214,309,748 |
| PNG files retained in source `frames` | 5,127 | 5,127 |
| PNG bytes retained in source `frames` | 860,690,823 | 860,690,823 |

The runtime references 567 fewer PNGs, saving 104,734,227 encoded bytes (about 99.9 MiB) in a manifest-filtered production package. Source artwork remains on disk. Byte counts are file lengths, not filesystem allocation sizes.

`conversion-report.json` contains every clip's before/after counts, exact durations, source hashes and preservation checks.

Historical 120 Hz conversion, firefly/Wink/Meow installation and v1/v2 production builders now reject a version-9-or-newer manifest. They cannot silently restore the old frame rate or action policy. The existing 0.3.1/0.3.2 freeze records remain unchanged; the 0.3.4 freeze entry clearly reports that this release has not been frozen.
