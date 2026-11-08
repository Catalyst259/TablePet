# 0.3.3 · 萤火虫单击互动

2026-11-08 开发版，尚未发布 GitHub Release 或安装器。

单击在起身、Wink、萤火虫三个有效动作中等概率选择。萤火虫采用用户批准的 v3 预览：10 秒、60fps，双眼追踪同一目标，原尺寸前爪从待机位置抬起，微侧身轻拍，随后向上追视并回到原待机。双击喵叫、系统双击判定间隔、睡眠唤醒、拖动、重复输入抑制及冷却沿用原行为。

猫仍使用 512×512 画布，位置和大小不变。640×640 飞虫层相对猫偏移 (-64,-100)，与猫使用相同的 600 个整数毫秒时间槽；后方遮挡在导出时按猫的 alpha 烘焙。装饰窗口沿用不激活、原生鼠标穿透和物理像素位置同步。拖动时两个层共用暂停后的帧索引，动作结束或挂起时隐藏飞虫层。

运行清单包含 12 个猫动作和 1 条装饰轨道，共 2211 张引用 PNG。新动作增加 936 张无损图片，约 80.7 MiB；原有 11 个动作的时间轴和 PNG 保持不变。缓存仍共用 128 MiB 上限，同时预读两条轨道。旧素材包没有萤火虫时保留起身/Wink；配对轨道缺失或无效时跳过该可选动作。损坏的飞虫 PNG 降级为同尺寸透明图，不显示猫的兜底图片。

## 重建与验证

```powershell
npm ci
node tools/build-firefly.cjs
node tools/build-firefly.cjs --verify
node tools/install-firefly.cjs
powershell -File tools/build.ps1 -Test -Publish
powershell -File tools/verify-runtime.ps1 -ProbeAppHost -Cycles 1000 -ExpectedIdleActions blink,yawn
```

全部输入及批准脚本保存在 `assets/strawberry-cat/sources/firefly-v3/` 和 `reference/firefly-v3/`，重建不依赖 `artifacts`、生图服务或 RIFE。构建程序只使用已导出的 PNG，不需要 Node.js。导出检查记录、PNG 哈希与批准预览的逐像素对照保存在 `reference/firefly-v3/`。

0.3.2 的 `assets:120hz`、`assets:freeze:check` 与冻结指纹仍用于历史版本复现；它们不代表包含新动作的当前清单，不能用历史重采样命令覆盖当前清单。

当前桌面验收结果见 [验收记录](ACCEPTANCE.md)。离屏 WPF 合成与解码探针不等同于显示器实测帧率；真实混合 DPI、多屏拔插、锁屏/休眠仍需要相应环境验证。
