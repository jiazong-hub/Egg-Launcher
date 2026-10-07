# 0002：Launcher 保持为 llama.cpp 的薄管理壳

状态：已采用（2026-09-11；v0.9.1 复核于 2026-09-21）

## 决策

llama.cpp 是 Local 模式唯一的模型运行时。Launcher 只管理 GGUF Profile、启动参数、BAT、进程生命周期、ChatGPT Desktop 模式切换、单模型 Catalog 和官方配置恢复。

模型按需加载、空闲卸载与新请求自动唤醒均使用 llama.cpp Router 原生能力。Profile 直接生成 `sleep-idle-seconds` 参数，Agent 不根据 ChatGPT 进程状态调用模型卸载接口。

参数默认值也属于单个模型 Profile：Launcher 只保存用户为该模型建立的参数快照，不把模型 A 的默认值传播给模型 B，也不提供无法根据硬件与模型证明正确的内置推荐。可选自动适配只运行当前 Runtime 自带的 `llama-fit-params`，Launcher 对其 `-c/-ngl/-ts/-ot` 输出做白名单解析，展示后由用户决定是否应用以及是否设为该模型专用默认。

公开 GGUF 搜索只读取 Hugging Face 元数据。模型下载通过 llama.cpp Router 原生 `POST /models` 发起，并通过 `/models/sse` 读取分片字节进度；Launcher 只负责把 `done/total` 可视化为容量、百分比和速度。缓存根目录由 `LLAMA_CACHE` 指向 Runtime 的 `models` 目录。Launcher 不实现网络文件传输、断点续传、校验、重试或格式转换，也不移动 llama.cpp 的原生缓存文件。

下载状态流和控制请求均采用有限等待。Launcher 可以展示 llama 已接受请求、等待首字节、传输与缓存路径核对等阶段，并把本次临时 Router 日志中已知的连接、DNS、TLS 或缺少 HTTPS 支持映射成不包含原始日志内容的诊断。SSE 的 `download_finished` 不是单独的成功依据；只有 `/models` 返回 Runtime `models` 目录内实际存在的路径才提交 Profile。

模型状态、立即加载/释放和缓存删除分别使用 Router 原生 `/models`、`/models/load`、`/models/unload` 与 `DELETE /models`。缓存删除采取双重来源门：llama 必须返回 `can_remove`，Launcher Profile 也必须保存同一下载 ID；V1 迁移 Profile 一律视为用户本地文件。Local 运行期间不执行删除。

运行数据来自 llama 原生 `/metrics`、`/slots` 和 Windows 系统计数器。内部 Router 地址只有在协议版本、Agent/Router PID、实际可执行文件路径、更新时间和 HTTP 回环地址全部校验后才使用。监控不访问 Desktop-facing 管理路由，不记录提示词，也不为了读取指标唤醒休眠模型。Windows 或驱动没有公开的数据标记为不可用。

模型 Chat Template 属于 llama.cpp 启动配置。若 GGUF 内置模板包含已确认会拒绝 Codex 后续系统消息的严格 Qwen 守卫，Launcher 可以从该 GGUF 自身的模板元数据生成模型专用副本，只放宽该守卫，并通过 `--chat-template-file` 交给 llama.cpp 渲染。该过程不解析、不改写网络请求，也不实现模型推理或工具协议。

Launcher.App 只是可退出的配置界面。Local 模式真正需要驻留的是 Launcher.Agent、回环安全代理与 llama.cpp Router；用户可以关闭界面后保留它们，也可以通过正常关闭通道完整停止。OpenAI 模式不需要这些本地服务，关闭界面时应结束无用 Agent。停止后台服务不等于切换模式。

Desktop-facing 回环层只做以下工作：

