# 草莓猫桌宠 TablePet

基于提供的草莓灰白猫原型和《Windows 桌面宠物实现方案》，使用 C#、WPF、.NET 10 LTS 和透明 PNG 帧动画。待机为较低趴坐，点击后随机起身或眨单眼。

**当前源码基线为 0.3.2 · 120Hz 冻结版。** 用户于 2026 年 10 月 30 日确认测试版效果后，将动画素材采样密度从约 240Hz 调整为 120Hz，保留 512×512 PNG 原图、动作总时长和首尾姿态。运行清单包含 11 个片段、1275 张 PNG、1907 个时间槽。冻结范围与复现方法见 [0.3.2 版本说明](docs/UPDATE-0.3.2.md)。

本版包含双击回应：双眼完全闭成陶醉的弯弧，头轻轻抬起、小嘴微张，右上角同步出现“喵”气泡，再柔和恢复，整个动作 1.4 秒。采用已确认的“连贯小幅版”：保留原表情，抬头幅度较前版约减 25%，抬起和回落各约 460ms，最高处自然转向。身体、前爪保持落地。气泡使用独立的穿透装饰窗口，不抢焦点；本次用文字呈现，不播放音频。单击会等待系统双击判定间隔，再播放原来的起身或 Wink；双击只播放一次喵叫，拖动不触发。睡眠中先唤醒再回应，正在回应时的点击不排队。

本版包含睡眠 Z 动画：右耳上方的小、中、大三个 Z 依次淡入，轻轻向右上浮动后淡出，每轮 3.6 秒，与现有呼吸共用计时。字母使用缩小、收紧后的确认位置；唤醒时收起，拖动时随呼吸冻结。独立透明装饰窗口保持字母区域的鼠标穿透，并跟随宠物的位置、大小和置顶设置。呼吸 PNG 保留原字节，播放时间线现为 120Hz。

待机只保留眨眼和打哈欠；点击随机播放起身或 Wink，两种反应机会相同。Wink 沿用 V9 表情和眼神，总长 1.5 秒；120Hz 时间线从此前确认的完整动画中取样。已发布的 0.3.1 点击互动版保留为历史版本，详见 [0.3.1 版本说明](docs/UPDATE-0.3.1.md)。

本版沿用此前的柔和眨眼与睡眠呼吸：睡眠时头部、肩背每轮轻柔起伏 3.6 秒，前爪保持落地。移动片段使用 **120Hz 素材时间线**，播放器跟随 WPF 合成器刷新；实际呈现帧率受显示器和合成器限制。静态待机和保持仍各为一帧，不持续高频绘制。

## 安装和运行

适用系统：**Windows 11 x64**。普通使用者直接安装即可，无需 .NET SDK、Node.js 或其他开发环境。

