# ChatGPT Desktop Local 真机测试与修正记录

> 历史记录：本文保留首次测试事实。后续加入的请求改写和本地 compaction 方案已于 2026-09-11 废弃并从程序删除；当前实现只保留透明安全转发，见 [架构决策 0002](../architecture/0002-thin-launcher-boundary.md)。

日期：2026-09-10—2026-09-11

## 结果概览

- Launcher 能扫描并添加 GGUF、保存 Profile 参数，并启动 ChatGPT Desktop。
- OpenAI → Local 配置事务成功，Local → OpenAI 恢复成功；官方模型恢复为 `gpt-5.6-sol`，账户与项目可正常使用。
- Local 模式中创建的本地项目 `D:\text` 已持久化。失败会话的 `cwd` 也是 `D:\text`，说明项目实际选择成功；Codex 输入框左上角未显示项目属于界面刷新问题。
- 前两轮 Local Codex 请求都没有到达 llama.cpp。任务在重试后以 `502 Bad Gateway` 结束；第二轮代理日志只有健康检查和模型清单，没有 `POST /v1/responses`。
- Router 正常启动并列出唯一模型，但测试期间没有模型加载或推理日志，因此 GPU 未加载是 502 的结果，不是 GGUF 或 Offload 参数问题。
- 用户消息与错误保存在本地 rollout/session index 中，但没有 assistant 消息，也未正常显示为可恢复任务。成功请求的跨模式历史可见性仍需复测。
- 第二轮还确认了三个独立问题：Launcher 在启动客户端前刷新首页导致“ChatGPT Desktop 已关闭”的陈旧状态；独立 Local Provider 使客户端失去原生账户外壳，出现英文界面；失败任务把 `approvals_reviewer = user` 固化到任务元数据，切回官方模式后打开该任务仍显示“请求批准”。

## 第一轮传输修正

- Responses 代理支持 `gzip`、`br`、`deflate`、`zstd` 请求解码，并以未压缩、正文不变的方式转发。
- POST/PUT/PATCH 即使没有 `Content-Length` 或 `Transfer-Encoding` 也会读取请求正文，覆盖 HTTP/2 流式正文场景。
- 转发到 llama.cpp 前剥离 `Authorization`、Cookie、API Key 与 OpenAI 组织/项目头。
- 新增 `%LOCALAPPDATA%\ChatGPTLocalLauncher\logs\proxy.jsonl`，只记录请求元数据、阶段、状态与安全化异常，不记录正文和认证信息。
- 新增 `agent.log`，记录 Agent 启停、Router 协调结果与错误。
- 502 响应带诊断 ID；Launcher 首页可以直接打开日志目录。
- 本地模型 Catalog 的用户可见描述、思考级别说明和基础指令改为中文。

## 第二轮根因与架构修正

- 根因不是 llama.cpp 模型加载失败，而是 ChatGPT Desktop 没有把 Codex 推理请求交给独立自定义 Provider；仅继续修改 HTTP 正文代理无法解决该问题。
- Local 主路由改为当前 ChatGPT/Codex 支持的内置 OpenAI 路由：设置顶层 `model`、本地-only `model_catalog_json` 和回环 `openai_base_url`，并保持 `model_provider` 未设置。这样既保留客户端原生账户外壳，又把 Codex Responses 请求导向本地代理。
- 本地请求进入代理后会剥离 Authorization、Cookie、API Key 和 OpenAI 组织/项目头；官方凭据不会继续转发给 llama.cpp。
- 权限模式改由 ChatGPT Desktop 输入框下方的原生控件管理；Launcher 不再为 Local 写入 `approval_policy` 或 `approvals_reviewer`。进入 Local 前仍快照原字段，切回 OpenAI 时恢复；Local 期间的权限改变不参与 Provider 所有权冲突判断。
- 切回 OpenAI 后保留一个不激活、不进入 Catalog 的旧 Provider 定义，仅用于让第一次实现期间创建的失败任务可以解析旧 Provider ID；它不会把本地模型重新暴露到官方模型列表。
- Launcher 启动 ChatGPT 后等待进程出现并重新刷新状态，首页不再长期显示“已关闭”。
- 代理对 Responses WebSocket 升级明确返回 HTTP 426，通知 Codex 对整个会话立即回退到 HTTP；这与当前 Ollama 的 ChatGPT 集成语义一致。

