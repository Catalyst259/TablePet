# 我们的回忆：静态插画

设计日期：2026 年 11 月 10 日。12 条回忆各配一幅草莓猫小插画，帮助用户在回看时区分共同经历。卡片以 80 × 80 DIP 显示图片，日期、标题和正文仍以正常文字呈现；不增加角色动画或新的解锁条件。

## 主题与文件

全部图片位于 `assets/strawberry-cat/memories/`，以已有回忆 ID 命名。旧存档中的回忆直接使用对应图片，无需重新解锁。

| 回忆 | ID／文件名 | 插画主题 |
| --- | --- | --- |
| 第一个相伴日 | `first_day.png` | 小猫与留下第一枚爪印的日记本 |
| 熟悉起来 | `stage_familiar.png` | 圆润的自然坐姿、微微歪头和小爱心 |
| 靠近一点 | `stage_close.png` | 闭着眼睛，让一只小手轻抚脸颊 |
| 不用多说 | `stage_in_sync.png` | 趴在合上的书旁，安静相伴 |
| 七个日常 | `together_7_days.png` | 七枚爪印组成的小台历 |
| 日子慢慢长 | `together_30_days.png` | 小猫看着草莓盆栽开花 |
| 六十个桌边日常 | `together_60_days.png` | 草莓盆栽结出果实，旁边放着回忆册 |
| 一起忙一会儿 | `first_work_completed.png` | 小猫与草莓标记的小电脑 |
| 一起歇一会儿 | `first_break_completed.png` | 软垫上休息的小猫与热茶 |
| 桌边微光 | `first_firefly_visit.png` | 小猫抬头看着一只发光的萤火虫 |
| 睡醒见面 | `first_gentle_wake.png` | 枕边刚醒的小猫伸懒腰 |
| 又见面了 | `first_return.png` | 小猫在打开的门边轻轻扶门迎接 |

## 制作与接入

使用内置 `image_gen` 工具制作，每幅图单独生成，未使用 CLI／API fallback。角色参考为 [现有草莓猫静态图](../assets/strawberry-cat/fallback.png)，保留灰白毛色、蓝眼睛、粉色脸颊及草莓奶油甜筒头饰。画面采用深棕描边、柔和暖色和少量大形状道具，使用透明背景，不在图中生成文字。

完整逐图提示词和生成模式保存在 [prompts.json](../assets/strawberry-cat/memories/prompts.json)，生成方式与成品校验记录在 [provenance.json](../assets/strawberry-cat/memories/provenance.json)。同日按反馈修正“靠近一点”的人手；“熟悉起来”最终采用圆润自然坐姿，保留原版粉色鼻子与倒 Y 形嘴，清除脸颊内多余黑线。随后局部修整台历、萤火虫和门边三张的爪子，保留各自场景。

整组以完整圆润的身体、短而圆的猫爪、少量短趾线、暖深棕描边和柔和阴影为准。抬爪时前腿和爪背自然衔接，不把爪子画成人手式翻腕；白色脸颊与胸口避免多余的深色内线。表情和场景光照可以随回忆内容变化。逐图复核后保留其余合适的构图，对应修改条目保留原始提示词与编辑来源说明。桌宠运行时只加载本地 PNG，不联网生成图片。

发布包把 PNG 复制到 `Assets/strawberry-cat/memories/`，`--check-assets` 检查全部回忆图片可加载。应用缓存缩略图；缺失或损坏的图片会省略，回忆文字保持完整。插画作为装饰不重复进入无障碍朗读，也不改变回忆的排序和已读规则。实际验证结果另见 [验收记录](COMPANIONSHIP-VALIDATION.md)。
