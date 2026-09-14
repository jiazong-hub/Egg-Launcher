# ChatGPT Local Launcher 真机验收清单

此清单只在可以完全关闭 ChatGPT Desktop 的合适时间执行。当前开发会话中不要执行真实模式切换。

## 测试前

1. 保存当前工作，完全退出 ChatGPT Desktop，并在任务管理器确认没有 `ChatGPT` 进程。
2. 不要移动、删除或手工覆盖 `%USERPROFILE%\.codex`；Launcher 只会修改受管理字段并在 `%LOCALAPPDATA%\ChatGPTLocalLauncher` 保存恢复信息和当前 Windows 用户 DPAPI 加密的备份。若出现“保护旧版配置备份”，推荐选择“是”：Launcher 会先加密并回读校验旧明文备份，再删除对应明文；它不会改当前配置或读取 `auth.json`。选择“否”只会保留旧明文并稍后再提示。
3. 首次测试使用不重要的项目和任务。当前本地模型的文本请求已通过，但 shell/apply_patch 工具可靠性尚未验收。
4. 确认 llama.cpp Runtime 与 GGUF 来自可信来源，并记录测试前的显存占用、官方模型、思考强度和权限模式。

## 模型搜索与原生下载

1. 在“本地模型”页点击“搜索并下载 GGUF”，输入较准确的公开模型名称；确认只返回带 GGUF 标记的 Hugging Face 仓库，并显示作者、许可证、更新时间和下载量。
2. 选择仓库，确认量化版本、大小和分片数来自远程文件元数据；gated 模型不得进入未授权下载流程。
3. 选择一个小型测试模型下载，确认界面持续显示 llama.cpp 返回的已下载容量、总容量和百分比，速度由相邻字节状态换算；下载过程中不应加载 GPU 模型权重。
   - 开始后应依次看到连接原生状态流、llama 已接受请求、等待首个字节、传输和核对缓存路径等阶段，不能只有静止的空进度条。
   - 临时断网或阻止 `llama-server.exe` 联网，确认界面最终显示 llama 原生连接/DNS/TLS诊断并停止临时 Router，而不是无限停在 0%。
   - 即使 llama 错误地发出完成事件，只要 `/models` 没有返回 `models` 目录内的真实文件路径，就必须判定失败。
4. 确认文件落在所选 Runtime 的 `models` 目录下，下载结束后临时 Router 已退出，模型自动登记为独立 Profile，并可继续编辑该模型专属参数。
5. 可另选模型测试“取消下载”；Launcher 不承诺自有续传或校验行为，后续处理以当前 llama.cpp 原生结果为准。
6. Local 模式运行时仍应允许搜索和查看模型详情，但点击下载必须明确拒绝启动第二个 llama-server；切回 OpenAI 且 Runtime 空闲后才能下载。

## OpenAI → Local

1. 启动 `Launcher.App.exe`。
2. 在首页按需勾选“登录 Windows 时启动后台 Agent”；确认界面报告已注册。此操作只创建当前用户的精确 Agent 启动项；若程序仍位于下载目录、共享目录或开发工作区且 ACL 允许普通用户修改，Launcher 会拒绝注册，请先移至仅当前用户可写的固定目录。
3. 在“本地模型”页选择 llama.cpp Runtime；确认版本、Router、空闲休眠能力和设备信息正确。
4. 扫描并手动添加模型；可选择“由 llama 自动适配”，确认界面先展示 `llama-fit-params` 的结果且只有再次确认才应用；也可自行编辑 Context、压缩安全余量、KV Cache、GPU Offload 和空闲休眠。Launcher 不自动给出或恢复通用硬件参数推荐；Context 未设置或小于 Desktop Catalog 技术下限 1,024 时应阻止切换。
   - 设置 Context 32K、压缩安全余量 8K，确认模型详情显示 Codex 压缩线 24K；安全余量等于或大于 Context 时必须拒绝保存。
   - 压缩安全余量不得生成 llama 输出 token 上限；它只应写入 Codex 的 `model_auto_compact_token_limit`。
   - 设置 Context 为 4K 或 8K，确认参数窗口、模型列表和 Local 启动确认均显示“建议使用 16K 或更高”的风险提醒，但仍允许保存和启动；Launcher 不得自动改变 Context 或安全余量。
   - 若模型内置模板含已知的严格 Qwen 系统消息守卫，确认 Profile 显示 `scripts\templates\<模型>.codex-compatible.jinja`，生成的 BAT 包含 `--chat-template-file`。
   - 编辑模型 A，选择“保存并设为此模型默认”，再改变并保存 A 的参数；“恢复此模型默认”应回到 A 的快照。
   - 模型 B 的参数不得改变，也不得继承 A 的专用默认；未建立专用默认的模型应禁用“恢复此模型默认”。
   - 自动适配正在运行时不得同时启动 Local 服务；应用建议后确认对应 BAT 与当前 Local Router preset 使用同一组参数。
   - Local 客户端、后台 Agent 或 llama-server 运行时，所有模型的“编辑参数”和“由 llama 自动适配”均应禁用；选择“停止服务并退出”且进程完全结束后可再次编辑。若模式仍为 Local，修改当前模型后应立即更新 Catalog、Codex 压缩线和 Router preset，但不得自行重启服务。
