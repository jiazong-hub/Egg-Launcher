# 0.9.4 正式发布检查记录

检查日期：2026-10-02（Asia/Shanghai）。本记录保留候选检查过程；已于同日 11:46（Asia/Shanghai）公开发布，使用预发布标记，不覆盖旧 Release。

## 最终发布

- 源码提交 `75393acaabb543491f6f61c89856d73d11f598cc`，标签 `v0.9.4`；工作树干净后重新构建，ProductVersion 为 `0.9.4+75393acaabb543491f6f61c89856d73d11f598cc`。
- 完整门禁重跑通过：281 项 .NET 测试、在线依赖审计、Agent 自检、格式检查、Release 构建、签名安装程序封装；8 项 Python 离线检查再次通过。
- 正式 EXE 大小 67,582,664 bytes，SHA-256：`5a7e4624faf2a1701ae07b98d857c68a008232c1eff037e1e8d887b4ac84508d`。GitHub 上传 digest 与本地一致，附带独立 .sha256 文件。
- [公开发布页](https://github.com/jiazong-hub/Egg-Launcher/releases/tag/v0.9.4)。源代码和标签已推送，用户明确批准公开发布。此前候选包及草稿说明为过程记录。
- 本次发布后的文档状态更新不移动版本标签，也不替换已签名二进制。

## 文档与版本

- 版本 0.9.4，程序集/文件版本 0.9.4.0，移除 informational version 的 -local 后缀。
- 更新 CHANGELOG、用户指南、README 发布候选状态、签名政策、验收清单；新增发布说明及中英文“关于”页条目。
- README 下载入口保留已公开的 0.9.3，发布完成后再改为 0.9.4，避免死链接。
- 发布日期暂不填写；正式发布时使用实际日期。

## 自动检查证据

通过 scripts/Publish-Portable.ps1 的完整流程（未跳过依赖审计）：

| 检查 | 结果 |
|---|---|
| 锁定依赖恢复 | 通过 |
| Release warn-as-error 构建 | 0 警告、0 错误 |
| dotnet format 验证 | 通过 |
| .NET 自动化测试 | 281/281 通过，0 跳过 |
| 在线依赖漏洞审计 | 通过，未发现已知漏洞 |
| win-x64 自包含发布 | App/Agent 通过 |
| 发布后 Agent 自检 | 通过 |
| Runtime 许可随包复制 | 通过 |
| 文件 SHA-256、ZIP 解包逐文件校验 | 通过 |
| HTTPS 兼容脚本离线回归 | 8/8 通过 |
| Git diff --check | 通过 |

首轮格式检查发现新增代码排版问题，已统一 C# 空白格式。首轮测试 253 项中 7 项失败：5 项测试数据不满足新增上下文余量规则、1 项错误提示匹配过时、1 项旧断言要求覆盖用户新审批选择。修正测试数据/断言，未放宽产品规则；新增 28 项参数边界、显式滚动参数和环境过滤回归，最终 281 项全部通过。

沙箱内审计因 NuGet 网络连接被拒绝而失败；允许联网环境重跑完整流程后通过，没有使用 SkipDependencyAudit 绕过。

HTTPS 离线回归使用临时脚本副本及确定性传输替身，运行实际监督/下载子进程；不连接互联网，不改生产脚本或真实 Codex 配置。覆盖请求预算、无数据超时和临时文件清理、持续下载、参数覆盖、HEAD、已有文件保护、大小限制和非法参数。不能代替真实 HTTPS/重定向/DNS/沙箱验收。

本地证据（不提交构建产物或日志）：

- artifacts/release-check-0.9.4.log
- artifacts/release-network-check-0.9.4.log
- artifacts/release-check-0.9.4-20261002/BUILD-INFO.txt
- artifacts/release-check-0.9.4-20261002/SHA256SUMS.txt
- scripts/Test-NetworkCompatibility.py 可重跑离线脚本检查。

第一份自包含检查包未签名，仅作自动检查证据。最初候选包基于未提交工作树，ProductVersion 中的旧 Git 后缀不能代表全部改动；BUILD-INFO 的 SourceTreeSha256 记录构建输入摘要。发布准备使用本次源码提交重新生成签名包。

## 签名与安装程序

- 既有证书在维护者当前用户证书库中可用，私钥存在，指纹与 signing-policy.md 一致；没有导出私钥或新建证书。
- `Publish-Installer.ps1 -RequireSignature -AllowUntrustedSelfSignedCertificate` 已通过，重跑 281 项测试及依赖审计，验证项目二进制签名、安装程序签名与指定证书一致；Inno 编译过程中生成并签署卸载程序。自签名仍不构成 Windows 公共信任。
- 单独查询安装包 Authenticode 状态为 `UnknownError`，具体原因为证书链终止于不受信任根证书，签署者指纹匹配项目证书；这符合自签名分发的信任限制。不能描述为 Windows 公共信任验证通过；没有为此安装信任根证书。
- 候选包：`artifacts/installer-output-0.9.4-20261002-030541-dbd63b41/Egg-Launcher-0.9.4-Setup-win-x64.exe`，同目录提供 `.sha256`。构建日志：`artifacts/release-installer-check-0.9.4.log`；payload 的 BUILD-INFO 记录 `Signature=authenticode-self-signed-verified`。
- 候选包内检查记录为构建时快照，仓库此记录补充签名完成结果；正式提交后的最终发布包需重新构建以包含最后文档和提交信息。
- 安装/升级/卸载会影响当前程序与客户端环境，本开发会话不自动执行；需在合适时间进行真机验收。

## 用户验收反馈（2026-10-02）

用户确认刚才已完成测试，并要求准备发布。记录为候选包用户验收认可；没有逐项报告安装升级、卸载或真实网络超时的独立结果，因此不将各项具体场景全部标记为实测通过。

## 未独立记录的覆盖范围与发布步骤

1. 正式候选包安装、0.9.3 升级、卸载及 Local 配置恢复验收；升级前备份用户数据，旧版不保证读取新版 Profile。
2. 最新两阶段超时在实际 Codex 沙箱、代理与真实 HTTPS 下载中验证。
3. 中英文/深浅主题/Windows 缩放和窗口尺寸矩阵，以及 Profile 迁移和配置恢复的最终真机检查。
4. Windows 11、Vulkan、ROCm 等未覆盖组合如无设备，明确保留限制。
5. 用户已同意准备发布；提交源码、生成对应提交的最终包及标签并创建草稿，公开发布前确认。

自动检查通过不代表以上真机项已经通过，也不代表正式发布完成。
