# ChatGPT Launcher / Local LLM Launcher
## Windows 开源版需求规格与开发设计文档

> 文档用途：用于后续交由 Codex 进行项目开发。  
> 项目定位：面向 Windows 的开源 llama.cpp 本地模型管理器与 ChatGPT Desktop 双环境启动器。Chat、Work、Codex 均以 ChatGPT Desktop 当前集成形态为准。  
> 硬性首发环境：Windows 10 22H2 x64；同时保持 Windows 11 x64 兼容。  
> 当前首个验证环境：Windows 10 22H2、AMD Ryzen 7 5800X、64GB RAM、Radeon RX 7900 XT 20GB、llama.cpp + Vulkan。  
> 当前首个验证模型：Qwen3.8-27B-Opus-Distill-v2 Q4_K_M。  
> 注意：本项目必须设计为通用软件，不允许把上述硬件、路径、显卡品牌、模型名称或参数硬编码为唯一适配对象。

## 0.1 已确认的产品边界（2026-09-10）

以下约束优先于本文中尚未更新的旧表述：

1. V1 只连接 `llama.cpp` 与 ChatGPT Desktop，不单独适配 Codex CLI、IDE 扩展或其他聊天客户端。
2. `SelectedMode = OpenAI | Local` 是持久化配置。关闭 ChatGPT Desktop 只结束本次客户端会话，不等于切换模式。
3. 只有用户明确点击 OpenAI / Local 或切换本地模型时，Launcher 才实质修改受管理配置。
4. Local 模式保留 Local 配置与轻量 Router；模型和 KV Cache 的空闲卸载、显存释放及下一次请求自动重载使用 llama.cpp 原生 `--sleep-idle-seconds`。
5. 用户可以绕过 Launcher 直接启动 ChatGPT Desktop；客户端必须按上次保存的模式工作。该能力由登录启动的后台 Agent 与 llama.cpp Router 共同保证。
6. 两种模式共享项目目录、项目列表、开发任务、历史记录和可查阅的上下文。允许用户跨模型继续任务，但 UI 应提示其兼容性风险，不强制禁止。
7. 两种模式隔离推理传输、账户凭据与模型视图：OpenAI 模式不显示本地模型，Local 模式不显示在线模型。为共享 ChatGPT Desktop 原生项目和历史，Local 可以保留官方账户外壳及左下角账户名称，但不得把凭据或账户元数据发送给本地 Provider；这不等同于独立的本地账户登录。
8. Launcher 可以修改切换所必需的用户级 Provider / Catalog 配置，但不得破坏官方账户、工作区、凭据、项目或历史状态；切回 OpenAI 后应如同 Local 从未存在。
9. llama.cpp 是 Local 模式唯一的模型运行时，独立负责模型加载、推理、采样、KV Cache、硬性上下文容量、批处理及其原生 API；Codex 负责对话历史、token 监控、语义摘要和压缩后历史重建。Launcher 不复制任一方的能力。
10. 模型默认参数属于单个 Profile。用户可把当前参数保存为该模型的专用默认；新增、恢复或修改其他模型时不得读取或套用该快照。Launcher 不提供无法依据模型与硬件证明正确的内置推荐回退；可选自动适配只调用当前 llama.cpp 自带的 `llama-fit-params`，结果必须先展示、再由用户确认。
11. Launcher UI 与后台 Agent 生命周期分离：OpenAI 模式关闭 UI 时应正常结束无用 Agent；Local 模式关闭 UI 时由用户选择保留服务或停止代理、Router 与 Agent。停止后台服务不得隐式切换模式或恢复官方配置。
12. Launcher 的回环代理只承担网络边界：限制本机访问、剥离官方凭据、进行必要的传输解压并透明转发。不得解析或改写对话语义、工具、提示词、上下文和模型响应。
13. 每个模型使用简单 Profile 保存 llama.cpp 启动参数，并生成可单独运行的 BAT。Launcher 不解析任意 BAT，也不把 BAT 扩展为新的模型运行时。
14. 模型状态、加载、释放、缓存清单和缓存删除调用 llama Router 原生接口；缓存删除必须同时通过 llama `can_remove` 与 Launcher 下载来源校验，Local 运行期间禁止删除。
15. 运行监控以 llama `/models`、`/metrics`、`/slots` 和 Windows/驱动公开计数为准；未返回的数据不得估算。当前版本不加入性能基准测试。
16. Local 必须使用保留现有 OpenAI 登录状态、但具有独立身份的回环 Provider，使 Codex 选择原生本地 compaction，并把摘要生成作为普通 Responses 推理交给当前 llama.cpp 模型。Launcher 不生成、解析或存储摘要，不创建 compaction item；每个模型保存独立的压缩安全余量，Launcher 仅把 `Context - 压缩安全余量` 同步为 Codex 原生自动压缩线。
17. 压缩安全余量不是单次输出 token 限制。Codex 负责在下一次推理前检查阈值、选择安全压缩节点、生成摘要请求和重建历史；llama.cpp 只执行推理并管理硬性 Context。Local 客户端、后台 Agent 或当前 Runtime 的 llama 服务运行期间，不允许修改任何模型的配置参数；Local 模式已经保存但服务完全停止时允许编辑，并事务性同步当前模型配置。
18. llama.cpp 上游端口必须随机且仅监听回环；启动与绑定之间若发生竞态，只能对明确的地址占用错误执行有限换端口重试，不能掩盖模型或参数错误。Desktop-facing 端口首次进入 Local 时随机生成并持久化；若后续被占用，不得结束未知进程，只能在 Desktop 已关闭时通过同一配置事务迁移 Provider、Launcher 设置与恢复记录。App 只能探测 Agent 状态中通过协议、进程身份和心跳校验的实际端点。
19. 模型下载界面必须忠实显示 llama Router 的原生阶段、SSE 字节进度和最终缓存路径。若 llama 没有返回当前模型的进度或终止事件，Launcher 应有限等待并停止临时 Router；允许把本次原生日志中已知的连接、DNS、TLS 与 HTTPS 构建错误映射为不含原文和凭据的诊断，但不得自行接管下载、续传、重试或完整性校验。

---

# 1. 项目背景

本项目最初用于解决以下实际问题：

1. 用户同时需要使用 OpenAI 官方 ChatGPT/Codex 与本地大语言模型。
2. 本地模型由 llama.cpp 提供推理服务。
3. 用户希望获得与 `ollama launch chatgpt` 类似的使用体验：
   - 进入 OpenAI 模式时，只看到并使用 OpenAI 官方环境。
   - 进入 Local LLM 模式时，只看到并使用本地模型。
   - 两种模式不能同时使用。
   - 切换模式前必须完全关闭 ChatGPT/Codex 客户端。
   - 本地项目 / Repository 仍然可以被两种模式访问。
4. 用户不希望手工维护复杂的 llama.cpp 启动命令。
5. 用户希望启动器能够扫描本地 GGUF 模型、管理每个模型自己的参数，并自动生成 BAT 启动脚本。
6. 项目最终需要开源，可在不同 Windows 设备、不同显卡品牌和不同 llama.cpp 构建环境中部署。

因此，本项目不是单纯的“模型启动 BAT 生成器”，而是：

> **一个面向 Windows 的 llama.cpp 本地模型管理器 + ChatGPT/Codex 双环境启动器。**

---

# 2. 项目核心目标

项目必须同时解决四个问题：

```text
ChatGPT / Codex 双模式切换
        +
llama.cpp Runtime 管理
        +
GGUF 本地模型管理
        +
启动参数 / BAT 脚本管理
```

最终用户体验应尽量简单：

```text
双击 Launcher
        ↓
选择：
OpenAI
或
Local LLM
```

OpenAI 模式：

```text
官方 ChatGPT / Codex
        ↓
官方 OpenAI 账户
        ↓
GPT 在线模型
```

Local LLM 模式：

```text
选择本地模型
        ↓
Launcher 启动 llama-server
        ↓
等待模型加载完成
        ↓
启动 ChatGPT / Codex 本地模式
```

正常使用时，普通用户不应该需要：

- 手工输入 llama.cpp 命令行参数；
- 手工修改 `config.toml`；
- 手工启动 `llama-server.exe`；
- 手工关闭 llama-server；
- 记忆 `-c`、`-ngl`、`-ctk` 等参数的实际命令行格式；
- 手工编写 BAT。

---

# 3. 开源与通用化原则

## 3.1 不绑定固定电脑

代码中不得写死：

- RX 7900 XT；
- NVIDIA RTX 系列；
- AMD Radeon 系列；
- Vulkan；
- CUDA；
- ROCm；
- `E:\llama.cpp`；
- Qwen3.8-27B；
- 80K Context。

