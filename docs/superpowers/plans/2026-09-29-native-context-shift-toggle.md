# Native Context Shift Toggle Implementation Plan

> 历史实施方案，已由 0.9.4 后续设计与实现替代；当前行为以用户指南和发布说明为准。关闭滚动显式输出 --no-context-shift，开启输出 --context-shift，能力检测与失效范围也已更新。下文保留原方案，不作为当前运行规则。

> **For agentic workers:** This plan covers only the native llama.cpp toggle. Keep the existing uncommitted diagnostic work intact. Implement inline after user approval; do not synchronize Git until the user requests it.

> 本文是待确认的实施方案。当前只做方案设计，不改动功能代码、不生成安装包、不执行验证，也不进行 Git 同步。

**目标：** 为每个本地模型增加默认关闭的“上下文滚动”开关，仅把选择传递给 llama.cpp 原生 `--context-shift`。

**架构：** 开关是模型 Profile 的显式布尔参数。Router preset 在该模型的小节写入 `context-shift = true`，独立 BAT 写入 `--context-shift`；关闭时两者均不写该参数，沿用目前 llama.cpp 默认关闭的行为。Egg 不裁剪请求、拦截流、设置输出上限或修改 Codex 历史。

**技术栈：** C#/.NET、WPF、模型 Profile JSON、llama.cpp Router preset/BAT。

---

## 已确认的运行边界

- llama.cpp 当前文档将 `--context-shift` 描述为生成时上下文滚动，默认关闭；Router preset 的键名与命令行参数相同，省略前导短横线。
- 滚动只处理正在进行的生成；下一次 Codex 请求若携带过长历史，仍可能失败。功能名称和提示文案不得承诺“自动压缩历史”或“保证不断联”。
- llama.cpp 当前实现会在多模态投影器和共享 prompt 等情形拒绝滚动。首版至少禁止与 Profile 的视觉模式同时启用；其他由运行时报告的限制原样呈现。
- Egg 安装包不含 llama.cpp Runtime。用户可切换 Runtime，因此编辑时和启动时均须校验当前可执行文件的能力。

依据：[llama.cpp server 文档](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)、[llama.cpp server 实现](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/server-context.cpp)。

## Task 1：模型配置和旧版迁移

**涉及文件：**

- `src/Launcher.Models/Profiles/ModelProfile.cs`
- `src/Launcher.Models/Profiles/ModelParameterDefaults.cs`
- `src/Launcher.Models/Profiles/JsonModelProfileStore.cs`
- `src/Launcher.Models/Profiles/ModelProfileValidator.cs`
- 必要时 `src/Launcher.Models/Profiles/ModelProfileFactory.cs`

- [ ] 在 `ModelProfile` 增加 `bool ContextShiftEnabled`，默认 `false`，并将 Profile schema 从 7 升至 8。旧 Profile 反序列化后保持关闭。
- [ ] 在 `ModelParameterDefaults` 的快照和恢复路径加入同名字段，保证“保存为此模型默认”和“恢复此模型默认”不会丢失开关，也不会传播到其他模型。
- [ ] 兼容旧版高级参数中手动写入的 `context-shift` / `no-context-shift`：迁移时把明确的布尔值转为新字段，并从 `ExtraArguments` 与模型默认快照中移除旧键。两键冲突或值无法解释时保留原文件并给出明确诊断，不静默猜测。
- [ ] 将这两个键列入受管理参数，避免 Profile 字段与高级参数同时输出相反的值。
- [ ] 配置校验禁止 `ContextShiftEnabled && VisionEnabled`。不因为关闭滚动而改变现有 Context、压缩安全余量或 KV Cache 参数。

## Task 2：模型参数界面与能力检查

**涉及文件：**

- `src/Launcher.App/ProfileEditorWindow.xaml`
- `src/Launcher.App/ProfileEditorWindow.xaml.cs`
- `src/Launcher.App/Localization/Strings.zh-CN.xaml`
- `src/Launcher.App/Localization/Strings.en-US.xaml`
- 复用 `src/Launcher.Runtime/Detection/LlamaRuntimeOptionDetector.cs`

