# 发布包与真实素材运行检查

本记录区分发布文件、无界面托管入口、真实素材的内存解码与状态仿真，以及实际Windows桌面操作。前几项不能替代真实点击穿透、焦点、多屏和休眠测试。

## 当前0.2.1发布包

2026年10月27日00:47（Asia/Shanghai），`dist/TablePet-0.2.1-win-x64`通过独立自包含入口检查、1316张PNG及源包SHA-256一致性检查，以及100次真实素材状态仿真，共37213条断言。Release单元测试75/75通过，构建零警告、零错误。新增20张眨眼过渡和悬停/点击分离规则见 `UPDATE-0.2.1.md`。此轮不修改播放器/缓存，不重复宣称0.2.0的性能数据为新版本实测。

复现：`powershell -File tools/verify-runtime.ps1 -PublishDirectory dist/TablePet-0.2.1-win-x64 -ProbeAppHost -Cycles 100`。机器可读证据仍写入 `artifacts/runtime-check`；未操作正在运行的旧桌宠。

## 0.2.0发布包历史记录

2026年10月27日00:05（Asia/Shanghai）对 `dist/TablePet-0.2.0-win-x64` 完成发布验证，退出码0。包内包含Microsoft.NETCore.App与Microsoft.WindowsDesktop.App 10.0.14；发布AppHost为Windows x64。`--check-assets`在不存在的DOTNET_ROOT、仅含System32的PATH下成功读取全部真实PNG，没有清单/帧回退；未启动窗口、托盘、设置或单实例信号。

全部1296张实际引用PNG、manifest、图标和fallback与源码SHA-256一致；8个片段的规定首尾共享同一像素。1000次真实素材状态仿真通过，共39803条断言；Release构建零警告、零错误，自动单元测试66/66通过。100/125/150/200%和120/240/480DIP参数通过原始512像素读取及alpha命中检查；原生位图现在由WPF在呈现时缩放，不能把这些参数检查当成不同物理显示器的实测。

新动作的逐帧视觉审查见 `MOTION-QA.md`，真实墙钟120/240Hz的非交互解码/缓存回放数据见 `MOTION-PERFORMANCE.md`。发布验证没有测量DWM实际窗口呈现帧率，也没有重新进行此前被用户Esc停止的桌面操作。

```powershell
powershell -File tools/build.ps1 -Test -Publish
powershell -File tools/verify-runtime.ps1 -PublishDirectory dist/TablePet-0.2.0-win-x64 -ProbeAppHost
powershell -File tools/benchmark-motion.ps1
```

机器可读发布证据保存在 `artifacts/runtime-check/summary.json` 与 `asset-runtime.json`。自检时限已随素材量增加到60秒。构建脚本兼容Windows PowerShell 5.1的UTF-8读取，并输出带版本ZIP、最新ZIP别名和SHA-256。

## 以下为0.1.0历史检查记录

以下数量、路径和旧的尺寸解码描述仅对应10月26日首版，不代表当前v2素材；当前数据以上节及动作验收记录为准。

## 当前证据

2026 年 10 月 26 日 21:58（Asia/Shanghai），对最终发布目录 `dist/TablePet-0.1.0-win-x64` 执行 `tools/verify-runtime.ps1 -PublishDirectory dist/TablePet-0.1.0-win-x64 -ProbeAppHost`，整体退出码为 0。`TablePet.exe` PE 机器类型为 AMD64（`0x8664`），`TablePet.runtimeconfig.json` 使用 `includedFrameworks`，包含 `Microsoft.NETCore.App` 与 `Microsoft.WindowsDesktop.App` 10.0.14。包内已检查到非空的 `coreclr.dll`、`hostfxr.dll`、`hostpolicy.dll`、`System.Private.CoreLib.dll`、`PresentationCore.dll`、`PresentationFramework.dll`、`WindowsBase.dll` 和应用程序集。

自包含入口检查也已通过：将子进程 `DOTNET_ROOT` 和 `DOTNET_ROOT_X64` 指向不存在的目录，PATH 限制为 Windows System32，再执行发布程序的 `--check-assets`。该进程成功加载真实素材并解码，未发生清单或帧回退，以 0 退出。此检查未创建窗口、托盘、设置或实例信号，没有干扰原来运行的桌宠。它证明发布程序使用随包运行时进入托管代码并加载资产；不能证明某台全新 Windows 电脑上的完整桌面交互。

最终发布角色包已与源码角色包逐项比较 SHA-256，manifest、fallback、tray.ico 和全部实际使用帧一致。8 个片段、18 张实际使用的 PNG 全部满足 RGBA、统一画布、透明角落和规定的首尾像素相等检查；12 组尺寸/DPI 参数的实际 WPF 解码通过。使用发布包资产完成 1000 次真实素材状态仿真，共 4019 个断言通过，覆盖普通眨眼、完整入睡、入睡中断倒播、睡眠循环、唤醒、起身、保持、回位、拖拽冻结、冷却和系统暂停恢复。源包和发布包分别检查通过。该证据证明资产可被应用播放器使用，不能替代视觉观感或真实桌面输入测试。