5. 选中该模型，点击“切换并启动”。确认 Launcher.Agent 和无模型 Router 启动成功。
   - 切换到 Local 后，“选择 Runtime 文件夹”必须禁用；只有回到 OpenAI 后才能改用另一个 Runtime。更换 Runtime 后不得沿用旧目录的模型 ID。
6. ChatGPT Desktop 打开后验证：
   - 不再出现“完成 Windows 设置 · config_load”页面；
   - 只显示当前选中的一个本地模型；
   - 不显示 OpenAI 在线模型；
   - 左下角可以继续显示官方账户名称，这是共享原生项目和历史所需的 Desktop 外壳；它不是“本地账户”，也不表示请求使用官方账户；
   - 官方账户凭据和账户元数据没有被发送到本地 Provider；
   - 原有项目列表和任务历史仍然可见；
   - 可以打开并查阅既有任务上下文；
   - 连续使用至少三个无副作用短提示验证本地文本回复，不能在第二条消息就出现上下文溢出；客户端的可见 token 数不包含全部隐藏指令和工具 schema；
   - 在测试任务中逐步接近当前模型窗口，确认 Codex 在硬上限前显示自动压缩，压缩完成后任务可继续、上下文占用明显下降；摘要质量取决于当前模型，但不得出现 `Cannot determine type of 'item'`；
   - 若成功，确认 `%LOCALAPPDATA%\ChatGPTLocalLauncher\logs\proxy.jsonl` 出现 `codex_compaction_request`，其 `protocolShape` 为 `codex_local_summary_turn`，随后同一请求的 `upstream_response` 为 200。若出现 `remote_v2_trigger` 或 `legacy_remote_endpoint`，说明 Codex 没有走本地压缩，应判定失败；
   - 日志不得包含用户文字、摘要正文、认证信息、模型名称或任务 ID；Launcher 不得改写请求或响应，也不得生成摘要或 compaction item。
   - 在 ChatGPT Desktop 输入框下方选择所需权限模式，再新建测试任务确认生效。Launcher 不替用户选择权限；旧失败任务可能保留旧的任务级权限，需单独记录，不能据此判定全局恢复失败。

## Local 模型 A → B

1. 完全退出 ChatGPT Desktop，但不要切换回 OpenAI。
2. 在 Launcher 的“本地模型”页选择另一个已管理模型，点击“切换并启动”。
3. 确认不需要经过 OpenAI；Agent 只重启自己拥有的 Router，并等待 `/models` 返回模型 B 后才启动客户端。
4. 确认客户端只显示模型 B，项目列表、任务历史和上下文保持可见。
5. 再次退出客户端；后续 Local→OpenAI 验证必须仍恢复最初进入 Local 前保存的官方字段，而不是模型 A 或 B 的字段。

## 运行监控、立即加载与释放

1. 进入“运行与缓存”，确认 CPU 型号、内存容量/模块、显卡与 llama `--list-devices` 信息符合本机；厂商或驱动未提供的字段应显示不可用，而不是 `0%` 或猜测值。
2. Local Router 运行时，确认模型状态来自 llama `/models`；模型加载后检查 `/metrics` 与 `/slots` 数据，包括实际 Slot token、KV、请求数和 token/s。
   - 在该页面停留至少 30 秒，确认状态不会在 Router 正常运行时变成“未通过校验”；Agent 状态心跳应持续更新。
3. 对比 llama 自带浏览器页面。Slot token 应反映系统提示、项目上下文和工具 schema，因此可以高于窗口可见文字；若 llama 未返回 `n_past/n_tokens`，Launcher 应显示未返回，不自行估算。
4. 点击“立即释放当前模型”，确认 llama 状态转为已释放且显存下降；再点“立即加载当前模型”，确认由 llama 重新载入。正在处理请求时不要执行此项验收。
5. NVIDIA 观察 Compute/CUDA，AMD 观察 Compute；若 Windows WDDM 驱动没有公开 Performance Counters，应显示不可用，不影响 llama 推理。

