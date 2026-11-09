# 工作循环第三版：专注下视

2026-11-09。工作时双眼微微看向电脑，上眼睑略收，呈现温和、清醒的专注感。趴姿、电脑位置与第二版的整猫呼吸保持一致。

预览文件在 `assets/strawberry-cat/work-preview-v3/`：

- `index.html`：60fps 时间线预览，可暂停、拖动、半速及切换背景。
- `work-loop.gif` / `work-loop.mp4`：9.6 秒循环的方便观看版本。
- `master.png`：专注眼神工作静帧；`eyes-comparison.png`：待机眼神、工作眼神、闭眼对比。
- `motion-contact.png` / `blink-contact.png`：整段循环与眨眼过渡采样。
- `manifest.json` / `timeline.json` / `frames/`：透明 PNG 与 60fps 时间线。
- `verification.json`：构建核验结果。

眼神使用内置 image_gen 修改，生成原图保存在 `assets/strawberry-cat/sources/work-v3/focused-eyes.png`，完整提示词与三个参考图路径见 `prompts.json`。下视参考是萤火虫动作 5.1 秒的猫帧。只提取生成图的两眼及周边过渡区域，眼部遮罩外严格保留原始 idle RGBA，避免重新生成带来的身体、头饰或脸部轮廓变化。遮罩及来源散列记录在 `keys.json`。

闭眼复用原 `frames/blink-03.png` 的眼部，使用 RIFE 4.6 制作“专注睁眼 → 闭眼 → 专注睁眼”。每次眨眼仍为 375ms，约 140ms 闭合、17ms 闭眼、余下时间睁开；睁眼后保持新的下视方向。身体时钟始终独立连续：最大上移 2px、4.8 秒一轮，电脑与 Working 标牌固定。

上述 17ms 指精确闭眼关键帧的保持时间；其前后插值帧也会逐渐接近闭眼。已核对眼部采样和浅深背景采样。构建检查全部通过：65 个眨眼相位的眼外像素一致、三个关键帧 RGBA 精确、眨眼后恢复新眼神、身体时钟连续、固定道具与遮爪正确、虚拟循环终点与起点一致、输入散列未变。最终输出 576 个时间槽、189 张引用 PNG；视频 512×512 / 60fps，GIF 384×384 / 288 帧，两者均为 9.6 秒。

重建：

```powershell
node tools/prepare-work-v3.cjs
node tools/build-work-preview-v3.cjs
```

依赖现有 Node.js、Sharp、FFmpeg / FFprobe 与 `.tools/motion-rife`。重建只读取已保存的生成图，不调用图片服务。此版仍是独立候选预览，未接入应用工作模式；第一、二版保留供比较。
