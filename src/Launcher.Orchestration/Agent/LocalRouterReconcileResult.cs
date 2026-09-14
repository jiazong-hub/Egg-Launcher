using Launcher.Core.Configuration;

namespace Launcher.Orchestration.Agent;

public sealed record LocalRouterReconcileResult(
    ProviderMode SelectedMode,
    LocalRouterReconcileAction Action,
    int? RouterProcessId,
    string? Message);
