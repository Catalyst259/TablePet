# TablePet Windows 安装包

`TablePet.iss` 使用 Inno Setup 6 构建中文、按当前用户安装的 Setup，不要求管理员权限。版本从应用项目读取；0.6.0 的默认输入为已验证的 `dist/TablePet-0.6.0-win-x64.zip`，输出为 `dist/TablePet-0.6.0-Setup.exe`。

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

`.sha256` 文件用于本地构建与验证，不上传到 GitHub Releases。

全新安装默认位于 `%LocalAppData%\Programs\TablePet`，执行文件为 `TablePet.exe`，安装界面、桌面和开始菜单快捷方式均使用“TablePet”。桌面快捷方式默认创建，可在向导中取消；安装完成页可选择启动程序。已有注册安装沿用原目录，重命名不会搬动安装目录或个人数据。

继续使用原骨骼版的 AppId `{E5578DE9-62E6-4E72-8DC8-B4ED2FAB57EE}`，支持同版本覆盖安装和后续升级。安装前会检查目标目录：历史版本注册目录、含 `TablePet.dll` 的目录，以及无法确认属于当前产品的 `TablePet.exe` 均会被拒绝。当前 AppId 的注册目录，或同时带有骨骼资源包且执行文件数字版本至少为 0.5.0 的便携程序可以正常重装；空的新默认目录不会因名称与历史版本相同而被拒绝。静默安装和隐藏目录页的升级也执行此检查。

覆盖安装前，沿用 `Local\TablePet.Skeletal.Exit` 请求当前程序正常退出，并等待其 `Local\TablePet.Skeletal.StrawberryCat` 互斥量释放，兼容尚未改名的版本且不触碰历史版本进程。旧骨骼资源清理仅限安装目录中的 `Assets\strawberry-cat\skeletal`，同时移除同目录的旧 `TablePet.Skeletal.exe` 和符号文件。旧“草莓猫桌宠 · 骨骼版”快捷方式只在目标确实指向本次安装目录的新旧程序时清理，指向其他目录的快捷方式会保留。

开机启动值保留内部名称 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\TablePet.Skeletal`；安装后仅在其精确指向同目录的旧程序时更新为 `TablePet.exe`，关闭状态不会被开启，指向其他便携目录的值不变。卸载由 Windows“已安装的应用”或安装目录的卸载程序完成，仅删除精确指向本安装目录新旧执行文件的启动值。安装和卸载均保留 `%LocalAppData%\DesktopPet.Skeletal` 和历史版本 `%LocalAppData%\DesktopPet` 的个人配置与成长记录。

简体中文语言文件及来源说明在 [Languages/README.md](Languages/README.md)。自动解包与哈希核验只证明载荷一致性，真实安装、同版本覆盖、快捷方式与卸载行为需要相应的安装验证，不能由编译成功代替。
