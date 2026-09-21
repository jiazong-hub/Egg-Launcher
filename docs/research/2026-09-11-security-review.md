# 代码安全筛查与修复记录

> 历史记录：本文所述压缩适配覆盖的是已废弃实现。该实现已于同日从源码和测试中删除；当前安全边界见 [架构决策 0002](../architecture/0002-thin-launcher-boundary.md)。
>
> 本文中的测试数量、签名状态和“尚未关闭”项目只代表 2026-09-11 当时状态。0.9.0 复核保留于[历史文档](../release-security-review-0.9.0-beta.md)，当前结论见 [0.9.1 安全与稳定性复核](../release-security-review-0.9.1.md)。

日期：2026-09-11

## 已修复

- **官方凭据与账户元数据隔离**：Desktop-facing 回环代理改用请求头白名单，仅允许必要的内容协商头；Authorization、Cookie、API Key、账户/组织/项目标识及未来未知头均不转发。浏览器 `Origin` 请求被拒绝。
- **最小网络表面**：公共代理只接受 `POST /v1/responses`、`POST /v1/responses/compact`、只读的 `GET /models`/`GET /v1/models`、`GET /health` 及 Responses WebSocket 的 426 回退；模型加载、卸载和其他 llama.cpp 管理接口不公开。
- **配置数据保护**：官方 `config.toml` 原始字节使用 Windows DPAPI CurrentUser 加密，密文格式包含长度校验并用 SHA-256 验证往返一致性；备份和恢复临时文件使用受保护 ACL。
- **旧备份迁移安全**：发现旧 `.bak` 时只提示，不静默删除；用户确认后先生成并解密校验 `.dpapi`，活动事务先原子更新引用，最后删除对应明文。
- **崩溃一致性与并发**：Provider 配置和 Launcher 设置使用带阶段记录的恢复事务；App/Agent 启动时自动完成可证明的提交或回滚。模式级与配置级跨进程文件锁避免多个实例并发切换；`config.toml` 原子替换前再次核对读取版本的 SHA-256，外部改写会停止提交而不会被覆盖。
- **进程与显存回收**：Agent 仅管理自己启动的精确 PID，并把 llama-server 放入启用 `KILL_ON_JOB_CLOSE` 的 Windows Job Object。Router 意外退出时先停止陈旧代理再重建。
- **版本隔离**：App 在复用后台 Agent 前校验协议版本、PID 与程序路径；升级后残留的旧 Agent 会被明确拒绝，不会与新版配置事务混用。
- **日志与诊断**：代理、Agent 和 Router 日志均设置大小或数量上限；诊断不写请求正文、凭据和完整异常数据。异常上游 JSON 不再把原状态误报为代理 502。
- **登录启动防劫持**：注册用户级 Run 项之前检查 Agent 文件和所在目录 ACL；普通用户组或 Codex 沙箱可修改时拒绝注册。
- **依赖供应链**：启用 NuGet Audit 和锁文件；`dotnet restore --locked-mode` 成功，直接及传递依赖的联网漏洞检查无发现。
- **权限归属清晰且可恢复**：权限模式由 ChatGPT Desktop 原生控件管理，Launcher 不为 Local 强制选择 reviewer，也不把 Local 期间的权限变化误判为 Provider 冲突；切回 OpenAI 时恢复进入 Local 前的原权限字段，不更改 sandbox 配置。

## 验证结果

- 86/86 自动化测试通过；行覆盖率 73.58%，分支覆盖率 61.43%。新增完整官方模型预设往返、低于 16K Context 时零写入拒绝，以及新旧 Codex 本地压缩协议、凭据剥离和截断摘要拒绝测试。
- Debug 与 Release 全解决方案构建通过，0 警告、0 错误；代码格式检查通过。
- 隔离真实链路通过：Responses 返回 `LOCAL_SMOKE_OK`；Codex 经 WebSocket 426 回退 HTTP 后返回 `CODEX_LOCAL_OK`；模型卸载成功；结束后无 llama-server 残留。
- NuGet 锁定恢复与已知漏洞检查通过。
- 隔离测试没有修改真实 Codex 配置，没有读取真实 `auth.json`，并保持正在使用的 ChatGPT Desktop 运行。

## 尚未关闭的发布边界

- 真实 ChatGPT Desktop 已确认 Local 请求可到达 llama.cpp 并加载 GPU；16K 自动压缩修正、完整官方模型预设恢复、项目/成功历史可见性和直接启动路径仍需要用户在可完全关闭客户端时按验收清单复测。
- 严格 Codex 工具调用门（实际成功执行 shell/apply patch）仍未通过，当前只能确认文本开发链路；9B 模型的工具调用可靠性不能承诺。
- 预览二进制目前没有 Authenticode 签名。正式对外发布前应使用可信代码签名证书签署 Launcher.App、Launcher.Agent 及安装包；SHA-256 清单只能证明文件一致性，不能替代发布者身份认证。
