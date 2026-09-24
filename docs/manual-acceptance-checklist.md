# Egg Launcher 真机验收清单

此清单只在可以完全关闭 ChatGPT Desktop 的合适时间执行。当前开发会话中不要执行真实模式切换。

## 0.9.2 本地版本状态（2026-09-24）

- 此前 Release 构建通过；本地 v0.9.2 标签的自动化测试和下列完整沙箱手工验收尚未执行。
- 用户已确认 OpenAI 模式可查阅旧 Local 项目。原任务跨 Provider 续聊的第二阶段已放弃，不属于本版验收目标。
- 此版增加按模型保存的 Codex 命名权限配置；公开发布前仍须完成验收。

## 0.9.1 公测封存状态（2026-09-21）

- 已由用户真机确认：OpenAI → Local、本地模型 GPU 加载与 token 输出、Codex 原生自动压缩、Local → OpenAI 恢复及持续稳定使用。
- 已由自动化确认：249 项测试覆盖配置事务、恢复、端口迁移、进程所有权、凭据剥离、模型扫描、Profile、BAT、监控解析、缓存保护、下载状态、语言偏好、MTP/视觉指纹和思考档位客户端。
- 模型搜索可返回仓库和 GGUF 元数据；下载仍为实验性，当前网络环境下的失败不阻断核心公测。
- AMD/Vulkan、无官方账户的全新 ChatGPT Desktop，以及不同 llama.cpp 构建仍属于跨设备复测项。
- Codex 工具执行质量取决于所选模型、上下文、Chat Template、项目权限与客户端策略，不能仅用文本推理成功推导。

## 测试前

1. 保存当前工作，完全退出 ChatGPT Desktop，并在任务管理器确认没有 `ChatGPT` 进程。
2. 不要移动、删除或手工覆盖 `%USERPROFILE%\.codex`；Launcher 只会修改受管理字段并在 `%LOCALAPPDATA%\ChatGPTLocalLauncher` 保存恢复信息和当前 Windows 用户 DPAPI 加密的备份。若出现“保护旧版配置备份”，推荐选择“是”：Launcher 会先加密并回读校验旧明文备份，再删除对应明文；它不会改当前配置或读取 `auth.json`。选择“否”只会保留旧明文并稍后再提示。
3. 首次测试使用不重要的项目和任务。本地文本与上下文压缩链路已通过；shell、apply_patch、浏览器等工具仍应按每个模型单独验收。
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
7. 选择搜索结果后确认“当前模型仓库”可打开正确仓库；选择具体量化文件后“当前模型版本”可打开对应页面。点击空白取消选择时两个按钮应恢复禁用。
8. 对 llama 原生 Hugging Face 缓存结构进行验收：即使实际 GGUF 位于 `blobs` 且没有扩展名，只要 snapshot/ref 能解析为目标文件，下载仍应成功并自动登记；再次扫描不得重复添加。

## 界面、语言与托盘

1. 分别切换深色和浅色主题，确认主背景、侧栏、卡片、文字、边框、按钮和提示条均可读，不出现双层圆角或任务栏遮挡。
2. 在中文、英文和“跟随 Windows 显示语言”之间切换，重启后确认偏好保持；英文长按钮不得挤成不可读的两行。
3. 单击托盘图标确认窗口只打开一次；退出过程中重复单击不得让窗口重新出现。
4. 右键菜单的当前模式、禁用状态和模式子菜单必须与主界面一致；悬停状态按多行显示，切换语言后不得短暂混用旧语言值。
5. 主窗口前台时观察状态低频更新；最小化或隐藏到托盘后确认停止周期性昂贵检测，打开托盘菜单时只按需刷新。
6. 页面切换、选项卡、提示出现/消失应流畅；模式卡片不得持续跳动，复选框对勾不得闪烁。

## OpenAI → Local

