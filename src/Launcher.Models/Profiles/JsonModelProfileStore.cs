using System.Text;
using System.Text.Json;
using Launcher.Models.Scanning;

namespace Launcher.Models.Profiles;

public sealed class JsonModelProfileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<ModelProfileLoadResult> LoadAsync(
        string runtimeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        var root = Path.GetFullPath(runtimeRoot);
        var profilesDirectory = GetProfilesDirectory(root);
        if (!Directory.Exists(profilesDirectory))
        {
            return new ModelProfileLoadResult(Array.Empty<ModelProfile>(), Array.Empty<string>());
        }

        var profiles = new List<ModelProfile>();
        var diagnostics = new List<string>();
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(profilesDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var profile = await JsonSerializer.DeserializeAsync<ModelProfile>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                if (profile is null)
                {
                    diagnostics.Add($"{Path.GetFileName(path)}：文件为空。");
                    continue;
                }

                if (!TryMigrateLegacyContextShiftArguments(profile, out var migratedProfile, out var migrationError))
                {
                    diagnostics.Add(
                        $"{Path.GetFileName(path)}：旧版 --context-shift/--no-context-shift 参数无法无歧义迁移（{migrationError}）；未加载 Profile，源文件保留原样。");
                    continue;
                }

                profile = migratedProfile;

                if (profile.SchemaVersion is >= 1 and < ModelProfile.CurrentSchemaVersion)
                {
                    // V1 did not record provenance. Treat it as a user-owned local file so
                    // cache deletion can never become enabled by an unsafe guess. V1/V2 did
                    // not record a per-model compaction reserve, so migrate to a conservative
                    // context-relative value (32K and above defaults to 8K).
                    var previousSchemaVersion = profile.SchemaVersion;
                    var migratedReserve = previousSchemaVersion <= 3
                        ? ModelProfile.CalculateInitialCompactionSafetyReserve(profile.ContextSize)
                        : profile.CompactionSafetyReserve;
                    var migratedDefaults = profile.DefaultParameters is null
                        ? null
                        : previousSchemaVersion <= 3
                            ? profile.DefaultParameters with
                            {
                                CompactionSafetyReserve = ModelProfile.CalculateInitialCompactionSafetyReserve(
                                    profile.DefaultParameters.ContextSize),
                            }
                            : profile.DefaultParameters;
                    profile = profile with
                    {
                        SchemaVersion = ModelProfile.CurrentSchemaVersion,
                        ReasoningCapabilityStatus = ReasoningCapabilityStatus.Unknown,
                        SupportedReasoningLevels = [],
                        DefaultReasoningLevel = null,
                        ExposeReasoningEffortInChatGpt = false,
                        ReasoningResponsesVerified = false,
                        ReasoningClientCompatible = null,
                        ReasoningClientExecutablePath = null,
                        SupportsThinkingSwitch = null,
                        DefaultThinkingEnabled = null,
                        ThinkingEnabled = null,
                        ReasoningLevelAliases = new Dictionary<string, string>(),
                        ReasoningCapabilitySignature = null,
                        ReasoningCapabilityCheckedAtUtc = null,
                        ReasoningValidationDetails = "旧版思考验证需要按当前模板重新检测。",

                        SourceKind = previousSchemaVersion == 1
                            ? ModelSourceKind.LocalFile
                            : profile.SourceKind,
                        CompactionSafetyReserve = migratedReserve,
                        DefaultParameters = migratedDefaults,
                        ModelType = previousSchemaVersion <= 3 ? ModelType.Unknown : profile.ModelType,
                        ModelTypeSource = previousSchemaVersion <= 3
                            ? ModelTypeSource.Legacy
                            : profile.ModelTypeSource,
                    };
                }

                profile = NormalizeMtpSafety(profile, root);
                profile = NormalizeVisionSafety(profile, root);

                // Keep old conversation values visible for correction. Structural
                // validation remains mandatory; saving and starting validate all bounds.
                var errors = ModelProfileValidator.Validate(profile, root, validateConversationLimits: false);
                if (errors.Count > 0)
                {
                    diagnostics.Add($"{Path.GetFileName(path)}：{string.Join("；", errors)}");
                    continue;
                }

                if (!identifiers.Add(profile.Id))
                {
                    diagnostics.Add($"{Path.GetFileName(path)}：Profile ID {profile.Id} 重复。");
                    continue;
                }

                if (!aliases.Add(profile.Alias))
                {
                    identifiers.Remove(profile.Id);
                    diagnostics.Add($"{Path.GetFileName(path)}：模型 Alias {profile.Alias} 重复。");
                    continue;
                }

                profiles.Add(profile);
            }
            catch (JsonException exception)
            {
                diagnostics.Add(
                    $"{Path.GetFileName(path)}：JSON 无效（{exception.Message}）。{BuildBackupHint(path)}");
            }
            catch (IOException exception)
            {
                diagnostics.Add(
                    $"{Path.GetFileName(path)}：读取失败（{exception.Message}）。{BuildBackupHint(path)}");
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(
                    $"{Path.GetFileName(path)}：没有读取权限（{exception.Message}）。{BuildBackupHint(path)}");
            }
        }

        return new ModelProfileLoadResult(
            profiles
                .OrderBy(profile => profile.DisplayOrder)
                .ThenBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            diagnostics);
    }

    private static bool TryMigrateLegacyContextShiftArguments(
        ModelProfile profile,
        out ModelProfile migratedProfile,
        out string? error)
    {
        if (!TryMigrateContextShiftArguments(
                profile.ExtraArguments,
                out var profileArguments,
                out var profileContextShiftEnabled,
                out error))
        {
            migratedProfile = profile;
            return false;
        }

        IReadOnlyDictionary<string, string?>? defaultArguments = null;
        bool? defaultContextShiftEnabled = null;
        if (profile.DefaultParameters is not null
            && !TryMigrateContextShiftArguments(
                profile.DefaultParameters.ExtraArguments,
                out defaultArguments,
                out defaultContextShiftEnabled,
                out error))
        {
            migratedProfile = profile;
            return false;
        }

        var migratedDefaults = profile.DefaultParameters is null
            ? null
            : profile.DefaultParameters with
            {
                ContextShiftEnabled = defaultContextShiftEnabled ?? profile.DefaultParameters.ContextShiftEnabled,
                ExtraArguments = defaultArguments!,
            };

        migratedProfile = profile with
        {
            ContextShiftEnabled = profileContextShiftEnabled ?? profile.ContextShiftEnabled,
            ExtraArguments = profileArguments,
            DefaultParameters = migratedDefaults,
        };
        error = null;
        return true;
    }

    private static bool TryMigrateContextShiftArguments(
        IReadOnlyDictionary<string, string?>? extraArguments,
        out IReadOnlyDictionary<string, string?> migratedArguments,
        out bool? contextShiftEnabled,
        out string? error)
    {
        var remainingArguments = new Dictionary<string, string?>(StringComparer.Ordinal);
        var contextShiftValues = new List<bool>();
        var noContextShiftValues = new List<bool>();

        if (extraArguments is not null)
        {
            foreach (var argument in extraArguments)
            {
                var isContextShift = argument.Key.Equals("context-shift", StringComparison.OrdinalIgnoreCase);
                var isNoContextShift = argument.Key.Equals("no-context-shift", StringComparison.OrdinalIgnoreCase);
                if (!isContextShift && !isNoContextShift)
                {
                    remainingArguments[argument.Key] = argument.Value;
                    continue;
                }

                if (!TryParseLegacySwitchValue(argument.Value, out var switchEnabled))
                {
                    migratedArguments = extraArguments;
                    contextShiftEnabled = null;
                    error = $"--{argument.Key} 的值应为空、true 或 false";
                    return false;
                }

                // A positive flag enables context shifting; the negative flag disables it.
                (isContextShift ? contextShiftValues : noContextShiftValues)
                    .Add(isContextShift ? switchEnabled : !switchEnabled);
            }
        }

        if (contextShiftValues.Distinct().Skip(1).Any()
            || noContextShiftValues.Distinct().Skip(1).Any())
        {
            migratedArguments = extraArguments ?? remainingArguments;
            contextShiftEnabled = null;
            error = "同一旧版参数以不同大小写重复且值冲突";
            return false;
        }

        var positiveValue = contextShiftValues.FirstOrDefault();
        var negativeValue = noContextShiftValues.FirstOrDefault();
        if (contextShiftValues.Count > 0
            && noContextShiftValues.Count > 0
            && positiveValue != negativeValue)
        {
            migratedArguments = extraArguments ?? remainingArguments;
            contextShiftEnabled = null;
            error = "同时存在的两个旧版参数表达了相反设置";
            return false;
        }

        migratedArguments = remainingArguments;
        contextShiftEnabled = contextShiftValues.Count > 0
            ? positiveValue
            : noContextShiftValues.Count > 0
                ? negativeValue
                : null;
        error = null;
        return true;
    }

    private static bool TryParseLegacySwitchValue(string? value, out bool enabled)
    {
        if (value is null)
        {
            enabled = true;
            return true;
        }

        if (bool.TryParse(value, out enabled))
        {
            return true;
        }

        enabled = false;
        return false;
    }

    private static ModelProfile NormalizeMtpSafety(ModelProfile profile, string runtimeRoot)
    {
        if (profile.MtpSource == MtpSourceKind.External)
        {
            var verified = profile.MtpCapabilityStatus == MtpCapabilityStatus.Verified
                           && !string.IsNullOrWhiteSpace(profile.MtpDraftModelRelativePath)
                           && !string.IsNullOrWhiteSpace(profile.MtpValidationSignature)
                           && profile.MtpValidatedAtUtc is not null;
            return verified
                ? profile
                : profile with
                {
                    MtpEnabled = false,
                    MtpCapabilityStatus = string.IsNullOrWhiteSpace(profile.MtpDraftModelRelativePath)
                        ? MtpCapabilityStatus.Unknown
                        : MtpCapabilityStatus.ExternalConfigured,
                    MtpValidationSignature = null,
                    MtpValidatedAtUtc = null,
                };
        }

        if (profile.MtpCapabilityStatus is MtpCapabilityStatus.EmbeddedCandidate
            or MtpCapabilityStatus.Verified)
        {
            return profile;
        }

        try
        {
            var modelPath = Path.GetFullPath(Path.Combine(runtimeRoot, profile.ModelRelativePath));
            if (File.Exists(modelPath) && GgufContextMetadataReader.ReadMetadata(modelPath).HasEmbeddedMtp)
            {
                return profile with { MtpCapabilityStatus = MtpCapabilityStatus.EmbeddedCandidate };
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or ArgumentException
                                          or NotSupportedException
                                          or OverflowException)
        {
        }

        return profile.MtpEnabled ? profile with { MtpEnabled = false } : profile;
    }

    private static ModelProfile NormalizeVisionSafety(ModelProfile profile, string runtimeRoot)
    {
        var verified = profile.VisionCapabilityStatus == VisionCapabilityStatus.Verified
                       && !string.IsNullOrWhiteSpace(profile.VisionValidationSignature)
                       && profile.VisionValidatedAtUtc is not null
                       && (profile.VisionSource != VisionSourceKind.External
                           || !string.IsNullOrWhiteSpace(profile.VisionProjectorRelativePath));
        if (verified)
        {
            try
            {
                return VisionValidationFingerprint.IsCurrent(profile, runtimeRoot)
                    ? profile
                    : profile with
                    {
                        VisionEnabled = false,
                        VisionCapabilityStatus = profile.VisionSource == VisionSourceKind.External
                            && !string.IsNullOrWhiteSpace(profile.VisionProjectorRelativePath)
                            ? VisionCapabilityStatus.ExternalConfigured
                            : VisionCapabilityStatus.BuiltInCandidate,
                        VisionValidationSignature = null,
                        VisionValidatedAtUtc = null,
                    };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }

        return profile.VisionEnabled
            ? profile with { VisionEnabled = false }
            : profile;
    }

    public async Task<int> RecreateMissingProfilesFromBackupsAsync(
        string runtimeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        var root = Path.GetFullPath(runtimeRoot);
        var profilesDirectory = GetProfilesDirectory(root);
        if (!Directory.Exists(profilesDirectory))
        {
            return 0;
        }

        var recreatedCount = 0;
        var reservedIdentifiers = new List<ModelProfile>();
        foreach (var backupPath in Directory.EnumerateFiles(
                     profilesDirectory,
                     "*.json.bak",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profilePath = backupPath[..^4];
            if (File.Exists(profilePath))
            {
                continue;
            }

            try
            {
                await using var stream = new FileStream(
                    backupPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var previous = await JsonSerializer.DeserializeAsync<ModelProfile>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                if (previous is null)
                {
                    continue;
                }

                var primaryPath = Path.GetFullPath(Path.Combine(root, previous.ModelRelativePath));
                if (!File.Exists(primaryPath))
                {
                    continue;
                }

                var candidate = new GgufModelCandidate(
                    primaryPath,
                    Path.GetRelativePath(Path.Combine(root, "models"), primaryPath),
                    previous.DisplayName,
                    previous.KnownSizeBytes ?? new FileInfo(primaryPath).Length,
                    previous.KnownShardCount ?? 1,
                    previous.RemoteModelId,
                    previous.RemoteRepositoryId,
                    previous.RemoteQuantization);
                var recreated = ModelProfileFactory.CreateDefault(
                    candidate,
                    root,
                    reservedIdentifiers,
                    detectModelType: false) with
                {
                    Id = previous.Id,
                    Alias = previous.Alias,
                    ShowInModePage = previous.ShowInModePage,
                    DisplayOrder = previous.DisplayOrder,
                    ModelType = previous.SchemaVersion >= 4
                        ? previous.ModelType
                        : ModelType.Unknown,
                    ModelTypeSource = previous.SchemaVersion >= 4
                        ? previous.ModelTypeSource
                        : ModelTypeSource.Legacy,
                };
                await SaveAsync(root, recreated, cancellationToken).ConfigureAwait(false);
                reservedIdentifiers.Add(recreated);
                recreatedCount++;
            }
            catch (Exception exception) when (
                exception is JsonException
                    or IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or ArgumentException)
            {
                // A corrupt backup or missing model remains unmanaged and can be scanned normally.
            }
        }

        return recreatedCount;
    }

    public async Task SaveAsync(
        string runtimeRoot,
        ModelProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentNullException.ThrowIfNull(profile);
        var root = Path.GetFullPath(runtimeRoot);
        var errors = ModelProfileValidator.Validate(profile, root);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        var modelPath = Path.GetFullPath(Path.Combine(root, profile.ModelRelativePath));
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Profile 指向的 GGUF 主模型不存在。", modelPath);
        }

        var profilesDirectory = GetProfilesDirectory(root);
        Directory.CreateDirectory(profilesDirectory);
        var path = Path.Combine(profilesDirectory, profile.Id + ".json");
        var temporaryPath = Path.Combine(
            profilesDirectory,
            $".{profile.Id}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(profile, SerializerOptions) + Environment.NewLine;
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
            if (!File.Exists(path + ".bak"))
            {
                File.Copy(path, path + ".bak", overwrite: false);
            }

        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the original save result; stale temp files are never loaded as profiles.
            }
        }
    }

    public void DeleteLauncherMetadata(string runtimeRoot, ModelProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentNullException.ThrowIfNull(profile);
        var root = Path.GetFullPath(runtimeRoot);
        var errors = ModelProfileValidator.Validate(profile, root, validateConversationLimits: false);
        if (errors.Count > 0 || profile.SourceKind != ModelSourceKind.LlamaCache)
        {
            throw new InvalidDataException("只允许清理来源已经验证的 llama 缓存 Profile。");
        }

        var profilesDirectory = GetProfilesDirectory(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var suffix in new[] { ".json", ".json.bak", ".missing" })
        {
            var path = Path.GetFullPath(Path.Combine(profilesDirectory, profile.Id + suffix));
            if (!path.StartsWith(profilesDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Profile 删除路径逃逸了受管理目录。");
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public void DeleteOwnedProfileFiles(string runtimeRoot, string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId.IndexOfAny(['/', '\\', '\0', '\r', '\n']) >= 0)
        {
            throw new InvalidDataException("Profile ID 不能用于删除受管理文件。");
        }

        var profilesDirectory = GetProfilesDirectory(runtimeRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var suffix in new[] { ".json", ".json.bak", ".missing" })
        {
            var path = Path.GetFullPath(Path.Combine(profilesDirectory, profileId + suffix));
            if (!path.StartsWith(profilesDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Profile 删除路径逃逸了受管理目录。");
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public static string GetProfilesDirectory(string runtimeRoot) =>
        Path.Combine(Path.GetFullPath(runtimeRoot), "scripts", "profiles");

    public bool IsMarkedMissing(string runtimeRoot, string profileId) =>
        File.Exists(GetMissingMarkerPath(runtimeRoot, profileId));

    public void MarkMissing(string runtimeRoot, string profileId)
    {
        var path = GetMissingMarkerPath(runtimeRoot, profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"), new UTF8Encoding(false));
        }
    }

    public void ClearMissingMarker(string runtimeRoot, string profileId)
    {
        var path = GetMissingMarkerPath(runtimeRoot, profileId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string GetMissingMarkerPath(string runtimeRoot, string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId.IndexOfAny(['/', '\\', '\0', '\r', '\n']) >= 0)
        {
            throw new InvalidDataException("Profile ID 不能用于生成受管理文件路径。");
        }

        var profilesDirectory = GetProfilesDirectory(runtimeRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(profilesDirectory, profileId + ".missing"));
        if (!path.StartsWith(profilesDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Profile 状态路径逃逸了受管理目录。");
        }

        return path;
    }

    private static string BuildBackupHint(string path) => File.Exists(path + ".bak")
        ? $"检测到备份 {Path.GetFileName(path)}.bak；为避免套用过期参数，未自动恢复。"
        : "未检测到对应备份。";
}