## llama 缓存管理

1. 先切回 OpenAI 并确认当前 Runtime 没有 `llama-server` 进程，再点击“刷新缓存”。临时 Router 不应加载模型。
2. 只有由新版 Launcher 调用 llama 下载、且 llama 返回 `can_remove=true` 的模型才应出现；手工 GGUF 与旧版来源不明 Profile 不得出现删除按钮。
3. 选择一个可重新下载的测试模型并确认删除。确认模型文件由 llama 删除，对应 Launcher Profile 与带归属标记的 BAT 被清理，官方账户、项目、历史不受影响。
4. Local 模式或 Local 服务运行时尝试管理缓存，应被拒绝。

## Local 持久化与直接启动

1. 退出 ChatGPT Desktop，不点击 OpenAI。
2. 确认 `SelectedMode` 仍为 Local；若 Profile 启用了空闲休眠，等待其设置的时间，确认 llama.cpp 已卸载模型权重与 KV Cache并释放显存，轻量 Router 继续存在。
3. 绕过 Launcher，从 Windows 开始菜单直接启动 ChatGPT Desktop。
4. 确认客户端仍按 Local 配置运行，并能在首次请求时重新加载当前模型。
5. 若已启用登录启动，注销并重新登录 Windows 后重复第 3—4 步，确认 Launcher UI 无需运行。
6. Local 服务运行时关闭 Launcher，分别验证“仅关闭窗口”和“停止服务并退出”：前者应保持 Local 可用，后者应停止 Agent、代理与 Router、释放便携包目录，同时保持 `SelectedMode = Local`。
7. 切回 OpenAI 后关闭 Launcher，确认无 `Launcher.Agent.exe` 残留，便携包目录无需重启即可重命名或删除。
   - 若正常退出失败，Launcher 只有在 PID、可执行路径、启动时间、协议与新鲜心跳全部匹配时才可提示强制结束；无法确认归属时不得按进程名误杀。

## Local → OpenAI

1. 再次完全退出 ChatGPT Desktop。
2. 打开 Launcher，点击首页的 OpenAI。
3. ChatGPT Desktop 启动后验证：
   - Local Provider 和本地模型均不可见；
   - 原 OpenAI 账户仍保持登录；
   - 在线模型与官方工具恢复正常；
   - 测试前记录的官方模型、思考强度、详细程度和服务层级准确恢复，不显示意外的“自定义”；
   - 项目列表、任务历史和上下文未被复制、隐藏或清空；
   - 本地模型仍未加载，Launcher 管理的 Router 已停止。

## 异常处理

1. 在 ChatGPT Desktop 已关闭时，用其他程序占用设置中保存的 Desktop-facing 回环端口，再启动 Local；确认 Agent 不结束该程序，而是迁移到新的随机回环端口，设置、恢复记录和 `config.toml` 中 Provider 地址一致，随后客户端可正常启动。
2. 保持 ChatGPT Desktop 运行并制造同样的端口冲突；确认 Launcher 拒绝迁移和改写配置，提示先关闭客户端。
3. 制造 llama.cpp 内部端口的瞬时地址占用；确认 Agent 仅换内部端口重试。模型文件、模板或参数错误必须直接报告，不能被当作端口冲突循环重试。

- 若切换被拒绝，先确认 ChatGPT 的所有进程确实退出，不要强制杀死或绕过保护。
- 若提示配置冲突，停止测试；不要删除 `config.toml`、`auth.json` 或恢复记录。
- 若出现 502，点击 Launcher 首页的“打开诊断日志文件夹”，保留 `agent.log`、`proxy.jsonl`、`recovery.json` 和错误文字。`safety_proxy_error` 中的诊断 ID、stage、exceptionType 是首要信息；llama.cpp 返回的非成功状态会以 `upstream_error` 原样传回。
- `proxy.jsonl` 不应包含提示词正文、Authorization、Cookie 或 API Key；若发现这些内容，立即停止测试并报告。
- 只有 Launcher 明确报告恢复成功后，才继续启动 OpenAI 模式。

## 通过标准

以上流程全部通过，并且切回 OpenAI 后账户、在线模型、官方工具、项目和历史与测试前一致，才可把真实 ChatGPT Desktop 集成标记为已验收。
