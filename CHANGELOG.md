# 变更记录

本项目采用语义化版本号。当前 0.x 阶段为公开测试期，界面和兼容范围仍可能调整。

## 0.9.0-beta — 2026-09-14

### 新增

- OpenAI 与 Local LLM 双模式切换，并持久化上一次模式。
- llama.cpp Runtime 探测、设备识别、独立与多分片 GGUF 扫描。
- 每模型 Profile、专用默认参数、BAT 和 Router preset。
- Context、压缩安全余量、GPU Offload、Flash Attention、K/V Cache、Batch、并发、空闲释放和 Chat Template 参数。
- Codex 原生本地上下文压缩能力声明与动态压缩线同步。
- CPU、内存、GPU、显存、llama 进程、模型、Slot、KV Cache 与 token/s 监控。
- llama 原生立即加载、立即释放和受保护缓存删除。
- 公开 Hugging Face GGUF 搜索与 llama 原生下载界面；此项为实验性功能。
- Windows 当前用户登录时启动后台 Agent 的可选开关。
- 自签名 Authenticode 作者签名及关于页证书指纹。

### 安全与稳定性

- 官方配置使用字段级可恢复事务，加入跨进程锁、提交前哈希检查和中断恢复。
- 官方配置备份使用 Windows DPAPI CurrentUser 加密；不读取或复制 `auth.json` 内容。
- Desktop-facing 代理限制为回环地址和必要路由，并剥离认证、Cookie 与账户元数据。
- Agent 通过协议、路径、PID、启动时间和心跳确认身份；不终止归属不明的进程。
- llama 子进程受 Windows Job Object 管理，避免异常退出后遗留模型进程。
- 内部端口随机化，公开端口仅在 ChatGPT 已关闭且发生冲突时事务迁移。
- 日志限制大小，不记录 Prompt、摘要正文、凭据、模型名称或任务 ID。

### 修复

- 修复 Desktop 未使用自定义 Provider 主路由导致的 502 和 GPU 不加载。
- 修复严格 Qwen Chat Template 对后续 system/developer 消息的拒绝。
- 修复 Local 切回 OpenAI 后模型、思考强度、详细程度、服务层级、上下文和权限字段恢复。
- 修复上下文容量和安全余量未及时传递给 Codex 的问题。
- 修复 ChatGPT 进程状态刷新、列表滚动、Agent 身份复用和端口竞态处理。
- 修复下载进度无限停留、完成事件误判和缺少本地缓存路径核对。

### 已知限制

- 模型下载为实验性，依赖当前 llama.cpp Router 构建和网络环境。
- CUDA 已完成主要真机验证；AMD/Vulkan 仍需更多硬件组合验证。
- Local 模式保留官方账户外壳以共享项目与历史，左下角仍可能显示账户名称或额度提示。
- ChatGPT Desktop 如果强制要求登录，Launcher 不提供绕过登录的能力。
- 本地模型的工具调用和压缩摘要质量由模型、量化、上下文、模板和参数共同决定。

