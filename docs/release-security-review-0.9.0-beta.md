# 0.9.0 公测版安全与稳定性复核

> 历史复核记录：当前复核见 [0.9.1 公测版安全与稳定性复核](release-security-review-0.9.1.md)。本文中的测试数量和实现边界只代表 0.9.0 封存时状态。

复核日期：2026-09-14

## 源码结论

- 版本、开发者、产品名称和关于页信息一致。
- 未发现生产源码中的真实密码、PFX、私钥、用户绝对路径或临时调试中断。
- 控制台输出仅用于 Agent 自检、诊断和隔离 smoke 命令，不是隐藏调试分支。
- Release 构建成功；所有编译项目使用可空引用、最新分析级别、确定性构建、依赖锁文件和 NuGet Audit。
- 当前仓库基线包含源码、测试和文档；忽略 `.tools`、`artifacts`、`bin/obj`、日志、压缩包和私钥材料。

## 配置与账户安全

- 不读取或复制 `auth.json` 内容，不整目录覆盖 `.codex`。
- Provider 切换只修改受管理字段，并在切换前保存 DPAPI CurrentUser 加密恢复点。
- 两阶段恢复日志、模式/配置跨进程锁、提交前 SHA-256 检查和原子替换共同处理并发与崩溃。
- 受管字段被外部修改时停止提交，不用旧快照覆盖未知新配置。
- 切回 OpenAI 恢复官方模型和相关偏好，不删除 Local Profile。

## 网络与隐私

- Desktop-facing 服务只监听 loopback，公开 Responses、模型只读信息和健康检查所需路由。
- 拒绝浏览器 Origin 和管理接口；凭据、Cookie、账户/组织/项目 Header 不进入 llama.cpp。
- 除必要的 gzip、Brotli、deflate、Zstandard 传输解压外，代理不改写请求或响应语义。
- 日志只保留有限诊断字段，不记录 Prompt、摘要正文、凭据、模型名称和任务 ID。

## 进程与文件安全

- App 与 Agent 单实例；Agent 复用前校验协议、PID、路径、启动时间和新鲜心跳。
- llama 进程由创建它的 Agent 通过 Windows Job Object 管理，不按进程名批量结束。
- 公开端口被未知程序占用时不结束对方；只有 ChatGPT 已关闭才允许事务迁移。
- BAT 只覆盖带 Launcher 归属标记的生成文件，不覆盖同名手写脚本。
- 缓存删除要求 llama `can_remove` 与 Launcher 下载来源双重确认，手工 GGUF 不进入删除流程。

## 自动化与人工验证

- 166/166 自动化测试通过，0 失败、0 跳过。
- Release 解决方案构建通过，0 个警告、0 个错误；PowerShell 发布脚本语法与 `dotnet format --verify-no-changes` 均通过。
- 已联网查询 NuGet 官方漏洞索引，直接与传递依赖均未发现已知漏洞。
- 用户真机确认核心 Local 推理、GPU 加载、上下文自动压缩、OpenAI 恢复和持续运行稳定。
- 模型下载为实验性；失败不会改写当前 Provider 或影响已有本地模型运行。

## 剩余边界

- AMD/Vulkan、未登录客户端和更多 llama.cpp 版本需要持续兼容性测试。
- 模型及其 Chat Template 决定工具调用和摘要质量，Launcher 不应越界补写模型能力。
- 自签名 Authenticode 提供作者连续性和文件完整性，不提供第三方身份背书。
