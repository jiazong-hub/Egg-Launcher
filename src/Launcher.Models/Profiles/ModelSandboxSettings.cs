namespace Launcher.Models.Profiles;

/// <summary>
/// Codex command-sandbox settings associated with one local model. A null
/// ModelProfile.SandboxSettings means the launcher must leave the current Codex
/// sandbox configuration untouched.
/// </summary>
public sealed record ModelSandboxSettings
{
    public SandboxNetworkAccess NetworkAccess { get; init; } = SandboxNetworkAccess.InheritCodexSettings;

    public IReadOnlyList<SandboxPathPermission> AdditionalPaths { get; init; } =
        Array.Empty<SandboxPathPermission>();
}

public enum SandboxNetworkAccess
{
    InheritCodexSettings,
    Disabled,
    Full,
}

/// <summary>
/// Additional absolute directory access granted to Codex commands for a model.
/// Writable access includes reading the directory.
/// </summary>
public sealed record SandboxPathPermission
{
    public string Path { get; init; } = string.Empty;

    public bool AllowWrite { get; init; }
}
