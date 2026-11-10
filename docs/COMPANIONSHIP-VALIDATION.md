# 陪伴成长实现与验收记录

日期：2026 年 11 月 10 日。分支：`skeleton`。对应 [设计](COMPANIONSHIP-DESIGN.md) 与 [内容表](COMPANIONSHIP-CONTENT.md)。本记录区分功能实现、自动化证据和真实桌面验证，未把代码接线或假时钟测试当作平台实测。

## 当前结果

- 0.6.0 发布前全量验证：Core 329 项、App 181 项，共 510 项通过，0 失败、0 跳过。TRX 位于 `artifacts/release-0.6.0/test-results/`，分别为 `release-0.6.0_net10.0_20261110213812.trx` 与 `release-0.6.0_net10.0_20261110213822.trx`。包含最终回忆插画加载、缓存、缺图降级和窄卡片布局检查；本页下方保留各次迭代的验证范围。
- Release 构建通过，0 警告、0 错误（本次主任务构建记录）。
- 点击文字调度调整后的全量测试基线：Core 329 项、App 176 项，共 505 项通过，0 失败、0 跳过。每次实际接受的日常点击统一使用新短句，取消旧单字“喵”兜底；旧存档文字计数、文字开关和保存失败等路径均包含在回归中。
- 随后的口语修订改写 42 处短句，内容 ID、数量、成长与调度逻辑不变。针对修订后的真实内容运行文案选择、应用接线与全量文案渲染检查，共 51 项通过（Core 31、App 20）；78 条短句仍逐条覆盖 3 种尺寸 × 3 档 DPI。重新目检 120 DIP / 96 DPI 的最长文案，无裁切。
- 首版包含四阶段关系、每日有效陪伴账本、72 条阶段短句、6 条辅助短句、12 条永久回忆、陪伴面板、两个独立设置开关和独立存档。初始实现没有增加素材；随后按用户要求加入下述 12 幅回忆静态插画，角色动画和语音仍未新增。
- 真实桌面已检查独立目录的初见面板：0 天状态正确；有效可见陪伴后变为 1 天，出现“第一个相伴日”；该回忆的 `isRead` 已落盘。设置中关闭“记录相伴成长”后，独立目录的 `companionshipEnabled` 保存为 `false`，原有日账本与回忆保留。
- 独立 `grown` 示例存档（60 天、12 条回忆、关闭成长）的真实窗口显示“默契”、60 天和成长暂停提示，不显示下一阶段进度。首次打开只将首屏最新两条记为已读；滚至底部后，旧回忆及关闭按钮未裁切。示例数据用于界面验收，不是真实用户的 60 天体验。
- 全部 78 条真实内容已逐条通过 120／240／480 DIP × 96／144／192 DPI 的 9 组 WPF 离屏渲染检查。最长文案生成 9 张图片，主任务目检了 120 DIP @ 96 DPI、240 DIP @ 144 DPI 和 480 DIP @ 192 DPI，文字清晰且画布边缘无裁切；这不等同于真实显示器切换 DPI 的验证。
- 本次日常点击文字调整已完成自带运行时的本地预览发布，目录为 `dist/TablePet.Companionship-preview-win-x64/`，publish 退出 0；更新后的安装目录 `--check-assets` 退出 0。首版此前已使用同字节可执行文件副本和独立 `packaged-smoke` 数据目录做界面验证，面板显示 1 个相伴日和“第一个相伴日”；该次界面记录不冒充本次点击修改后的实机点击测试。没有发布到 GitHub。
- 更新时正常退出已安装的旧进程，备份后将本次程序复制到 `D:\_APPS\TablePet.Skeletal`，可执行文件及符号文件与发布输出的哈希一致。退出后至替换和自检完成，`settings.json`、`companion-state.json` 及其 `.bak` 哈希保持一致。随后重新启动安装版，进程响应正常；桌面快捷方式仍指向固定安装路径且不附加预览参数。
- 留存提升尚未测量；多周试用和默契阶段后的长期体验观察属于后续产品验证。
- 首版包已以带／不带末尾目录分隔符的同一路径启动验证，第二次启动复用原进程；随后通过该独立目录的 `--quit` 正常退出。临时验收程序副本已移除；本次修改未变更数据目录身份逻辑。

