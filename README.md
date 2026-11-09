# TablePet 骨骼重写版

从 TablePet 0.4.0 独立重写的 Windows WPF 桌宠。原仓库保持冻结；此目录是单独的 Git 仓库，没有继承原仓库的历史图片和 Git 对象。

保留全部 16 个动作片段、萤火虫装饰轨道、原始时间槽和交互行为，在运行时重建画面，替代整帧 PNG 序列。动画资源由 **211.58 MiB 降至 58.18 MiB，减少 72.50%**；包含 fallback、图标和清单的全部运行素材由 213.22 MiB 降至 59.83 MiB，减少 71.94%。

## 渲染方式

- 萤火虫：移植已批准的身体、肩部、前臂、爪、眼球与高光控制；少量纹理和骨骼姿态在 C# 中合成。飞虫使用小尺寸旋转附件，独立定位、透明度和遮挡。
- 工作模式：一张基础猫贴图、局部眼睛表情修正及独立电脑前景。原有根骨骼呼吸位移、进出场淡化继续由同一个行为时钟驱动。
- 其余动作：拟合头、身体、双耳、双眼、口鼻、尾巴和双爪的二维骨骼，逆向蒙皮后叠加无损修正数据。旧 AI 补帧会改变毛发、表情和透明边缘，这些不能由少量刚性骨骼表达的内容需要保留。

这是**骨骼和贴图驱动、带无损修正的混合实现**。不是所有动作都能仅靠几张贴图和骨骼曲线保持原版像素；精确度优先于进一步丢弃纹理。无需在运行时解压或写出完整 PNG 序列，也不依赖 Python、Node.js、RIFE 或原仓库。

## 运行与构建

发布目录的 `TablePet.Skeletal.exe` 可直接运行。它使用单独的进程通信标识、开机启动项和 `%LocalAppData%/DesktopPet.Skeletal/` 设置目录，可与冻结的原版并存。

使用 .NET SDK 10.0.403 构建，仓库已包含编译好的骨骼资源：

```powershell
powershell -File tools/build-skeletal.ps1 -Test -Publish
```

可用 `-DotnetPath <dotnet.exe>` 指定 SDK，或运行 `tools/setup-dotnet.ps1` 在本仓库安装 SDK。发布包自带 .NET，不要求使用者安装开发环境。

```powershell
# 不启动窗口或写入用户设置的素材检查
.\dist\TablePet.Skeletal-0.5.0-win-x64\TablePet.Skeletal.exe --check-assets

# 对冻结原版作实际 WPF 逐像素、时序、缓存与缩放验证
dotnet run --project tests/TablePet.PlaybackProbe -c Release -- --verify-skeletal `
  ../TablePet/assets/strawberry-cat ./assets/strawberry-cat ./artifacts/skeletal-verification
```

详细动作来源见 [动画盘点](docs/ANIMATION-AUDIT.md)。`reference/frozen-source.json` 保存原工作副本全部受版本控制文件及未忽略的本地候选的 SHA-256；只读核验命令为 `node tools/frozen-source.cjs verify ../TablePet`。

## 验证记录

全量验收与包体实测记录在 `reference/skeletal/`。原生桌面上的多屏、混合 DPI、锁屏/恢复及长时间使用，仍需要对应环境验收；离屏像素与计时测试不代表显示器实际呈现帧率。

## 重新编译骨骼资源

日常构建不需要此步骤。仅在需要从冻结原图重新制作时，在本目录安装 Node.js 依赖及 Python 制作工具，再传入原仓库的只读路径。制作脚本的输出始终位于本仓库；运行时不需要这些工具。

```powershell
npm ci
python -m venv .tools/rig-python
.tools/rig-python/Scripts/python.exe -m pip install -r tools/rig-requirements.txt
npm run assets:compile -- ../TablePet
npm run assets:size -- ../TablePet
```

编译先拟合骨骼并生成无损残差，再以 Brotli 11 重压并逐块验证；14 个跨动作共享姿态及 9 个动作入口提升为独立检查点，避免反向播放和冷启动时反复重算。原来的大图和制作历史继续保存在被冻结的仓库中，无须复制进这个新仓库。
