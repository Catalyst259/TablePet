# 0.3.4 · 60fps 与待机动作调整

2026-11-08。所有移动动画统一使用 60fps 素材时间线；静态待机、起身保持仍各用一张图片。动画时长和首尾姿势保持不变，现有 PNG 直接取样，不重新绘制或缩放。萤火虫原本就是 60fps，其 10 秒动作和两条 600 槽时间线原样保留。

## 互动规则

| 触发方式 | 动作 | 每次选择的概率 |
| --- | --- | --- |
| 自动待机 | 眨眼 | 40% |
| 自动待机 | 打哈欠 | 20% |
| 自动待机 | 起身右瞥 → 保持 → 趴下 | 20% |
| 自动待机 | 萤火虫追视与轻拍 | 20% |
| 单击 | 喵叫 | 50% |
| 单击 | Wink 单眼眨 | 50% |
| 双击 | 喵叫 | 100% |

待机采用权重 2:1:1:1 的独立抽样，允许连续选中同一动作，确保眨眼的触发概率是其余每项的两倍。默认在上一个动作结束后等待 3–8 秒再抽样，沿用个人设置中的间隔；长动作的播放时间不占用下一轮等待时间。自动动作不刷新无交互入睡期限，已到期限时先自然收尾再入睡。

起身保持继续使用设置中的保持时长。萤火虫和起身过程中点击会保留一次回应，等完整收尾后再播放喵叫或 Wink，避免身体跳姿势或猫与萤火虫分离；拖动会一起暂停两个图层。单击仍等待 Windows 双击判定，双击只播放一次喵叫。

## 素材与构建

`reference/motion-60hz/` 保存转换前的完整清单和验证记录。60fps 导出按原时间挑选 PNG，保留总时长、循环标志和首尾；动态回程动画也限制为 60fps。打包只保留当前清单引用的猫和装饰层 PNG，历史制作素材保留在源码中。

```powershell
npm run assets:60hz:test
npm run assets:60hz:check
powershell -File tools/build.ps1 -Test -Publish
powershell -File tools/verify-runtime.ps1 -ProbeAppHost -Cycles 1000 -ExpectedIdleActions blink,yawn,rise,firefly
powershell -File tools/build-installer.ps1
powershell -File tools/verify-installer.ps1
```

重建当前清单使用 `npm run assets:60hz`。历史 120Hz 和 0.3.3 安装脚本不能覆盖当前清单。动画源文件重建需要 Node.js；安装和运行不需要 Node.js、模型或额外 .NET 环境。

验证结果和成品大小见 [验收记录](ACCEPTANCE.md)。60fps 指素材时间线，实际显示由 WPF 合成器与屏幕决定。