## 自动化复核

- gzip 与 zstd Desktop 风格请求可经真实回环代理完成解码和透明转发。
- 上游收到的请求不含 Authorization 与 Content-Encoding。
- 诊断日志不包含测试提示词和 Bearer token。
- 新路由使用临时 Codex Home 和顶层 `openai_base_url`，不依赖独立 Provider；隔离 Codex → 兼容代理 → llama.cpp 返回 `CODEX_LOCAL_OK`。
- 增加 426 后，Codex 只进行一次 WebSocket 尝试，随后立即走 HTTP 并在 1.84 秒完成；没有此前的五次 404 重试。
- Responses 直接推理仍得到 HTTP 200 / `completed`，随后模型成功卸载；测试结束后没有 `llama-server` 进程或测试端口残留。
- 隔离测试只在临时 Codex Home 写入本地哨兵认证，不读取、不替换真实 `auth.json`，也没有切换正在使用的官方 Desktop 配置。
- 2026-09-11 安全修复后再次验证：本地 Responses 精确返回 `LOCAL_SMOKE_OK`，隔离 Codex 精确返回 `CODEX_LOCAL_OK`；模型通过内部 Router 控制接口成功卸载，公共代理拒绝该管理接口，最终无 `llama-server` 残留。

## 第三次真机测试与即时修正

- 用户关闭官方客户端后点击“切换并启动”，按钮在等待期间禁用，随后恢复，但 Desktop 没有启动。
- Agent 日志证明 14:20:02 Local Router 已成功就绪；代理日志随后每 250ms 收到启动器的 `GET /models` 就绪探测并全部返回 404。原因是安全白名单只允许 `/v1/models`，而 UI 的 Router 客户端使用 `/models`。
- 公共代理现同时允许只读 `/models` 与 `/v1/models`，仍拒绝 `/models/unload` 等管理接口。新增真实无权重 Router 冒烟已确认 public health/models 返回预期模型，启动等待不再超时。
- 首页增加一秒周期的进程状态刷新；“检测到运行进程”表示仍有 ChatGPT 进程，只有全部退出后才显示“已关闭”并允许安全切换。
- 本地模型页移除权限选择框，改为提示使用 Desktop 原生权限控件；扫描结果和已管理模型列表在自身无法继续滚动时会把滚轮传递给外层页面。
- 新版 App 会通过 `runtime-state.json` 核对 Agent 协议版本、PID 与可执行文件路径；发现上一版 Agent 仍在后台时立即提示结束旧进程，不再把旧 Agent 当作当前版本继续等待。

## 第四次真机测试与上下文修正

