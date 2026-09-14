using Launcher.ChatGPT.Configuration;
using Launcher.Core.Configuration;

namespace Launcher.Orchestration.ModeSwitch;

public sealed record ModeSwitchResult(
    bool Changed,
    ProviderMode SelectedMode,
    string? SelectedModelId,
    ManagedConfigSnapshot? ConfigTransaction);