本次源码审计还发现并促成一处修复：关闭置顶且猫咪被其它窗口遮挡时，原先只用全局光标位置与 alpha 掩码会错误触发悬停。`PlatformService.IsPointerOverPet` 现通过 `WindowFromPoint`/`GetAncestor` 判断实际命中窗口，`PetWindow.Hit` 在没有鼠标捕获时要求该命中属于宠物。此处已完成代码修复；遮挡条件下的真实鼠标行为仍待桌面验收，不能据静态代码标为已实测。

冷却设置的立即生效问题也已修复：行为控制器记录反应结束时间，调整 `CooldownMs` 后据此重建当前期限，并在系统恢复时清空旧基准。新增的增加冷却与改为零的回归检查已在主构建验证中通过；当前核心及应用自动测试总计 54 项。该数量与下方真实素材检查的断言数量分别统计。

## 可重复检查

```powershell
# 最终素材与发布包已准备好后，进行结构、文件一致性、WPF 解码和状态循环检查
powershell -File tools\verify-runtime.ps1 -PublishDirectory dist/TablePet-0.1.0-win-x64

# 仅检查自包含发布结构，不启动应用
powershell -File tools\verify-runtime.ps1 -PublishDirectory dist/TablePet-0.1.0-win-x64 -SkipAssets

# 验证自包含 AppHost 无界面加载真实资产；不会干扰已有桌宠实例
powershell -File tools\verify-runtime.ps1 -PublishDirectory dist/TablePet-0.1.0-win-x64 -ProbeAppHost
```

默认脚本将源资产与发布资产逐项比较 SHA-256，并在独立 STA 测试进程中使用应用自己的 `AssetCatalog` 和 `BehaviorController`：

- 读取全部真实 PNG，检查统一画布、可见主体和真正透明的角落。
- 检查 idle、blink、fall_asleep、sleep_loop、wake、rise、hold、settle 的规定首尾连接处使用像素相同的姿态。
- 以 120、240、480 DIP 和 100%、125%、150%、200% 参数调用实际 WPF 解码缓存。此项验证图像解码尺寸，不验证显示器切换或窗口几何。
- 使用可控时钟执行 1000 次真实素材睡眠、入睡中断、唤醒、起身、保持、拖拽冻结、回位、冷却与暂停恢复循环。此项是加速逻辑循环，不是长时间桌面驻留或内存泄漏测量。

`-ProbeAppHost` 以不存在的 `DOTNET_ROOT`/`DOTNET_ROOT_X64`、仅含 Windows System32 的 PATH，以及 `DOTNET_MULTILEVEL_LOOKUP=0` 启动发布程序的 `--check-assets` 入口；要求 15 秒内以 0 退出。该入口会加载和解码真实素材，检测到清单或帧回退则返回失败。它在单实例协调、设置、窗口和托盘初始化之前执行，因此不向现有桌宠实例发信号、不创建窗口、不修改设置。这比仅执行一个空入口更强，但仍不能代替完整桌面启动或全新 Windows 电脑的验证。

机器可读证据位于 `artifacts/runtime-check/summary.json` 和 `asset-runtime.json`。这些文件按实际运行生成，未运行的检查不能据此标为通过。

## 完成审计边界

本轮按用户确认先交付当前电脑可运行的首版，并列明尚待验收的环境项。源码已覆盖透明无边框窗口、窗口不激活、置顶与整窗穿透、拖拽捕获、主体 alpha 命中、系统暂停恢复、设置与托盘、原子 JSON 持久化、单一行为控制器和本地素材缓存。以下仍需要实际目标环境证据：

1. 在记事本连续输入时启动、触碰和拖拽宠物，确认输入焦点不被夺取。
2. 点击真正透明像素后，由独立后方应用实际接收点击；不能用窗口样式或程序内 alpha 命中代替。
3. 真实拖拽移出身体后的捕获释放，以及整窗穿透与托盘恢复。
4. 100%、125%、150%、200% 与混合 DPI 多屏拖动、负坐标工作区和断开副屏。
5. 系统锁屏、休眠及恢复后的行为和可见性，以及正常退出后托盘图标清理。
6. 没有开发工具的 Windows 11 x64 电脑上的完整发布包运行。
7. 最终动画的逐帧视觉一致性、深浅背景边缘和播放观感，以及实时时长下的 CPU、内存与长时间运行记录。

未发现新的明确功能缺口不等于以上验收已完成。实际通过结果应追加到 `docs/ACCEPTANCE.md`，保留对应环境、操作、时间和失败复现信息。
