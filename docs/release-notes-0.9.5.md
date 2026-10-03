# Egg Launcher 0.9.5

发布日期：2026-10-04（Asia/Shanghai）。

- 增加 Qwen／Unsloth 聊天模板兼容适配及模板管理功能，支持检测、生成兼容模板、查看模板来源与验证状态、打开模板位置和恢复内置模板，并预留其他模型系列的适配扩展方式。
- 沙箱常用目录新增 Kotlin Daemon 缓存，支持按需添加读写权限。

## 使用说明与范围

模板检测需要关闭客户端并停止同一 Runtime 的服务及下载。只有已识别、无歧义的规则才生成兼容模板，且通过隔离渲染检查后才设置路径。用户指定模板和被编辑的生成文件不会被静默覆盖。恢复内置模板需要保存，原文件保留。

Kotlin Daemon 项使用 `%LOCALAPPDATA%\kotlin\daemon`，默认读写但不自动添加；保存后重启客户端生效。目录尚未创建时可以预先添加，不开放整个 AppData。

用户报告 AMD 7900XT 测试机的 Qwen3.8-27B-UD-Q4_K_M 经模板适配后连续运行五个多小时，导出日志中的 11 次 Codex 压缩均成功。该结果不代表所有 GGUF、后端或 llama 原生 context shift 均已验证；本版本没有修改 CUDA／Vulkan 后端。

保留跨模式历史查阅及现有在线配置恢复机制。模型下载、下载代理及上下文安全余量默认值没有在本版本调整。

自动检查与未覆盖的真机项目见[发布检查记录](release-checks-0.9.5.md)。安装包沿用项目自签名证书，信任边界及指纹见[签名政策](signing-policy.md)。

## 下载

- [Windows x64 安装包](https://github.com/jiazong-hub/Egg-Launcher/releases/download/v0.9.5/Egg-Launcher-0.9.5-Setup-win-x64.exe)
- [SHA-256](https://github.com/jiazong-hub/Egg-Launcher/releases/download/v0.9.5/Egg-Launcher-0.9.5-Setup-win-x64.exe.sha256)
- [Release 与标签源码](https://github.com/jiazong-hub/Egg-Launcher/releases/tag/v0.9.5)

安装包包含 .NET 运行时，不包含 ChatGPT Desktop、llama.cpp 或模型。旧版 0.9.4 Release 保留供回退。
