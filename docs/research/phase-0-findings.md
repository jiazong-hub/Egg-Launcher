# Phase 0 实测记录

> 历史记录：本文记录了早期兼容性探索。其中的请求正文正规化、工具过滤和本地 compaction 方案已于 2026-09-11 废弃并从程序删除；当前边界见 [0002：Launcher 保持为 llama.cpp 的薄管理壳](../architecture/0002-thin-launcher-boundary.md)，v0.9.1 的用户可见行为见[当前功能说明](../user-guide.md)。

日期：2026-09-10—2026-09-11

## 本机环境

- Windows 10 Enterprise 22H2 x64，Build 19045。
- 项目内 .NET SDK：10.0.401；未修改系统 PATH。
- ChatGPT Desktop 进程可以按精确进程名 `ChatGPT` 检出。
- 用户级 Codex 配置与历史状态存在；只读探测只返回配置键名，未读取 `auth.json` 内容。
- `D:\llama.cpp\llama-server.exe`：`0.4.0-dev`，build 10873，commit `6de9cdb26`。
- 该 Runtime 支持 `--models-dir`、`--models-preset`、`--models-max`、`--models-autoload`、`--list-devices` 与 `--sleep-idle-seconds`。
- 当前模型目录识别出一个主模型 `Qwen3.8-9B-Coder-Q4_K_M.gguf`；`mmproj-Qwen3.8-9B-Coder-BF16.gguf` 被正确排除为非独立主模型。

## 上游契约

OpenAI 官方配置参考确认：用户级配置位于 `~/.codex/config.toml`；Provider、`openai_base_url`、`model_catalog_json` 等机器级键不能由项目内 `.codex/config.toml` 覆盖。`model_provider` 未设置时使用内置 OpenAI Provider，顶层 `openai_base_url` 可以改变其请求入口；自定义 Provider 则可以显式声明 `supports_websockets = false` 与 `requires_openai_auth = false`。

- <https://learn.chatgpt.com/docs/config-file/config-reference>

当前 Ollama 的 ChatGPT 集成实现表明：使用本地路由时设置顶层 `model`、`model_catalog_json`、`openai_base_url`，保持 `model_provider` 未设置；修改前保存恢复状态；已有 `auth.json` 时不创建或替换认证文件；对 WebSocket 升级返回 426，让 Codex 立即回退到 HTTP。本项目在第二轮真机测试后采用这一主路由，并继续坚持“字段级恢复、绝不读取或覆盖已有认证”。代理会在转发到 llama.cpp 前剥离认证头。

- <https://github.com/ollama/ollama/blob/main/cmd/launch/codex_app.go>

当前 llama.cpp 文档确认 Router 为无模型主进程，按请求动态加载模型；preset 使用 INI，键名对应去掉前导短横线的命令行参数，并支持 `load-on-startup` 与 `stop-timeout`。

- <https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md#using-multiple-models>

## 已完成的代码验证