首台 RX 7900 XT 电脑只作为开发验证机。

---

## 3.2 不负责安装 llama.cpp

V1.0 不负责：

- 自动下载 llama.cpp；
- 自动安装 CUDA；
- 自动安装 ROCm；
- 自动安装 Vulkan Runtime；
- 自动安装显卡驱动；
- 自行实现 GGUF 下载、断点续传、文件校验或格式转换。

用户需要自行准备可运行的 llama.cpp。Launcher 可以搜索 Hugging Face 的公开 GGUF 元数据，
但下载、缓存、分片与取消必须调用当前 llama.cpp Router 的原生 `/models` 与 `/models/sse` 能力，
不得退化为 Launcher 自有下载器。缓存根目录通过 `LLAMA_CACHE` 指向 Runtime 的 `models` 目录。

Launcher 只负责：

```text
用户指定 llama.cpp 文件夹
        ↓
Launcher 验证 Runtime
        ↓
搜索公开 GGUF 元数据（可选）
        ↓
调用 llama.cpp 原生下载（可选）
        ↓
扫描模型
        ↓
管理模型参数
        ↓
生成启动脚本
        ↓
启动模型
        ↓
连接 ChatGPT / Codex
```

---

## 3.3 支持不同显卡品牌

Launcher 不直接通过“N卡 / A卡”判断运行后端。

应以用户实际提供的 llama.cpp Runtime 为准。

支持目标：

- NVIDIA + CUDA build；
- NVIDIA + Vulkan build；
- AMD + Vulkan build；
- AMD + ROCm/HIP build；
- CPU-only build；
- 后续可扩展多 GPU。

Launcher 应通过 llama.cpp 提供的设备枚举能力获取当前可用设备，而不是自行猜测。

---

# 4. 总体软件架构

建议模块：

```text
Launcher
│
├── App Shell / UI
│
├── Background Agent
│   ├── 用户登录时启动
│   ├── 维护 SelectedMode
│   └── 维护 Router 与安全回环入口
│
├── Runtime Manager
│   ├── llama.cpp 路径管理
│   ├── Runtime 验证
│   ├── llama.cpp 版本读取
│   └── Device 枚举
│
├── Model Scanner
│   ├── 扫描 models
│   ├── 识别 GGUF
│   ├── 分片模型识别
│   ├── mmproj 排除
│   └── Metadata 读取
│
├── Model Profile Manager
│   ├── 模型配置
│   ├── 默认值
│   ├── 编辑
│   ├── 保存
│   └── 恢复默认
│
├── Parameter Definition System
│   ├── UI 控件类型
│   ├── 可选值
│   ├── Tooltip
│   ├── CLI 映射
│   └── 兼容性检查
│
├── Script Generator
│   └── 生成 BAT
│
├── Local Server Manager
│   ├── 启动 llama.cpp Router
│   ├── Router / 模型子进程 PID 管理
│   ├── Health Check
│   ├── 日志采集
│   └── Router 停止
│
├── ChatGPT Desktop Mode Manager
│   ├── OpenAI Mode
│   ├── Local LLM Mode
│   ├── 模式互斥
│   └── 异常恢复
│
└── Persistence Layer
    ├── app settings
    ├── model profiles
    ├── runtime state
    └── backup / recovery state
```

---

# 5. 主界面

Launcher 首页保持极简。

建议结构：

```text
┌────────────────────────────────┐
│        ChatGPT Launcher        │
│                                │
│        [   OpenAI   ]          │
│                                │
│        [ Local LLM ]           │
│                                │
│                           ⚙    │
└────────────────────────────────┘
```

按钮：

1. `OpenAI`
2. `Local LLM`
3. 右下角全局设置 `⚙`

首页不直接展示本地模型。

---

# 6. OpenAI 模式

点击 `OpenAI` 后：

```text
检查 ChatGPT/Codex 是否正在运行
        ↓
若正在运行：拒绝切换
        ↓
确认/恢复官方运行环境
        ↓
停止 Launcher 管理的 Router / 模型子进程
        ↓
保存 SelectedMode = OpenAI
        ↓
启动官方 ChatGPT Desktop
```

OpenAI 模式目标：

- 使用正常 OpenAI 账户；
- 使用在线 GPT 模型；
- 使用官方 Codex；
- 使用官方联网 / Tools；
- 不启动任何本地 GGUF；
- 不运行 llama-server；
- 不暴露 Local LLM Provider；
- 不显示本地模型；
- 不占用 Local LLM 所需显存。

体验要求：

> 进入 OpenAI 模式后，应让用户感觉本地 Qwen / llama.cpp 从未存在过。

---

# 7. Local LLM 模式

点击 `Local LLM` 后进入模型列表页面。

示意：

```text
┌────────────────────────────────────────┐
│              Local LLM                 │
│                                        │
│  ● Qwen3.8-27B Opus v2          ⚙      │
│    Q4_K_M · 80K · Q8 KV                │
│                                        │
│  ○ Model B                       ⚙      │
│    Q4_K_M · 64K · Q8 KV                │
│                                        │
│  ○ Model C                       ⚙      │
│    Q5_K_M · 32K · F16                  │
│                                        │
│  [ 扫描模型 ]               [ Launch ] │
└────────────────────────────────────────┘
```

用户操作：

1. 选择模型；
2. 可点击模型右侧齿轮查看配置；
3. 点击 `Launch` 后才真正启动。

进入 Local 模式时应：

- 保存 `SelectedMode = Local` 与选定模型 ID；
- 应用 Local Provider 与仅含当前模型的 Catalog；
- 启动或复用轻量 llama.cpp Router；
- 等待 Router 与模型目录健康检查；模型在首个推理请求到达时由 llama.cpp 加载；
- 用户直接启动 ChatGPT Desktop 时，由后台 Agent / Router 按已保存 Profile 提供服务。

不允许：

> 点击模型名称即立即加载。

---

# 8. 模式切换硬规则

OpenAI 与 Local LLM 永远互斥。

## 8.1 OpenAI → Local LLM

必须：

```text
关闭 ChatGPT/Codex
        ↓
确认客户端进程完全退出
        ↓
选择 Local LLM
```

---

## 8.2 Local LLM → OpenAI

必须：

```text
关闭 ChatGPT/Codex
        ↓
停止 llama-server
        ↓
等待模型卸载
        ↓
释放 GPU VRAM
        ↓
选择 OpenAI
```

---

## 8.3 禁止热切换

禁止：

```text
ChatGPT 正在运行
        ↓
直接切换 Provider
```

禁止：

```text
模型 A 正在运行
        ↓
不关闭客户端直接加载模型 B
```

---

## 8.4 客户端运行时行为

若 Launcher 检测到 ChatGPT/Codex 正在运行：

弹窗：

```text
ChatGPT/Codex 当前正在运行。

请先完全关闭客户端后再切换运行模式。
```

只提供：

`确定`

V1.0 不建议 Launcher 强制结束 ChatGPT/Codex 进程。

## 8.5 关闭客户端与直接启动

关闭 ChatGPT Desktop 时：

- 不改变 `SelectedMode`；
- 不自动恢复 OpenAI 配置；
- 达到 Profile 设置的空闲时间后，由 llama.cpp 原生休眠卸载模型与 KV Cache 并释放显存；
- Local Router 可以保持驻留，但必须保持无模型、无 GPU 占用的轻量状态。

绕过 Launcher 直接启动 ChatGPT Desktop 时：

- OpenAI 模式按官方配置启动；
- Local 模式按当前回环端点、当前模型 Catalog 与保存的 Profile 启动；
- 后台 Agent 必须在用户登录后可用，不能要求 Launcher UI 一直打开；
- 若当前 llama.cpp Runtime 不支持 Router 或 `--sleep-idle-seconds`，本版本应清晰报告 `Runtime Unsupported`；不得在 Launcher 内补写模型服务能力。

---

# 9. 项目 / Repository 共享原则

OpenAI 与 Local LLM 应可访问同一批真实项目目录。

例如：

```text
D:\AndroidProjects\ProjectA
E:\Projects\ProjectB
F:\Code\ProjectC
```

共享内容：

- 项目源代码；
- Git Repository；
- Kotlin / Java / C++ 等源码；
- Gradle；
- MD 文档；
- 资源文件；
- APK；
- 编译产物；
- ChatGPT Desktop / Codex 的项目列表；
- 历史开发任务与会话索引；
- 可查阅的历史上下文与开发记录。

原则：

> 模型环境隔离，但项目文件不隔离。

---

## 9.1 允许但不建议跨模型继续同一开发任务

两个模式必须能够打开并查阅同一任务历史。用户也可以选择：

