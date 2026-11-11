# 测试基准

此目录只提交自动检查需要的稳定输入。验收结论保存在 [骨骼重写摘要](../docs/VALIDATION.md) 和 [陪伴功能摘要](../docs/COMPANIONSHIP-VALIDATION.md)。

`firefly-expected.json` 保存原逐帧基线的 600 个猫时间槽与 600 个装饰时间槽的 RGBA SHA-256，供 `TablePet.FireflyProbe` 独立比较骨骼渲染结果。它是测试输入，不能用本次渲染结果覆盖期望值。

```powershell
dotnet run --project tests/TablePet.FireflyProbe -c Release -- .
```

探针报告写入 `artifacts/firefly-csharp/verification.json`。素材编译与体积报告写入 `artifacts/asset-compilation/`；完整 WPF 比对可按 [开发说明](../docs/DEVELOPMENT.md) 临时获取 `legacy` 参考目录后运行。重新制作萤火虫资源时，编译脚本从归档原图重新计算测试基准，基准变更应与资源变更一并审阅。

逐帧诊断、性能采样、截图和个人工作副本清单不属于测试基准，不提交到此目录。