- 模式与运行状态分离。
- JSON 设置原子写入和 `.bak` 备份。
- ChatGPT 配置键名与历史状态只读发现。
- ChatGPT 运行时禁止切换。
- 字段级 Local 配置应用、Local A→B 更新与 OpenAI 恢复事务；真实配置入口受客户端关闭检查和用户确认保护，当前开发会话未触发。
- 恢复时保留未知设置、项目表和 Local 使用期间新增的非受管设置。
- 受管字段被第三方修改时停止自动恢复并报告冲突。
- Runtime 版本、设备与 Router 能力探测。
- GGUF、mmproj 与完整/缺失分片识别。
- Profile 参数校验与 Router preset 生成。
- Router 只允许回环地址，V1 强制 `models-max = 1`。
- 生成的 ChatGPT model catalog 强制只包含当前选中的一个本地模型，并在写入 Provider 配置前校验 Catalog 与 model slug 一致。
- Router Process Manager 只持有并停止自己启动的精确 PID，分别泵送 stdout/stderr；日志名包含随机事务后缀，避免快速重启冲突。
- 模式切换协调层将配置应用与 `SelectedMode` 持久化组成事务：只有显式切换才改写 Provider 字段，关闭客户端不会触发恢复；Local→OpenAI 保留最后选择的本地模型，供下次切回。
- Local A→B 不经过 OpenAI：更新事务始终保留首次进入 Local 前的官方恢复点，配置、生成物或设置保存失败时回滚到模型 A；首次 OpenAI→Local 的最终记录写入失败也会自回滚。
- 后台 Agent 监督层按持久化模式启动或停止自己拥有的 Router；客户端由运行转为关闭时，调用精确模型 ID 的 `/models/unload`，保留 Local 配置和无模型 Router。
- Agent 记录当前模型、Runtime Root、公共端口与 Router preset 内容哈希的运行指纹；模型或该模型参数/Template 变化后停止旧代理和自有 Router，再按新 preset 启动目标 Router。UI 只有在 `/models` 精确出现目标 alias 后才启动 ChatGPT。
- 首页提供显式的当前用户 Windows 登录启动开关；注册值只指向同包的 `Launcher.Agent.exe`，可逆且不会在未点击时自动修改注册表。Release Agent 使用无控制台窗口子系统。
- 早期回环 Responses 兼容代理曾改写 `system/developer` 和工具定义；该实现随后按薄壳边界删除。当前代理不解析 JSON 正文，只进行安全边界和必要的传输解压。
- 正式 Local 配置使用内置 OpenAI Provider 的顶层 `openai_base_url` 路由，保持 `model_provider` 未设置，并以 local-only Catalog 隐藏在线模型；本地代理剥离认证头。切回 OpenAI 时字段级恢复原 Provider、URL、Catalog 与权限默认值。旧 `chatgpt_local_launcher` 定义作为不激活的兼容项保留，以便旧失败任务解析，但不进入模型列表。
- Launcher 不再为 Local 强制写入权限模式；Desktop 输入框下方的原生权限控件是唯一交互入口。权限字段只做进入 Local 前快照和 OpenAI 恢复，Local 期间改变 reviewer 不会触发 Provider 配置冲突。
- 安全筛查后代理改为严格路由和请求头白名单，拒绝 Origin、账户元数据和管理端点；Router 控制请求只走 Agent 私有动态回环端口。
- 官方配置备份已改为 Windows 当前用户 DPAPI 加密，并设置仅当前用户、SYSTEM 与 Administrators 可访问的 ACL；旧明文备份只在用户确认后执行“先加密并校验、再更新恢复引用、最后删除明文”的迁移。
- 模式切换增加跨进程文件锁、启动恢复协调器和配置/Launcher 设置两阶段提交；崩溃后根据当前受管字段完成提交或回滚，无法证明归属时停止并报告冲突。
- Router 进程加入 Windows Job Object，Agent 被终止时操作系统回收其 llama-server 进程树；异常 Router 退出会先清理陈旧代理再重启。日志均设大小/数量上限。
- 登录启动会拒绝位于普通用户组或 Codex 沙箱可修改目录中的 Agent；应用和 Agent 均启用单实例。
- NuGet 审计已启用，所有项目生成锁文件；联网 `--locked-mode` 恢复成功，直接和传递依赖均未发现已知漏洞。

## 本机 Smoke Test

