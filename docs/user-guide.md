# ChatGPT Local Launcher 0.9.0 用户功能说明

本文是 0.9.0 公测版的页面与交互基线，可作为后续 UI 重构的信息架构依据。启动器是 llama.cpp 与 ChatGPT Desktop 之间的管理外壳，不重复实现推理、KV Cache、模型调度或语义摘要。

## 1. 模式与数据边界

- `OpenAI` 与 `Local LLM` 互斥；切换前必须完全关闭 ChatGPT Desktop。
- 关闭启动器或客户端不会自动切回 OpenAI；当前模式和所选本地模型会保留。
- 用户可在 ChatGPT 关闭后直接执行 Local 模型 A → B，不必先回到 OpenAI。
- 项目、任务历史和开发上下文共用；Provider、可见模型列表和请求目标隔离。
- Local 复用 ChatGPT Desktop 的账户外壳以显示原生项目与历史，因此仍可能看到官方账户名称或额度提示。这不代表请求发送到 OpenAI。
- 本地代理会在请求进入 llama.cpp 前移除 Authorization、Cookie 和账户相关 Header。
- 切回 OpenAI 时恢复进入 Local 前的官方模型、思考强度、详细程度、服务层级、上下文和权限字段。

## 2. 首页

### 当前持久化模式

- `OpenAI`：确认后恢复官方 Provider 配置并启动 ChatGPT Desktop。
- `Local LLM`：进入本地模型选择，不会仅因点选标签而立刻改写配置。
- 当前模式显示来自持久化配置，不等同于某个进程此刻是否运行。

### 环境状态

- ChatGPT Desktop：运行中或已关闭；每秒刷新。
- Launcher Agent：停止、运行、身份不匹配或异常。
- llama.cpp：停止、Router 就绪、模型已加载、模型已休眠或异常。
- 切换、保存和恢复期间显示忙碌状态并禁止重复操作。

### 后台启动与诊断

- `登录 Windows 时启动后台 Agent`：写入或移除当前用户 Run 项；不使用管理员服务。
- 启动项只允许指向可信固定目录中的同包 Agent；可被普通用户组或沙箱修改的路径会被拒绝。
- `打开诊断日志文件夹`：打开 `%LOCALAPPDATA%\ChatGPTLocalLauncher\logs`。

## 3. 本地模型页

### llama.cpp Runtime

- `选择文件夹`：选择包含 llama.cpp 可执行文件的 Runtime 根目录。
- 探测内容：版本、Router、空闲休眠、Chat Template、metrics 和设备列表等能力。
- Local 模式或当前 Runtime 的服务运行时禁止更换 Runtime。
- 缺少必要目录时会询问是否创建；能力不足时显示具体诊断。
- `搜索并下载 GGUF（实验性）`：打开模型搜索与下载窗口。

### 扫描结果

- `扫描模型`：扫描 Runtime 的 `models` 目录。
- 支持单文件 GGUF 和完整多分片 GGUF；缺失分片会报告异常。
- `mmproj` 不作为独立主模型加入。
- `添加选中模型`：建立 Profile、BAT、Catalog 和 preset；不会加载模型或启动 ChatGPT。
- 首次添加后可选择调用 Runtime 的 `llama-fit-params`，应用前必须由用户确认。

### 已管理模型

- 模型列表显示所有已管理 Profile，并保留当前选中项。
- 摘要显示名称、别名、来源、路径、总大小、分片、当前参数和专用默认值。
- `编辑选中模型`：打开参数窗口。
- `由 llama 自动适配`：展示原生建议；可选择应用并保存为当前模型默认。
- `模型详情`：显示文件、来源、大小、分片、架构、参数量、训练上下文、模态与运行参数；部分信息仅在 llama 已运行且返回时可用。
- `切换并启动`：确认模型和上下文后写入 Local 配置，启动 Agent/Router，验证 Responses 路径，再启动 ChatGPT Desktop。

## 4. 模型参数窗口

- 显示名称：可编辑。
- 别名与模型文件：只读。
- Context：4K、8K、16K、32K、64K、80K、128K，对应 llama.cpp `-c`。
- 压缩安全余量：1K、2K、4K、8K、12K、16K、24K、32K。
- Codex 压缩线：`Context - 压缩安全余量`；安全余量不是输出 token 上限。
- Context 低于 16K 时仅提示风险，不自动修改或阻止保存。
- GPU Offload：Auto、All、0（仅 CPU）。
- Flash Attention：Auto、On、Off。
- K/V Cache：F16、BF16、Q8_0、Q4_0、Q4_1、IQ4_NL、Q5_0、Q5_1、F32。
- Batch：Default、128、256、512、1024、2048。
- Micro Batch：Default、64、128、256、512。
- 并发 Slot：1、2、4。
- 空闲释放：30 秒、1 分钟、5 分钟、15 分钟、禁用。
- Jinja Chat Template：开关及可选模型专用模板文件。
- `恢复此模型默认`：仅当当前模型已保存专用默认快照时可用。
- `保存`：保存当前 Profile。
- `保存并设为此模型默认`：保存并更新当前模型自己的默认快照，不影响其他模型。
- `取消`：放弃本次编辑。
- Local 客户端、Agent 或当前 Runtime 的 llama 服务运行时，所有模型参数编辑与自动适配均禁用。