```text
GPT 做一半
        ↓
Qwen 在同一 Thread 强行接着做
```

原因：

- 模型理解差异；
- 编码习惯差异；
- 工具调用差异；
- 上下文差异；
- 架构判断差异。

Launcher 不得因模型切换而复制、迁移、隐藏或清空任务历史。具体开发 Thread 是否跨模型继续由用户决定；产品只提示风险，不作强制限制。

---

# 10. ChatGPT/Codex 集成隔离原则

目标行为参考 `ollama launch chatgpt` 的使用理念：

- OpenAI 模式只使用 OpenAI；
- Local 模式只使用本地 Provider；
- 项目仍可访问；
- 每次切换前关闭客户端。

但项目实现不得假设某一种内部配置接口永远稳定。

因此：

> **ChatGPT/Codex Provider 切换必须抽象成独立 Adapter 层。**

Adapter 只管理配置、Catalog、客户端生命周期和恢复事务。若某个 llama.cpp 构建不能满足 ChatGPT Desktop 所需接口，应报告 Runtime 不兼容；不得通过持续扩展 Launcher 来重新实现 Responses、工具调用或语义压缩。Codex 原生本地 compaction 仍可通过普通 Responses 请求调用 llama.cpp 完成摘要推理。

建议：

```text
ChatGPTIntegrationAdapter
│
├── DetectClient()
├── IsClientRunning()
├── BackupOfficialState()
├── ReadManagedConfiguration()
├── BeginSwitchTransaction()
├── EnterOpenAIMode()
├── EnterLocalMode()
├── RestoreOfficialManagedFields()
├── CommitSwitchTransaction()
├── LaunchClient()
└── RecoverFromInterruptedSwitch()
```

Codex 开发阶段必须首先审计当前 ChatGPT/Codex Windows 客户端与 Ollama `launch chatgpt` 的最新实现，再决定最终修改哪些配置 / catalog / state。

不能把当前观察到的内部文件格式永久硬编码到业务层。

---

# 11. 异常恢复

由于 Launcher 会切换 ChatGPT/Codex Provider 状态，因此必须具备恢复机制。

若上一次运行：

- Launcher 崩溃；
- Windows 强制关机；
- llama-server 异常退出；
- Local 模式切换未完成；

下次 Launcher 启动时应：

```text
检测上次模式状态
        ↓
检查 ChatGPT/Codex
        ↓
检查 llama-server
        ↓
检查是否存在未完成切换
        ↓
恢复到一致状态
```

安全原则：

> 任何异常都不能永久破坏用户原有 OpenAI 官方环境。恢复必须是字段级、归属明确、可重复执行的恢复，不得用整目录覆盖破坏切换期间产生的项目、历史或未知配置。

---

# 12. llama.cpp Runtime 配置

首次启动如果未配置 llama.cpp：

显示：

```text
未配置 llama.cpp

请选择 llama.cpp 所在文件夹。

[ 选择文件夹 ]
```

用户通过目录选择器选择 Runtime Root。

例如：

```text
C:\AI\llama.cpp
D:\LLM\llama.cpp
E:\llama.cpp
```

---

# 13. Runtime 验证

选择目录后必须验证。

至少检查：

```text
llama-server.exe
```

建议同时识别：

```text
llama-cli.exe
llama-bench.exe
```

验证流程：

```text
选择目录
        ↓
检查 llama-server.exe
        ↓
运行 --version
        ↓
运行 --list-devices
        ↓
验证成功
```

失败时禁止保存该 Runtime Root。

---

# 14. llama.cpp 版本

Launcher 应记录：

- llama.cpp build / version；
- 检测日期；
- Runtime Root；
- 支持的主要命令参数。

可通过：

```text
llama-server.exe --version
```

读取。

---

# 15. 设备识别

Launcher 应运行：

```text
llama-server.exe --list-devices
```

获取 llama.cpp 实际可见的设备。

不得自行根据：

- NVIDIA；
- AMD；
- Intel；

直接猜后端。

显示示例：

```text
llama.cpp：bXXXXX
Backend：Vulkan
Device：AMD Radeon RX 7900 XT
```

或：

```text
llama.cpp：bXXXXX
Backend：CUDA
Device：NVIDIA GeForce RTX 4090
```

---

# 16. Runtime 目录结构

当用户选择 Runtime Root 后，如果不存在以下目录：

```text
models
scripts
logs
```

Launcher 可询问并自动创建。

推荐：

```text
<llamaRoot>\
│
├── llama-server.exe
├── llama-cli.exe
├── llama-bench.exe
├── ...
│
├── models\
│
├── scripts\
│   └── profiles\
│
└── logs\
```

---

# 17. Models 扫描

Launcher 扫描：

```text
<llamaRoot>\models\
```

支持递归扫描。

目标：

```text
*.gguf
```

---

# 18. GGUF 扫描分类

扫描结果至少区分：

## 18.1 主模型

例如：

```text
Qwen3.8-27B-Q4_K_M.gguf
```

---

## 18.2 分片模型

例如：

```text
model-00001-of-00003.gguf
model-00002-of-00003.gguf
model-00003-of-00003.gguf
```

必须识别为：

> 一个模型。

不能显示为三个独立模型。

---

## 18.3 mmproj

例如：

```text
xxx-mmproj-f16.gguf
```

默认不得作为普通 LLM 单独加入模型列表。

---

## 18.4 后续预留

V1.0 可先不完整支持：

- LoRA；
- draft model；
- speculative model；
- vision/mmproj 绑定；
- MTP 独立管理。

但数据结构必须预留。

---

# 19. 模型扫描行为

点击：

`扫描模型`

后：

```text
扫描 models
        ↓
与已管理模型比对
        ↓
显示新增模型
```

新发现模型：

```text
发现 2 个新模型

+ Model A
+ Model B
```

需要用户点击：

`添加`

才进入模型数据库。

不得扫描后自动启用所有模型。

---

# 20. GGUF Metadata

建议 Launcher 读取 GGUF Metadata，而不完全依赖文件名。

目标信息：

- 模型名称；
- Architecture；
- 参数规模；
- Quantization；
- 文件大小；
- 原生 Context；
- 模型描述；
- 分片信息；
- 是否可能属于视觉附件；
- 其他可读取 metadata。

模型列表可以显示：

```text
Qwen3.8-27B Opus Distill v2
Q4_K_M · 27B · 16.8GB · Native 256K
```

---

# 21. 模型 Profile

每个本地模型必须拥有独立 Profile。

Profile 内容至少包括：

```text
id
displayName
modelPath
modelType
quantization
nativeContext

context
gpuOffload
device
flashAttention
kCache
vCache
parallel
jinja

batchMode
batch
ubatch

host
port
alias

extraArgs

defaultProfile
hardwareFingerprint
lastModified
```

---

# 22. Profile 是配置源，BAT 是生成物

设计原则：

> **Model Profile / JSON 是 Source of Truth。**

BAT 只由 Launcher 自动生成。

不建议：

```text
用户修改 BAT
        ↓
Launcher 再解析 BAT
```

正确方向：

```text
用户修改 Launcher UI
        ↓
保存 Profile
        ↓
自动重新生成 BAT
```

---

# 23. Scripts 目录

建议：

```text
<llamaRoot>\scripts\
│
├── profiles\
│   ├── model-a.json
│   ├── model-b.json
│   └── model-c.json
│
├── model-a.bat
├── model-b.bat
└── model-c.bat
```

这样即使用户不用 Launcher：

> 仍然可以直接双击对应 BAT 启动模型。

这是本项目的开放设计原则之一。

---

# 24. BAT 路径设计

生成 BAT 时尽量避免写死盘符。

由于 BAT 位于：

```text
<llamaRoot>\scripts\
```

可以利用脚本自身路径推导：

```text
<llamaRoot>
```

使整个 llama.cpp 文件夹移动到不同磁盘后仍具有一定可迁移性。

---

# 25. Local LLM 模型列表

模型列表每个条目至少显示：

```text
模型显示名称
量化
Context
KV Cache
当前状态
```

例如：

```text
Qwen3.8-27B Opus v2               ⚙
Q4_K_M · 80K · Q8 KV
```

状态可包括：

- Ready；
- Running；
- Invalid Path；
- Runtime Unsupported；
- Profile Needs Review。

---

# 26. 模型配置页面

点击模型右侧：

`⚙`

进入配置页。

默认只读。

示意：

