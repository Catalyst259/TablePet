# 0.3.1 冻结版本

2026 年 10 月 29 日冻结。点击互动提供 **起身和 Wink** 两种等概率反应；自动待机动作只保留 **眨眼和打哈欠**。Wink 使用最终确认的 V9 连贯过渡版，保留原有表情与眼神，适度加快闭眼和睁眼。

对应 Git 标签为 `v0.3.1`，安装包与便携包见 [GitHub Release](https://github.com/Catalyst259/TablePet/releases/tag/v0.3.1)。此前的 0.3.0 冻结说明保留为历史记录，不代表本版动作分类。

## 冻结范围

| 动作阶段 | 时长 | 说明 |
| --- | ---: | --- |
| Wink 闭眼 | 440ms | 保留 V9 的准备、半闭和闭眼姿态。 |
| Wink 表情停留 | 350ms | 完整闭眼表情保持，不受起身保持设置影响。 |
| Wink 睁眼 | 330ms | 半睁前后分别分配 110ms、220ms，细分过渡并均衡眼睑变化。 |
| Wink 表情恢复 | 380ms | 回到与待机完全相同的首尾图。 |
| Wink 全程 | 1500ms | 367 个时间槽、277 张引用 PNG。 |
| 普通眨眼 | 375ms | 素材与时间轴保持不变。 |
| 打哈欠 | 4400ms | 素材与时间轴保持不变。 |

V9 的 8 张关键图保持逐字节一致，没有重新生图或增加眼珠转动。过渡采用局部细分插值、端点残差修正和按眼睑运动距离重采样，避免在每个关键姿态处反复停顿。所有制帧步骤均在离线完成，运行时仍播放透明 PNG。

入睡、睡眠呼吸、唤醒、起身、保持和蹲下素材保持不变。除 Wink 外，其余片段引用的 1557 张 PNG 及时间轴与调整前一致。完整运行素材包为 10 个片段、3485 个时间槽、1833 张引用 PNG。

## 互动行为

- 有效点击随机选择起身或 Wink，两种反应各占一半。起身继续使用原有保持、蹲下流程；Wink 播放完直接回到待机。两者共用互动冷却。
- 自动待机池只包含眨眼和打哈欠，权重为 4:1。旧清单中的 happy、wink 自动动作条目不再参与待机选择。
- 睡眠或唤醒途中点击，会在醒来后衔接一次点击动作。动作进行中不叠加或排队；拖拽和挂起继续遵循既有行为。
- 悬停只打断入睡或唤醒睡眠，清醒时刷新互动时间。旧设置无需重置。
- 可选 Wink 缺失或不合法时回退为起身，不影响基础动作使用。

## 安装、运行和预览

安装版为 `TablePet-0.3.1-Setup.exe`，默认更新 `%LocalAppData%\Programs\TablePet`，保留 `%LocalAppData%\DesktopPet` 中的个人设置。安装器通过应用退出信号正常关闭旧实例，再更新程序文件。版本未改变设置文件格式。

便携包为 `TablePet-0.3.1-win-x64.zip`，包含 .NET 运行时。完整解压后运行 `TablePet.exe`；目录内的 `Start-Preview.cmd` 可正常退出旧实例后启动便携版。

冻结动作预览：`assets/strawberry-cat/previews/actions-preview-0.3.1.html`。它展示当前动作和分类；历史 V8、V9、V10 候选及旧版预览保留为制作记录，不参与运行时随机动作选择。

源码构建与安装包制作：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/setup-dotnet.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1 -Test -Publish
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-installer.ps1
```

仅编译或运行已冻结的源码不需要生图服务、RIFE、Node.js 或历史本地候选目录。

## 冻结与验证记录

冻结指纹保存在 `reference/releases/frozen-0.3.1.json`，用于核对素材清单、片段时间轴、引用 PNG、版本信息和重建配置。Wink 的选择配置为 `reference/idle-v9-fluid-selection.json`，导入记录为 `reference/wink-v9-fluid-runtime.json`。

最终候选的资源检查与无窗口播放检查通过，Wink 播放探针没有同步缓存缺失；详见 `reference/idle-v9-fluid-review.json`。这些检查验证加载、时间轴和缓存，不证明动画自然度或显示器实际呈现帧率。

本版 129 项单元测试通过（Core 90 项、App 39 项）。发布验证还包括自包含构建、程序 `--check-assets`、`tools/verify-installer.ps1` 及实际安装后的版本、素材检查；安装包验证摘要与 SHA-256 校验文件随 GitHub Release 交付。冻结指纹用于验证源码中的素材和制作配方。

多屏混合 DPI、另一台干净 Windows 电脑和长期运行的既有待验收项保持不变，见 [验收范围](ACCEPTANCE.md)。
