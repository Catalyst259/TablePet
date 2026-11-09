# 萤火虫动作的运行时骨骼重建

原版已批准的 v3 动作本身由程序化 rig 生成。本次移植直接恢复这套 rig 的运行时求值：头部围绕 `(232, 400)` 转动并通过肩部权重衔接躯干；原始前爪围绕腕部转动和平移；前臂连接肩与腕；右前爪保持落地；眼球、瞳孔与高光跟随头部和视线。补洞区域保留原来的轮廓形变和遮挡顺序。

运行包只有 `skeletal/firefly/firefly.json` 和 `textures.bin.z`，共 2,643,781 字节。对应旧素材为 937 个独立 PNG，共 84,855,753 字节，减少约 96.9%。这里包含旧待机共用帧，汇总整个素材包时应去重。

贴图包保留原前爪、前臂、身体补洞、嘴部、虹膜、瞳孔和高光附件，以及 125 个去重的萤火虫小尺寸附件。飞虫附件保留 Sharp 的原旋转/缩放采样；它们不是 640×640 的逐帧画布。600 个时间槽保存姿态参数和附件引用，画布及猫体遮挡由 C# 运行时计算。没有烘焙整帧，也没有像素残差补丁。

`FireflyRenderer` 不依赖 Node、Sharp、源仓库或源图片。其不可变贴图数组占用 23,741,648 字节；另外有约 2 MiB 身体权重表、姿态参数、对象开销和临时输出。原 `AssetCatalog` 的 128 MiB 图像缓存预算不包含这些不可变数据。渲染调用不复用可变输出，不同线程可同时调用。

编译脚本只读取单独的 `legacy` 参考目录；所有准备脚本在当前 `skeleton` 工作目录的 `artifacts/firefly-authoring` 副本内执行。此目录是制作缓存，不参与打包。按根目录 README 获取 `../TablePet.Legacy` 后，在 `skeleton` 工作目录运行：

```powershell
npm ci
node tools/compile-firefly.cjs ../TablePet.Legacy
dotnet run --project tests/TablePet.FireflyProbe -c Release -- .
```

独立探针覆盖 600 个猫时间槽和 600 个装饰时间槽，与冻结 PNG 的完整 RGBA SHA-256 比较，全部相等；另验证 384 次并发渲染、输出缓冲区隔离、索引边界以及截断、摘要错误、尺寸错误和非法姿态档案的拒绝。结果在 `reference/firefly-csharp-verification.json`，制作体积记录在 `reference/firefly-compilation.json`。这些是实际 CPU 渲染测试，不代表显示器帧率验收。
