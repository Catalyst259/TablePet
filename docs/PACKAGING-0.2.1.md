# 0.2.1 打包记录

日期：2026-10-27。此轮仅保存源码、制作安装程序和验证发布载荷，未改变桌宠动作或交互。

## 交付文件

- `TablePet-0.2.1-Setup.exe`：中文 Windows x64 安装向导，包含运行时和素材；按当前用户安装、默认创建桌面及开始菜单快捷方式，支持系统应用列表卸载。
- `TablePet-0.2.1-source.zip`：源码、测试、完整动画素材、原始参考图片与 Word 方案、提示词、依赖锁文件、构建与安装脚本、项目文档。ZIP 内附 `SOURCE-MANIFEST.sha256`。
- `TablePet-0.2.1-win-x64.zip`：保留已验证的便携版。

每个交付文件旁均有 `.sha256`。源码归档排除 `.git`、本机 SDK、`node_modules`、编译缓存、发布物和运行设置；全部源码文件逐项解压读取并比对 SHA-256，确保备份与工作区一致。

## 安装包构建

使用 Inno Setup 6.7.3，编译成功。编译器官方安装包的 Windows 数字签名有效，制作时仅静态提取到项目工具目录。编译器及解包工具来源见 `installer/README.md`。

成品大小为 292,953,737 字节，SHA-256：

```text
0a687b4ccff6ae1a20a79a60538c087505360845a19be567edef245325d95591
```

草莓猫应用和本次安装包未配置代码签名证书，Windows 可能显示“未知发布者”。编译工具自身的签名不代表应用成品已签名。

## 验证范围

安装器使用 innounp 静态检查压缩内容并解包。逐文件比对版本化发布目录，只有根 README 和安装说明使用本轮更新版本。

已通过：

- 安装包压缩完整性检查，1833 个载荷文件逐一 SHA-256 比对一致。
- 应用和安装包版本均为 0.2.1；自带 .NET Core 与 Windows Desktop 10.0.14。
- 1316 张被引用 PNG、2062 个动画帧槽完整；解包后的 `TablePet.exe --check-assets` 退出码为 0。
- 验证脚本在 Windows PowerShell 5.1 执行成功；既有桌宠进程 PID 30008 保持运行，用户设置 SHA-256 未变。

真实执行安装向导、快捷方式启动、覆盖升级、卸载流程，以及无开发环境的第二台电脑和混合 DPI 多屏环境仍待验收。本轮未安装应用、修改注册表或操作桌面界面。

源码备份可用 `tools/package-source.ps1` 重建；安装器可用 `tools/build-installer.ps1` 重建；`tools/verify-installer.ps1` 可复现静态验包及无界面素材自检。生成的详细检查报告位于工作区 `artifacts/installer-check` 与 `artifacts/source-package-check`。
