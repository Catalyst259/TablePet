# 草莓猫桌宠 TablePet

基于提供的草莓灰白猫原型和《Windows 桌面宠物实现方案》，使用 C#、WPF、.NET 10 LTS 和透明 PNG 帧动画。待机为较低趴坐，触碰后由前爪撑起前半身。

**0.2.2 呼吸优化版**让睡眠时的头部与肩背轻柔起伏：每轮3.6秒，默认240DIP大小下约2.8DIP起伏，前爪保持落地，闭眼表情稳定。使用生图模型制作关键姿态，再以现成RIFE补帧。保留0.2.1的375ms柔和眨眼和“悬停唤醒、点击起身”，其余七个片段不变。移动片段按 **240fps 时间轴**制作，播放器跟随WPF合成器刷新；本机屏幕查询约为120Hz，不能在该屏幕上实际显示240fps。静态姿态不持续高频绘制。

## 安装和运行

适用系统：**Windows 11 x64**。普通使用者直接安装即可，无需 .NET SDK、Node.js 或其他开发环境。

1. 从 [Releases 下载页面](https://github.com/Catalyst259/TablePet/releases/latest) 下载安装包，或直接下载 [TablePet-0.2.2-Setup.exe](https://github.com/Catalyst259/TablePet/releases/download/v0.2.2/TablePet-0.2.2-Setup.exe)。
2. 双击安装包，按中文向导完成安装。默认目录为 `%LocalAppData%\Programs\TablePet`，无需管理员权限，所需 .NET 运行时和动画素材已内置。
3. 完成页选择启动，或使用桌面、开始菜单中的“草莓猫桌宠”快捷方式。右键猫咪或托盘图标可打开设置和退出。

安装程序默认创建快捷方式，可从 Windows“设置 → 应用 → 安装的应用”卸载。更新时运行新安装包，个人设置会保留。开机启动默认关闭，可在桌宠设置中手动开启。应用和素材完全本地运行，无联网服务。详细说明见 [docs/INSTALLATION.md](docs/INSTALLATION.md)。

安装包尚未配置代码签名证书，Windows 可能显示“未知发布者”。可用 PowerShell 的 `Get-FileHash -Algorithm SHA256 <安装包路径>` 对照同一 Release 的 `.sha256` 文件确认下载完整性。

需要免安装版本时，可按下面的源码构建步骤生成 `dist\TablePet-0.2.2-win-x64.zip`；完整解压后双击其中的 `TablePet.exe`。直接切换便携版本前，先从托盘退出旧实例；否则再次启动只会打开旧实例的设置。GitHub 自动提供的 “Source code (zip)” 是源码备份，需要先构建。

- 移入猫咪身体并停留片刻，只会打断入睡或唤醒睡眠，随后保持趴坐；清醒时悬停只刷新互动时间。
- 轻点身体才会起身；睡眠中点击先唤醒再起身。悬停唤醒过程中点击，也能在醒来后接上起身。旧的悬停/点击设置自动兼容，无需重置。
- 按住身体拖动以移动位置。静止悬停只触发一次。
- 默认 3–8 秒眨眼，无互动 60 秒后入睡。睡眠或入睡时，悬停或明确点击可唤醒。
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
.\dist\TablePet-0.2.2-win-x64\TablePet.exe
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
```

发布结果：`dist\TablePet-0.2.2-win-x64\TablePet.exe`、`dist\TablePet-0.2.2-win-x64.zip` 和最新包别名 `dist\TablePet-win-x64.zip`，各 ZIP 附 SHA-256 校验文件。`--settings` 启动参数适合开发时直接查看设置页。`--check-assets` 以无窗口方式逐一检查全部素材，退出码 0 表示通过，2 表示缺失或损坏；该检查不会操作正在运行的桌宠实例。

## 源码备份和本地清理

`v0.2.1`、`v0.2.2` 标签保存对应的原始源码快照，含动画帧、关键姿态、原始生成图、Word 方案、提示词、构建脚本及逐文件 `SOURCE-MANIFEST.sha256`。`main` 在此基础上维护仓库说明。历史源码可随时从 GitHub 标签页下载，或重新导出：

```powershell
git fetch origin --tags
git archive --format=zip --output=TablePet-v0.2.1-source.zip v0.2.1
git archive --format=zip --output=TablePet-v0.2.2-source.zip v0.2.2
```

确认远端标签完整后，本地 `TablePet-*-source.zip` 和安装包验包产生的 `artifacts/installer-check/*/extracted` 目录可删除。`dist` 中的便携解压目录可在保留对应 ZIP 后删除；`.tools`、`node_modules`、`bin`、`obj` 为可重新安装或构建的本机依赖与缓存，不纳入 Git。当前工作目录中的源码、`assets`、`reference` 和 `.git` 是继续开发使用的文件。

桌宠安装目录 `%LocalAppData%\Programs\TablePet`、个人设置 `%LocalAppData%\DesktopPet` 与这些开发目录独立。清理开发副本时保留它们。

睡眠对比 QA 默认读取旧版解压素材。清理旧目录后，可从标签恢复基准再传给检查脚本：

```powershell
New-Item -ItemType Directory -Force artifacts | Out-Null
git archive --format=zip --output=artifacts/qa-v0.2.1.zip v0.2.1
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

动画通过 `CompositionTarget.Rendering` 按显示刷新推进，时间轴预计算后二分定位。PNG 以原始512像素流式解码，WPF负责显示缩放；128 MiB解码像素缓存配合后台预读，预读中的未来帧受到保护。该预算不包含WPF、程序及临时解码的其他内存。鼠标位置仍以30ms间隔采样；静态姿态解绑绘制回调，拖动时冻结动作，隐藏或系统暂停后停止调度。

## 素材维护

运行时读取 `Assets/strawberry-cat/manifest.json`。画布、锚点、片段列表、帧时长和循环方式独立于用户行为设置。各片段保持统一画布，透明 alpha 用于身体命中检测；窗口的空白点击穿透由 Windows 分层透明实现。AI 生图原始输出、提示词与切图脚本保留在仓库中。

素材已预先生成，构建和运行桌宠不需要Node.js、RIFE模型或联网。当前包含1216张实际使用PNG、2062个帧槽；睡眠使用257张PNG（256张新增过渡帧与原共同睡姿），组成864槽、3.6秒的无缝循环。眨眼仍为375ms、闭眼90ms。移动片段使用约240Hz时间轴，慢动作靠近停止点可重复同一帧。

关键姿态使用内置image_gen生成，再以现成RIFE 4.6模型补充过渡，Sharp负责切图、锚点对齐、透明双底还原和打包；睡眠最靠近休息姿态的15张帧使用FFmpeg标准浮点alpha合成消除推理接缝。无需自制动画引擎。重建时使用Node.js24，`npm ci`，`node tools/motion-setup-rife.cjs`，并安装[FFmpeg 8.1.1](https://ffmpeg.org/download.html)加入PATH，再 `npm run assets:prepare`。这些工具只用于素材制作，运行与编译桌宠使用已保存PNG即可。原图与关键帧已随源码保存，提示词在 `reference/motion-v2-prompts.json`、`reference/motion-v2-followup-prompts.json` 和 `reference/sleep-v4-prompts.json`，流程说明见 [tools/motion-pipeline.md](tools/motion-pipeline.md)。`assets:prepare:v1` 仅供复现旧版，会覆盖当前清单，不用于当前构建。

直接打开 `assets/strawberry-cat/previews/motion-preview.html` 可选动作、慢放和切换深浅背景；它使用当前PNG时间轴。`sleep-breathing.gif` 展示本版呼吸，`sleep-breathing-comparison.gif` 在240px尺寸比较新旧睡眠；`blink-touchup.gif` 保留眨眼对比，`animation-preview-v2.gif` 是较早完整动作预览。GIF仅为50fps预览，不能用来测量桌宠实际刷新率。单独重建睡眠可运行 `node tools/touchup-sleep.cjs`，重建眨眼可运行 `node tools/touchup-blink.cjs`。睡眠源图与关键姿态在 `sources/sleep-v4`、`keyframes-v4/sleep`，完整提示词和候选选择记录在 `reference/sleep-v4-prompts.json`。

## 验证范围

当前Release的75项自动测试通过，本版呼吸优化与验收记录见 [docs/UPDATE-0.2.2.md](docs/UPDATE-0.2.2.md)，眨眼和交互微调记录见 [docs/UPDATE-0.2.1.md](docs/UPDATE-0.2.1.md)。0.2.0的逐帧与高帧率缓存测量见 [docs/MOTION-QA.md](docs/MOTION-QA.md) 和 [docs/MOTION-PERFORMANCE.md](docs/MOTION-PERFORMANCE.md)，本轮未修改播放器与缓存。已操作的桌面验证与待验收项见 [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md)。多屏混合DPI、拔屏、真实锁屏/休眠、无开发环境电脑和长时间运行仍需相应环境验证。

配置说明见 [docs/SETTINGS.md](docs/SETTINGS.md)。动画视觉一致性仍可通过替换素材包继续打磨，无需修改行为控制器。
