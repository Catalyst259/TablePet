# 0.3.0-preview.2 表情优化版

这是本地预览版本，尚未提交、推送 GitHub 或发布 Release。公开安装版仍为 0.2.2。

## 本轮变化

- 普通眨眼保持原样。
- 哈欠所有时间点延长到上一版的两倍，完整动作 4.4 秒，等价于原来的 0.5 倍速；姿态沿用原图。
- 眯眼笑重新生成眼睑和中间姿态：下眼睑柔和抬起，上眼睑放松，最终为浅闭眼线，保留原本的小猫嘴。消除旧版下垂半睁眼到高拱笑眼的神态突变。
- 单眼眨眼约 180 毫秒闭合、600 毫秒保持、280 毫秒恢复。另一只眼轻微收窄并同步恢复，避免全程保持大眼直视。
- 点击打断长表情时，回程跳过连续重复的停留帧，从当前可见姿态在最多 240 毫秒内收回，再接起身。原动作的闭眼保持不会被缩短。
- 睡眠呼吸、入睡、唤醒、起身、蹲下、悬停只影响睡眠及点击才起身的规则保持不变。

## 打开新版

直接打开 `assets/strawberry-cat/previews/idle-actions-preview-v2.html` 查看这一版的预览快照，或打开持续更新的 `idle-actions-preview.html`。可以点播、正常速度或慢放、暂停拖动，以及切换背景。

本地便携目录为 `dist/TablePet-0.3.0-preview.2-win-x64`。双击其中的 **Start-Preview.cmd** 会请求旧实例正常退出，再运行新版；不覆盖安装目录，沿用个人设置。程序内置 .NET 运行时，无需开发环境。关闭预览版后，可以通过原桌面快捷方式运行已安装版本。

## 素材与制作

采用内置 `image_gen` 重新生成关键姿态，复用 Sharp 注册、现成 RIFE 4.6 补帧及已有 FFmpeg 端点修正。未引入新渲染引擎，运行时无需网络或生成模型。

- 原始候选：`assets/strawberry-cat/sources/idle-v6`。
- 已注册关键帧、完整时间配方：`assets/strawberry-cat/keyframes-v6`。
- 最终新笑眼/单眼帧：`assets/strawberry-cat/frames/v6`。
- 完整提示词及取舍：`reference/idle-v6-happy-prompts.json`、`reference/idle-v6-wink-prompts.json`。
- 哈欠保留 v5 的关键姿态与提示词。

舍弃了带睫毛尖角、额外眉线、灰色皱纹及压扁高光的候选。眼部合成保留原鼻嘴和身体像素。原始候选留存以便复查，不参与播放。

| 动作 | 完整时长 | 时间槽 | 引用 PNG（含公共待机帧） |
| --- | ---: | ---: | ---: |
| 哈欠 | 4400 ms | 1056 | 342 |
| 眯眼笑 | 1500 ms | 360 | 133 |
| 单眼眨眼 | 1060 ms | 254 | 76 |

整包包含 11 个片段、3732 个时间槽、1764 张引用 PNG。动画采用 240 Hz 时间轴；实际显示刷新率由屏幕和 WPF 合成器决定。GIF 是 50 fps 的查看副本。

## 验证记录

- 构建无警告、无错误；107 项单元测试通过（Core 70、WPF 37），包含长闭眼停留后的预读与重复入口姿态的回归检查。
- 47 项素材检查通过；原有 8 个片段及 1216 张 PNG 与 `v0.2.2` 基准完全一致；哈欠原关键帧未变且时间配方严格乘二。
- 19 项浏览器检查通过；四动作、慢放、拖动、背景、随机播放均正常，无脚本或图片载入错误。
- 真实行为控制器覆盖 27 个点击时点、243 项断言；回程保留当前帧，末帧与起身首帧一致，初始停留仅 4 毫秒。
- 发布目录通过 100 轮真实素材状态循环、51,282 项断言；源码与发布素材 SHA-256 逐项一致。受限 PATH、无可用 DOTNET_ROOT 时，独立 apphost 素材检查退出码 0，无回退。
- 使用最终发布程序集复查 WPF 播放：哈欠、笑眼、单眼均为 0 次同步缓存缺失；三动作取帧 P95 不超过 0.025 ms，最大不超过 0.186 ms，缓存峰值 127.5/128 MiB。入口预热选择前三张不同姿态，可放入预算的小片段在后台完整预读，以跨过闭眼保持提前准备恢复帧。

视觉复核包括 240px 全身、放大眼部、深浅背景、完整闭合与恢复序列，以及点击回程。未在这些检查中发现旧版的神态跳变、冻结的另一只眼、眼缘尖刺或首尾位置跳动。技术检查与观感复核分别记录，不以测试数量代替视觉判断。

浏览器预览按 240 CSS 像素乘当前 DPI 准备 bitmap，减少显示缓存占用。无窗口 Edge 的时序观察保留了偶发约 50–100 ms 的刷新间隔及 Long Task 记录，其根因未完全证实；这些记录不能用来宣称实际桌面恒定帧率。详见 `artifacts/idle-v6-qa/playback-observation-summary.json`。桌宠 WPF 的素材解码、缓存和播放时钟另行验证。

当前素材清单 SHA-256：`19bac52010f9808fd0d0ce701645c2ecbb1306e472c2671e9be594cbdc8bbcfc`。

报告保存在 `artifacts/idle-v6-qa`、`artifacts/idle-v6-runtime`，构建配方记录在 `artifacts/idle-v6/build-report.json`。多屏、不同 DPI 和另一台干净电脑的既有待验收项仍见 `docs/ACCEPTANCE.md`。

## 重建

```powershell
npm ci
npm run assets:idle
npm run assets:idle:check
npm run assets:idle:preview
powershell -ExecutionPolicy Bypass -File tools/build.ps1 -Test -Publish
```

素材重建需要已配置的 RIFE 和 FFmpeg，详见 `tools/motion-pipeline.md`。普通程序构建使用已保存的 PNG，无需重新生图或补帧。版本化 HTML 是本次交付快照，重建后的持续预览入口为 `idle-actions-preview.html`。