复现命令（使用仓库本地 SDK 10.0.403）：

```powershell
.\.tools\dotnet\dotnet.exe build TablePet.slnx -c Release
.\.tools\dotnet\dotnet.exe test TablePet.slnx -c Release --logger 'trx;LogFilePrefix=click-text'
```

点击调度全量基线的 TRX：

- `tests/TablePet.Core.Tests/TestResults/click-text_net10.0_20261110180944.trx`：329 / 329。
- `tests/TablePet.App.Tests/TestResults/click-text_net10.0_20261110180954.trx`：176 / 176。

口语修订后定向验证的 TRX：

- `tests/TablePet.Core.Tests/TestResults/natural-copy_net10.0_20261110182331.trx`：31 / 31。
- `tests/TablePet.App.Tests/TestResults/natural-copy_net10.0_20261110182348.trx`：20 / 20。

修订后的渲染图位于 `artifacts/companionship-rendering-natural/`。自动测试验证内容结构、选择逻辑与呈现边界；文案另做逐句审阅，不以测试通过代替文案判断。

首次测试因语言后缀 JSON 被 SDK 识别为卫星资源而失败；`TablePet.Core.csproj` 已显式设置 `WithCulture="false"` 和稳定资源名，以上结果来自修复后的整套测试。旧失败报告不能用作当前结果，生成的 `TestResults` 不参与提交或发布。

最终审查另修复并补充了日常点击承载里程碑、暂停时保留阶段提示、切换离开工作清理旧提示，以及设置页重复选择工作和键盘选择的回归。设置的外部加载不算主动选择，非法时长也不会开启陪伴计时。`ApplicationDataLocationTests` 验证尾部分隔符、大小写、相对路径段等目录拼写归一化，同一存档目录共用单实例通信；显式指定默认目录也不会绕过默认实例。

## 证据文件

下表中的方法名均指以下已检查的源码。测试通过只证明其断言覆盖的范围。

| 简称 | 文件 | 覆盖内容 |
| --- | --- | --- |
| Growth | [CompanionshipTests.cs](../tests/TablePet.Core.Tests/CompanionshipTests.cs) | 假时钟成长、日账本、活动资格、全部回忆与状态校验 |
| Facts | [BehaviorFactTests.cs](../tests/TablePet.Core.Tests/BehaviorFactTests.cs) | 实际行为控制器事件、点击去重、自然完成与取消 |
| Speech | [CompanionDialogueTests.cs](../tests/TablePet.Core.Tests/CompanionDialogueTests.cs) | 每次有效点击短句、选择与去重、优先级、持久化回执与降级 |
| Session | [CompanionSessionTests.cs](../tests/TablePet.App.Tests/CompanionSessionTests.cs) | 行为事实到成长和文字的应用层接线 |
| Store | [CompanionStateServiceTests.cs](../tests/TablePet.App.Tests/CompanionStateServiceTests.cs) | 真实临时目录的保存、失败重试、备份恢复和版本保护 |
| Panel | [CompanionWindowTests.cs](../tests/TablePet.App.Tests/CompanionWindowTests.cs) | WPF 面板内容、阶段、设置独立性与历史保留 |
| Bubble | [SpeechBubbleWindowTests.cs](../tests/TablePet.App.Tests/SpeechBubbleWindowTests.cs) | 既有气泡像素边界、原生穿透、不激活与跟随 |
| Render | [CompanionRenderingTests.cs](../tests/TablePet.App.Tests/CompanionRenderingTests.cs) | 全部真实新文案的三尺寸、三 DPI 离屏渲染 |
| Host | [CompanionHostTests.cs](../tests/TablePet.App.Tests/CompanionHostTests.cs) | 实际 WPF 陪伴窗口、隐藏／穿透上下文、单实例及最小化恢复 |
| Settings | [SettingsWindowTests.cs](../tests/TablePet.App.Tests/SettingsWindowTests.cs) | 工作模式初选、重复选择、非法参数与键盘事件兼容 |

## 32 项设计验收映射

“自动已验”指该规则已有通过的测试，必要的组合行为也已核对源码；“部分已验”表示仍有明确缺口。真实锁屏、休眠和混合 DPI 不由伪造系统事件替代。