1. 启动 `Launcher.App.exe`。
2. 在首页按需勾选“登录 Windows 时启动后台 Agent”；确认界面报告已注册。此操作只创建当前用户的精确 Agent 启动项；若程序仍位于下载目录、共享目录或开发工作区且 ACL 允许普通用户修改，Launcher 会拒绝注册，请先移至仅当前用户可写的固定目录。
3. 在“本地模型”页选择 llama.cpp Runtime；确认版本、Router、空闲休眠能力和设备信息正确。
4. 扫描并手动添加模型；可选择“由 llama 自动适配”，确认界面先展示 `llama-fit-params` 的结果且只有再次确认才应用；也可自行编辑 Context、压缩安全余量、KV Cache、GPU Offload 和空闲休眠。Launcher 不自动给出或恢复通用硬件参数推荐；Context 未设置或小于 Desktop Catalog 技术下限 1,024 时应阻止切换。
   - 新添加模型只识别一次 Dense/MoE；识别失败时类型保持未指定，并禁止打开运行参数，直到用户手动选择。
   - 将已确定类型改为另一类型时，确认旧参数配置被重建而非直接沿用；主模型文件不得修改。
   - 参数窗口依次显示基础参数、视觉模块、MTP 推测解码、高级参数、模型信息五个选项卡；未调整的字段不写入 Profile。
   - 设置 Context 32K、压缩安全余量 8K，确认模型详情显示 Codex 压缩线 24K；安全余量等于或大于 Context 时必须拒绝保存。
   - 压缩安全余量不得生成 llama 输出 token 上限；它只应写入 Codex 的 `model_auto_compact_token_limit`。
   - 设置 Context 为 4K 或 8K，确认参数窗口、模型列表和 Local 启动确认均显示“建议使用 16K 或更高”的风险提醒，但仍允许保存和启动；Launcher 不得自动改变 Context 或安全余量。
   - 若模型内置模板含已知的严格 Qwen 系统消息守卫，确认 Profile 显示 `scripts\templates\<模型>.codex-compatible.jinja`，生成的 BAT 包含 `--chat-template-file`。
   - 编辑模型 A，选择“保存并设为此模型默认”，再改变并保存 A 的参数；“恢复此模型默认”应回到 A 的快照。
   - 模型 B 的参数不得改变，也不得继承 A 的专用默认；未建立专用默认的模型应禁用“恢复此模型默认”。
   - 自动适配正在运行时不得同时启动 Local 服务；应用建议后确认对应 BAT 与当前 Local Router preset 使用同一组参数。
   - Local 客户端、后台 Agent 或 llama-server 运行时，所有模型的“编辑参数”和“由 llama 自动适配”均应禁用；选择“停止服务并退出”且进程完全结束后可再次编辑。若模式仍为 Local，修改当前模型后应立即更新 Catalog、Codex 压缩线和 Router preset，但不得自行重启服务。
   - 更改 Context 后确认提示“已配置 xx，重新加载后生效”；服务重载前左侧运行上限可以继续显示旧值，重载后必须更新。
   - 对未验证思考档位的模型确认参数页开关禁用；在模型管理检测到明确档位后才可启用。开关关闭时启动 Local 不得执行档位探测或因探测超时失败。
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

## 0.9.2 按模型沙箱设置

1. 备份并记录测试前 `config.toml` 中的沙箱配置和 ChatGPT Desktop 当前审批方式。为模型 A 设置“禁止命令联网”和一个测试目录的只读权限，保存后确认只修改模型 Profile，尚未启动时 Codex 配置没有变化。
2. 为模型 B 设置“完整联网”和一个可写测试目录；确认不同模型的设置分别保存在各自 Profile。
3. 退出 ChatGPT Desktop，切换并启动模型 A。检查 Local 模式只为命令沙箱应用 A 的网络和只读路径规则；尝试读取测试目录应成功，写入应被拒绝。
4. 完全退出客户端后切换到模型 B。检查 A 的规则没有残留；联网与 B 的读写目录按设定生效。
5. 再切换到一个没有 `SandboxSettings` 的旧模型。检查 Codex 原始沙箱配置恢复，A/B 的设置没有泄漏到该模型。
6. 切回 OpenAI 并逐项比较沙箱相关配置与开始前记录；确认 `approval_policy`、`approvals_reviewer` 和客户端审批选择没有变化。
7. 分别验证网络设置“沿用 Codex 默认”“关闭”“完整联网”；完整联网不应写入域名白名单。
8. 使用 `:danger-full-access`、启用临时目录排除或预先定义 `permissions.egg_launcher_active` 的配置尝试切换；确认启动器给出冲突并停止，原 `config.toml` 保持不变。
9. 在模型切换事务期间人为结束启动器后重启，检查恢复记录能回滚或继续事务；审批字段仍不得被覆盖。

## MTP 推测解码

