# 项目约定

- 当前项目在本仓库的 `skeleton` 分支迭代。`legacy` 分支及旧版本标签仅用于历史归档；不要把新功能合入旧版。
- 对外名称统一为 `TablePet`：程序 `TablePet.exe`、安装包 `TablePet-版本号-Setup.exe`、便携包 `TablePet-版本号-win-x64.zip`、快捷方式 `TablePet`，不再添加 `.Skeletal` 或 `.Skeleton`。已有内部数据目录、单实例标识和安装身份为升级兼容保留，历史发布文件名不追溯改写。
- 使用当前骨骼资源和构建流程。只有重现原版或重新制作素材时，才临时获取单独的 `legacy` 参考目录；不要恢复旧逐帧资源到当前运行目录。
- GitHub Releases 不上传 `.sha256` 文件。构建和验证仍可在本地生成、读取校验文件。
- 未提交的历史素材和实验资料可能保存在仓库外的 `TablePet.LocalArchive`，不属于可随意删除的构建缓存，也不参与发布。