| 编号 | 当前证据与实际覆盖 | 结论／边界 |
| --- | --- | --- |
| G01 | Growth `AutostartCannotGrowAndRepeatedActivationAddsNoDirectPoints` 推进一小时且未主动激活，账本与回忆均为空；Session `AutomaticWorkDoesNotActivateGrowthButABusyClickDoes` 覆盖自动工作。 | 自动已验。 |
| G02 | Growth `VisibleTimeHasDocumentedThresholdsAndDailyCap` 对 59、60、300、600、1200 秒及一小时分别断言分数、相伴日与 20 分钟上限。 | 自动已验。 |
| G03 | Growth `ShortSessionsAccumulateButReopeningDoesNotRestoreAnActiveWindow` 将两个 30 秒会话经序列化重建后相加，重启后先无授权、重新操作后才到 3 分。 | 自动已验。 |
| G04 | Growth `AutostartCannotGrowAndRepeatedActivationAddsNoDirectPoints` 连续激活 100 次不直接加分；Facts `DailyClickCommitsOnceAndSpeechLinksToItsFact` 验证回应中的重复点击和冷却。面板 `OpenCompanion` 仅调用相同 `Activate`。 | 自动规则已验；面板反复点击不单独授分由接线确认。 |
| G05 | Growth 的每日上限参数覆盖一小时；`AllTwelveMemoriesCanBeEarnedOnceAndSequenceIsIndependentOfWallTime` 在既有成长后继续识别工作、休息、萤火虫与唤醒。引擎每日上限只约束 `AddPresence`。 | 自动规则与独立回忆接线已验。 |
| G06 | Growth `AllFourPublishedGrowthPathsReachTheirStages` 覆盖每日 1／5／10／20 分钟的各阶段到达日；成长入口不以日常／工作模式区分倍率，Session 验证工作同用此引擎。 | 四条路径自动已验；模式一致由共用规则及接线支持。 |
| G07 | Growth `WindowCarriesAcrossMidnightAndStopsAtItsOriginalDeadline`、`HiddenLockedAndDraggedTimeDoesNotCountOrExtendTheWindow` 验证授权到期和重新激活。 | 自动已验。 |
| G08 | Growth 隐藏／不可用会话／拖拽测试；Session `HidingDoesNotBackfillPresenceWhenTheCatReturns`；Speech `PausingFreezesTheVisibleBubbleAndHidingClearsIt` 与过期提示测试。 | 逻辑自动已验；真实锁屏／休眠恢复待对应环境验证。 |
| G09 | Growth `PausedWorkStillCountsCompanionshipButCannotEarnWorkMemory`；Session `PausedWorkStillAccumulatesVisiblePresenceButNotWorkQualification`；日常暂停只控制自动动作，不设置工作资格暂停。Session `DailyAutomaticPauseKeepsTheMilestonePendingWhileClicksStillRespond` 与 Speech 的自动暂停测试验证暂停中仍可回应、不消费阶段提示。 | 自动已验；日常暂停接线已核对。 |
| G10 | Growth `AShortIntervalCrossingMidnightIsSplitWithoutDoubleCounting`、`DateTravelReusesBudgetsAndNeverLocksOutTheCorrectDate`、`SuspiciousSamplingAndCalendarChangesDiscardTheirInterval`。 | 正常跨午夜、回拨、前跳与时区变更自动已验。 |
| G11 | Growth `DateTravelReusesBudgetsAndNeverLocksOutTheCorrectDate` 先前跳 100 天再返回原日，原日继续到 4 分，日期数保持 2。 | 自动已验。 |
| I01 | Facts 的单击去重、自然睡眠点击唤醒来源、悬停先唤醒不冒认点击、萤火虫后排队互动；既有动作回归测试随完整测试运行。 | 自动已验。 |
| I02 | Facts `CancelledDelayedClickNeverCommits` 覆盖拖拽、挂起与切工作取消；唤醒回忆只由已提交的 `FromNaturalSleep` 事实触发。 | 自动已验与接线已核对。 |
| W01 | Growth `MillisecondWorkLoopsHaveNoRewardOrMemoryShortcut` 完成 1000 个 1 ms 工作／休息轮次，分数为 0、回忆为空。 | 自动已验。 |
| W02 | Facts 自然到点与取消的两项测试；Growth `ACompletedWorkFactSurvivesALaterCancellationButBreakMustMatchItsRound`。 | 自然完成、提前取消、完成后切模式自动已验。 |
| W03 | Facts `ChangingDurationKeepsTheRoundIdentityAndRequiresAnActualDeadline`；Growth 工作回忆须满足 600000 ms，毫秒轮次与取消用例证明非完成／短时长不会解锁。 | 自动规则已验；改时长与有效资格通过同一轮次 ID 接线。 |
| W04 | Session 自动工作测试；Speech `AutomaticWorkStartDoesNotConsumeTheDailyGreetingButExplicitStartDoes`；成长授权及当前工作／休息资格不序列化。 | 自动启动不激活已验；进程重启不携带轮次资格由状态结构确认。 |
| W05 | Session `AutomaticWorkDoesNotActivateGrowthButABusyClickDoes` 验证点击保留基础忙碌文字、开启计时、先无分、满一分钟才 3 分；Facts 验证动画 token 不变。 | 自动已验。 |
| W06 | Growth `DefaultWorkAndBreakCanFinishAfterTheWindowExpires` 使用 25／5 分钟和进出场耗时，在授权过期后自然结束仍取得工作与休息回忆；Session 验证父工作 ID 映射。 | 自动已验。 |
| C01 | Speech `AWorkCueReplacesAMilestoneWaitingForDiskWithoutLaterReplayingIt` 验证工作提示覆盖待保存阶段提示；成长回忆与消费为独立状态，文字模块不发动画指令。 | 调度自动已验；不追加动作由模块职责确认。 |
| C02 | Speech `ReturnWinsGreetingAndStageOnARestClickWithoutDiscardingTheStage` 验证休息回归优先；`FirstDailyGreetingKeepsThePendingMilestoneForTheNextClick` 验证日常问候／回归先展示、阶段留给后续点击；`DailyClickCarriesPendingMilestoneOnlyAfterItsOwnSaveReceipt` 覆盖全部四种里程碑。 | 自动已验；日常与休息点击均覆盖，一次性提示等对应消费版本落盘。 |
| C03 | Speech `EveryAcceptedDailyClickHasNewTextEvenWithAnOldExhaustedAllowance`、`RandomSelectionAlwaysChoosesAClickLine` 覆盖连续点击、旧存档计数与每次选中短句；`BothMeowAndWinkCuesUseNewCopyAndRespectTheTextSwitch` 覆盖两种动作来源与文字开关；Session 的同日重启用例确认问候不重复而普通短句继续。 | 本次全量测试已通过；动作冷却和成长授分规则不变。 |
| C04 | Growth `ReadingMemoriesOrDisablingTextConsumesMilestonesWithoutRemovingGrowth`；Speech `OneTimeCueDoesNotResurrectAfterItsContextBecomesInvalid` 的 read／disable 分支。实机 first_day 已读落盘；grown 示例首屏只把可见最新两条标记已读，屏外十条仍未读。 | 自动与可见回忆已读实机已验。 |
| C05 | Render `EveryBundledLineRendersInsideThePetAtSupportedSizesAndDpi` 逐条检查全部 78 条内容 × 3 尺寸 × 3 DPI，断言有图像且边缘透明；最长文案三组目检通过。Bubble `BubbleIsNativeClickThroughTracksOwnerAndClosesWithIt` 验证原生窗口样式、不激活及穿透。 | 内容渲染、原生样式与抽样目检已验；真实多屏混合 DPI 切换仍未实测。 |
| P01 | Store `FailedReplacementRetainsOldBytesAndCanRetryTheSameRevision` 真实文件锁定失败后重试；独立快照测试证明保存不覆盖内存；Panel 的 `RefreshStatus` 提供错误与重试。 | 存储自动已验；失败提示接线确认。 |
| P02 | Store `BrokenPrimaryRestoresBackupAndPreservesTheBrokenBytes`、`InvalidPrimaryAndBackupBlockWritesUntilAnExplicitRestart`；面板区分重试读取和显式重建入口。 | 真实文件恢复自动已验；重建前保留原字节。 |
| P03 | Growth 短会话重建；Panel `ResetDefaultsRestoresBothOptionsWithoutWritingTheIndependentGrowthFile`、`TurningOffGrowthKeepsTheStageAndMemoriesVisibleAndDurable`；Session `FirstGreetingIsDurableAndDoesNotRepeatAfterRestart`。 | 自动已验；独立实机目录关闭成长后历史保留。 |
| P04 | Store `RestartAfterMilestoneConsumptionWasSavedKeepsTheStageWithoutReplayingTheBubble` 将阶段回忆与提示消费真实落盘，在处理保存回执前丢弃进程内对象并重建；断言阶段、3 分和回忆保留，消费标记保留且无提示重放。 | 真实读盘重建自动已验；模拟进程状态丢失，并非操作系统强杀或断电试验。 |
| P05 | Speech `InvalidCatalogUsesSafeNewCopyInsteadOfTheLegacyMeow` 覆盖内容异常时的新短句兜底；`SaveFailureUsesRegularNewCopyForItsFullDurationWithoutReplayingTheGreeting` 覆盖存档失败时不播未保存的问候；Store 未来 schema／rules 版本及字段形状变化测试、运行中出现未来文件测试继续保护原文件。 | 本次全量测试已通过；未来文件不被新写入替换，日常文字不回退单字“喵”。 |
| P06 | Speech `GreetingWaitsForItsOwnSavedRevision` 断言旧回执不能授权新消费；Store `OutOfOrderConcurrentRequestsCannotReplaceANewerDurableRevision` 覆盖乱序保存；应用只传回执，不替换内存。 | 本次全量测试已通过。 |
| P07 | Growth `TurningRecordingOffDoesNotMisinterpretUntrackedUseAsAbsence` 覆盖关闭 8 天再开启、第一次只重建基准，之后真实间隔 7 天可识别回归。 | 自动已验。 |
| U01 | Host `HiddenClickThroughCatCanOpenOnePanelWithoutBecomingVisible` 使用实际 PetWindow／CompanionWindow，开启穿透并执行隐藏后打开面板，再次打开复用同一窗口并恢复最小化，猫保持隐藏、分数为 0；`TrayService` 将常驻入口接至同一回调。 | 实际 WPF 窗口与回调自动已验，托盘接线已审查；未自动点击 Windows 通知区域菜单。 |

