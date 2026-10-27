# 0.3.0-preview.3 整体动作优化

这是本地预览版本，尚未提交、推送 GitHub 或发布 Release。公开安装版仍为 0.2.2。

## 动作设计

- 普通眨眼保留原样。
- 眯眼笑保持头部方向，不增加歪头。眼头厚、向外眼尾渐细并略抬高，脸颊和肩背轻微配合，保留原有小猫嘴。
- 单眼眨眼加入克制的头肩倾斜，另一只眼轻微放松；360 ms 收拢，600 ms 闭眼停留，440 ms 回到原姿态。嘴部不额外开合。
- 哈欠保持已认可的 4.4 秒节奏，头、耳朵、头饰和肩胸一起轻抬，前爪落地。峰值头饰比待机抬高约 8/512 画布像素，默认 240 DIP 大小时约 3.75 DIP。
- 睡眠呼吸、入睡、唤醒、起身、蹲下，以及悬停只影响睡眠、点击才起身的规则保持不变。

## 打开预览

打开 `assets/strawberry-cat/previews/idle-actions-preview-v3.html` 查看本版快照，支持单独点播、慢放、暂停拖动和深浅背景。`idle-actions-preview.html` 是随素材持续更新的入口，v2 快照保留。

本地便携目录为 `dist/TablePet-0.3.0-preview.3-win-x64`。双击其中的 **Start-Preview.cmd**，会请求旧实例正常退出再启动新版；不覆盖安装目录，沿用个人设置。自包含包无需开发环境。关闭预览版后，可用原桌面快捷方式运行已安装版本。

## 素材和配方

使用内置 `image_gen` 生成透明关键姿态，复用 Sharp 注册、RIFE 4.6 补帧和现有 FFmpeg 端点修正。注册只参考前爪，保留完整 RGBA 姿态的头、耳、头饰、肩背运动，不再将生成图裁回固定身体。画布中 alpha≤3 的透明噪点沿用既有补帧规则清零。

- 原始候选：`assets/strawberry-cat/sources/idle-v7`；选择配置：该目录的 `selection.json`。
- 注册关键帧与时间配方：`assets/strawberry-cat/keyframes-v7`。
- 最终动作：`assets/strawberry-cat/frames/v7`。
- 完整提示词、筛选记录：`reference/idle-v7-happy-prompts.json`、`reference/idle-v7-wink-prompts.json`、`reference/idle-v7-yawn-prompts.json`。
- 用户眼线参考与冻结基线：`reference/idle-v7`。基线随源码保存，重建不依赖旧版解压目录或本机 artifacts。

| 动作 | 时长 | 时间槽 | 引用 PNG（含公共待机帧） |
| --- | ---: | ---: | ---: |
| 哈欠 | 4400 ms | 1056 | 342 |
| 眯眼笑 | 1600 ms | 384 | 99 |
| 单眼眨眼 | 1400 ms | 336 | 112 |

整包为 11 个片段、3838 个时间槽、1766 张引用 PNG。移动时间轴为 240 Hz，实际显示刷新率取决于屏幕与 WPF 合成器；GIF 是 50 fps 查看副本。原始候选和历史帧保留用于追溯，只有清单引用的图片参与播放。

## 验证记录

- 构建无警告、无错误，107 项单元测试通过（Core 70、WPF 37）。
- 43 项素材检查通过；原有 8 个片段及 1216 张 PNG 字节不变。所有动作首尾精确回到共同待机帧，透明边界和前爪落点通过检查。
- 19 项无窗口 Edge 浏览器检查通过：四个动作、慢放、拖动、背景和随机播放正常，无脚本错误或图片载入失败。
- 真实行为控制器的 27 个点击时点、243 项断言通过，覆盖闭合、停留与睁眼期间的打断；回程从当前可见帧开始、最多 240 ms 收回，与起身共享精确待机边界。
- 最终自包含包通过 100 轮真实素材交互、51,302 项断言；发布素材与源码 SHA-256 一致。受限 PATH、没有可用 DOTNET_ROOT 的 apphost 素材检查退出码 0，无回退。
- 最终发布程序集只运行一次 240 Hz 解码/缓存探针。三种新动作同步缓存缺失均为 0，取帧 P95 分别为 0.0210/0.0213/0.0220 ms，像素缓存峰值 127.5/128 MiB。旧 settle 片段本次有 3 次缓存缺失、最大取帧 4.2139 ms，记录原样保留；这不是新动作的测量结果。

视觉复核查看了 240px 全身的完整时序、各段过渡、首尾接缝及放大眼部的浅深背景联系表。happy 没有新增明显歪头；wink 的另一眼、脸颊和头肩有联动；yawn 头胸随张口轻抬并落回。happy 尾巴右侧约 10/512 画布像素的舒展仍可在并排帧中看见，当前判断为小幅外摆，未见突兀充气或接缝跳变。

针对闭眼末段高光另外检查了 120 Hz 时间采样：happy 和 wink 的亮点随闭合单调消失、回睁单调恢复，在三档亮度阈值中均未出现重新闪亮；600 ms 闭眼停留期间无残留高光。逐帧和像素检查不能替代人的连续播放观感，不据此宣称艺术效果绝对最优。

报告位于 `artifacts/idle-v7-qa`、`artifacts/idle-v7-runtime`；完整制作配方位于 `assets/strawberry-cat/keyframes-v7`。素材清单 SHA-256：`07ddac20e6c8844c8ad2fa4c23a1fa72b46a708b2c1f9e6a52fc897eac19b102`。

验证未切换或操作正在运行的桌宠。无窗口缓存探针不测量 DWM 的实际呈现帧率；多屏混合 DPI、另一台干净 Windows 电脑与长期运行的既有待验收项见 `docs/ACCEPTANCE.md`。

## 重建

```powershell
npm ci
npm run assets:idle
npm run assets:idle:check
npm run assets:idle:preview
powershell -ExecutionPolicy Bypass -File tools/build.ps1 -Test -Publish
```

素材重建需要已配置的 RIFE 和 FFmpeg，详见 `tools/motion-pipeline.md`。普通程序构建直接使用已保存 PNG，无需重新生成图片。`node tools/build-idle-v7.cjs --normalize-only` 只注册关键帧供检查；`--stage-only` 补帧并生成候选清单而不切换活动清单。