当前发布版本为 **0.3.2 · 120Hz**，安装包和免安装包见 [0.3.2 Release](https://github.com/Catalyst259/TablePet/releases/tag/v0.3.2)。两种包均内置 .NET 运行时，支持保留现有个人设置。

1. 从 [Releases 下载页面](https://github.com/Catalyst259/TablePet/releases/latest) 下载安装包，或直接下载 [TablePet-0.3.2-Setup.exe](https://github.com/Catalyst259/TablePet/releases/download/v0.3.2/TablePet-0.3.2-Setup.exe)。
2. 双击安装包，按中文向导完成安装。默认目录为 `%LocalAppData%\Programs\TablePet`，无需管理员权限，所需 .NET 运行时和动画素材已内置。
3. 完成页选择启动，或使用桌面、开始菜单中的“草莓猫桌宠”快捷方式。右键猫咪或托盘图标可打开设置和退出。

安装程序默认创建快捷方式，可从 Windows“设置 → 应用 → 安装的应用”卸载。更新时运行新安装包，个人设置会保留。开机启动默认关闭，可在桌宠设置中手动开启。应用和素材完全本地运行，无联网服务。详细说明见 [docs/INSTALLATION.md](docs/INSTALLATION.md)。

安装包尚未配置代码签名证书，Windows 可能显示“未知发布者”。可用 PowerShell 的 `Get-FileHash -Algorithm SHA256 <安装包路径>` 对照同一 Release 的 `.sha256` 文件确认下载完整性。

需要免安装版本时，从 [0.3.2 Release](https://github.com/Catalyst259/TablePet/releases/tag/v0.3.2) 下载 `TablePet-0.3.2-win-x64.zip`，或按下面步骤自行构建；完整解压后双击其中的 `TablePet.exe`。直接切换便携版本前，先从托盘退出旧实例；否则再次启动只会打开旧实例的设置。便携目录附带 `Start-Preview.cmd`，双击可正常退出旧实例并启动新版。GitHub 自动提供的 “Source code (zip)” 是源码备份，需要先构建。

0.3.2 安装器已通过静态解包、1774 个载荷文件逐文件 SHA-256 比对，以及解包程序的全部素材解码检查。本次未执行真实安装、覆盖升级或卸载；发布校验摘要见 `reference/releases/distribution-0.3.2.json`，下载文件的验证记录随 Release 提供。

- 移入猫咪身体并停留片刻，只会打断入睡或唤醒睡眠，随后保持趴坐；清醒时悬停只刷新互动时间。
- 轻点身体随机触发起身或 Wink；睡眠中点击先唤醒再播放其中一种。悬停唤醒过程中点击，也能在醒来后接上点击动作。动作进行中不叠加或排队，完成后共用互动冷却。旧的悬停/点击设置自动兼容，无需重置。
- 双击身体播放闭眼陶醉、轻抬头和微张嘴，同时显示右上角“喵”气泡。单击和双击互斥，判定时间与位置容差跟随 Windows 设置；气泡和表情使用同一时钟，拖动时一起冻结。
- 按住身体拖动以移动位置。静止悬停只触发一次。
- 默认每隔 3–8 秒播放待机动作（仅眨眼、打哈欠），无互动 60 秒后入睡。睡眠或入睡时，悬停或明确点击可唤醒。
- 右键猫咪打开菜单；系统托盘提供隐藏/显示、设置、暂停自动动作、置顶、鼠标穿透、恢复位置、退出。
- 再次双击程序会打开已运行实例的设置，不会产生第二只猫。`TablePet.exe --settings` 也可打开设置，`TablePet.exe --quit` 可正常退出。
- 鼠标穿透后，通过系统托盘恢复。关闭设置不会退出宠物。自启动默认关闭，需在设置中手动开启。

设置文件：`%LocalAppData%\DesktopPet\settings.json`。日志：同目录的 `TablePet.log`，超过 256 KiB 后轮转。损坏配置会备份为带时间戳的文件，再使用默认配置。程序不会监听全局键盘。

## 开发和构建

使用 Windows x64 和 .NET SDK **10.0.403**（`global.json` 固定版本，允许补丁滚动）。所有运行所需的最终 PNG 已保存在 Git 中，克隆后即可构建，无需本机旧的解压目录、源码 ZIP 或生图服务。

```powershell
git clone https://github.com/Catalyst259/TablePet.git
cd TablePet

# 没有对应 SDK 时执行；下载微软官方 SDK 到本项目 .tools，并校验 SHA-512
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup-dotnet.ps1

# 生成带运行时的便携版本，并运行测试
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build.ps1 -Test -Publish

# 运行构建结果
.\dist\TablePet-0.3.2-win-x64\TablePet.exe
```

其它常用命令：

```powershell
# 构建、自动测试
powershell -File tools\build.ps1 -Test

# 构建、测试、发布含运行时的 Windows x64 ZIP
powershell -File tools\build.ps1 -Test -Publish

# 使用 Inno Setup 6 制作安装 EXE，详见 installer/README.md
powershell -File tools\build-installer.ps1

# 保存本版源码 ZIP（含素材和重建脚本，排除本机依赖和编译缓存）
powershell -File tools\package-source.ps1

# 在开发环境启动
powershell -File tools\build.ps1 -Launch

# 校验 120Hz 重采样结果和冻结素材指纹（需要 Node.js）
npm run assets:120hz:check
npm run assets:freeze:check
```

构建脚本的发布结果：`dist\TablePet-0.3.2-win-x64\TablePet.exe`、`dist\TablePet-0.3.2-win-x64.zip` 和最新包别名 `dist\TablePet-win-x64.zip`，各 ZIP 附 SHA-256 校验文件。另行执行安装器脚本时输出 `dist\TablePet-0.3.2-Setup.exe`，源码打包脚本输出 `dist\TablePet-0.3.2-source.zip`。这些脚本在本地生成文件；公开下载使用上方 Release 链接。`--settings` 启动参数适合开发时直接查看设置页。`--check-assets` 以无窗口方式逐一检查全部素材，退出码 0 表示通过，2 表示缺失、损坏或可选动作回退；该检查不会操作正在运行的桌宠实例。

## 源码备份和本地清理

本次 0.3.2 冻结指纹保存在 `reference/releases/frozen-0.3.2.json`，`main` 继续用于后续开发。`v0.3.1` 保留上一版冻结源码；`v0.2.1`、`v0.2.2` 标签保留对应的原始源码快照，含动画帧、关键姿态、原始生成图、Word 方案、提示词、构建脚本及逐文件 `SOURCE-MANIFEST.sha256`。历史源码可随时从 GitHub 标签页下载，或重新导出：

```powershell
git fetch origin --tags
git -c core.autocrlf=false archive --format=zip --output=TablePet-v0.2.1-source.zip v0.2.1
git -c core.autocrlf=false archive --format=zip --output=TablePet-v0.2.2-source.zip v0.2.2
git -c core.autocrlf=false archive --format=zip --output=TablePet-v0.3.1-source.zip v0.3.1
```

确认远端标签完整后，本地 `TablePet-*-source.zip` 和安装包验包产生的 `artifacts/installer-check/*/extracted` 目录可删除。`dist` 中的便携解压目录可在保留对应 ZIP 后删除；`.tools`、`node_modules`、`bin`、`obj` 为可重新安装或构建的本机依赖与缓存，不纳入 Git。当前工作目录中的源码、`assets`、`reference` 和 `.git` 是继续开发使用的文件。

桌宠安装目录 `%LocalAppData%\Programs\TablePet`、个人设置 `%LocalAppData%\DesktopPet` 与这些开发目录独立。清理开发副本时保留它们。

导出历史版本时显式关闭 `core.autocrlf`，可避免 Windows Git 自动改写文本换行，确保文件内容与原始 `SOURCE-MANIFEST.sha256` 一致。

睡眠对比 QA 默认读取旧版解压素材。清理旧目录后，可从标签恢复基准再传给检查脚本：

```powershell
New-Item -ItemType Directory -Force artifacts | Out-Null
git -c core.autocrlf=false archive --format=zip --output=artifacts/qa-v0.2.1.zip v0.2.1
Expand-Archive artifacts/qa-v0.2.1.zip artifacts/qa-v0.2.1
node tools/qa-sleep-breathing.cjs assets/strawberry-cat artifacts/qa-v0.2.1/assets/strawberry-cat
```

## 结构和依赖

- `src/TablePet.Core`：独立于 WPF 的时钟、有限状态机、帧播放器、交互识别及配置类型。
- `src/TablePet.App`：WPF 窗口、预解码图像缓存、设置界面及 Windows 适配。
- `assets/strawberry-cat`：PNG 帧、manifest、原始生成图和预览。
- `tests`：xUnit 行为、素材回退和设置持久化测试，使用独立临时目录。
- `docs`：设置字段与实际验收状态。
- `reference`：需求文本和生图提示词记录。
- `installer`：Inno Setup 安装脚本、简体中文语言文件及打包说明。

生产功能复用 WPF 图像/窗口、System.Text.Json、WinForms NotifyIcon、Windows 工作区/鼠标/会话 API；没有引入自制渲染引擎或全局输入钩子。测试依赖版本固定在对应 `.csproj`。

状态机以单调时钟计算时间；入睡中断只倒播已经显示过的帧，所有完成回调检查动画令牌。眨眼中点击先等眨眼结束；呼吸中唤醒先从当前姿态平滑收敛到共同睡姿，避免突然跳帧。悬停和点击使用不同信号，拖拽释放不自动起身。

动画通过 `CompositionTarget.Rendering` 按显示刷新推进，时间轴预计算后二分定位。PNG 以原始512像素流式解码，WPF负责显示缩放；128 MiB解码像素缓存配合最多两个后台预读线程，正在解码的帧去重，预读中的未来帧受到保护，过期结果不会干扰新动作。该预算不包含WPF、程序及临时解码的其他内存。鼠标位置仍以30ms间隔采样；静态姿态解绑绘制回调，拖动时冻结动作，隐藏或系统暂停后停止调度。

## 素材维护

运行时读取 `Assets/strawberry-cat/manifest.json`。画布、锚点、片段列表、帧时长和循环方式独立于用户行为设置。各片段保持统一画布，透明 alpha 用于身体命中检测；窗口的空白点击穿透由 Windows 分层透明实现。AI 生图原始输出、提示词与切图脚本保留在仓库中。

素材已预先生成，构建和运行桌宠不需要 Node.js、RIFE 模型或联网。0.3.2 包含 11 个片段、1275 张实际引用 PNG、1907 个时间槽。其中 Meow 为 85 张 PNG（与待机共享首尾）、168 个时间槽、总长 1.4 秒；Wink 为 137 张 PNG、180 个时间槽、总长 1.5 秒。睡眠呼吸使用 257 张 PNG，组成 432 槽、3.6 秒的循环；眨眼总长仍为 375ms。活动画面以约 8–9ms 的时间槽取样，重复姿态保留独立时间槽，以维持既有交互回程规则。原 240Hz 清单保存在 `reference/motion-120hz/source-manifest-240hz.json`；0.3.1 的 1833 张 PNG、3485 槽记录仍按历史版本保留。

关键姿态使用内置image_gen生成，再以现成RIFE 4.6模型补充过渡，Sharp负责切图、锚点对齐、透明双底还原和打包；睡眠最靠近休息姿态的15张帧使用FFmpeg标准浮点alpha合成消除推理接缝。无需自制动画引擎。重建时使用Node.js24，`npm ci`，`node tools/motion-setup-rife.cjs`，并安装[FFmpeg 8.1.1](https://ffmpeg.org/download.html)加入PATH，再 `npm run assets:prepare`。这些工具只用于素材制作，运行与编译桌宠使用已保存PNG即可。原图与关键帧已随源码保存，提示词在 `reference/motion-v2-prompts.json`、`reference/motion-v2-followup-prompts.json` 和 `reference/sleep-v4-prompts.json`，流程说明见 [tools/motion-pipeline.md](tools/motion-pipeline.md)。`assets:prepare:v1` 仅供复现旧版，会覆盖当前清单，不用于当前构建。

120Hz 冻结基线可用 `npm run assets:120hz` 从保存的 240Hz 清单重新生成，随后运行 `npm run assets:120hz:check` 和 `npm run assets:freeze:check`。该步骤只重写清单，不重新编码 PNG，也不依赖本机 `dist`、`artifacts` 或模型缓存。历史制作配方仍以 240Hz 生成完整姿态，修改原动画后应先审阅并更新采样源，再重新取样、验收和冻结；不要把历史生成命令的输出直接当作本版冻结结果。

当前冻结动作预览为 `assets/strawberry-cat/previews/actions-preview-0.3.2.html`。`actions-preview-0.3.1.html`、历史 `motion-preview.html`、睡眠、眨眼 GIF 和旧版待机预览保留为制作档案；其时间线和动作分类不代表本版。120Hz 桌面效果以当前程序读取的运行清单为准。GIF 仅为 50fps 预览，不能用来测量桌宠实际刷新率。单独重建睡眠可运行 `node tools/touchup-sleep.cjs`，重建眨眼可运行 `node tools/touchup-blink.cjs`。睡眠源图与关键姿态在 `sources/sleep-v4`、`keyframes-v4/sleep`，完整提示词和候选选择记录在 `reference/sleep-v4-prompts.json`。

本版 Wink 的重建选择配置为 `reference/idle-v9-fluid-selection.json`，使用 `tools/build-wink-candidate.cjs` 生成候选，再以 `node tools/install-wink-v9-smooth.cjs --selection reference/idle-v9-fluid-selection.json` 导入运行素材。原始 V9 关键图保留；细分过渡、端点残差修正及按眼睑运动距离重采样只用于离线制帧，不增加运行时依赖。

## 验证范围

0.3.2 的冻结范围、素材指纹和复现命令见 [0.3.2 版本说明](docs/UPDATE-0.3.2.md) 与 `reference/releases/frozen-0.3.2.json`；历史 0.3.1 记录保留在 [0.3.1 版本说明](docs/UPDATE-0.3.1.md)。测试覆盖点击选择、Wink 完成与冷却、睡眠中点击、待机动作衔接、拖拽和挂起，以及旧素材包兼容。资源检查和无窗口播放探针验证实际 PNG 的加载与缓存，不等同于显示器实际呈现帧率。

历史呼吸优化记录见 [0.2.2 版本说明](docs/UPDATE-0.2.2.md)，眨眼和交互记录见 [0.2.1 版本说明](docs/UPDATE-0.2.1.md)，0.3.0 的旧待机动作范围见 [0.3.0 版本说明](docs/UPDATE-0.3.0.md)。已操作的桌面验证与待验收项见 [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md)。多屏混合 DPI、拔屏、真实锁屏/休眠、无开发环境电脑和长时间运行仍需相应环境验证。

配置说明见 [docs/SETTINGS.md](docs/SETTINGS.md)。动画视觉一致性仍可通过替换素材包继续打磨，无需修改行为控制器。