```text
模型名称
Qwen3.8-27B Opus v2

模型文件
...\Qwen3.8-27B...gguf

上下文长度 ⓘ
[ 80K ▼ ]

GPU Offload ⓘ
[ All ▼ ]

Flash Attention ⓘ
[ On ▼ ]

K Cache ⓘ
[ Q8 ▼ ]

V Cache ⓘ
[ Q8 ▼ ]

并行数量 ⓘ
[ 1 ▼ ]

空闲释放显存 ⓘ
[ 5 分钟 ▼ ]

设备 ⓘ
[ Auto ▼ ]

Jinja ⓘ
[ ON ]

▶ 高级设置

⟳              [ 编辑 ]
```

---

# 27. 参数配置 UI 原则

核心原则：

> **能选择，就不让普通用户手工输入。**

普通用户不应该直接填写：

```text
81920
q8_0
all
```

而应看到：

```text
80K
Q8
All
```

Launcher 负责映射到底层 llama.cpp CLI。

---

# 28. Context 下拉菜单

建议选项：

```text
Auto / Follow Model
4K
8K
16K
24K
32K
48K
64K
80K
96K
128K
256K
```

ChatGPT Desktop Codex 的隐藏系统指令、项目上下文和工具 schema 会占用大量窗口。技术下限为 1,024 tokens；当用户选择低于 16K 时，参数窗口、模型列表和 Local 启动确认必须显示“建议使用 16K 或更高”的非阻断提醒，但仍允许保存、生成 BAT 和启动。Launcher 不得自动提高 Context、改变压缩安全余量或把某一台电脑和某一个模型的推荐值硬编码为强制最低值。

Local Catalog 必须向 ChatGPT/Codex 声明与 llama.cpp `--ctx-size` 一致的窗口。Launcher 根据该模型 Profile 的压缩安全余量写入 `model_auto_compact_token_limit = Context - 压缩安全余量`，但不生成摘要或 compaction item，也不改写历史输入；Codex 达到阈值后在安全节点通过普通 Responses 请求让当前本地模型生成摘要，并由 Codex 重建历史。llama.cpp 只执行这些请求及硬性容量管理。

内部映射：

```text
4K      -> 4096
8K      -> 8192
16K     -> 16384
24K     -> 24576
32K     -> 32768
48K     -> 49152
64K     -> 65536
80K     -> 81920
96K     -> 98304
128K    -> 131072
256K    -> 262144
```

如果设置超过模型 metadata 中的原生 Context：

显示：

```text
⚠ 超过模型原生上下文
```

V1.0 不应自动声称所有模型都能安全扩展。

---

# 28.1 压缩安全余量

建议选项：1K、2K、4K、8K、12K、16K、24K、32K，并允许已有 Profile 保留其他合法值。

定义：Codex 开始下一次模型推理前应保留的最小空闲上下文。计算式：

```text
Codex 自动压缩线 = Context - 压缩安全余量
```

例如 Context 32K、压缩安全余量 8K，则自动压缩线为 24K。该参数不等于一次完整回答、工具调用或命令执行的最大输出量，也不得映射为 llama.cpp 的输出 token 上限。用户改变 Context 或安全余量后必须重新校验 `1K <= 安全余量 < Context`；Local 运行期间禁止修改，下一次切换到 Local 时必须把最新结果同时同步到 Codex 配置、Catalog 和 llama Router preset。

---

# 29. Context Tooltip

鼠标移至：

`上下文长度 ⓘ`

弹出：

> **上下文长度（Context）**  
> 决定模型一次可以记住和处理多少文本、代码及聊天历史。  
> 增大：可以处理更长项目和对话。  
> 代价：通常会增加显存占用，并可能降低长上下文推理速度。  
> 如果模型因显存不足无法启动，可优先降低该参数。

---

# 30. Flash Attention

普通 UI 不建议只做二态复选框。

建议三态：

```text
Auto
On
Off
```

映射：

```text
Auto -> 不强制或对应 auto
On   -> -fa on
Off  -> -fa off
```

Tooltip：

> **Flash Attention**  
> 一种优化 Attention 计算的方法，通常可以提高性能并降低长上下文的内存压力。  
> Auto：由 llama.cpp 决定。  
> On：强制开启。  
> Off：关闭。  
> 大多数现代 GPU 推荐 Auto 或 On。

---

# 31. KV Cache

普通模式只展示：

```text
F16
Q8
Q4
```

映射：

```text
F16 -> f16
Q8  -> q8_0
Q4  -> q4_0
```

分别设置：

- K Cache；
- V Cache。

---

# 32. KV Cache Tooltip

> **KV Cache 精度**  
> KV Cache 是模型在当前上下文中保存“工作记忆”的缓存。  
> F16：精度最高，显存占用最大。  
> Q8：显著降低 KV 显存占用，通常适合长上下文使用。  
> Q4：进一步节省显存，但量化程度更高。  
> 显存不足时，可以考虑从 F16 改为 Q8 或 Q4。

高级模式以后可以提供更多 llama.cpp 支持的数据类型。

---

# 33. GPU Offload

基础模式：

```text
Auto
All
```

高级模式可增加：

```text
Custom
```

普通用户不直接输入 `-ngl 47`。

Tooltip：

> **GPU Offload**  
> 决定模型有多少计算层放到显卡执行。  
> All：尽可能全部放在 GPU，通常速度最快，但需要足够显存。  
> Auto：由 llama.cpp 根据当前设备与显存决定。  
> 如果模型无法加载，可以尝试 Auto。

---

# 34. Device

通过：

```text
llama-server.exe --list-devices
```

动态填充。

示例：

```text
Auto
AMD Radeon RX 7900 XT
NVIDIA GeForce RTX 4090
...
```

如果只有一张设备：

默认 `Auto`。

---

# 35. Parallel

下拉：

```text
1
2
4
8
```

Tooltip：

> **并行数量（Parallel）**  
> 决定 llama-server 同时处理多少独立请求。  
> 数值越大，可以同时服务更多请求，但会提高内存与 KV Cache 占用。  
> 单用户本地使用推荐 1。

---

# 36. Jinja

使用开关：

```text
ON / OFF
```

Tooltip：

> **Jinja Chat Template**  
> 用于按模型内置的聊天模板组织系统、用户和助手消息。  
> 对多数现代聊天模型通常建议开启。

---

# 37. 基础设置 / 高级设置

配置页面分两层。

## 37.1 基础设置

显示：

- Context；
- GPU Offload；
- Device；
- Flash Attention；
- K Cache；
- V Cache；
- Parallel；
- Jinja。

---

## 37.2 高级设置

默认折叠：

`▶ 高级设置`

展开后：

- Batch；
- uBatch；
- CPU Threads；
- mmap / load mode；
- KV Offload；
- MTP；
- Speculative Decoding；
- mmproj；
- Host；
- Port；
- Alias；
- Extra Args。

---

# 38. Batch / uBatch

基础界面隐藏。

高级界面默认：

```text
☑ 使用 llama.cpp 默认值
```

只有取消后才允许设置。

避免普通用户误改造成 OOM。

---

# 39. 参数 Tooltip 统一风格

每一个参数 Tooltip 尽量回答三件事：

```text
1. 它是什么？
2. 增大 / 开启有什么作用？
3. 代价是什么？
```

例如：

GPU Offload：

> 决定多少模型层在 GPU 执行。  
> 更多 GPU 层通常更快。  
> 代价是占用更多显存。

Context：

> 决定可记住多少内容。  
> 增大后可处理更长代码和对话。  
> 代价是显存占用和长上下文计算成本增加。

---

# 40. 参数说明边界

Launcher 应解释参数用途、收益和代价，但不能把固定值标记为适合所有用户的“推荐”。准确建议至少需要同时知道：

- GPU；
- VRAM；
- Runtime backend；
- 模型架构；
- 模型大小；

V1.0 不实现 Launcher 自有硬件评估器，因此只展示中性选项、llama.cpp 原生 `auto/default` 含义和风险说明。若 Runtime 提供 `llama-fit-params`，用户可主动调用并确认其建议；用户保存的专用默认仍只属于当前模型。

---

# 41. 参数联动风险提示

Launcher 应提供非强制风险提示。

例如：

```text
8GB VRAM
+
128K Context
+
F16 KV
+
GPU All
```

可显示：

```text
⚠ 当前配置可能需要较高显存。

建议：
- 降低 Context；
或
- 使用 Q8 / Q4 KV；
或
- 将 GPU Offload 改为 Auto。
```

注意：

> V1.0 不要伪装成能够精准计算所有模型的 VRAM。

Dense、MoE、Hybrid Attention、SWA 等架构差异较大。

因此只提供：

`风险提示`

而不是：

`保证显存占用 = XX.XX GB`

---

# 42. Parameter Definition System

每个参数不应该只保存一个字符串。

建议抽象：