## 实机范围与后续验证

本次界面检查使用 `artifacts/companionship-preview/fresh` 和 `grown` 中的独立设置和成长存档，未改用用户日常存档。窗口可见性与基础版一致；测试数据不参与发布。

真实多屏混合 DPI、锁屏／休眠恢复和长时间运行需要对应桌面条件，不能由离屏渲染、单屏截图和假时钟测试推断通过。没有进行操作系统强杀或断电试验，也没有通过自动化点击 Windows 托盘菜单。

设计中的 10—20 人、多周及默契后的试用尚未实施，没有据此发消息、创建邀请或加入遥测。试用完成前，只能说明成长机制与体验入口已经实现，不能宣称留存率已提高。

## 本地预览包

预览目录：`dist/TablePet.Companionship-preview-win-x64/`。它使用当前骨骼资源并自带 .NET 运行时，不需要重新制作素材。

打开目录中的 `Start-Companionship-Preview.cmd` 可启动陪伴面板，设置和相伴记录保存到 `%LOCALAPPDATA%\DesktopPet.Skeletal.CompanionshipPreview`，与平时桌宠的成长存档分开。直接运行 `TablePet.Skeletal.exe` 则使用正常用户目录；需要隔离体验时使用该启动脚本。

本地检查命令：

```powershell
.\dist\TablePet.Companionship-preview-win-x64\TablePet.Skeletal.exe --check-assets
```