1. 使用带 `nextn_predict_layers` 元数据的 GGUF，确认参数窗口在“基础参数”和“高级参数”之间显示“MTP 推测解码”，状态提示检测到内置 MTP，但默认不开启。
2. 启用内置 MTP 并保留各参数为空，确认生成的 preset 只增加 `spec-type = draft-mtp`，不会擅自写入 N Max 或 P Min。
3. 在“本地模型管理”选择主模型并点击“加载外置 MTP”，确认目录外文件、主模型自身和无效 GGUF 会被拒绝；合法文件必须经过隔离 Router 联合加载及一次极短推理，失败时不得修改原配置。
4. 验证成功后确认只建立关联，MTP 开关仍关闭；参数页不再允许直接浏览或更换外置文件。只有内置候选或已验证外置来源才能操作开关。
5. 确认外部模式生成 `spec-draft-model`，且本地模型扫描不会把 `mtp-*.gguf` 或启动器 MTP 下载目录中的文件显示成独立聊天模型。
6. 更换主模型、辅助文件、Runtime，或外置文件的大小/修改时间发生变化后，应让验证失效并自动关闭 MTP；普通 MTP 参数调整不应丢失文件兼容性验证。
7. 使用不匹配的辅助 GGUF 验证临时 Router 会退出、原配置保持不变，并显示 llama.cpp 的当前配置验证错误。
8. 搜索仓库时确认 MTP 候选文件单独列出；一次只能存在一个下载任务。主模型完成后自动添加，MTP 文件完成后不自动添加或关联。
9. 内置 MTP 候选或外置 MTP 验证成功后，确认本地模型名称显示为 `【模型类型】【MTP】模型名称`；MTP 开关关闭时标识仍保留，因为标识代表能力而非启用状态。
10. 取消外置 MTP 关联后，确认标识立即消失（若主模型同时包含内置 MTP，则回退为内置来源并继续显示）；删除或替换已关联的外置文件后重新进入管理页，确认标识消失、MTP 自动关闭且磁盘文件不会被启动器删除。

## 视觉模块

1. 使用内置视觉模型或匹配的外置 mmproj，确认只有经过 llama `/models` 能力检查和真实 `input_image` 隔离请求后才显示 `【Vision】`。
2. 加载外置视觉模块失败时，正式 Profile、Catalog 和当前运行配置不得改变；成功后只建立关联，视觉开关仍由参数页控制。
3. 开启视觉后确认 Catalog 声明图片输入，关闭后只声明文本；未验证或未知能力的开关必须禁用。
4. 验证 `data:image/...` 输入可透传；`file:`、HTTP(S) 图片引用不由 Launcher 代为读取或下载。
5. 取消视觉关联不删除 mmproj。主模型、mmproj、Runtime/MTMD 库或模板变化后，能力指纹应失效、标识撤销并自动关闭开关。

## 模型列表移除与缺失文件

1. 对未运行模型选择“从列表中移除”，确认只删除 Profile、BAT 和受管配置，不删除任何主 GGUF、MTP、mmproj 或缓存文件。
2. 对当前配置模型执行移除时，应提示建议先切回 OpenAI 并询问是否继续；当前正在运行的模型必须拒绝移除。
3. 手动从硬盘移走模型后重新进入模型管理，确认条目变灰并提示模型文件已删除；仍可从列表移除旧配置。
4. 将模型文件恢复或重新扫描添加，确认新 Profile 被重建，旧失效配置不会自动复用。

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
   - 当前官方模型仍为官方 Provider；本地模型不作为可选在线模型出现；配置中保留未激活的本地历史 Provider 定义；
   - 原 OpenAI 账户仍保持登录；
   - 在线模型与官方工具恢复正常；
   - 测试前记录的官方模型、思考强度、详细程度和服务层级准确恢复，不显示意外的“自定义”；
   - 项目列表、任务历史和上下文未被复制、隐藏或清空；
   - 本地模型仍未加载，Launcher 管理的 Router 已停止。
4. 在 OpenAI 模式打开一条先前由 Local 模型创建的任务，确认可以查看完整历史且不再反复出现 `chatgpt_local_launcher not found`；不要在该旧任务内发送新消息，原任务跨 Provider 续聊已放弃。
5. 再打开一条官方模式创建的任务，确认仍可正常继续；检查本地 Router 未重新启动，模型未因查阅旧 Local 任务而加载。
6. 完全退出 Desktop，再由 Launcher 启动一次 OpenAI；重复第 4—5 步，确认旧版恢复记录也能安全补入兼容定义。

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
