# 0002：Launcher 保持为 llama.cpp 的薄管理壳

状态：已采用（2026-09-11）

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
- 请求正文和 llama.cpp 响应保持语义透明。

Local 模式把回环地址声明为一个使用现有 OpenAI 登录状态、但身份不是 OpenAI 的自定义 Provider。这样 Desktop 继续使用同一账户外壳、项目和本地历史，安全代理仍会在进入 llama.cpp 前剥离认证；与此同时 Codex 会使用其原生本地 compaction：Codex 监控 token、通过普通 Responses 请求让当前本地模型生成摘要，并由 Codex 自己重建“摘要 + 保留历史”。Launcher 只把每模型的 `Context - 压缩安全余量` 写成 Codex 自动压缩线，并记录不含正文的压缩诊断；它不限制单次输出、不生成摘要、不解析摘要，也不创建 compaction item。

Local 客户端、后台 Agent 或当前 Runtime 的 llama-server 运行时禁止修改任何模型参数；Local 模式已经保存但服务完全停止时仍允许编辑。保存当前 Local 模型时，模型文件事务内嵌模式配置事务，把 Profile、BAT、Context、压缩线、Catalog 和 Router preset 一并提交或回滚；非当前模型先更新 Profile/BAT，并在下次切换时提交其运行配置，避免运行中热改产生配置与已加载服务不一致。

## 明确不做

- 不实现模型加载、推理、采样、KV Cache、批处理或模型调度；
- 不改写 system/developer 消息；
- 不过滤、模拟或重写工具定义；
- 不生成上下文摘要或 Codex compaction item；
- 不把不兼容的 llama.cpp 构建补成另一套模型服务。
- 不实现性能基准、显存预测器或自有自动调参算法；

如果 llama.cpp 的原生 API 与当前 ChatGPT Desktop 不兼容，Launcher 应明确报告受支持边界，并通过升级、选择兼容 Runtime 或 llama.cpp 原生模型模板参数解决。只有纯传输差异可以进入回环安全层。

## 配置文件与 BAT

结构化 Profile 是 Launcher 的配置源，生成的 BAT 是可读、可迁移、可独立运行的产物。Launcher 直接从 Profile 构造受控参数，不反向解析任意 BAT，从而避免脚本注入、端口识别和进程归属不确定性。
