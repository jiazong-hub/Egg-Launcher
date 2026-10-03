# 0.9.5 正式发布检查记录

发布日期：2026-10-04（Asia/Shanghai）。用户已确认提交本地 Git、同步 GitHub 并公开发布 0.9.5；使用正式 Release，不使用预发布标记。

## 发布范围

Qwen／Unsloth 模板兼容与管理，以及 Kotlin Daemon 沙箱常用目录。模板操作的线程修复归入模板功能，不单列更新项目。
版本为 0.9.5，程序集和文件版本为 0.9.5.0，移除开发标识。
更新中英文关于页、CHANGELOG、README、用户指南、验收清单及随安装包分发的当前发布说明。

## 检查状态

候选完整门禁已通过，未跳过在线依赖审计：

| 检查 | 结果 |
|---|---|
| 锁定依赖恢复、Release 警告门禁 | 通过，0 警告、0 错误 |
| dotnet format 检查 | 通过 |
| .NET 自动测试 | 314/314 通过，0 跳过 |
| 在线依赖漏洞审计 | 通过，0 已知漏洞 |
| App/Agent win-x64 自包含发布 | 通过 |
| 发布后 Agent 自检 | 通过 |
| 签名、Runtime 许可及文件哈希 | 通过 |
| HTTPS 兼容脚本离线检查 | 8/8 通过 |
| Git diff --check | 通过 |

候选日志：`artifacts/release-installer-0.9.5-candidate.log`。正式提交后重新执行完整签名流程，确保包内 ProductVersion 对应发布源码提交。最终提交、包摘要和 GitHub 校验在发布后追加；该后续文档提交不移动版本标签。

## 最终发布与回下载核验

- 正式源码提交：`d13c74c8cc35a3b18f02452f4d7804062d7897da`；标签 `v0.9.5` 指向该提交，已与 main 一并推送 GitHub。
- 工作树干净后完整重跑门禁成功：314/314 .NET 测试、0 跳过、0 已知依赖漏洞、Release 构建和格式检查、Agent 自检、自包含封装及签名全部通过。
- 包内 ProductVersion：`0.9.5+d13c74c8cc35a3b18f02452f4d7804062d7897da`；安装程序文件版本为 0.9.5。
- 签署者指纹：`44A03B7EA758C184DB1F15E5ADC6DE7EBDC8153C`。PowerShell 状态为 NotTrusted，原因是既有作者自签名证书不属于 Windows 公共信任；脚本已验证签署者一致及文件签名完整性，没有安装信任根。
- 安装包大小：67,608,192 bytes；SHA-256：`beab7cd50b8eef33496f2dc501bdfeeeffb982c2d829b25627648e4614dd6c58`。
- [0.9.5 Release](https://github.com/jiazong-hub/Egg-Launcher/releases/tag/v0.9.5) 已公开，draft=false、prerelease=false，GitHub latest 为 v0.9.5。附带 EXE 与独立 SHA-256 文件，0.9.4 保留。
- GitHub 上传 digest 与本地一致；从公开下载链接重新下载 EXE，实际计算 SHA-256 一致，公开校验文件也一致。
- 标签源码 ZIP 已实际下载并通过容器检查；README、版本文件和 CHANGELOG 与标签提交逐字节一致。测试机详细报告、原始日志、临时脚本和 artifacts 未纳入源码。
- README 快捷下载入口与 Release 的实际资产地址一致。
- 最终日志：`artifacts/release-installer-0.9.5-final.log`；回下载结果：`artifacts/release-0.9.5-download-verification/result.json`。本段发布后记录作为独立文档提交，不重打包、不移动 v0.9.5 标签。

## 已有真机证据

用户反馈模板适配后 27B 模型连续输出五个多小时。提供的日志中 11 次 Codex 压缩均成功，没有发现原严格消息位置错误或压缩触发的 Worker 重启。测试机详细分析与原始资料仅保留本地，不提交或分发。
Kotlin Daemon 项只验证了代码构建，测试机实际授权后的效果尚未独立验收。
Windows 11、ROCm、新正式包安装升级卸载、完整语言主题缩放矩阵和所有未知模型模板均不记为已通过。

## 安全与兼容边界

继续使用现有配置事务、跨模式历史查阅和受控 Runtime 生命周期。模板规则未命中或有歧义时不自动生成，渲染检查不宣称实际推理验证通过。
模型下载代理保持现状。研究记录仅用于追溯，不作为已实现功能，也不分发测试机原始日志或临时调查脚本。
沿用已有作者自签名证书，不安装信任根或导出私钥；签名有效性与 Windows 公共信任分开记录。