```text
ParameterDefinition
├── id
├── displayName
├── category
├── controlType
├── values
├── nativeDefaultValue
├── userSavedDefaultValue
├── tooltip
├── cliKey
├── cliMapper
├── dependencies
├── compatibilityRules
└── advanced
```

例如：

```text
id: context
displayName: 上下文长度
controlType: dropdown
cliKey: -c
```

以后新增 llama.cpp 参数时，可尽量通过 Parameter Definition 扩展，而非重写 UI。

---

# 43. 与 llama.cpp 版本联动

llama.cpp 更新非常频繁。

Launcher 应：

1. 读取 `--version`；
2. 必要时读取 `--help`；
3. 根据当前 Runtime 检查功能可用性。

例如某版本不支持某参数：

UI 中显示：

```text
Flash Attention
[ Disabled ]
```

Tooltip：

> 当前 llama.cpp 版本不支持该选项，请升级 Runtime。

核心业务层不要假设所有版本参数完全一致。

---

# 44. 模型配置编辑规则

点击齿轮后：

默认：

`只读`

底部：

`[ 编辑 ]`

点击编辑后：

```text
[ 取消 ]    [ 保存 ]
```

只有进入编辑态才能修改参数。

---

# 45. 保存

任何修改：

> 只有点击 `保存` 后才真正写入 Profile。

保存时：

```text
验证参数
        ↓
保存 JSON
        ↓
重新生成 BAT
```

---

# 46. 取消

点击：

`取消`

行为：

- 放弃当前未保存修改；
- 恢复编辑前值；
- 返回只读。

---

# 47. 恢复默认按钮

配置页增加小按钮：

`⟳`

视觉风格类似 Refresh。

不要设计成大型文字按钮。

含义：

> 恢复该模型在当前设备上的默认 / 初始配置。

---

# 48. 恢复默认安全规则

点击 `⟳`：

```text
配置恢复默认值
        ↓
标记为“未保存”
        ↓
必须点击保存
        ↓
才真正生效
```

点击取消：

> 原配置保持不变。

---

# 49. 运行时配置锁定

只要该模型正在运行：

- 模型选择禁用；
- Edit 禁用；
- 恢复此模型已保存的默认参数禁用；
- Context 禁用；
- KV 禁用；
- Device 禁用；
- 其他参数禁用。

允许：

- 查看配置；
- 查看状态；
- 查看日志。

提示：

```text
当前模型正在运行。

请先关闭 ChatGPT/Codex 并停止 Local LLM 后再修改配置。
```

---

# 50. 模型切换

Local 模式下 ChatGPT/Codex 内部禁止切换本地模型。

客户端只暴露：

> 当前 Launcher 启动的单个模型。

切换：

```text
关闭 ChatGPT/Codex
        ↓
停止 llama-server
        ↓
等待模型卸载
        ↓
重新打开 Launcher
        ↓
Local LLM
        ↓
选择其他模型
        ↓
Launch
```

---

# 51. Device Profile / Hardware Fingerprint

跨设备开源版本应记录当前硬件环境。

建议保存：

```text
GPU Name
VRAM
Backend
llama.cpp Version
Runtime Path
```

例如：

```text
AMD Radeon RX 7900 XT
20480 MB
Vulkan
bXXXXX
```

---

# 52. 硬件变化检测

如果整个 llama.cpp 文件夹复制到另一台电脑：

原机器：

```text
RX 7900 XT 20GB
```

当前机器：

```text
RTX 4070 12GB
```

Launcher 应提示：

```text
检测到运行硬件发生变化。

原配置：
RX 7900 XT 20GB

当前：
RTX 4070 12GB

建议重新检查模型运行参数。

[ 重新配置 ] [ 保留原配置 ]
```

避免错误套用旧设备参数。

---

# 53. Local LLM 状态栏

Local 模型页建议显示：

```text
llama.cpp：bXXXXX
Backend：Vulkan
GPU：AMD Radeon RX 7900 XT
VRAM：20GB
Models：3
```

NVIDIA 示例：

```text
llama.cpp：bXXXXX
Backend：CUDA
GPU：NVIDIA RTX 4090
VRAM：24GB
Models：5
```

---

# 54. 全局设置页

首页右下角：

`⚙`

进入全局设置。

至少包括：

```text
llama.cpp 路径
[ E:\llama.cpp ] [浏览]

Models
E:\llama.cpp\models

Scripts
E:\llama.cpp\scripts

Logs
E:\llama.cpp\logs

ChatGPT/Codex
自动检测 / 当前检测状态
```

V1.0：

`models / scripts / logs`

默认跟随 llamaRoot。

高级版本再允许独立修改。

---

# 55. Local LLM 启动流程

完整流程：

```text
点击 Local LLM
        ↓
确认 ChatGPT/Codex 已关闭
        ↓
读取 Runtime Root
        ↓
验证 llama-server.exe
        ↓
读取 Runtime / Devices
        ↓
读取模型数据库
        ↓
显示本地模型
        ↓
用户选择模型
        ↓
读取 Profile
        ↓
确保 BAT / Router preset 与 Profile 同步
        ↓
启动或复用 llama.cpp Router
        ↓
记录 Router PID
        ↓
按需预热当前模型子进程
        ↓
等待 Router / 模型 Health Check
        ↓
以事务方式切换 ChatGPT Desktop Local Provider
        ↓
保存 SelectedMode = Local 与当前模型
        ↓
启动 ChatGPT Desktop
```

---

# 56. Health Check

Launcher 主动启动 Local 模式时，不允许在 Router / 预热模型尚未就绪时立即打开 ChatGPT Desktop。用户绕过 Launcher 直接打开客户端时，由 Background Agent 尽早预热，Router 仍需支持请求触发加载。

应轮询本地健康端点。

例如：

```text
http://127.0.0.1:<port>/health
```

直到：

`Ready`

再继续启动客户端。

模型加载失败：

- 不启动客户端 Local 模式；
- 显示错误；
- 提供日志；
- 提供返回模型列表。

---

# 57. llama-server PID 管理

Launcher 必须记录：

> 本次 Launcher 自己启动的 llama-server PID。

只管理自己的进程。

不能粗暴地：

```text
taskkill /IM llama-server.exe /F
```

把用户其他 llama-server 全部杀掉。

---

# 58. Local 模式退出

当用户关闭 ChatGPT/Codex 后：

建议 Helper / Launcher 检测。

然后：

```text
停止或休眠当前模型子进程
        ↓
等待正常退出
        ↓
模型卸载
        ↓
显存释放
        ↓
保留 SelectedMode = Local
        ↓
Router 可保持轻量驻留
```

不得因为用户关闭 ChatGPT Desktop 就恢复官方 Provider。只有用户明确选择 OpenAI 时，`ChatGPTIntegrationAdapter` 才执行 OpenAI 切换事务。

核心目标：

> 下次直接打开 ChatGPT Desktop 时仍使用上一次模式；下次明确选择 OpenAI 时必须得到纯净官方环境。

---

# 59. 日志

建议目录：

```text
<llamaRoot>\logs\
```

Local 启动日志格式：

```text
2026-09-10_qwen38-27b_080000.log
```

至少记录：

- Launcher version；
- llama.cpp version；
- Runtime Root；
- Device；
- Backend；
- Model；
- GGUF Path；
- 参数；
- 最终命令；
- server stdout；
- server stderr；
- health 状态；
- 退出原因。

---

# 60. BAT 生成

每个 Profile 自动生成：

```text
<llamaRoot>\scripts\<model-id>.bat
```

BAT 应等价于 UI 配置。

例如当前首个验证模型：

```bat
llama-server.exe ^
-m "<MODEL_PATH>" ^
-ngl all ^
-c 81920 ^
-fa on ^
-ctk q8_0 ^
-ctv q8_0 ^
-np 1 ^
--jinja ^
--alias qwen38-opus-27b-80k ^
--host 127.0.0.1 ^
--port 8080
```

实际生成时需根据 Runtime Root 与 Scripts 路径处理相对路径。

---

# 61. 当前首个验证模型

首个开发验证模型：

```text
Qwen3.8-27B-Opus-Distill-v2
```

GGUF：

```text
Qwen3.8-27B-Opus-Distill-v2-Q4_K_M.gguf
```

验证设备：

```text
Ryzen 7 5800X
64GB RAM
RX 7900 XT 20GB
Windows 10
llama.cpp Vulkan build
```

已验证稳定参数：

```text
Context        81920
GPU Offload    all
Flash Attention on
K Cache        q8_0
V Cache        q8_0
Parallel       1
Jinja          on
Host           127.0.0.1
Port           8080
```

Batch / uBatch：

> 使用当前 llama.cpp 官方默认值。

不主动写入：

```text
-b
-ub
```

---

# 62. 当前实测数据（仅作为开发验证记录）