- 只监听 HTTP 回环地址；
- 只公开 Responses、模型清单和健康检查所需路由；
- 拒绝浏览器 Origin 和管理路由；
- 使用请求头白名单剥离 Authorization、Cookie 和账户元数据；
- 在 llama.cpp 无法读取 Desktop 压缩传输时解码 gzip、Brotli、deflate 或 Zstandard；
- 对话、工具和 llama.cpp 响应保持语义透明。仅在用户显式设置且原生验证通过的思考开关下设置 enable_thinking；开启时清除冲突的 none 努力值，正向档位保持原值。用户开启“显示思考过程”时，保留原始 reasoning 内容并增加 Desktop 读取的 summary 展示副本及 SSE 事件；已有原生摘要不覆盖。后续请求只清理与原始思考完全相同的展示副本，避免重复输入。该兼容处理不生成新的思考或上下文摘要。

Local 模式把回环地址声明为一个使用现有 OpenAI 登录状态、但身份不是 OpenAI 的自定义 Provider。这样 Desktop 继续使用同一账户外壳、项目和本地历史，安全代理仍会在进入 llama.cpp 前剥离认证；与此同时 Codex 会使用其原生本地 compaction：Codex 监控 token、通过普通 Responses 请求让当前本地模型生成摘要，并由 Codex 自己重建“摘要 + 保留历史”。Launcher 只把每模型的 `Context - 压缩安全余量` 写成 Codex 自动压缩线，并记录不含正文的压缩诊断；它不限制单次输出、不生成摘要、不解析摘要，也不创建 compaction item。

Local 模式下，当前配置模型在客户端、后台 Agent 或当前 Runtime 的 llama-server 任一运行时禁止编辑参数；服务完全停止后允许编辑。其他可用模型仍可编辑，其保存不会覆盖当前 Local 配置。自动适配另有运行条件：必须处于 OpenAI 模式，且当前 Runtime 没有运行中的 llama-server。

参数保存与模式配置使用两个独立入口。`SaveProfileArtifactsAsync` 调用 `ModelArtifactWriter`，由模型文件事务提交或回滚 Profile、备份与 BAT；保存当前配置的 Local 模型时，也将 Catalog 和 Router preset 纳入同一文件事务。保存当前 Local 模型时，在模型文件事务内调用 `ModeSwitchCoordinator` 的 Local 更新路径，立即提交 Codex 受管理配置和恢复记录；模式更新失败时由配置事务回滚，再由外层模型事务恢复文件。保存不启动或重载服务。非当前模型只更新自身文件。`ActivateLocalAsync` 在冷启动或切换模型时重新读取已保存 Profile，再调用 `ModeSwitchCoordinator`，通过已有模式配置事务应用 Codex 的 Context、压缩线、工具输出预算、SSE 等待时间和沙箱设置，并更新 Catalog 与 Router preset。客户端已运行且仍使用同一 Local 模型时，满足复用条件的启动操作可复用现有服务，不重新应用参数。

## 明确不做

- 不实现模型加载、推理、采样、KV Cache、批处理或模型调度；
- 不改写 system/developer 消息；
- 不过滤、模拟或重写工具定义；
- 不生成上下文摘要或 Codex compaction item；
- 不把不兼容的 llama.cpp 构建补成另一套模型服务。
- 不实现性能基准、显存预测器或自有自动调参算法；

如果 llama.cpp 的原生 API 与当前 ChatGPT Desktop 不兼容，Launcher 应明确报告受支持边界，并通过升级、选择兼容 Runtime 或 llama.cpp 原生模型模板参数解决。传输差异及明确限定的思考显示兼容处理可以进入回环安全层；不借此实现推理或改写工具协议。

## 配置文件与 BAT

结构化 Profile 是 Launcher 的配置源，生成的 BAT 是可读、可迁移、可独立运行的产物。Launcher 直接从 Profile 构造受控参数，不反向解析任意 BAT，从而避免脚本注入、端口识别和进程归属不确定性。

## v0.9.1 模型能力边界

