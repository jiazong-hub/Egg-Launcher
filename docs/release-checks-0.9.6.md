# 0.9.6 正式发布检查记录

发布日期：2026-10-06（Asia/Shanghai）。用户已授权本地 Git 提交、同步 GitHub 和正式发布 0.9.6。

## 发布范围

模型参数保存与首次启动同步、BAT 的 MTP/视觉参数补齐、统一自动并行数生成，以及配置文件回滚。项目版本 0.9.6，程序集及文件版本 0.9.6.0。

更新 CHANGELOG、README、用户指南、验收清单、第三方声明、签名政策和中英文关于页。历史版本说明保持原日期与结论。

## 检查状态

- 本次修复此前 Release 编译零警告、零错误，323/323 自动测试通过。
- 0.9.6 候选完整门禁通过：锁定依赖恢复、Release 零警告/零错误、格式检查、323/323 自动测试（0 跳过）、在线依赖漏洞审计（0 已知漏洞）、App/Agent win-x64 自包含发布、Agent 自检、Runtime 许可文件及签名/哈希检查均通过。未跳过在线审计。
- HTTPS 兼容脚本离线回归 8/8 通过；Git diff --check 通过。候选日志：`artifacts/release-installer-0.9.6-candidate.log`。
- 提交后重新构建正式签名包，使包内 ProductVersion 对应版本标签的源码提交；最终发布及回下载核验将在独立文档提交中追加，不移动版本标签。

## 实机反馈与边界

用户在 2026-10-06 明确反馈“关于配置文件的修改，经过测试没有问题”。这是本次配置同步测试包的实机反馈，不扩展为所有模型、后端或完整测试矩阵结论。诊断原始文件、测试包与临时脚本不提交。

0.9.6 正式包全新安装、升级、卸载恢复、Windows 11、ROCm 和完整语言/主题/缩放矩阵尚未独立验收。保持 Lazy Mode、后台 Agent 核心机制、MTP/视觉验证规则及推理后端现状。

沿用作者自签名证书，不安装信任根或导出私钥。签名完整性与 Windows 公共信任分开记录。

## 最终发布与公开下载核验

- 正式源码提交：`025f20d3087e50627f9a2d9eae4d80ed488b6615`；标签 `v0.9.6` 指向该提交，已与 main 推送 GitHub。
- 从干净工作树重建正式包：Release 零警告/零错误、格式检查、323/323 自动测试（0 跳过）、在线依赖审计（0 已知漏洞）、自包含发布、Agent 自检、Runtime 许可、签名及哈希检查均再次通过。
- 包内 ProductVersion：`0.9.6+025f20d3087e50627f9a2d9eae4d80ed488b6615`，安装程序产品版本 0.9.6。
- 安装包、应用、Agent 及卸载程序沿用原作者自签名证书。签署者指纹：`44A03B7EA758C184DB1F15E5ADC6DE7EBDC8153C`。本次 PowerShell 安装包签名状态显示 UnknownError，状态消息为“已处理证书链，但是在不受信任提供程序信任的根证书中终止”，属于自签名根不被公共信任；脚本已核对签署者及签名读取，没有安装信任根或导出私钥。
- 安装包大小：67,609,616 bytes；SHA-256：`7d533fae1bdf37a6fe4b1cbbceaa8fe49b8a765f1917975a1a956f8e8d5e0e91`。
- [0.9.6 Release](https://github.com/jiazong-hub/Egg-Launcher/releases/tag/v0.9.6) 已公开，draft=false、prerelease=false，GitHub latest 为 v0.9.6。EXE 与独立 SHA-256 文件已上传，0.9.5 历史 Release 保留。
- GitHub 资产 digest 与本地一致；从公开链接重新下载 EXE，实际 SHA-256 与本地及公开校验文件一致。
- 标签源码 ZIP 已实际下载并完成容器检查；README、CHANGELOG、版本文件和 0.9.6 发布说明与标签源码逐字节一致。源码中没有 artifacts、测试包或私钥。
- README 下载入口与正式资产地址一致。最终日志：`artifacts/release-installer-0.9.6-final.log`；公开下载核验结果：`artifacts/release-0.9.6-download-verification/result.json`。
- 本段为发布后的独立文档记录，不重打包、不移动 v0.9.6 标签；当前 main 保留完整最终核验结果。
