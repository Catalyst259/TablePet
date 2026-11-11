# TablePet

一只住在 Windows 桌面上的草莓猫。

<img src="assets/strawberry-cat/fallback.png" width="200" alt="头顶草莓和甜筒的灰白色小猫" />

闲下来时逗逗它，忙的时候让它陪你工作。它会打哈欠、打盹，偶尔和萤火虫玩一会儿。

[下载 Windows 版](https://github.com/Catalyst259/TablePet/releases/latest) · [使用说明](docs/SETTINGS.md)

## 和小猫相处

- **点点它。** 单击、双击都能互动；拖动可以换个位置，右键打开菜单。
- **一起工作，也一起休息。** 工作模式默认是 25 分钟工作、5 分钟休息，到点会提醒你。时长可以在设置里调整。
- **慢慢熟悉起来。** 相处久了，关系会从初见走到熟悉、亲近和默契，小猫的回应也会跟着变化。
- **留下共同的日常。** 第一次一起工作、一次睡醒见面，都可能留下一条回忆。在“我们的日常”里可以回看相伴天数和 12 条带插画的回忆。

不用每天打卡，离开一段时间也不会扣熟悉度。成长记录保存在本机；想安静待着时，可以在设置里关闭陪伴文字或成长记录。

## 下载和使用

适用于 **Windows x64**。在 [Releases](https://github.com/Catalyst259/TablePet/releases/latest) 下载名字以 `-Setup.exe` 结尾的安装包，按提示安装即可，已自带 .NET 运行时。也可以选择 ZIP 便携包，完整解压后运行里面的桌宠程序。

打开后，右键小猫就能切换日常／工作模式、进入设置，或打开“我们的日常”。托盘菜单里也有这些入口。

目前发布的 0.6.0 文件名仍带 `.Skeletal` 后缀；源码中的程序名称已统一为 TablePet。更新和卸载的具体说明见 [安装说明](docs/INSTALLATION.md)。

## 源码和构建

项目使用 C# / WPF，当前开发分支是 `skeleton`。建议这样克隆，避免顺带下载旧版素材归档：

```powershell
git clone --single-branch --branch skeleton --no-tags https://github.com/Catalyst259/TablePet.git TablePet
cd TablePet
```

在 Windows 上安装 .NET SDK 10.0.403，然后运行：

```powershell
powershell -File tools/build-skeletal.ps1 -Test -Publish
```

生成的程序和便携包在 `dist/`。只想编译和跑测试，可以去掉 `-Publish`。也可以用 [SDK 安装脚本](tools/setup-dotnet.ps1) 准备开发环境；制作安装包见 [安装器说明](installer/README.md)。

仓库里已经带有编译好的动画资源。需要修改动画、重建素材或查找旧版本时，见 [开发说明](docs/DEVELOPMENT.md)。

## 更多说明

- [设置与操作](docs/SETTINGS.md)
- [成长和回忆规则](docs/COMPANIONSHIP-DESIGN.md)
- [回忆插画](docs/MEMORY-ILLUSTRATIONS.md)