## 5. 运行与缓存页

### 设备与运行监控

- CPU：型号、逻辑处理器、实时占用率。
- 内存：模块厂商、容量、类型、频率、已用/总量；系统未公开的字段显示不可用。
- GPU：适配器名称、总占用、Compute/CUDA、总显存和 llama 显存。
- llama 进程：进程数、CPU 和 RAM。
- `/models`：当前模型加载状态。
- `/metrics`：请求、Prompt/输出 token 与 token/s；不支持 metrics 的 Runtime 不阻断核心运行。
- `/slots`：Slot、KV Cache 和实际已用 token。该数值包含系统提示、项目上下文和工具 schema，通常高于窗口可见文字。
- `立即刷新`：主动更新一次所有可用指标。

### 模型驻留控制

- `立即加载当前模型`：调用 llama 原生 `/models/load`。
- `立即释放当前模型`：调用 llama 原生 `/models/unload`；下次请求可重新加载。
- 有活跃请求、状态未知、目标不匹配或服务不可用时禁止操作。

### 缓存管理

- `刷新缓存`：读取 llama 返回的可删除缓存及其来源。
- `删除选中缓存`：仅处理 llama `can_remove=true` 且 Profile 记录为 Launcher 下载的模型。
- 删除前必须确认；Local 运行中、手工 GGUF 和来源不明 Profile 禁止删除。
- 删除模型缓存不会删除官方账户、项目或历史。

## 6. 模型搜索与下载（实验性）

- 搜索框支持按钮或 Enter 搜索公开 Hugging Face GGUF 仓库。
- 仓库信息包括作者、用途、许可证、下载量、更新时间和 gated 状态。
- 变体信息包括量化、大小和分片数量。
- `下载选中变体`：检查磁盘空间后，通过临时 llama Router 原生下载到 Runtime 的 `models` 目录。
- 下载状态显示阶段、已下载/总字节、百分比和滚动速度。
- `取消下载`：请求 llama 取消；下载进行中必须先取消才能关闭窗口。
- gated/私有模型暂不支持；Launcher 不自行实现断点续传、文件传输或格式转换。
- 下载失败不会影响已经稳定的模型扫描、模式切换和本地推理。

## 7. 上下文压缩

- llama.cpp 按 Context 管理硬容量、KV Cache 和推理。
- Launcher 把每模型 `Context - 安全余量` 写成 Codex 可见的自动压缩线。
- Codex 监控真实 token，并在安全节点通过当前本地模型生成摘要，再用“摘要 + 最近原文”重建历史。
- Launcher 不猜测下一次回答长度，不生成摘要，不限制单次输出，也不保存第二套对话。
- 压缩摘要质量取决于模型能力、量化、KV Cache 精度和 Context；参数修改后会在允许保存时同步到 Profile、BAT、Catalog 和 preset。

## 8. 关闭行为

Local 服务运行时关闭启动器会显示：

- `取消`：返回启动器。
- `仅关闭窗口`：保留 Agent、代理和 llama Router，ChatGPT Local 继续可用。
- `停止服务并退出`：停止启动器拥有的后台服务，但保留 Local 模式与 Profile；已打开的 Local 客户端不能继续请求。

OpenAI 模式关闭启动器时会结束不再需要的 Agent。若正常停止失败，只有在路径、PID、启动时间、协议和心跳全部匹配时才允许询问强制终止；所有权不明的同名进程不会被结束。

## 9. 重要提示与对话框

- `保护旧版配置备份`：建议选择“是”；程序先 DPAPI 加密并回读验证，再删除对应明文备份。选择“否”不会影响当前配置，但以后仍会提示。
- Local 启动确认：显示模型、Context、账户外壳、凭据剥离、权限归属和官方配置恢复说明。
- OpenAI 恢复确认：只恢复受管 Provider 字段，不清空账户、项目或历史。
- Context 低于 16K：风险提醒，不阻止用户决定。
- 参数无效：列出全部错误并阻止保存。
- 缓存删除：提示不可逆并要求确认。
- ChatGPT 正在运行：阻止模式切换、Runtime 更换和配置事务。
- 配置冲突：停止写入，要求用户先处理外部修改，不自动覆盖。

## 10. 关于页

- 产品：ChatGPT Local Launcher。
- 版本：0.9.0 公测版。
- 开发者：甲总不是贾总。
- 显示产品薄壳边界、CUDA/Vulkan 兼容说明、下载实验标记和签名说明。
- 自签名 Authenticode 用于作者连续性与签名后完整性，不代表微软或第三方 CA 公共信任。
- 用户可通过 Windows 文件属性的“数字签名”页核对证书 SHA-256 指纹。

## 11. 数据文件与日志

- Launcher 数据与恢复记录：`%LOCALAPPDATA%\ChatGPTLocalLauncher`。
- ChatGPT/Codex 官方配置：只修改受管理字段，不整目录覆盖 `.codex`。
- 官方原始配置备份：当前 Windows 用户 DPAPI 加密。
- 模型 Profile、BAT、模板和 preset：位于用户选择的 llama.cpp Runtime 中。
- 诊断日志有大小/数量上限，不应包含 Prompt、摘要正文、认证凭据或完整账户元数据。