该组数据不应硬编码为其他设备默认值。

首台验证机实测：

```text
64K Context：
约 18.5~18.6 / 20GB VRAM

80K Context：
基础状态约 18.8~19.0 / 20GB VRAM

43K 实际已用上下文：
Token Generation 约 30 t/s

短上下文实际 server：
Token Generation 约 34 t/s

GPU Compute：
约 99%

CPU：
约 7% 以内

GPU 温度：
约 69℃
```

说明：

> 该数据只用于验证 Launcher 生成的参数能够成功运行，不作为跨设备推荐算法的硬编码依据。

---

# 63. Runtime Compatibility

V1.0 至少需要验证：

```text
Windows 10
Windows 11
```

Runtime 类型至少考虑：

```text
Windows x64 Vulkan
Windows x64 CUDA
Windows x64 ROCm
CPU build
```

实际兼容性应以 llama.cpp Runtime 是否能正常运行及枚举设备为准。

---

# 64. UI 风格

整体要求：

- 简洁；
- 稳重；
- 不花哨；
- 接近系统工具；
- 信息密度适中；
- 参数页面可解释；
- 尽量避免“命令行感”。

颜色不应根据 GPU 品牌做强绑定。

例如不要：

- NVIDIA 页面强制绿色；
- AMD 页面强制红色。

保持中性统一 UI。

---

# 65. Tooltip UI

建议：

```text
参数名 ⓘ
```

鼠标移动到：

- 参数名；
- `ⓘ`

均显示 Tooltip。

Tooltip 应：

- 简短；
- 不超过必要篇幅；
- 使用普通用户语言；
- 避免过度技术化。

---

# 66. Launcher 状态机

必须把“持久化模式”和“瞬时运行状态”分开建模：

```text
SelectedMode = OpenAI | Local

RuntimeState =
    Stopped
    Starting
    Running
    Stopping
    SwitchingToOpenAI
    SwitchingToLocal
    Recovering
    Error
```

推荐组合状态：

```text
OpenAIConfigured
LocalConfiguredStopped
LocalStarting
LocalRunning
LocalStopping
SwitchingToOpenAI
SwitchingToLocal
Recovering
Error
```

`LocalConfiguredStopped` 是正常稳定态，不是退出后的临时状态：Local 配置仍有效，Router 可驻留，但模型未加载、显存已释放、ChatGPT Desktop 未运行。

---

# 67. OpenAIConfigured / LocalConfiguredStopped

两种配置态均允许：

- 全局设置；
- 扫描模型；
- 编辑模型；
- 恢复默认；
- 启动当前模式；
- 在客户端已关闭时执行显式模式切换。

`LocalConfiguredStopped` 下不得把“模型未加载”误判为 OpenAI 模式。

---

# 68. OpenAI Running

禁止：

- Local LLM；
- Provider 切换；
- 本地模型启动。

切换：

> 必须关闭 ChatGPT/Codex。

---

# 69. LocalStarting

状态：

```text
正在加载模型...
```

显示：

- 模型名；
- Loading；
- 可选简化日志。

禁止：

- 换模型；
- 编辑；
- OpenAI。

---

# 70. LocalRunning

允许：

- 查看当前模型；
- 查看参数；
- 查看日志；
- 查看 Runtime 状态。

禁止：

- 模型切换；
- 参数编辑；
- 恢复默认；
- OpenAI。

---

# 71. LocalStopping

执行：

```text
等待 ChatGPT/Codex 关闭
        ↓
停止 server
        ↓
释放资源
        ↓
清理运行状态
        ↓
LocalConfiguredStopped
```

该流程不修改 Provider，不恢复 OpenAI 配置。

---

# 72. Error

错误页必须至少提供：

- 简短错误原因；
- 日志路径；
- 复制错误信息；
- 返回；
- 恢复到切换事务开始前的一致模式；
- 仅在用户目标模式为 OpenAI 或切换事务无法判定时，提供恢复官方环境操作。

---

# 73. OpenAI / Local 环境隔离验收标准

## OpenAI 模式

验收：

- llama-server 不运行；
- Local GGUF 不加载；
- 在线账号正常；
- 官方模型正常；
- Local Provider 不出现；
- 本地模型不占 GPU 显存；
- 官方账户、工作区、项目与历史任务保持正常；
- 切回 OpenAI 后不残留 Launcher 管理的 Local Catalog / Provider 字段。

---

## Local 模式

验收：

- 只有当前选定 Local LLM；
- llama-server 正常；
- 当前模型可以响应；
- OpenAI 在线 Provider 不参与；
- 不依赖 OpenAI API Key；
- 项目 Repository 仍能打开；
- OpenAI 模型不出现在模型选择列表；
- 原有项目列表、历史任务和上下文可查阅；
- 关闭客户端后模式仍为 Local；达到 Profile 空闲时间后，llama.cpp 卸载模型与 KV Cache 并释放显存；
- 重启 Windows 后直接打开 ChatGPT Desktop 仍可按 Local 配置工作。

---

# 74. 本地网络访问能力

Local LLM 未来用于 Codex 开发时，希望支持：

```text
git clone
git pull
git fetch
curl
Gradle
npm
pip
PowerShell
```

典型需求：

> “这是一个 GitHub 地址，请读取源码，分析其中某模块，将有用实现整合进当前 Android 项目，编译并修复错误。”

目标链路：

```text
Local LLM
        ↓
Codex Tool / Terminal
        ↓
GitHub
        ↓
clone / fetch
        ↓
读源码
        ↓
修改项目
        ↓
Gradle
        ↓
修复
        ↓
APK
```

注意：

> Web Search / Browser / MCP 等高级工具是否能在当前 ChatGPT/Codex + llama.cpp 本地端点环境下正常使用，需要开发阶段单独验证，不能在 PRD 中假定 100% 可用。

---

# 75. 文件与 Office 类任务

未来 Local 模式希望能够通过 Codex Tool / shell 完成：

- Markdown；
- TXT；
- JSON；
- Python；
- Word；
- PPT；
- APK；
- 代码项目；
- 文案。

Launcher 本身不负责编辑 Word/PPT。

它只负责提供 Local LLM 环境。

真正文件生成由：

- Codex；
- Local LLM；
- Python；
- 其他工具链；

完成。

---

# 76. 开源项目建议技术栈

建议优先选择：

## 方案 A：C# + .NET + WinUI / WPF

优点：

- Windows 原生；
- 进程管理方便；
- Registry / App 检测方便；
- 文件系统操作成熟；
- JSON 配置成熟；
- 开发 Launcher 类工具非常合适。

推荐：

```text
.NET 10 LTS
WPF
MVVM
System.Text.Json
```

如果重点追求：

> Win10 稳定兼容 + 开发简单

优先考虑：

`WPF + .NET 10 LTS，win-x64 self-contained 发布`

项目必须避免 Windows 11 专属 API，并在真实 Windows 10 22H2 Build 19045 上持续执行启动、切换、托盘、登录启动和发布包验收。

---

# 77. 建议项目代码结构

```text
src/
├── Launcher.App/
│   ├── Views/
│   ├── ViewModels/
│   └── App.xaml
│
├── Launcher.Agent/
│   ├── LoginStartup/
│   ├── ClientWatcher/
│   ├── RouterCoordinator/
│   └── AgentHost/
│
├── Launcher.Core/
│   ├── Models/
│   ├── Services/
│   ├── Interfaces/
│   └── StateMachine/
│
├── Launcher.Runtime/
│   ├── LlamaRuntimeManager/
│   ├── DeviceDetector/
│   ├── RouterProcessManager/
│   ├── ModelProcessManager/
│   └── HealthChecker/
│
├── Launcher.Models/
│   ├── GgufScanner/
│   ├── GgufMetadata/
│   ├── ProfileManager/
│   └── ParameterDefinitions/
│
├── Launcher.Scripts/
│   └── BatGenerator/
│
├── Launcher.ChatGPT/
│   ├── ClientDetector/
│   ├── IntegrationAdapter/
│   ├── BackupManager/
│   └── RecoveryManager/
│
└── Launcher.Tests/
```

---

# 78. 配置文件建议

Launcher 自己的全局配置不要放进 llama.cpp 可执行文件目录中的临时随机位置。

建议：

```text
%LOCALAPPDATA%\ChatGPTLocalLauncher\
```

保存：

```text
settings.json
runtime-state.json
recovery.json
managed-config-snapshot.json
```

模型 Profile 则按用户需求同时保存在：

```text
<llamaRoot>\scripts\profiles\
```

这样模型配置与 llama.cpp Runtime 可以一起迁移。

---

# 79. Profile JSON 示例

