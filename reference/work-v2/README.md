# 工作循环第二版

2026-11-09。按最新反馈保留原来的趴坐姿势，电脑屏幕朝猫、背面对观众并遮住两只前爪。整只猫缓慢小幅起伏，眨眼期间持续运动；电脑和 Working 标牌固定。

本目录记录独立动画候选，尚未接入应用工作模式。

- 预览：`../../assets/strawberry-cat/work-preview-v2/index.html`，支持循环、暂停、拖动进度、半速和深浅背景。
- 视频：同目录 `work-loop.mp4`，512×512、60fps、9.6 秒。
- 动图：同目录 `work-loop.gif`，384×384、约 30fps、无限循环；用于方便观看。
- 透明 PNG：同目录 `frames/`，`manifest.json` 包含 576 个时间槽、187 张引用图片；`timeline.json` 记录每槽原图、位移和时间。
- 布局静帧：同目录 `master.png`；浅深底采样：`motion-contact.png`；核验报告：`verification.json`。

猫沿用 `assets/strawberry-cat/frames/fall_asleep-00.png` 与正式清单中的原有 375ms 眨眼素材。电脑与标牌由内置 image_gen 生成；完整提示词和原输出路径在 `prompts.json`，生成源图在 `assets/strawberry-cat/sources/work-v2/`。第一张带光晕的道具图仅作为生成历史保留，采用 `props-clean.png`。

`props-layout.json` 记录裁切和摆放：源图裁切 (85,145,1680,517)，缩小到 390×120，放在 512×512 画布的 (82,382)。缩小后 alpha≤3 置零、alpha≥252 置为不透明，并清除全透明像素的 RGB，生成 `work-preview-v2/props.png`；生成源图不变。

整只猫共用纵向位移 `dy = -2 × (1 - cos(2πt/4800)) / 2`，范围 −2～0 像素，周期 4.8 秒；头、身体、尾巴和头饰不做独立旋转或缩放。亚像素位移使用预乘 alpha 插值。眨眼分别从 1250ms、6700ms 开始，与身体时钟独立。9.6 秒后自然循环，不强行复制首帧作为末帧。

已通过的检查：

- 构建前后猫原素材、读取的正式清单与道具层散列不变。
- 头、身体和尾部使用同一位移，眨眼期间位移分别继续变化约 0.463px、0.193px。
- 两个运动极值位置的两只前爪区域均完全被遮挡。
- 36,734 个道具不透明像素逐帧一致。
- 虚拟终点 t=9600ms 与起点完全一致；导出视频为 576 帧、60fps、9.6 秒。
- 已目视检查浅深背景采样，脸部无遮挡，保留原趴姿。

重建（使用已保存的道具层）：

```powershell
node tools/build-work-preview-v2.cjs
```

依赖现有 Node.js、Sharp、FFmpeg / FFprobe。脚本只写本版候选输出及 `artifacts/work-v2/`，不调用图片服务，不修改应用代码、正式清单或第一版归档。此处核验针对导出的动画素材，未进行应用内工作模式验收。
