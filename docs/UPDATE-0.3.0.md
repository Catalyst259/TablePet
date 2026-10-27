# 0.3.0 冻结版本

依据最终确认，待机动作只保留 **眨眼、打哈欠、轻眨单眼**，从当前素材清单、随机池和预览中移除眯眼笑。版本冻结于本次 Git 提交，未创建新的 GitHub Release；公开安装包仍为 0.2.2。

## 冻结范围

- 普通眨眼 375 ms，保持已认可的动画。
- 打哈欠 4400 ms，保留头胸轻微起伏和前爪落地。
- 轻眨单眼 1400 ms，保留轻微头肩倾斜、另一眼放松及 600 ms 闭眼停留。
- 原有入睡、睡眠呼吸、唤醒、起身、停留和蹲下保持不变；悬停只唤醒，点击才起身。
- 与 preview.3 相比，所有保留片段的时间轴及引用 PNG 不变；只删除 happy 播放引用及其动作元数据。
- 随机池权重为 blink:yawn:wink = 4:1:1，有多个动作时避免连续重复。

当前素材包为 10 个片段、3454 个时间槽、1668 张引用 PNG。移动动画沿用 240 Hz 时间轴，实际呈现帧率受屏幕和 WPF 合成器限制。

历史候选、提示词与 v2/v3 预览保留为制作档案，包含当时尝试过的笑眼；它们不属于本版播放列表。当前重建配置只接受 yawn 和 wink，不会重新加入 happy。普通眨眼仍由既有构建流程保留。

## 查看和运行

浏览器打开 `assets/strawberry-cat/previews/idle-actions-preview-0.3.0.html`，或持续入口 `idle-actions-preview.html`，均只有三个动作。

当前源码构建：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/setup-dotnet.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1 -Test -Publish
```

输出 `dist/TablePet-0.3.0-win-x64` 和便携 ZIP，内置 .NET 运行时。退出正在运行的旧版后运行 `TablePet.exe`；本机交付目录另附 `Start-Preview.cmd`，可请求旧实例正常退出并启动冻结版，不覆盖安装目录或个人设置。用户点击前不会自动切换现有桌宠。

素材制作流程和生图提示词位置见 [制作流程](../tools/motion-pipeline.md)；仅编译、运行或克隆源码不需要生图模型、RIFE、Node.js 或旧的本地压缩包。

## 冻结检查

- 107 项单元测试通过，构建零警告、零错误。
- 36 项素材检查通过；1668 张实际引用 PNG 的 SHA-256 与已认可的 preview.3 一致，保留的 10 个片段时间轴完全一致。
- 冻结版浏览器检查验证只有三个动作按钮，随机播放、慢放、拖动和背景正常，未出现图片载入或脚本错误。
- 最终自包含包通过 20 轮真实素材交互、47,193 项断言，三个动作均实际参与自动选择；点击、悬停、暂停、拖拽、挂起及睡眠截止边界通过。
- 发布素材与源码 SHA-256 一致。受限 PATH、失效 DOTNET_ROOT 时独立 apphost 检查退出码 0，未操作现有桌宠。

冻结指纹保存在 `reference/idle-v7/frozen-0.3.0.json`，包含素材清单、各片段时间轴、PNG 汇总、选择配置和制作配方的 SHA-256。运行报告保存在 `artifacts/idle-0.3.0-qa` 和 `artifacts/idle-0.3.0-runtime`；这些本机诊断缓存不入库。

多屏混合 DPI、另一台干净 Windows 电脑和长期运行的既有待验收项保持不变，见 [验收范围](ACCEPTANCE.md)。
