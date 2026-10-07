# Desktop 配套 CLI 定位修复与审查（2026-10-07）

## 修改范围

- 思考档位客户端兼容性检测和 Agent 的 `--smoke-codex`、`--smoke-codex-tools`、`--smoke-codex-compaction` 共用 `CodexDesktopCliResolver`。
- 使用与客户端启动流程相同的可信 Desktop 安装定位器。读取该安装的 `app/resources/codex.exe`，从 `%LOCALAPPDATA%/OpenAI/Codex/bin` 中选择长度和 SHA256 都一致的运行副本。
- 不再依赖 `%LOCALAPPDATA%/Programs/OpenAI/Codex/bin/codex.exe`，不要求独立安装 CLI，不要求额外沙箱组件，不改动沙箱联网检测。
- 找不到安装、配套文件或一致的缓存时提供对应原因。档位兼容性返回未确认，调用方仍保留模型原生检测结果。
- 验证指纹规则从 3 更新为 4，使此前基于独立 CLI 的旧结论需要重新检测。继续使用现有 CLI 文件指纹，不增加单独的版本管理机制。

## 审查结果

本次变更范围内未发现需要继续修正的问题。重点检查了缓存选择、取消传播、错误处理、模型检测结果保留、旧结果失效和 Agent 三个入口的调用关系。

- 缓存按文件内容匹配，不按目录名或修改日期猜测版本；同长度的旧版本也会被拒绝。
- 不能读取的单个旧缓存不会挡住其他匹配副本；取消不会被转换成普通定位失败。
- 思考兼容性定位失败返回空路径和未确认状态，不将模型的已验证档位改成不支持。
- 三个 Agent 诊断入口共享同一处路径解析，实际对话、工具或压缩测试的执行逻辑不变。

## 验证

- 针对性测试 30/30 通过；完整测试 367/367 通过。
- 软件解决方案 Release 构建成功，0 警告、0 错误。
- 新增 5 项测试：无独立 CLI/无沙箱附属文件时正确定位、拒绝旧缓存与独立 CLI 回退、缺少安装组件、缺少初始化缓存、取消传播。
- 本机实测定位到 `C:/Users/YZK/AppData/Local/OpenAI/Codex/bin/ca9abb0b4d8ac692/codex.exe`，`--version` 为 0.159.0；其路径与此前核实的 Desktop app-server 进程一致。
- 实测读取该组件协议 schema，low、medium、xhigh 的兼容性检查通过。
- 验证程序位于 `artifacts/desktop-cli-check`。此程序不加载模型、不修改 Desktop 配置；构建辅助验证程序时出现 NuGet 在线漏洞数据源无法连接警告，不影响上述运行结果。

7900XT 测试机尚未实机验收；无独立 CLI 路径的场景已由隔离文件测试覆盖。本次未执行会加载本地模型的完整 Agent 对话/工具/压缩冒烟任务。
