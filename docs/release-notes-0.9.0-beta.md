# Egg Launcher 0.9.0 公测版发布说明

> 历史发布记录：当前版本为 0.9.1，最新说明见 [Egg Launcher 0.9.1 公测版发布说明](release-notes-0.9.1.md)。本文保留 0.9.0 当时的范围与结论，不作为当前界面和能力说明。

发布日期：2026-09-14  
开发者：甲总不是贾总  
目标系统：Windows 10 22H2 / Windows 11 x64

## 发布结论

0.9.0 已达到核心功能冻结条件，可以作为 UI 重构前的稳定公测基线。用户真机测试确认本地模型可由 ChatGPT Desktop 调用、GPU 能加载并输出 token，Codex 能依据模型 Context 和压缩安全余量执行原生本地自动压缩，切回 OpenAI 后官方环境可继续使用。

## 核心能力

- OpenAI / Local LLM 显式切换与上次状态保留。
- 共享 ChatGPT 原生项目、任务历史与开发上下文，同时隔离 Provider 和模型列表。
- llama.cpp Runtime 探测、GGUF 扫描、多分片识别、每模型 Profile 与 BAT。
- 用户自定义参数、每模型专用默认和可选 llama 原生自动适配。
- Codex 原生本地上下文压缩；Launcher 只同步容量与压缩线。
- 后台 Agent、直接启动 ChatGPT、本地随机端口、进程所有权和恢复事务。
- 设备、系统、llama、模型、Slot、KV Cache 与 token 速度监控。
- llama 原生立即加载、释放和受控缓存管理。
- 公开 GGUF 搜索与下载界面，其中下载为实验性。

## 已验证

- Windows 10 22H2 x64。
- ChatGPT Desktop 的 Local 文本推理、本地 GPU 加载和多轮对话。
- Context 接近压缩线时，Codex 在下一次回答或工具操作的安全节点执行压缩。
- Local → OpenAI 后官方账户、在线模型和主要偏好恢复。
- 自动化测试覆盖配置事务、恢复、代理安全、端口、进程、模型、Profile、监控、缓存和下载状态。
- Release 构建、格式检查、发布后 Agent 自检、签名验证和 ZIP 逐文件哈希校验。

## 已知限制

- 模型下载仍受网络、代理、TLS、Hugging Face 可访问性和 llama.cpp 构建影响，不作为核心稳定性结论。
- CUDA 是当前主要实测后端；AMD/Vulkan 路径依靠 llama 原生设备识别和 Auto 参数，但仍需不同硬件真机复测。
- Local 模式需要复用 ChatGPT Desktop 账户外壳才能共享原生项目和历史，因此账户名称或额度提示仍可能可见。
- 未登录的干净 ChatGPT Desktop 能否进入主界面由客户端自身策略决定；Launcher 不绕过登录。
- 小模型、激进量化、低精度 KV Cache 或很小 Context 可能降低工具调用和压缩摘要质量，这属于模型与参数能力边界。
- 公司终端防护、加密软件、网络代理和驱动计数器可能改变下载、文件和监控行为，应按个体环境记录。

## 升级与回退

- 解压新版到独立目录，不要覆盖正在运行的旧目录。
- 切换或更换版本前完全退出 ChatGPT Desktop。
- 旧 Agent 身份不匹配时新版会拒绝复用；按提示正常停止旧服务后再启动。
- 模式和 Profile 保存在用户数据与 llama Runtime 中，关闭程序不会自动切回 OpenAI。
- 如需回到官方环境，使用当前版本明确点击 `OpenAI`，确认恢复成功后再移除旧程序目录。

## 完整性与签名

- 发布包随附 `SHA256SUMS.txt`，外部另提供 ZIP 的 `.sha256` 文件。
- Launcher 自有 EXE/DLL 使用项目自签名 Authenticode 证书签署。
- 证书指纹见 [签名策略](signing-policy.md)；自签名不代表第三方公共信任。
