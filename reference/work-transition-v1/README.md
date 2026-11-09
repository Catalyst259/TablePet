# 待机与工作之间的过渡

2026-11-09。沿用已确认的工作第三版，只补待机进入工作、工作返回待机的独立素材和预览，尚未接入 WPF 应用。

预览目录：`assets/strawberry-cat/work-transition-v1/`。

- `index.html`：完整演示及“开始工作 / 返回待机”交互，支持暂停、半速、深浅背景。
- `work-transition.gif`：384×384、297 帧、9.9 秒、无限循环。
- `work-transition.mp4`：512×512、594 帧、60fps、9.9 秒。
- `transition-contact.png`：上排进入、下排退出，0/100/200/300/400ms。
- `manifest.json`：400ms enter、400ms 示例 exit、9.9 秒 demo 的透明 PNG 时间线。
- `gaze/manifest.json`：33 个眼神相位；文件路径相对 gaze 目录，0 为原待机，32 为已确认工作睁眼。
- `verification.json`、`gaze/verification.json`：素材与边界校验。

眼神直接由原 idle 与已确认 focused 素材通过 RIFE 4.6 插帧，反向复用。没有重新生成角色或道具。过渡只允许眼部变化，整图 alpha 及眼外 RGBA 与原图完全一致。RIFE 黑底推理 RGB 用于不透明眼区，原 alpha 保留；两端残差按四相位窗口校正，避免端点跳变。

进入时，400ms 内眼神从待机切到专注，电脑和 Working 小标同步淡入；结束时精确接工作循环的原点。退出时，如果当前处于眨眼，等待本次睁眼后立即退；否则立即退。道具淡出、眼神反向切回待机，身体继续当前呼吸时钟并乘以 `1 − S(elapsed/400)`，其中 `S(u)=6u^5−15u^4+10u^3`。这使任意起伏相位的位移、速度、加速度连续收回零，避免跳回地面。

`tools/work-transition-motion.cjs` 是预览与离线导出的共用时间逻辑。示例 exit 帧以工作时间 7075ms 为起点；正式接入时不能把该固定示例强行接到任意相位，应按当前呼吸相位使用共用退出公式和独立道具层。

完整演示在 1000ms 进入，1400ms 开始工作；8200ms（工作第 6800ms，眨眼中）请求退出，8475ms 睁眼后立即退出，8875ms 回原待机。结尾停留至 9900ms，方便观看并循环。

已验证：

- 进入起点、进入终点、返回待机和完整演示接缝像素精确。
- 已确认工作循环的代表帧合成后像素一致，源素材和正式 manifest 构建前后不变。
- 32 个状态边界场景通过，包括眨眼起止、重复退出、跨循环、低频采样和重新开始。
- 385 个呼吸相位、31,185 个退出采样都保持在 −2～0px；两端连续性数值检查通过。
- 视频帧率、帧数、时长通过；已目视检查眼神插帧和深浅背景过渡采样。
- HTML 的 demo → 手动切换保留工作时间，按钮和倍速共用同一时间轴，已做代码审查。

浏览器工具的 URL 安全策略拒绝打开本地 `file://` 预览，因此未执行浏览器自动点按验收；未尝试绕过该限制。以上不等同于应用内运行验收。

重建：

```powershell
node tools/build-work-gaze-transition.cjs
node tools/build-work-transition-preview.cjs
```

需要现有 Node.js、Sharp、FFmpeg / FFprobe 和 `.tools/motion-rife`。推理及视频序列缓存位于 `artifacts/work-transition-v1/`。
