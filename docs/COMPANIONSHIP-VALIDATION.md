# 陪伴成长与界面验收摘要

覆盖 2026 年 11 月 10 日的 0.6.0 发布及 11 月 11 日的设置与“我们的日常”界面改进。功能规则见 [成长设计](COMPANIONSHIP-DESIGN.md) 和 [内容表](COMPANIONSHIP-CONTENT.md)。

## 自动验证

| 范围 | 已完成的检查 |
| --- | --- |
| 0.6.0 发布基线 | Core 329 项、App 181 项，共 510 项通过，0 失败、0 跳过；Release 构建通过 |
| 后续设置与日常面板改进 | 完整 App 测试 191 项通过；覆盖预设、自定义时长、隐藏输入项及损坏存档保护 |
| 成长规则 | 四阶段、有效陪伴门槛与上限、跨午夜、时钟变化、隐藏／拖拽／暂停、全部 12 条回忆及保存重建 |
| 工作与点击资格 | 自动启动不直接激活成长、点击去重、自然完成与提前取消、工作和休息轮次关联 |
| 陪伴文字 | 72 条阶段短句与 6 条辅助短句、上下文优先级、旧存档兼容、一次性提示消费及保存失败 |
| 存储 | 文件替换失败重试、主文件损坏恢复、主备份均损坏时停止写入、未来版本保护、设置与成长独立 |
| 文字呈现 | 78 条内容逐条通过 120／240／480 DIP × 96／144／192 DPI 的 WPF 离屏渲染；最长文案抽样目检无裁切 |
| 回忆插画 | 12 个 ID 的资源完整性、解码缓存、缺图或损坏降级、窄卡片与不同 DPI 的内容呈现 |

主要自动测试入口：

- [CompanionshipTests](../tests/TablePet.Core.Tests/CompanionshipTests.cs)：成长账本、阶段与回忆。
- [BehaviorFactTests](../tests/TablePet.Core.Tests/BehaviorFactTests.cs)、[CompanionDialogueTests](../tests/TablePet.Core.Tests/CompanionDialogueTests.cs)：资格事实、点击与文字调度。
- [CompanionSessionTests](../tests/TablePet.App.Tests/CompanionSessionTests.cs)、[CompanionStateServiceTests](../tests/TablePet.App.Tests/CompanionStateServiceTests.cs)：应用接线及真实文件读写。
- [CompanionWindowTests](../tests/TablePet.App.Tests/CompanionWindowTests.cs)、[SettingsWindowTests](../tests/TablePet.App.Tests/SettingsWindowTests.cs)：面板内容、设置和异常状态。
- [CompanionRenderingTests](../tests/TablePet.App.Tests/CompanionRenderingTests.cs)、[CompanionMemoryIllustrationTests](../tests/TablePet.App.Tests/CompanionMemoryIllustrationTests.cs)：文字及插画呈现。

## 界面与安装检查

真实桌面已检查独立示例存档的初见状态、首个相伴日与已读持久化；关闭成长后历史保留。60 天、12 条回忆的示例存档正确显示“默契”和暂停状态，滚动后内容与关闭按钮完整。示例存档用于界面验收，不代表真实用户的长期体验。

设置和日常面板改进完成 39 张 WPF 离屏渲染，覆盖日常／工作／自定义设置、初见空状态、多条回忆、默契、暂停、损坏与未来版本存档、说明展开和列表底部。设置视口为 620×760 与 460×480，日常视口为 520×720 与 420×480，另检查 192 DPI；无横向溢出，固定页脚可见，24 项下拉框及滑块模板结构检查通过。

12 幅回忆插画均检查主题、肢体结构及 80 DIP 卡片呈现；窄卡片和 192 DPI 渲染通过。图片属于静态装饰，不改变成长规则、回忆条件或角色动画。

0.6.0 发布安装包完成完整性检查和解包，40 个载荷文件与便携 ZIP 逐项哈希一致，23 个运行素材通过自检。后续本地更新时，发布与安装副本的素材自检均通过，设置、成长主文件及备份在程序替换期间保持一致。更名后已验证实际覆盖升级、同版本重装及快捷方式启动。

## 验证边界

- 离屏布局和 DPI 检查不能替代真实显示器间切换；设置与面板改版当轮未完成完整的原生鼠标／键盘交互复验。
- 锁屏、休眠与混合 DPI 的真实环境验证不能由模拟事件或假时钟测试代替。
- 0.6.0 发布前的安装器验证是完整性与解包检查；后续实际升级验证不回写为发布前已完成。
- 多周使用、长期阶段体验和留存效果尚未测量。

## 复现

```powershell
powershell -File tools/build-skeletal.ps1 -Test
```

可通过 `-DotnetPath` 指定 .NET SDK。测试输出、截图和部署明细只保存在本地忽略目录；公开记录保留验收结果、测试入口与已知边界。
