using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
                await stream.DisposeAsync().ConfigureAwait(false);
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
                // Registered models remain manageable when a formerly selected attachment goes
                // missing. Runtime intent is read from the user document and checked at startup.
                var validationProfile = profile.ConfigurationInitialized
                    ? RegistrationValidationProfile(profile)
                    : profile with { ChatTemplateRelativePath = null, DefaultParameters = null };
                var errors = ModelProfileValidator.Validate(validationProfile, root, validateConversationLimits: false, requireRuntimeEvidence: false);
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

                if (!profile.ConfigurationInitialized)
                {
                    await SaveRegistrationAsync(root, profile, cancellationToken).ConfigureAwait(false);
                    profile = JsonSerializer.Deserialize<ModelProfile>(await File.ReadAllTextAsync(Path.Combine(profilesDirectory, profile.Id + ".json"), cancellationToken).ConfigureAwait(false), SerializerOptions)!;
                }
                try
                {
                    profile = await ModelConfigurationFile.ReadAsync(profile.PreferredConfigurationPath ?? "", profile, root, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    profile = profile with { ConfigurationError = exception.Message };
                    diagnostics.Add($"{profile.DisplayName}：预选配置不可用 / Configuration unavailable: {exception.Message}");
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

        return profile;
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

        return profile;
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

        // Missing user configuration files are never recreated from legacy backups.
        await Task.CompletedTask.ConfigureAwait(false);
        return 0;
    }

    public Task SaveRegistrationAsync(string runtimeRoot, ModelProfile profile, CancellationToken cancellationToken = default)
        => SaveAsync(runtimeRoot, profile, cancellationToken, registrationOnly: true);

    // Historical runtime fields in a registration must not hide a model after a type change.
    // The selected parameter document remains subject to full validation on load/save/startup.
    private static ModelProfile RegistrationValidationProfile(ModelProfile profile) => profile with
    {
        RuntimeParameters = new(),
        CustomParameters = new(),
        Ngram = new(),
        MtpEnabled = false,
        VisionEnabled = false,
        ChatTemplateRelativePath = null,
        DefaultParameters = null,
        MoeExpertPlacement = null,
        CpuMoeLayers = null,
    };

    public async Task<ModelProfile> ReadRegistrationAsync(string runtimeRoot, ModelProfile expected, CancellationToken token = default)
    {
        var root = Path.GetFullPath(runtimeRoot);
        ModelConfigurationFile.GetDirectory(expected, root);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(GetProfilesDirectory(root), expected.Id + ".json"), token).ConfigureAwait(false);
        return ParseRegistration(bytes, expected);
    }

    private static ModelProfile ParseRegistration(byte[] bytes, ModelProfile expected)
    {
        var registration = JsonSerializer.Deserialize<ModelProfile>(bytes, SerializerOptions)
            ?? throw new InvalidDataException("模型登记为空 / Empty model registration.");
        if (registration.Id != expected.Id || !string.Equals(registration.ModelRelativePath, expected.ModelRelativePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模型登记已改变，请重新打开管理页面 / Model binding changed; reopen management.");
        if (!Enum.IsDefined(registration.ModelType) || !Enum.IsDefined(registration.ModelTypeSource))
            throw new InvalidDataException("模型类型登记无效 / Invalid registered model type.");
        return registration;
    }

    /// <summary>Changes registration metadata only. Never writes, creates or deletes a parameter document.</summary>
    public async Task<ModelProfile> UpdateModelTypeAsync(string runtimeRoot, ModelProfile expected, ModelType type,
        Func<bool> canChange, CancellationToken token = default)
    {
        if (type is not (ModelType.Dense or ModelType.MoE)) throw new InvalidDataException("请选择 Dense 或 MoE / Select Dense or MoE.");
        var root = Path.GetFullPath(runtimeRoot);
        ModelConfigurationFile.GetDirectory(expected, root); // Validate the ID before constructing a registration path.
        var path = Path.Combine(GetProfilesDirectory(root), expected.Id + ".json");
        if (!canChange()) throw new InvalidOperationException("请先停止本地服务和检测 / Stop local services and detection first.");
        var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var registration = ParseRegistration(bytes, expected);
        if (registration.ModelType != expected.ModelType)
            throw new IOException("模型类型已被其他操作更改，请重新打开管理页面 / Model type changed elsewhere; reopen management.");
        if (registration.ModelType == type) return registration;
        var updated = registration with { ModelType = type, ModelTypeSource = ModelTypeSource.UserSelected };
        var errors = ModelProfileValidator.Validate(RegistrationValidationProfile(updated), root, validateConversationLimits: false, requireRuntimeEvidence: false);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        var json = JsonNode.Parse(bytes)?.AsObject() ?? throw new InvalidDataException("模型登记格式无效 / Invalid model registration.");
        SetMetadata(nameof(ModelProfile.ModelType), (int)type);
        SetMetadata(nameof(ModelProfile.ModelTypeSource), (int)ModelTypeSource.UserSelected);
        if (!canChange()) throw new InvalidOperationException("本地服务状态已改变，未保存类型 / Local service state changed; type was not saved.");
        if (ModelConfigurationFile.Digest(path) != digest) throw new IOException("模型登记已被外部修改，请重试 / Registration changed outside the launcher.");
        await ModelConfigurationFile.WriteAtomicallyAsync(path, json.ToJsonString(SerializerOptions) + Environment.NewLine, token).ConfigureAwait(false);
        return updated;

        void SetMetadata(string name, int value)
        {
            var matches = json.Select(pair => pair.Key).Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("模型登记包含重复字段 / Duplicate registration fields.");
            json[matches.FirstOrDefault() ?? name] = value;
        }
    }

    /// <summary>Changes the presentation name only; identity, configurations and resource evidence remain untouched.</summary>
    public async Task<ModelProfile> UpdateDisplayNameAsync(string runtimeRoot, ModelProfile expected, string name,
        Func<bool> canChange, CancellationToken token = default)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
            throw new InvalidDataException("显示名称不能为空或包含控制字符 / Enter a display name without control characters.");
        var root = Path.GetFullPath(runtimeRoot);
        ModelConfigurationFile.GetDirectory(expected, root);
        var path = Path.Combine(GetProfilesDirectory(root), expected.Id + ".json");
        if (!canChange()) throw new InvalidOperationException("请先停止本地服务和检测 / Stop local services and detection first.");
        var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var registration = ParseRegistration(bytes, expected);
        if (registration.DisplayName != expected.DisplayName)
            throw new IOException("显示名称已被其他操作更改，请重新打开管理页面 / Display name changed elsewhere; reopen management.");
        if (registration.DisplayName == name) return registration;
        var json = JsonNode.Parse(bytes)?.AsObject() ?? throw new InvalidDataException("模型登记格式无效 / Invalid model registration.");
        var matches = json.Select(pair => pair.Key).Where(key => key.Equals(nameof(ModelProfile.DisplayName), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("模型登记包含重复字段 / Duplicate registration fields.");
        json[matches.FirstOrDefault() ?? nameof(ModelProfile.DisplayName)] = name;
        if (!canChange()) throw new InvalidOperationException("本地服务状态已改变，未保存名称 / Local service state changed; name was not saved.");
        if (ModelConfigurationFile.Digest(path) != digest) throw new IOException("模型登记已被外部修改，请重试 / Registration changed outside the launcher.");
        await ModelConfigurationFile.WriteAtomicallyAsync(path, json.ToJsonString(SerializerOptions) + Environment.NewLine, token).ConfigureAwait(false);
        return registration with { DisplayName = name };
    }

    /// <summary>Updates only the next-launch pointer; parameter documents and other metadata are untouched.</summary>
    public async Task<ModelProfile> UpdatePreferredConfigurationAsync(string runtimeRoot, ModelProfile expected, string selectedPath,
        Func<bool> canChange, CancellationToken token = default)
    {
        var root = Path.GetFullPath(runtimeRoot);
        ModelConfigurationFile.GetDirectory(expected, root);
        if (!canChange()) throw new InvalidOperationException("模型正在使用，不能更改启动配置 / The model is in use; startup configuration cannot be changed.");
        var path = Path.Combine(GetProfilesDirectory(root), expected.Id + ".json");
        var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var registration = ParseRegistration(bytes, expected);
        selectedPath = ModelConfigurationSelection.RequireFile(selectedPath, registration, root);
        if (registration.ConfigurationInitialized
            && string.Equals(registration.PreferredConfigurationPath, selectedPath, StringComparison.OrdinalIgnoreCase))
            return registration;
        var json = JsonNode.Parse(bytes)?.AsObject() ?? throw new InvalidDataException("模型登记格式无效 / Invalid model registration.");
        SetField(nameof(ModelProfile.PreferredConfigurationPath), JsonValue.Create(selectedPath));
        SetField(nameof(ModelProfile.ConfigurationInitialized), JsonValue.Create(true));
        if (!canChange()) throw new InvalidOperationException("模型运行状态已改变，未更改启动配置 / Model state changed; startup selection was not saved.");
        ModelConfigurationSelection.RequireFile(selectedPath, registration, root);
        if (ModelConfigurationFile.Digest(path) != digest) throw new IOException("模型登记已被修改，请重试 / Model registration changed; retry.");
        await ModelConfigurationFile.WriteAtomicallyAsync(path, json.ToJsonString(SerializerOptions) + Environment.NewLine, token).ConfigureAwait(false);
        return registration with { PreferredConfigurationPath = selectedPath, ConfigurationInitialized = true };

        void SetField(string name, JsonNode? value)
        {
            var matches = json.Select(pair => pair.Key).Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("模型登记包含重复字段 / Duplicate registration fields.");
            json[matches.FirstOrDefault() ?? name] = value;
        }
    }

    public async Task SaveAsync(
        string runtimeRoot,
        ModelProfile profile,
        CancellationToken cancellationToken = default, bool registrationOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentNullException.ThrowIfNull(profile);
        var root = Path.GetFullPath(runtimeRoot);
        var validationProfile = registrationOnly
            ? profile.ConfigurationInitialized
                ? RegistrationValidationProfile(profile)
                : profile with { ChatTemplateRelativePath = null, DefaultParameters = null }
            : profile;
        var errors = ModelProfileValidator.Validate(validationProfile, root, validateConversationLimits: !registrationOnly, requireRuntimeEvidence: !registrationOnly);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        var modelPath = Path.GetFullPath(Path.Combine(root, profile.ModelRelativePath));
        if (!registrationOnly && !File.Exists(modelPath))
        {
            throw new FileNotFoundException("Profile 指向的 GGUF 主模型不存在。", modelPath);
        }

        var existingPath = Path.Combine(GetProfilesDirectory(root), profile.Id + ".json");
        if (File.Exists(existingPath))
        {
            var existing = JsonSerializer.Deserialize<ModelProfile>(await File.ReadAllTextAsync(existingPath, cancellationToken).ConfigureAwait(false), SerializerOptions);
            if (existing?.ResourceCatalogInitialized == true) profile = profile with { ResourceCatalogInitialized = true };
            if (!profile.ConfigurationInitialized && existing?.ConfigurationInitialized == true)
                profile = profile with { ConfigurationInitialized = true, PreferredConfigurationPath = existing.PreferredConfigurationPath };
        }
        if (!profile.ConfigurationInitialized)
        {
            var initial = ModelConfigurationFile.InitialPath(profile, root);
            var suffix = 2;
            while (File.Exists(initial)) initial = Path.Combine(Path.GetDirectoryName(initial)!, $"未命名-{suffix++}.json");
            await ModelConfigurationFile.WriteInitialAsync(initial, profile, root, cancellationToken).ConfigureAwait(false);
            profile = profile with { ConfigurationInitialized = true, PreferredConfigurationPath = initial };
        }
        if (!registrationOnly && profile.PreferredConfigurationPath is { } configurationPath)
            await ModelConfigurationFile.WriteAsync(configurationPath, profile, root, cancellationToken, requireExisting: true).ConfigureAwait(false);
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
            File.Move(temporaryPath, path, overwrite: true);

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
        var errors = ModelProfileValidator.Validate(profile with { RuntimeParameters = new(), CustomParameters = new(), Ngram = new() }, root, validateConversationLimits: false);
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