本次日常文字调整后的 publish 退出码为 0，安装目录素材自检退出码为 0。程序已更新至 `D:\_APPS\TablePet.Skeletal`，安装后的可执行文件和符号文件与发布输出逐项哈希一致；原桌面快捷方式继续使用该固定路径。旧程序备份位于工作区 `artifacts/installed-backups/click-text-20261110-181333`。更新前正常退出、更新后重新启动，设置和成长文件在二进制替换期间保持原样。临时验收副本和 smoke 数据不属于交付包。未提交或推送代码，未创建 GitHub Release。

同日口语修订后的 publish 和安装目录素材自检也均退出 0，最新版再次更新到相同安装目录并恢复运行。更新前的程序备份位于 `artifacts/installed-backups/natural-copy-20261110-182632`；退出后的设置、成长主文件和备份在程序替换与自检期间哈希不变。

## 回忆插画迭代

12 条回忆各加入一幅独立生成的透明 PNG，全部为 1254 × 1254 像素，总计约 16 MiB。图像来自内置 image_gen，以当前草莓猫形象为参考；完整提示词和来源记录保存在 `assets/strawberry-cat/memories/prompts.json` 与 `provenance.json`。运行时只读取本地文件，不调用模型。主题和接入说明见 [回忆插画](MEMORY-ILLUSTRATIONS.md)。