- 修正后的内置 Provider 路由生效：Desktop 可以进入 Local，发送“你好”后 GPU 实际加载模型，显存占用约 5.8/8 GB。这确认 Codex → 回环代理 → llama.cpp 的真实客户端链路已经打通，先前的 502 主路由问题已解决。
- 旧 Profile 仍使用 8,192 tokens。代理日志显示首个极短请求的原始 Desktop 正文约 399 KB，规范化后仍约 36 KB；llama.cpp 首轮 prompt 已为 7,778 tokens。后续简单问题分别达到约 8,586、8,523、8,535 tokens，超过 8,192 后返回 `exceed_context_size_error`。客户端显示的“1K”不是完整线上 prompt，还包含用户不可见的系统指令、项目上下文和工具 schema，不能用它判断 Router 是否还有容量。
- 新建 Profile 默认改为 16,384 tokens，并将 K/V Cache 默认改为 Q4_0，以适配本机 8 GB 显存。旧版低于 16K 的 Profile 在 Desktop 已关闭时会获得一次明确的一键优化确认；拒绝后不会修改 Profile、生成物或 ChatGPT 配置。
- Local model Catalog 与 ChatGPT 顶层配置均写入 16K 上下文和 12,288 tokens（75%）自动压缩阈值，给压缩和输出保留余量。协调器也设置 16K 硬门，避免 UI 之外的调用重新写入不可靠的 8K 配置。
- 因为内置 OpenAI Provider 会让 Codex 选择远程压缩协议，而 llama.cpp 不会原生返回 Codex 的 `compaction` output item，代理现会识别新版 `compaction_trigger` 和旧版 `/responses/compact`：禁用工具后让同一本地模型生成简洁检查点，再封装为 Codex 可接受的本地 opaque item；后续请求会在回环代理内解码为上下文。Authorization 和账户元数据仍不会转发，摘要也不会离开本机。
- 进入 Local 前现在快照完整官方模型预设：模型、思考强度、reasoning summary、verbosity、service tier、上下文和自动压缩设置。Local 期间只暴露 `none` 思考级别；切回 OpenAI 后逐字段恢复原预设，避免 `gpt-5.6-sol / xhigh` 被客户端显示为“自定义”。
- Local 为共享原生项目和历史而保留官方 Desktop 账户外壳，所以左下角仍会显示官方账户名称；该名称不表示请求被发送到 OpenAI。代理继续使用请求头白名单，Authorization、Cookie、API Key、账户、组织和项目标识均不会到达 llama.cpp。若把视觉身份也完全隐藏，就需要独立 Desktop 用户数据环境，与“原生项目/历史直接共享”目标冲突，因此本阶段不伪装或清空账户 UI。
- “保护旧版配置备份”是 Launcher 对早期明文 `config.toml.*.bak` 的一次性迁移提示。选择“是”会先用 Windows DPAPI CurrentUser 加密、回读校验并更新恢复引用，最后删除对应明文；不修改当前 `config.toml`，也不读取或修改 `auth.json`。默认按钮已设为“是”，提示文字也明确了该边界。

## 第五次真机测试与 zstd 传输修正

- Desktop 已把推理请求送到回环代理，但本次请求使用 `Content-Encoding: zstd`；旧代理不支持该编码，在 `decode_transport_body` 阶段直接返回 502，因此 llama.cpp 没有收到请求、GPU 也不会加载。客户端随后按默认策略重连五次。
- 回环代理现补充 zstd 解码，仍只做传输兼容：JSON 正文不改写，账户凭据继续剥离，模型加载和推理仍全部由 llama.cpp 负责。
- 集成测试覆盖真实 zstd 压缩正文、凭据剥离和正文逐字节不变；另验证解压后超过 16 MiB 的请求会在到达 llama.cpp 前被拒绝，避免异常压缩数据造成无界内存占用。

## 下一次真机测试重点

1. 使用新包选择旧 8K Profile 时接受“16K / Q4_0”优化，进入 Local 后确认 Launcher 首页显示 Desktop 已运行。
2. 确认 Codex 模型列表只显示当前本地模型；左下角官方账户名称可以保留，但不能出现在线模型，项目列表和历史应仍可用。
3. 在输入框下方直接选择需要的权限模式，并新建一个测试任务确认生效；旧失败任务单独检查，不把任务级旧值误判为全局配置被破坏。
4. 在 Codex 本地项目内连续发送至少三条无副作用短消息，确认 GPU 加载、均能回复，并且不再因 8K 上下文立即溢出；更长测试若触发自动压缩，应继续可用且日志出现 `local_compaction_adapted`。
5. 明确区分离线能力：询问实时金价时，本地模型没有联网搜索能力，应说明无法获知实时价格；不能把这个能力限制误判为 Router 故障。
6. 查看 `proxy.jsonl`：首次连接可出现一次 `websocket_http_fallback`（426），随后应出现 `POST /v1/responses` 的 `upstream_response` 200；若出现 `proxy_error`，记录诊断 ID、stage 和 exceptionType。
7. 成功回复后退出客户端、切回 OpenAI，确认原模型与思考强度不再显示“自定义”，再进入同一项目检查成功任务历史、权限默认值、在线模型与中文界面。

相关官方边界：

- Responses 请求支持 instructions、输入项、流式响应和多类工具：<https://developers.openai.com/api/reference/cli/resources/responses/methods/create>
- Chat/Work 项目聊天与 Codex 本地目录任务使用不同的工作流和索引：<https://learn.chatgpt.com/zh-Hans/docs/projects>
