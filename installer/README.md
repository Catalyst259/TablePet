# Windows 安装包

安装器使用标准 Inno Setup 6。稳定 AppId 用于后续原位升级；按当前用户安装，无需管理员权限。默认创建桌面与开始菜单快捷方式，支持 Windows 应用列表卸载。

先运行 `powershell -File tools\build.ps1 -Test -Publish`，再运行：

```powershell
powershell -File tools\build-installer.ps1 -IsccPath 'C:\路径\Inno Setup 6\ISCC.exe'
powershell -File tools\package-source.ps1
```

编译器请从 [Inno Setup 官方网站](https://jrsoftware.org/isdl.php) 获取。构建脚本也会查找项目 `.tools/inno-setup/compiler/ISCC.exe` 和常规安装目录。默认仅打包与项目版本一致的 `dist/TablePet-版本-win-x64`，不会混入旧版本；已存在的同名成品不会自动覆盖。

0.2.1 使用 Inno Setup **6.7.3**。官方编译器安装包来自 `https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe`，SHA-256 为 `9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732`，Windows 验证其 Pyrsys B.V. 数字签名有效。制作时仅静态提取编译器到项目工具目录，无需修改系统开发环境。

安装时通过应用已有退出事件正常保存设置并退出，最多等待 10 秒，失败则停止覆盖。卸载仅清理指向本安装目录的开机启动项，保留 `%LocalAppData%\DesktopPet` 用户设置；安装不会自动开启开机启动。便携版已启用的开机启动可在安装版设置中关闭再开启，以更新路径。

简体中文翻译文件位于 `Languages/ChineseSimplified.isl`，保留上游作者说明。分发包为自包含 .NET 10 Windows x64，不需要额外下载运行时。未配置代码签名证书。

## 非安装式验证

`tools/verify-installer.ps1` 使用现成 innounp 静态测试并解包安装器，核对每个文件的 SHA-256，然后对解包程序运行无界面 `--check-assets`。它不执行安装器、不创建快捷方式、不操作已运行的桌宠。验证记录写入 `artifacts/installer-check`。

```powershell
powershell -File tools\verify-installer.ps1 -ExtractorPath 'C:\路径\innounp.exe'
```

本版使用 [Inno Setup 官方第三方工具索引](https://jrsoftware.org/is3rdparty.php) 链接的 [Jürgen Rathlev innounp Unicode](https://www.rathlev-home.de/tools/prog-e.html) 2.71.2。作者下载地址为 `https://www.rathlev-home.de/tools/download/innounp-2.zip`；本次下载 ZIP 的 SHA-256 为 `10fce27b6344b7674d5625cc34afca1c88c12183d957c6d93e401097fa2556cd`。旧版 innoextract 1.9 不支持本版安装器格式。

这些检查不能替代真实安装、覆盖升级和卸载流程验收。
