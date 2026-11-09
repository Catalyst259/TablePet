# Work mode production assets

Install: `node tools/install-work-assets.cjs`

Read-only verification: `node tools/install-work-assets.cjs --verify`

The selected source artwork is work-preview-v3 and work-transition-v1/gaze (reference/work-artwork-selection.json). Production PNGs are copied without re-encoding to `assets/strawberry-cat/frames/work-v1/`. Only the four work clips are inserted into the current application manifest; existing clips, idleActions, decorations, version and other metadata retain their exact content and text.

| Clip | Slots | Duration | Layer |
| --- | ---: | ---: | --- |
| work_enter | 24 | 400 ms | Idle-to-focused cat, quintic gaze timing |
| work_loop | 576 | 9600 ms | Focused cat and approved blink; no body offset or props |
| work_exit | 24 | 400 ms | Focused-to-idle cat, quintic gaze timing |
| work_props | 1 | 17 ms, loop | Transparent computer and Working sign |

All slots are 16 or 17 ms. `work_enter` begins with the original idle and ends at `frames/work-v1/focused.png`; `work_exit` has the opposite endpoints. The loop starts and ends with that same focused PNG.

Core/WPF owns whole-cat vertical translation, `dy(t)=cos(2*pi*t/4800)-1` in 512px source coordinates. Props stay fixed and fade with the 400 ms quintic transition. Exit can start at any breathing phase after an active blink completes; continue that clock while damping its offset to zero. These behaviors are intentionally absent from the exported cat PNGs.

Verification confirms source and copy SHA-256, exact copied RGBA, exact clip timing and joins, unchanged non-work manifest content/text, and 33 representative cat + translation + props reconstructions matching the selected artwork pixel-for-pixel. 41 PNGs are imported; 42 PNGs are referenced including the existing idle. Details are in `verification.json`.
