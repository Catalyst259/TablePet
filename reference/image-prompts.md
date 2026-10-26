# 草莓猫素材来源与制作记录

本文保留0.1.0制作历史。当前0.2.0使用的新源图在 `assets/strawberry-cat/sources/motion-v2`，逐字内置image_gen提示词在 `reference/motion-v2-prompts.json` 和 `reference/motion-v2-followup-prompts.json`；后续用现成RIFE4.6生成过渡。当前验收见 `docs/MOTION-QA.md`，旧清单/预览保存在 `reference/motion-v1`。

本轮使用 Codex 内置 `image_gen`，没有调用外部 CLI 或另行配置 API key。角色参考来自用户提供的草莓灰白猫图片，副本保留在 `assets/strawberry-cat/sources/original-reference.png`。用户明确选择了较低趴坐待机、触碰后由前爪撑起前半身的动画路线。

## 源图与最终选用

源图保留在 `assets/strawberry-cat/sources`：

- `full-sheet-draft.png`：首次 6×6 动作草图，分辨率不足且格间有杂点，没有进入运行时。
- `blink.png`、`fall_asleep.png`、`sleep_loop.png`、`wake.png`、`rise.png`、`settle.png`：内置生图生成的六组 3×2 图集，每组六帧，画布为 1536×1024。
- `blink-v2.png`：针对第一版眨眼图集尾巴跨格问题进行的重新生成，作为最终眨眼帧来源。

最终运行包使用 8 个片段、39 个时间轴帧槽，共引用 18 张不同的 512×512 RGBA PNG。所有 36 张切分帧仍保留在 `frames` 中，源图没有删除。

待机选用 `fall_asleep-00.png`，睡眠基准选用 `sleep_loop-00.png`，半起身保持选用 `rise-05.png`。眨眼的首尾复用待机，入睡末尾复用睡眠基准。独立生成的 wake 中包含先抬高再重新趴下的动作，最终改用入睡序列倒播；settle 改用 rise 倒播。这样使每个片段边界共享完全相同的像素，也保留低头闭眼、睁眼抬头、撑起和回位的合理方向。

## 可重复的排布

运行 `tools/prepare-assets.cjs` 可从保存的源图重新构建切帧、manifest、图标、联系表和预览 GIF。脚本使用 Node.js 与 Sharp，实际切图和预览环境为 Codex bundled runtime。该脚本只裁切、清理低透明度杂点、统一缩放及排布，不绘制或合成新的猫咪姿态。

统一画布 512×512；鼻子横向锚点为 x=232，脚底基线为 y=478。独立生成的 rise 组整体比其他组小，因此整组乘以 1.04 校正；每组内部的缩放比例固定，不按每帧包围盒分别缩放。blink-v2 附带的低透明度地影已从 alpha 中清除，并重新按脚底对齐。

预览和可检查数据位于 `assets/strawberry-cat/previews`：

- `key-poses.png`：待机、睡眠、半起身三姿态。
- `animation-preview.gif`：按 manifest 时长播放的完整演示，演示中缩短了等待入睡时间，不代表用户配置的默认 60 秒。
- `animation-contact-sheet.png`：最终选用序列，逐帧标出时长，并交替使用浅深背景检查透明边缘。
- `asset-qa.json`：最终选用 PNG 的画布、alpha 像素数量和不透明包围盒。

## 生成提示词记录

前一素材制作会话没有将最初六组的逐字提示词保存到仓库，因此下面的初稿说明仅为制作要求重建，不能视为原始调用日志：保留原型脸型、蓝眼睛、灰白毛色、草莓奶油与甜筒，制作统一画布、低趴待机及前半身撑起的 RGBA 帧动画；每个动作分为六帧，按三列两行排列，要求透明背景、统一大小和脚底位置。

最后一次 blink 修复调用的提示词全文如下，输入为 `sources/blink.png`，参数 `transparent_background=true`：

```text
Use case: precise-object-edit. Asset type: production desktop pet 6-frame PNG blink sprite sheet. Edit the supplied 3-column by 2-row sheet. Preserve exactly the same cute gray-and-white strawberry-cream-waffle cat identity, drawing style, blue eyes, low crouched front-facing body and raised tail. The only essential repair is generous spacing: make all six cats SMALLER within their equal cells so that each entire cat including tail and strawberry fits fully with at least 60 pixels transparent clearance on all four cell edges. No cat or outline may touch a cell boundary or overlap another cat. Output exactly 1536x1024, strict equal 512x512 cells, exactly six cats read left to right then top to bottom. Every cat uses IDENTICAL body, decorations, tail, size, head angle and ground baseline. Change ONLY eyelids: frame1 fully open; frame2 half closed; frame3 almost closed; frame4 closed; frame5 half closed; frame6 fully open identical to frame1. Feet bottom baseline at relative y=440 within every cell. Character total width <=390 and total height <=360 inside each512cell, horizontally centered. True RGBA transparent background everywhere between and around cats; no gradient, no shadow, no panel, no text, no borders, no glow. Do not crop any part of any cat. Keep the characteristic strawberry, cream and waffle on its head.
```

该调用的原始输出保留在 `C:\Users\33776\.codex\generated_images\01a17862-bf3d-7cd1-a20e-872933e02a7f\exec-2615a3c2-3fd0-4c4e-b184-93e429136e05.png`，已复制到项目 `sources/blink-v2.png`。生成结果没有完全满足提示词中的边距和无阴影要求，因此仍需上述确定性排布与 alpha 清理。

## 视觉检查结论

已经检查浅色和深色底上的最终联系表与关键姿态：主体、耳朵、尾巴和头饰都处于画布内，没有明显地影或邻格残片；脚底保持在同一基线。各动画边界直接引用相同 PNG。AI 生成造成的细微脸型、毛尖和装饰变化仍可在逐帧放大时观察到，当前属于首版素材，不宣称已经达到人工逐帧精修的一致性。