```json
{
  "id": "qwen38-opus-27b",
  "displayName": "Qwen3.8-27B Opus v2",
  "modelPath": "../models/Qwen3.8-27B-Opus-Distill-v2-Q4_K_M.gguf",
  "context": 81920,
  "gpuOffload": "all",
  "device": "auto",
  "flashAttention": "on",
  "kCache": "q8_0",
  "vCache": "q8_0",
  "parallel": 1,
  "jinja": true,
  "batchMode": "default",
  "host": "127.0.0.1",
  "port": 8080,
  "alias": "qwen38-opus-27b-80k",
  "extraArgs": [],
  "hardwareFingerprint": {
    "gpu": "AMD Radeon RX 7900 XT",
    "backend": "Vulkan",
    "vramMB": 20480
  }
}
```

---

# 80. 安全边界

默认 llama-server：

```text
127.0.0.1
```

不要默认：

```text
0.0.0.0
```

避免无意暴露给局域网。

V1.0 不默认开放 Remote Access。

---

# 81. 不应实现的危险行为

禁止：

- 自动删除用户官方 ChatGPT 配置；
- 未备份就修改 OpenAI 环境；
- 强制杀死所有 llama-server；
- 强制杀死所有 ChatGPT/Codex；
- 自动修改未知 GGUF；
- 自动覆盖用户手写脚本；
- 在 Local 模式运行时修改当前模型 Profile 并假装立即生效；
- 同时运行 OpenAI 与 Local Provider；
- 热切换本地模型。

---

# 82. V1.0 功能范围

V1.0 必须完成：

1. OpenAI / Local LLM 双模式主页；
2. ChatGPT/Codex 运行检测；
3. 模式互斥；
4. llama.cpp Root 设置；
5. Runtime 验证；
6. llama.cpp Version；
7. Device 扫描；
8. 自动创建 models/scripts/logs；
9. GGUF 扫描；
10. 新模型添加；
11. 模型列表；
12. 模型 Profile；
13. 基础参数选择式 UI；
14. 参数 Tooltip；
15. 编辑 / 保存 / 取消；
16. 恢复默认；
17. 高级参数折叠区；
18. BAT 自动生成；
19. llama-server 自动启动；
20. Launcher 自有 Router 进程树管理；
21. Health Check；
22. Local 模式 ChatGPT/Codex 启动；
23. Local server 停止；
24. 日志；
25. 异常恢复；
26. 当前 Qwen3.8-27B 测试 Profile；
27. 开源 README；
28. License；
29. 基础自动化测试。
30. SelectedMode 与当前模型持久化；
31. 用户登录后台 Agent；
32. 支持绕过 Launcher 直接启动 ChatGPT Desktop；
33. llama.cpp 原生空闲休眠、模型自动重载及 Local 配置保留；
34. OpenAI / Local 共用项目列表、任务历史与上下文索引；
35. ChatGPT 用户级配置的事务式备份、字段级修改与恢复；
36. llama.cpp Router 能力探测、单模型限制与不兼容 Runtime 的明确拦截。
37. 可选调用 `llama-fit-params` 并按模型应用建议；
38. `/models`、`/metrics`、`/slots` 原生状态监控与 Windows CPU/RAM/GPU/显存可用指标；
39. 当前模型原生立即加载与释放；
40. 仅对已验证 llama 下载缓存开放原生清单与删除；
41. Profile 与 llama 已返回的模型详情展示。

---

# 83. V1.1 建议

可增加：

- 自动识别更多 GGUF metadata；
- 更好的分片模型识别；
- 参数兼容性动态检测；
- 更细的跨驱动 GPU 指标兼容；
- 基于更多模型 metadata 的解释性提示（不得替代 llama 原生建议或用户选择）；
- Profile 导入 / 导出；
- Launcher 自动更新。

---

# 84. V1.2 / V2.0 可考虑

以后再增加：

- 自动下载 llama.cpp；
- 自动选择 CUDA / Vulkan / ROCm；
- 私有 / gated Hugging Face 模型与凭据管理；
- 完整 Model Library 与镜像管理；
- mmproj 管理；
- LoRA；
- MTP；
- Speculative Decoding；
- 多 GPU；
- Remote llama-server；
- Web Search / MCP 自动集成；
- benchmark；
- VRAM 预测；
- Launcher 自有参数自动调优。

---

# 85. 第一阶段开发顺序

推荐 Codex 按以下顺序开发：

## Phase 0：集成可行性闭环

- 检测 ChatGPT Desktop 安装与进程；
- 审计当前用户级 Codex 配置，但不读取或复制凭据；
- 验证 `model_catalog_json`、内置 Provider 的回环重定向、透明安全入口与模式切换；
- 验证 llama.cpp Router 的 `--models-dir`、`--models-preset`、`--models-max 1`、`--models-autoload`；
- 验证 Responses API、历史任务可见性、直接启动与切回 OpenAI；
- 先形成可回滚 PoC，再扩展完整 UI。

## Phase 1：基础壳与配置

- WPF / WinUI 项目；
- 首页；
- 全局设置；
- Runtime Root；
- JSON Persistence。

## Phase 2：llama.cpp Runtime

- Runtime 检测；
- Version；
- Device；
- models/scripts/logs。

## Phase 3：模型扫描

- GGUF scanner；
- 分片；
- mmproj 排除；
- 模型列表。

## Phase 4：Model Profile

- 参数模型；
- Basic UI；
- Advanced UI；
- Tooltip；
- Edit / Save / Cancel；
- 恢复此模型已保存的默认参数。

## Phase 5：Script Generator

- Profile -> BAT；
- 相对路径；
- overwrite 策略；
- 手工 BAT 测试。

## Phase 6：Server Manager

- Router 与模型子进程；
- 精确 PID / ownership；
- stdout/stderr；
- health；
- unload / stop；
- Background Agent。

## Phase 7：ChatGPT / Codex 集成

- 审计 Ollama 当前 launch ChatGPT 实现；
- 审计当前 Codex Desktop；
- 实现 Adapter；
- OpenAI；
- Local；
- backup；
- restore；
- crash recovery。

## Phase 8：整体验收

- OpenAI -> Local；
- Local -> OpenAI；
- 项目访问；
- 模型切换；
- 异常关机；
- Runtime 移动；
- 不同 GPU。

---

# 86. 必须优先验证的高风险点

以下不能仅凭需求文档假设成立，Codex 开发时必须实际验证：

1. 当前 ChatGPT/Codex Windows 客户端的 Local Provider 接入机制；
2. 当前 Ollama `launch chatgpt` 的实际实现；
3. OpenAI 与 Local 模式切换时项目列表 / Repository 的保留行为；
4. Provider 切换涉及哪些配置文件 / 数据库 / model catalog；
5. ChatGPT 客户端更新后兼容性；
6. Responses API 与 llama-server 的当前兼容性；
7. Tool Calling / apply_patch / shell 的 Local LLM 兼容性；
8. GitHub 网络访问方式；
9. 本地模型模式下 Codex 是否会暴露不应使用的 OpenAI 功能；
10. Windows 10 环境中的客户端进程识别与启动方式。
11. ChatGPT Desktop 关闭后 Router 是否能可靠卸载模型并释放显存；
12. 用户绕过 Launcher 直接启动 ChatGPT Desktop 时的首次请求超时与模型预热策略；
13. 当前 llama.cpp Router 参数与 preset key 的版本差异。
14. 严格模型模板能否接受 Codex 在会话中追加的 `system/developer` 消息；若不能，可从该 GGUF 自身的模板元数据生成模型专用副本并通过 llama.cpp 原生 `--chat-template-file` 加载，或报告模型/Runtime 不兼容；不得由 Launcher 改写网络消息或过滤工具来伪装兼容。

必须把这些代码封装在：

`ChatGPTIntegrationAdapter`

避免影响其余模块。

---

# 87. 测试要求

## Unit Test

覆盖：

- Profile 序列化；
- CLI mapping；
- BAT generator；
- Context mapping；
- KV mapping；
- Runtime path；
- GGUF file classification；
- state machine。

## Integration Test

覆盖：

- llama-server 启动；
- health；
- stop；
- invalid GGUF；
- port occupied；
- runtime missing；
- ChatGPT running；
- hardware changed。

## Manual Test

至少使用：

- AMD + Vulkan；
- NVIDIA + CUDA 或 Vulkan；
- Windows 10；
- Windows 11。

---

# 88. 关键验收场景

## 场景 A：第一次安装

```text
启动 Launcher
↓
选择 llama.cpp
↓
验证
↓
自动创建目录
↓
扫描 models
↓
添加模型
↓
设置参数
↓
生成 BAT
↓
Local Launch
↓
模型正常运行
```

