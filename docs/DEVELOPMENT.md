# 开发说明

日常构建直接使用仓库中的骨骼资源，命令见 [README](../README.md)。这里介绍动画素材的重建、比对和旧版本归档。

## 动画资源

TablePet 使用骨骼和贴图驱动动画，并用无损修正保留原图的毛发、表情和透明边缘。萤火虫由骨骼姿态与附件合成，工作模式使用基础贴图和局部表情，其余动作使用骨骼预测加修正数据。

0.5.0 重写保留了原版 16 个动作及萤火虫轨道，动画资源从约 211.58 MiB 缩小到 58.18 MiB。0.6.0 另加入约 16 MiB 的回忆插画。详细结构见 [Legacy 动作](LEGACY-SKELETON.md)、[萤火虫骨骼](FIREFLY-RIG.md) 和 [原版动画来源](ANIMATION-AUDIT.md)。

## 重新制作素材

只在修改动画或比对原版时获取归档分支。以下命令都在当前 `skeleton` 工作目录运行：

```powershell
git clone --depth 1 --single-branch --branch legacy https://github.com/Catalyst259/TablePet.git ../TablePet.Legacy

npm ci
python -m venv .tools/rig-python
.tools/rig-python/Scripts/python.exe -m pip install -r tools/rig-requirements.txt
npm run assets:compile -- ../TablePet.Legacy
npm run assets:size -- ../TablePet.Legacy
```

制作脚本读取独立的 `legacy` 目录，输出写入当前工作目录。旧逐帧图片、制作草稿和预览继续留在归档中，不要重新加入 `skeleton`。

## 检查结果

与原版逐像素、时序和缩放比对：

```powershell
dotnet run --project tests/TablePet.PlaybackProbe -c Release -- --verify-skeletal `
  ../TablePet.Legacy/assets/strawberry-cat ./assets/strawberry-cat ./artifacts/skeletal-verification
```

使用已提交的基准检查萤火虫渲染：

```powershell
dotnet run --project tests/TablePet.FireflyProbe -c Release -- .
```

发布后的程序可用 `--check-assets` 检查素材完整性。详细报告和截图生成在本地 `artifacts/`，不提交到仓库。现有结果与验证范围见 [骨骼版验收摘要](VALIDATION.md)、[陪伴功能验收摘要](COMPANIONSHIP-VALIDATION.md) 和 [测试基准说明](../reference/README.md)。

## 旧版本与历史

- 当前开发在 `skeleton` 分支进行。
- 逐帧版本和制作素材保存在 `legacy`，基线提交为 `426f160`。
- 历史 Release 标签和下载附件保留原样。
- 2026-11-12 整理前的完整提交链保存在 `archive/skeleton-before-cleanup-20261112` 标签。

`skeleton` 已从精简后的根提交继续开发。已有旧历史的开发副本，建议先保存未提交改动，再按 README 的轻量克隆命令建立新副本。后续不要把旧开发分支合并回来，否则会重新带入归档历史。