- 从 `D:\llama.cpp\models` 生成单模型临时 preset，`load-on-startup = false`。
- Router 使用随机回环端口启动，`/health` 与 `/models` 均通过，模型清单只返回 `Qwen3.8-9B-Coder-Q4_K_M`。
- 对 `POST /v1/responses` 发送故意缺少 `model` 的请求，返回 HTTP 400；由此确认路由存在，并且在加载模型权重前完成契约探测。
- 早期改写正文的兼容代理曾使隔离 Responses 样本返回 HTTP 200；该结果仅保留为历史记录，不能作为当前透明代理的验收结论。
- 使用临时 Codex Home、隔离工作区、顶层 `openai_base_url` 和临时本地哨兵认证运行 OpenAI Codex `0.153.4`：WebSocket 收到一次 426 后立即回退 HTTP，最终精确返回 `CODEX_LOCAL_OK`，耗时 2.09 秒。
- 冒烟测试通过 Agent 私有 Router 地址调用官方 `POST /models/unload` 并成功卸载；Desktop-facing 代理对同一管理路径返回 404。随后 Router/Job 正常停止，复核没有 `llama-server` 残留。
- 直接把 Codex 请求交给 llama.cpp 时，Qwen3.8 严格模板会因中途 `system/developer` 消息报错。当前修复从 GGUF 元数据读取其内置模板，只在独立副本中放宽已知守卫，并通过 llama.cpp 原生 `--chat-template-file` 加载；代理正文保持透明。
- 测试完成后只停止 Process Manager 所有的 PID；复核无 `llama-server` 残留，测试端口无监听。
- Codex Home 只读探测已在当前沙箱环境下正确解析真实用户目录；发现配置与历史状态，仅报告受管键名，不读取 `auth.json` 内容。
- Phase 1 基础界面已可选择并验证 Runtime、扫描 GGUF、区分未添加/已管理模型，并把默认 Profile 原子保存到 `<llamaRoot>\scripts\profiles`。扫描本身不会自动添加模型，添加或编辑 Profile 不会加载权重。
- 基础 Profile 编辑器为 Context、压缩安全余量、GPU Offload、Flash Attention、KV Cache、并发、Jinja、Batch 和模型专用 Chat Template 提供控件与 Tooltip；Edit / Save / Cancel / 模型专用默认已接通。不存在硬件无关的“恢复内置推荐”；新增可选入口只调用 Runtime 自带的 `llama-fit-params` 并由用户确认。Local 客户端、后台 Agent 或当前 Runtime 的 llama 服务运行期间，所有模型参数均禁止编辑；Local 模式已保存但服务完全停止时允许事务性更新当前模型配置。
- Profile 保存后同步生成 `<llamaRoot>\scripts\<profile-id>.bat`；BAT 通过 `%~dp0..` 推导 Runtime Root，不写死盘符，并只覆盖带有 Launcher 归属标记的生成脚本，遇到同名手写 BAT 时拒绝覆盖。
- 模式切换 UI 已接入事务协调器：Local 按钮先进入模型选择，只有“切换并启动”才会执行；客户端运行、Agent 不可启动或其他前置条件失败时不写 Provider。OpenAI 恢复同样要求客户端关闭并由用户确认。
- ChatGPT Desktop 启动适配器从当前用户的 MSIX 注册读取候选，并交叉校验受信 Package Family、Publisher ID、Manifest 发布者、真实 Application ID 与 `ChatGPT.exe` 入口；版本号只用于多版本择优，不依赖英文显示名或固定 `App` ID。运行检查优先匹配进程 AUMID，身份暂时不可读时为保护配置而保守判定为运行中。本机 Win10 22H2 已用当前 `OpenAI.Codex_…` 包完成只读识别验证。
- 已生成 `win-x64` self-contained 便携预览包，同时包含 Launcher 与单实例 Agent。在本机未全局安装 .NET 10 Runtime 的条件下，Agent `--once`、日志落盘自检与 WPF 隐藏启动均通过。
- 当前自动化测试共 147 项，覆盖 Profile 来源与压缩安全余量迁移、模型专用默认、缓存元数据删除保护、llama-fit 输出白名单、拟合参数的 preset 生成、Codex 压缩线传播、Router 模型管理、Prometheus/Slot 数据解析、配置恢复、代理安全和 Router 生命周期。受限网络下无法刷新 NuGet 漏洞索引时会产生 NU1900 警告，但不影响使用已有锁定依赖编译。

## 首次真实 Desktop Local 测试

- OpenAI → Local 与 Local → OpenAI 均完成，官方配置和账户环境恢复正常。
- 新建本地项目已保存；失败 rollout 的 `cwd` 与该项目一致，证明 Codex 实际选中了项目，左上角未显示属于客户端刷新问题。
- Desktop 对本地 Provider 的请求以 502 结束；Router 已启动且列出目标模型，但没有收到推理请求，因此模型和 GPU 没有加载。
- 失败任务在本地 rollout/session index 中存在，但没有 assistant 消息，也未正常出现在任务列表。成功任务的跨模式可见性仍待复测。
- 已为兼容代理增加压缩请求、无长度 POST、敏感头剥离与不含正文/凭据的 JSONL 诊断；详见 [Desktop Local 首测记录](2026-09-10-desktop-local-first-test.md)。

## 第二次真实 Desktop Local 测试

- Launcher 可以再次切入 Local 并启动客户端，但首页客户端状态没有在启动后刷新，仍显示已关闭。
- Desktop 仍返回 502，GPU 未加载；`proxy.jsonl` 没有任何 Responses POST，证明独立自定义 Provider 主路由没有被 Desktop 使用。
- Local 下客户端账户外壳回退为英文，权限选择不可更改；切回官方后，打开失败任务显示“请求批准”。后者来自任务元数据中的 reviewer 值，并非官方全局配置被覆盖。
- 已把主路由迁移到内置 Provider + 顶层 `openai_base_url`，补齐权限字段往返、启动后状态刷新和 WebSocket 426 快速回退。新路由已经隔离端到端验证，但尚未在正在使用的真实 Desktop 上执行切换。

## 第四次真实 Desktop Local 测试