---

## 场景 B：OpenAI

```text
Launcher
↓
OpenAI
↓
官方 ChatGPT
↓
在线模型正常
↓
无 Local LLM
```

---

## 场景 C：Local

```text
关闭 ChatGPT
↓
Launcher
↓
Local LLM
↓
选择模型
↓
Launch
↓
llama-server
↓
health OK
↓
ChatGPT Local
```

---

## 场景 D：切模型

```text
Model A Running
↓
无法选择 Model B
↓
关闭客户端
↓
停止 server
↓
重新 Launcher
↓
Model B
```

---

## 场景 E：恢复默认

```text
进入模型设置
↓
Edit
↓
⟳
↓
参数恢复 Default
↓
未保存
↓
Save
↓
Profile + BAT 更新
```

---

## 场景 F：硬件迁移

```text
复制 Runtime 到另一台电脑
↓
Launcher 检测 GPU 不同
↓
提示重新检查参数
↓
原配置不自动强制运行
```

---

# 89. README 应包含

开源 README 至少说明：

- 项目是什么；
- 项目不包含 llama.cpp；
- 用户自行安装 Runtime；
- 用户可自行准备模型，或通过 Launcher 可视化调用 llama.cpp 原生下载；
- 支持 Windows 10/11；
- 使用 llama.cpp；
- 支持 OpenAI / Local 双模式；
- Local 模式风险；
- 安装步骤；
- 添加模型；
- 参数说明；
- License；
- 第三方项目声明；
- llama.cpp / OpenAI / Ollama 均属于各自项目，本项目与其官方关系需明确说明。

---

# 90. 开源 License

开发前应选择明确 License。

建议优先考虑：

```text
MIT
```

或：

```text
Apache-2.0
```

必须根据实际使用的第三方源码 / 参考实现检查兼容性。

如果只是参考 Ollama 使用理念、自己重新实现，不应直接复制不兼容许可源码。

---

# 91. 软件命名

当前文档使用临时项目名：

```text
ChatGPT Launcher
```

开源发布前建议重新确认正式名称。

正式名称应避免暗示：

> 本项目是 OpenAI / ChatGPT 官方产品。

README 与 About 中必须明确：

> 本项目为第三方开源工具。

---

# 92. About 页面

后续建议：

```text
App Name
Version
GitHub
License
llama.cpp Runtime
Runtime Version
Third-party Notices
```

可提供：

`Open Source Licenses`

---

# 93. 最终设计原则

必须长期遵守以下原则：

> **Launcher 管环境与模型。**

> **ChatGPT/Codex 管项目与开发。**

> **V1 只适配集成 Chat、Work、Codex 的 ChatGPT Desktop。**

> **OpenAI 与 Local LLM 永远互斥。**

> **项目可以共享，模型 Provider 不混用。**

> **切换 Provider 必须先退出 ChatGPT/Codex。**

> **关闭客户端不是切换 Provider；平时保留上一次模式。**

> **切换本地模型必须先卸载当前模型。**

> **Local 模式中客户端不能切换模型。**

> **模型参数运行中只读。**

> **参数只能 Edit 后修改。**

> **修改必须 Save 才生效。**

> **恢复默认也必须 Save 才生效。**

> **Profile 是配置源，BAT 是生成物。**

> **能通过下拉/开关选择的参数，不让普通用户手工输入。**

> **每个参数都应该告诉用户“它是什么、调整会怎样”。**

> **不得硬编码特定 GPU、Runtime 路径或模型。**

> **不得因 Local 模式破坏 OpenAI 官方环境。**

> **项目、任务历史和上下文共享；推理传输、账户凭据、Provider 和模型视图隔离。为共享原生历史，Local 保留的官方账户外壳不等于凭据参与本地推理。**

> **直接启动 ChatGPT Desktop 必须按当前持久化配置工作。**

> **进入 OpenAI 时应感觉 Local LLM 从未存在。**

> **进入 Local LLM 时应感觉 OpenAI 在线模型不存在。**

> **开源版本必须优先考虑跨设备、跨显卡、跨 llama.cpp 版本兼容。**

---

# 94. 给 Codex 的开发任务总指令

Codex 在开始实际编码前应首先：

1. 完整阅读本需求文档；
2. 不擅自删除、合并或弱化已明确的核心需求；
3. 先建立模块化架构；
4. 不把当前 RX 7900 XT、E 盘路径、Qwen 模型写死；
5. 将 ChatGPT/Codex Integration 作为独立 Adapter；
6. 在实现 ChatGPT 切换前，先审计当前 Ollama `launch chatgpt` 和当前 Codex Desktop 行为；
7. 所有可能修改官方 OpenAI 环境的操作必须先备份；
8. 先完成最小 ChatGPT Desktop + llama.cpp Router 集成 PoC，再并行完善 Runtime、Scanner、Profile、BAT 与 Server Manager；
9. 每完成一个 Phase 做独立测试；
10. 保证任意阶段失败均可恢复到原始 OpenAI 官方环境；
11. 保持项目开源可维护性；
12. 所有 UI 参数应通过 ParameterDefinition 驱动，避免大量硬编码控件；
13. 编写 README、License、Third-party Notices；
14. 最终输出可在 Windows 10 22H2 x64 与 Windows 11 x64 运行的 self-contained 发布包；
15. 实现登录启动的 Background Agent，使 Launcher UI 不运行时仍可维持当前模式；
16. 把 Provider 切换实现为可恢复事务，只修改明确归属 Launcher 的字段与文件；
17. 不得把关闭 ChatGPT Desktop 当作切回 OpenAI。

---

# 95. V1.0 完成定义（Definition of Done）

只有同时满足以下条件才能认为 V1.0 完成：

- [ ] Windows 10 22H2 Build 19045 x64 可运行；
- [ ] Windows 11 可运行；
- [ ] 可设置 llama.cpp Root；
- [ ] 可验证 llama-server；
- [ ] 可识别至少一种 AMD Runtime；
- [ ] 可识别至少一种 NVIDIA Runtime；
- [ ] 可扫描 GGUF；
- [ ] 可加入本地模型；
- [ ] 可管理多个模型；
- [ ] 每个模型配置独立；
- [ ] Basic 参数全部选择式；
- [ ] 参数 Tooltip 完整；
- [ ] 高级设置可折叠；
- [ ] Edit / Save / Cancel 正常；
- [ ] 当前参数可保存为当前模型专用默认，并可正常恢复；
- [ ] Profile 正常持久化；
- [ ] BAT 正常生成；
- [ ] BAT 可脱离 Launcher 独立启动；
- [ ] Launcher 可启动 llama-server；
- [ ] Health Check 正常；
- [ ] PID 管理正常；
- [ ] Local LLM 运行时配置锁定；
- [ ] 本地模型不能热切换；
- [ ] OpenAI / Local 不能热切换；
- [ ] OpenAI 模式正常；
- [ ] Local LLM 模式正常；
- [ ] 模式切换不破坏官方账号环境；
- [ ] 两种模式均可访问用户真实项目目录、项目列表、任务历史和上下文；
- [ ] Local 模式退出后模型正常卸载；
- [ ] Local 模式退出后显存释放；
- [ ] Local 模式退出后不恢复 OpenAI 配置；
- [ ] SelectedMode 和当前本地模型在重启后保持；
- [ ] Launcher UI 未运行时，直接启动 ChatGPT Desktop 仍按当前模式工作；
- [ ] Local Router 空闲时不加载模型、不占用 GPU；
- [ ] Local 模式只显示一个当前本地模型，OpenAI 模式不显示本地模型；
- [ ] 切回 OpenAI 后官方账户、模型、工具、项目与历史均正常；
- [ ] 异常切换可恢复；
- [ ] 日志完整；
- [ ] README 完整；
- [ ] License 明确；
- [ ] GitHub 开源仓库结构清晰。

---

# 96. 结论

本项目的最终方向不是为某一台电脑制作一个专用启动脚本，而是：

> **打造一个 Windows 平台通用、开源、可迁移的 llama.cpp 本地模型管理器与 ChatGPT/Codex 双环境启动器。**

它应该让不了解 llama.cpp 命令行的用户，也能够：

```text
选择 Runtime
↓
扫描模型
↓
图形化配置
↓
一键启动
↓
进入 ChatGPT/Codex Local LLM
```

同时又确保：

```text
OpenAI
与
Local LLM
```

在使用体验和运行状态上互不干扰。

项目最重要的产品价值不是“替用户写一条 BAT”，而是：

> **把 llama.cpp 的复杂参数、模型管理、运行状态和 ChatGPT/Codex 环境切换，封装成一个普通用户能够理解、控制、恢复并安全使用的图形化工具。**