本次定向验证为 16 / 16 通过，0 失败、0 跳过，涵盖图片解码及缓存、文件句柄释放、缺失／损坏／未知 ID 降级、12 个 ID 的发布资源完整性、面板和宿主行为、旧回忆显示及窄卡片布局。结果文件为 `artifacts/test-results/memory-art_net10.0_20261110185550.trx`。其余成长逻辑沿用上述全量基线，未为本次静态插画修改成长规则或存档结构。

实际 WPF 回忆列表已按 320 DIP / 96 DPI 和 380 DIP / 192 DPI 离屏渲染，输出在 `artifacts/memory-illustrations/`。逐卡检查 320 DIP 渲染，12 张图主题可辨、日期标题正文完整、图文不重叠；较窄布局也保持正常换行。该检查不冒充真实显示器 DPI 切换。PNG 尺寸、透明通道、文件体积和 SHA-256 记录在同目录的 `asset-check.json`。

本次插画迭代的 self-contained publish 成功；发布目录和安装目录的 `--check-assets` 均返回 0。已正常退出旧程序并更新 `D:\_APPS\TablePet.Skeletal`，共验证 37 个交付文件，其中 18 个有变更。旧文件备份在 `artifacts/installed-backups/memory-art-20261110-190650/`，首次部署明细在 `artifacts/memory-illustrations/deployment-initial.json`。退出后至替换和自检完成，设置、成长主文件及各自 `.bak` 的哈希保持不变；随后恢复运行，进程响应正常。桌面快捷方式仍指向固定安装路径，无预览参数。未创建 GitHub Release。

用户指出“靠近一点”的人手结构和“熟悉起来”的抬爪角度不自然后，使用内置 image_gen 分别编辑两张原图。修订后逐图检查五指结构、四肢数量、前腿与爪子的朝向，并复查实际 80 DIP 卡片呈现。图片完整性、解码及两种窄卡片渲染共 5 / 5 通过（`artifacts/test-results/memory-art-revised_net10.0_20261110192223.trx`）；新渲染在 `artifacts/memory-illustrations/revised/`。此次只替换静态 PNG，未修改程序逻辑；原图保留在 `artifacts/memory-illustrations/originals/`，提示词和来源清单已同步。

最后一轮按用户反馈，以原版鼻嘴和圆润身体为基础清理“熟悉起来”的脸颊内线，并局部修正 `together_7_days`、`first_firefly_visit`、`first_return` 的爪部。逐张检查全部 12 幅，最终只调整上述 4 幅；其余画面保留，统一采用暖棕描边、灰白毛色、圆爪和柔和阴影。两名独立复核者与主任务均检查了修订图和 80 DIP 卡片，未再发现明确的折腕、多爪或身体凹陷。鼻嘴沿用原版，未保留前一稿的 U 形笑嘴。

最终图片资源与窄卡片布局测试 5 / 5 通过，结果为 `artifacts/test-results/memory-art-final_net10.0_20261110204826.trx`。最终卡片图在 `artifacts/memory-illustrations/final/`；本轮原图及精确编辑提示词在 `artifacts/memory-illustrations/style-pass/`，项目素材目录中的提示词与来源清单已经同步。最终安装同步、自检、存档哈希和备份路径记录在 `artifacts/memory-illustrations/deployment.json`；此前人手／爪部修订的部署记录已保留为 `deployment-hand-paw-revision.json`。