- 内置 Provider 路由已经由真实 Desktop 验证：首个“你好”请求到达 llama.cpp，GPU 加载模型，显存约 5.8/8 GB。
- 失败原因转为可量化的 Context 不足。首轮 prompt 已有 7,778 tokens，后续简单请求超过 8,500 tokens，而旧 Profile 只有 8,192；Desktop 显示的约 1K 不是包含隐藏 instructions、项目上下文和工具 schema 的完整请求。
- 当时版本曾把新建/重置 Profile 固定为 16,384 + Q4_0 K/V Cache，并拒绝低于 16K 的 Profile；该硬件无关推荐后来删除。当前仅执行 Desktop Catalog 的 1,024 token 技术下限，具体参数由用户按模型和硬件设置。
- 曾尝试在代理中实现本地 compaction 与消息改写，后来确认这会让 Launcher 越界承担模型/协议能力，现已全部删除。当前代理不生成摘要、不改写消息；上下文能力与推理由 llama.cpp 和所选模型负责。
- 完整官方模型预设现参与快照与恢复；Local 固定使用 `none` 推理强度，切回后恢复原模型、思考强度、summary、verbosity、service tier 和上下文设置。

## 2026-09-13：Codex 原生本地自动压缩路径

- 当前 Codex `0.153.4` 会按 Provider 能力选择压缩协议：OpenAI 身份使用 Responses remote compaction v2（普通 `/responses` 中包含 `compaction_trigger`），普通自定义 Provider 使用 Codex 本地 compaction（把摘要提示作为普通 `/responses` 推理，再由 Codex 重建历史）。当前 llama.cpp 不认识前一种 OpenAI 专用 item，但能够执行后一种普通文本推理。
- Local 配置因此改为 `chatgpt_local_launcher` 自定义 Provider：`requires_openai_auth = true` 保留 Desktop 已登录账户外壳和共享项目状态，`supports_websockets = false` 直接使用已验证的 HTTP Responses，回环代理在进入 llama.cpp 前继续剥离全部认证和账户元数据。Provider 名称保持 `Local llama.cpp`，使 Codex 不把它误判为 OpenAI 远端压缩服务。
- 每个模型现保存独立的压缩安全余量，Launcher 写入 `model_auto_compact_token_limit = Context - 压缩安全余量`。它只是 Codex 下一次推理前的原生压缩触发线，不是输出限制；Codex 仍决定安全节点、摘要推理和历史重建。代理只读取 `X-Codex-Turn-Metadata` 的白名单枚举字段和 JSON `type` 名称用于诊断，不记录文字、模型名、任务 ID 或完整元数据。
- `codex doctor` 已用隔离覆盖验证该 Provider 配置可以在 `0.153.4` 正常加载，并确认 Provider 使用 ChatGPT auth、Responses HTTP 且 WebSocket 关闭；因为当前真实 Desktop 不能关闭，账户外壳、Local 推理和压缩后的连续使用仍列入下一次真机验收。
- 账户视觉边界已明确：Local 保留官方账户外壳以共享原生项目/历史，因此左下角姓名仍可见；代理传输层仍剥离全部凭据和账户元数据。视觉身份完全隔离需要独立用户数据环境，会与原生历史共享冲突。

## 下一道验收门

1. 使用修正后的包复测真实 Desktop Local 文本请求；确认日志不再出现 `System message must be at the beginning.`，模型由 llama.cpp 正常生成回复。
2. 用隔离环境进一步验证 Codex 工具调用（shell / apply patch），而不只验证文本返回。
3. 真机启用 Windows 登录启动项，注销/登录后验证 Agent 与直接启动 ChatGPT Desktop 路径；随后再验收 Local A→B。

由于本项目正运行在 ChatGPT Desktop/Codex 会话中，真实客户端关闭后的切换验收必须由会话外测试程序完成，不能在当前进程存活时绕过硬规则。

## 工具调用探索结果

- `--smoke-codex-tools` 使用 JSONL 事件并要求至少一个 `command_execution` 的 `exit_code = 0`，模型仅在最终文字中写出标记不会被误判为成功。
- Qwen3.8-9B 能生成 shell 函数调用，工具结果也能经兼容代理进入后续轮次；当前 Codex Desktop 宿主会拒绝嵌套 Codex 的进程创建，因此本机尚未得到成功执行事件。
- 在连续收到策略拒绝后，该 9B 模型曾生成超长且缺少闭合引号的工具参数 JSON，llama.cpp 返回解析错误。工具门保持未通过；不能据此宣称本地模型已具备可靠开发能力。