- Dense/MoE 类型是 Profile 的显式事实。首次添加可以读取 GGUF 元数据辅助识别，但未知结果不猜测；用户更改类型时重建参数配置，不把跨架构参数直接迁移。
- MTP、视觉和思考能力由实际元数据或原生隔离验证证明。MTP、视觉检测与关联位于模型管理；思考检测位于参数页“思考设置”，使用实际模板和 Responses 链路，分别确认开关、独立档位、默认值和别名。未知能力如实保留，不默认填中档，不映射。
- 思考 Responses 验证比较隔离原生进程记录的完整实际提示词，不以输入 token 数量充当传递证据。与可信 Desktop 安装匹配的缓存 CLI 协议 schema 单独确认原始档位兼容性，不依赖独立 CLI 安装。共享指纹检查覆盖编辑、保存、模式切换和 Agent 重启；依据不一致时由现有事务同步清理配置，客户端运行时阻止沿用失效设置。验证时间不构成失效依据。
- 外置 MTP 与 mmproj 是主模型的可撤销关联，不是独立聊天模型；下载辅助文件不会自动把它绑定到任何 Profile。
- 未设置的参数不写入 preset 或命令行，让当前 llama.cpp 保持自己的默认行为。Launcher 不以 UI 默认值伪装 Runtime 推荐。
- 从模型列表移除只删除 Profile 和 Launcher 拥有的生成物。删除用户模型数据属于用户或 llama 原生受保护缓存管理流程，不由普通列表操作承担。

- 档位反转只在 Catalog 构建时调整完整条目的排列，不修改原始验证列表、默认档位或请求值。UI 与档位调节开关联动，禁用时保留偏好；显示偏好不纳入能力验证指纹。


## v0.9.7 同版本更新：思考设置（2026-10-07）

依据当前源码，思考设置依次为“检测思考能力”“调节思考强度”“启用思考”，在原有内容容器内使用两条分割线分隔。检测区域包含实际模板状态、检测入口、已确认档位和详情，不再单独显示默认档位。

“默认思考强度”位于调节区域，只列出当前模型已验证的原始档位及“沿用模型默认”。按模型保存 PreferredReasoningLevel，不覆盖检测出的 DefaultReasoningLevel；配置和模型目录共用同一选择逻辑。仅在思考开启、档位验证及客户端兼容性通过、允许调节开启时传递。用户选择无效时回退有效的模型默认，不补齐或映射档位。反转只改变排列，不改变默认值或请求值。新对话使用该默认，已有对话可能保留自身选择。关闭调节、不可用或检测中，下拉框置灰并保留偏好。

“显示思考过程”默认关闭，位于“启用思考”下方；思考关闭、能力未确认或检测中时置灰并保留偏好。代理保留原始思考并通过 Desktop 的 summary 通道提供全文展示，不生成另一份摘要；原生摘要保留。后续输入仅清理完全相同的展示副本。show_raw_agent_reasoning 纳入配置恢复事务，切回在线恢复原值或原本缺省状态。显示及默认强度偏好不使已有能力验证失效。

实现与验证详见 [思考显示审查](../thinking-display-review-20261007.md)和[默认强度审查](../reasoning-default-review-20261007.md)。本次保持版本 0.9.7，不修改更新日志。

## v0.9.7 同版本修复：思考验证关联（2026-10-07）

思考能力没有按时间失效机制。修改 fit 显存余量、线程数、设备分配、加载和计算优化等已明确独立的资源参数，不再使思考验证失效；这些参数的原有范围校验及运行中编辑限制仍保留。RoPE、YaRN、SWA、思考覆盖和未知额外参数仍参与检查，模型、实际模板/Jinja、llama.cpp 可执行文件与 DLL、Desktop 配套 CLI 的验证依据检查也保留。

指纹规则升级为 5，Profile schema 保持 13。准确匹配的 v4 结果在编辑或自动适配修改参数前迁移，保留能力结果和检测时间，取消编辑不落盘。已经失效或已被清除的结果不能自动恢复。新增可选组件摘要用于说明具体变化；旧记录没有变化项时如实提示，文件读取失败单独报告。

“显示思考过程”的偏好本身不会阻止其他参数保存。真实验证依据变化时清理能力并同步禁用实际显示，保留显示、默认强度及反转等用户偏好；显式思考开关、档位调节及有效开启的显示仍按当前验证结果检查。当前本地模型的保存继续使用既有配置事务，其他模型不覆盖当前配置，切回在线仍恢复原配置。

完整排除项、迁移规则及运行参数审查见[思考验证关联审查](../reasoning-validation-scope-review-20261007.md)。
