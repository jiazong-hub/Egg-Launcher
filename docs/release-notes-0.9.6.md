# Egg Launcher 0.9.6

发布日期：2026-10-06（Asia/Shanghai）。

- 修复保存参数后配置不同步：同步模型 JSON、BAT，并更新当前 Local 模型的 Router preset 与 Catalog；保存只写本地文件，不启动或重载 llama.cpp。
- 修复再次启动同一模型可能沿用旧参数：启动前重新读取已保存配置，冷启动应用最新参数及 Codex 的上下文、压缩、工具输出与沙箱设置。
- 补齐 BAT 的 MTP 与视觉模块参数，与 Router 共用生成规则；统一自动并行数的生成。
- 配置写入失败时回滚 JSON、备份、BAT、Router preset 与 Catalog，避免部分保存。

## 使用说明与验证范围

编辑其他模型只更新它自己的 JSON/BAT，不覆盖当前 Local 模型的 Router preset/Catalog；该模型被选中启动时再应用它的配置。正在运行的模型仍需结束会话后编辑。冷启动沿用现有配置事务更新 Codex 受管理字段。

用户已在 AMD 7900XT 测试机确认本次配置文件同步修复测试无问题。该反馈不代表所有模型、硬件、MTP 文件或 llama.cpp 后端均已验证。此次未改变 Lazy Mode、推理后端、模型量化或聊天模板的语言指令。

自动检查及正式包验证见[发布检查记录](release-checks-0.9.6.md)，签名说明见[签名政策](signing-policy.md)。正式包的全新安装、升级与卸载恢复尚未独立验收。

## 下载

- [Windows x64 安装包](https://github.com/jiazong-hub/Egg-Launcher/releases/download/v0.9.6/Egg-Launcher-0.9.6-Setup-win-x64.exe)
- [SHA-256](https://github.com/jiazong-hub/Egg-Launcher/releases/download/v0.9.6/Egg-Launcher-0.9.6-Setup-win-x64.exe.sha256)
- [Release 与标签源码](https://github.com/jiazong-hub/Egg-Launcher/releases/tag/v0.9.6)

安装包包含 .NET 运行时，不包含 ChatGPT Desktop、llama.cpp 或模型。0.9.5 历史 Release 保留。
