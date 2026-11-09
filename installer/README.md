# 骨骼版 Windows 安装包

`TablePet.Skeletal.iss` 使用 Inno Setup 6 构建中文、按当前用户安装的 Setup，不要求管理员权限。默认输入为已发布的 `dist/TablePet.Skeletal-0.5.0-win-x64.zip`，输出为 `dist/TablePet.Skeletal-0.5.0-Setup.exe`。

先安装 Inno Setup 6.5.0 或更新版本，然后在仓库根目录执行：

```powershell
powershell -File tools/build-installer.ps1
powershell -File tools/verify-installer.ps1
```

若构建脚本未自动找到编译器，可指定实际的 `ISCC.exe` 路径：

```powershell
powershell -File tools/build-installer.ps1 `
  -IsccPath "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
```

构建脚本先核对发布 ZIP 与其 `.sha256` 文件，再解压至独立制作目录，并将该目录作为 `PublishDir` 传给 Inno Setup。安装载荷完整取自该 ZIP，包括文档和图标；不会用当前工作目录的 README 或文档覆盖已发布内容。`-ZipPath` 和 `-OutputDirectory` 可选择其他输入包与输出目录。脚本不会覆盖已存在的同名 Setup。

默认安装位置是 `%LocalAppData%\Programs\TablePet.Skeletal`，执行文件为 `TablePet.Skeletal.exe`，桌面和开始菜单快捷方式名为“草莓猫桌宠 · 骨骼版”。桌面快捷方式默认创建，可在向导中取消；安装完成页可选择启动程序。

骨骼版使用独立且固定的 AppId `{E5578DE9-62E6-4E72-8DC8-B4ED2FAB57EE}`，支持同版本覆盖安装和后续升级。安装前会检查目标目录；存在原版 `TablePet.exe`、`TablePet.dll`，或目标属于原版默认/已注册安装位置时，会拒绝安装，避免共用 `Assets` 覆盖原版。静默安装和隐藏目录页的升级也执行此检查。

覆盖安装前，只通过 `Local\TablePet.Skeletal.Exit` 请求骨骼版正常退出，并等待其 `Local\TablePet.Skeletal.StrawberryCat` 互斥量释放。不会发送原版的退出事件。旧骨骼资源清理仅限安装目录中的 `Assets\strawberry-cat\skeletal`。

卸载由 Windows“已安装的应用”或安装目录的卸载程序完成。卸载只在开机启动值 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\TablePet.Skeletal` 精确指向本安装目录的执行文件时删除该值；若它指向其他便携版或安装位置，则保留。安装和卸载均保留 `%LocalAppData%\DesktopPet.Skeletal` 和原版 `%LocalAppData%\DesktopPet` 的个人配置。

简体中文语言文件及来源说明在 [Languages/README.md](Languages/README.md)。自动解包与哈希核验只证明载荷一致性，真实安装、同版本覆盖、快捷方式与卸载行为需要相应的安装验证，不能由编译成功代替。
