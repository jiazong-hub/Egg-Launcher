# ADR 0001：模式与运行状态分离

状态：Accepted（2026-09-10；v0.9.1 复核于 2026-09-21）

## 决策

Launcher 将持久化模式与瞬时运行状态分开：

- `ProviderMode`：`OpenAI`、`Local`
- `RuntimePhase`：`Stopped`、`Starting`、`Running`、`Stopping`、`SwitchingToOpenAI`、`SwitchingToLocal`、`Recovering`、`Error`

关闭 ChatGPT Desktop 不改变 `ProviderMode`，也不要求停止 Local Router。模型达到 Profile 的空闲时间后，由 llama.cpp 进入休眠并卸载权重与 KV Cache；新请求由 llama.cpp 自动唤醒模型。

## 原因

用户希望第二天直接启动 ChatGPT Desktop 时继续使用上次模式。若把“关闭客户端”解释为“退出 Local”，就会产生不必要的配置恢复，也无法支持绕过 Launcher 直接启动。

## 后果

- Provider 配置只在显式切换时修改。
- Background Agent 必须独立于 WPF UI 存活。
- Agent 在持久化的随机公开回环端口维护透明安全代理，在动态内部端口维护 llama.cpp Router；直接启动 ChatGPT Desktop 时仍沿用该入口。内部端口发生绑定竞态时只对明确的地址占用错误重试；公开端口被占用时不结束未知进程，只在 Desktop 已关闭时事务性迁移 Provider 与 Launcher 设置。安全代理只隔离凭据和管理路由，不改写模型请求或响应语义。
- Agent 只追踪自己启动的 Router 进程树；模型实例生命周期由 Router 原生管理。
- 恢复记录必须包含切换前模式、目标模式和事务阶段。
- WPF 窗口的可见性、托盘交互和程序退出属于 UI 生命周期状态，不改变 `ProviderMode`。这些操作通过串行门收敛，退出开始后不得被托盘单击重新显示。
- 状态观测不是持久化状态转换：主窗口前台时低频采样，后台暂停，托盘和模型管理按需采样；任何观测失败都不得自行切换 Provider。