- [ ] 在“高级参数”页的 Context Checkpoints 附近增加独立的“上下文滚动（--context-shift）”开关，复用现有 `StartupToggleStyle`，默认关闭。单独成行，不与 Context Checkpoints 组成同一设置；提示文字明确写出“单次生成达到容量后丢弃较早上下文继续；可能遗忘前文；不修改 Codex 历史”。
- [ ] 编辑窗口已有的 `llama-server --help` 能力集合中检查 `context-shift`。确定不支持时禁止开启并显示原因；检测失败时不把“未知”当作“支持”，且不悄悄抹掉已经保存的开启状态。
- [ ] 加载 Profile、保存参数、保存为模型默认和恢复模型默认时都显示同一开关值。若当前勾选视觉模式，则阻止同时开启并解释 llama.cpp 的限制。
- [ ] 保持现有“运行中的当前模型参数不可热改”约束；保存后按现有事务更新 Profile、BAT、preset，下次模型启动时生效。

## Task 3：把参数交给原生运行时

**涉及文件：**

- `src/Launcher.Scripts/RouterPreset/RouterPresetGenerator.cs`
- `src/Launcher.Scripts/Batch/BatchScriptGenerator.cs`
- `src/Launcher.App/MainWindow.xaml.cs` 中的 `ActivateLocalAsync`

- [ ] 开启时，只在该模型的 Router preset 小节写 `context-shift = true`；关闭时不写 `context-shift` 或 `no-context-shift`。Router 主进程全局参数不改变。
- [ ] 独立 BAT 与 preset 保持一致：开启时写一次 `--context-shift`；关闭时不写。生成器不得再从 `ExtraArguments` 输出同名参数。
- [ ] 在实际激活 Local 模型前，对当前 Runtime 重新执行能力检测。已开启但 Runtime 不支持或无法确认 `--context-shift` 时，阻止启动并提示用户关闭开关或选择兼容 Runtime；避免 Router 运行后才因未知参数退出。
- [ ] 不修改 `LoopbackSafetyProxy`、Codex `model_auto_compact_token_limit`、模型输出 token 上限或历史文件。

## Task 4：说明和验收条件

**涉及文件：**

- `docs/user-guide.md`
- `docs/manual-acceptance-checklist.md`
- 版本说明在准备 v0.9.4 交付时更新

- [ ] 文档区分“llama.cpp 生成时滚动”和“Codex 对话摘要压缩”，说明信息遗忘风险和不保证下一次超长输入成功。
- [ ] 检查旧 Profile 默认关闭；模型 A 开启后模型 B 不受影响；保存、重开编辑器、保存为模型默认、恢复默认值后状态一致。
- [ ] 检查开启和关闭的 Router preset、BAT 分别只出现预期参数；在缺少参数的 Runtime 与视觉模式下，应在加载模型前得到明确提示。
- [ ] 在用户指定的 CUDA 32K 场景中，记录触顶后的 llama 原生日志、Responses 结束事件、工具调用完整性、后续 Codex 压缩及下一条消息结果。若生成完成但随后仍因历史过长或工具参数损坏而失败，只报告真实边界，不把该开关称为完整修复。
- [ ] Vulkan 与不同 llama.cpp 版本另行验收；不能从 CUDA 结果推定其行为。

## 实施风险与停止条件

1. `--context-shift` 可能在不同 llama.cpp 版本、模型模板和后端上表现不同；能力检测只证明参数存在，不证明真实运行稳定。
2. 滚动可能丢失本轮较早的指令或工具上下文，导致回答质量下降。开关默认关闭且只对选择的模型生效。
3. 若 CUDA 复现场景中出现未完成工具调用、Router 崩溃或后续压缩仍循环失败，应保留该结果为故障证据，停止将开关宣传为稳定性解决方案。

## 本方案不包含

- 代理层裁剪 Codex 历史；
- 输出接近上限时强行取消请求或伪造 `response.completed`；
- 损坏聊天的读取、分叉或恢复功能。
