# ADR 0001：模式与运行状态分离

状态：Accepted（2026-09-10；v0.9.9 复核于 2026-10-20）

## 决策

Launcher 将持久化模式与瞬时运行状态分开：

- `ProviderMode`：`OpenAI`、`Local`
- `RuntimePhase`：`Stopped`、`Starting`、`Running`、`Stopping`、`SwitchingToOpenAI`、`SwitchingToLocal`、`Recovering`、`Error`

关闭 ChatGPT Desktop 不改变 `ProviderMode`，也不要求停止 Local Router。模型达到 Profile 的空闲时间后，由 llama.cpp 进入休眠并卸载权重与 KV Cache；新请求由 llama.cpp 自动唤醒模型。

## 原因

用户希望第二天直接启动 ChatGPT Desktop 时继续使用上次模式。若把“关闭客户端”解释为“退出 Local”，就会产生不必要的配置恢复，也无法支持绕过 Launcher 直接启动。

## 后果

- Provider 配置在显式切换或执行需要应用最新配置的新启动事务时修改；参数保存、载入和预选只修改其各自文件/指针，不提前改写 Provider。
- Background Agent 必须独立于 WPF UI 存活。
- Agent 在持久化的随机公开回环端口维护透明安全代理，在动态内部端口维护 llama.cpp Router；直接启动 ChatGPT Desktop 时仍沿用该入口。内部端口发生绑定竞态时只对明确的地址占用错误重试；公开端口被占用时不结束未知进程，只在 Desktop 已关闭时事务性迁移 Provider 与 Launcher 设置。安全代理只隔离凭据和管理路由，不改写模型请求或响应语义。
- Agent 只追踪自己启动的 Router 进程树；模型实例生命周期由 Router 原生管理。
- 恢复记录必须包含切换前模式、目标模式和事务阶段。
- WPF 窗口的可见性、托盘交互和程序退出属于 UI 生命周期状态，不改变 `ProviderMode`。这些操作通过串行门收敛，退出开始后不得被托盘单击重新显示。
- 状态观测不是持久化状态转换：主窗口前台时低频采样，后台暂停，托盘和模型管理按需采样；任何观测失败都不得自行切换 Provider。

## 0.9.8：三种选择与运行快照

全局模式/模型预选、每模型启动配置预选、参数页当前载入文件分别维护。卡片配置菜单只改每模型启动配置；参数页应用先保存再改该指针。新启动读取同一 JSON 快照生成运行文件；已运行会话、休眠唤醒与后台恢复使用已应用快照。删除或改名 JSON 不改变当前会话，但缺失预选不能新启动，也不能静默回退旧 BAT。恢复 OpenAI 独立于用户 JSON。

## 0.9.9：接受率是运行观测

RuntimeState 的可选 `LatestDraftAcceptance` 只携带当前运行模型的原生单请求统计，未改变 Agent 协议版本 10。观测不修改参数 JSON、Provider 或模型预选。只对启用 MTP 的实际运行快照发布，并核对拥有的 Router 进程；UI 绑定可信进程、启动时间和模型。新结果在既有协调周期发布，旧结果保持到下一次统计；停止、在线或新运行时不继承旧样本。简易日志过滤不影响该数据。
