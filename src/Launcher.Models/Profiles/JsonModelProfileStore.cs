using System.Text;
using System.Text.Json;

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

                if (profile.SchemaVersion is 1 or 2)
                {
                    // V1 did not record provenance. Treat it as a user-owned local file so
                    // cache deletion can never become enabled by an unsafe guess. V1/V2 did
                    // not record a per-model compaction reserve, so migrate to a conservative
                    // context-relative value (32K and above defaults to 8K).
                    var migratedReserve = ModelProfile.CalculateInitialCompactionSafetyReserve(
                        profile.ContextSize);
                    var migratedDefaults = profile.DefaultParameters is null
                        ? null
                        : profile.DefaultParameters with
                        {
                            CompactionSafetyReserve = ModelProfile.CalculateInitialCompactionSafetyReserve(
                                profile.DefaultParameters.ContextSize),
                        };
                    profile = profile with
                    {
                        SchemaVersion = ModelProfile.CurrentSchemaVersion,
                        SourceKind = profile.SchemaVersion == 1
                            ? ModelSourceKind.LocalFile
                            : profile.SourceKind,
                        CompactionSafetyReserve = migratedReserve,
                        DefaultParameters = migratedDefaults,
                    };
                }

                var errors = ModelProfileValidator.Validate(profile, root);
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
            profiles.OrderBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(),
            diagnostics);
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
        var errors = ModelProfileValidator.Validate(profile, root);
        if (errors.Count > 0 || profile.SourceKind != ModelSourceKind.LlamaCache)
        {
            throw new InvalidDataException("只允许清理来源已经验证的 llama 缓存 Profile。");
        }

        var profilesDirectory = GetProfilesDirectory(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var suffix in new[] { ".json", ".json.bak" })
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

    public static string GetProfilesDirectory(string runtimeRoot) =>
        Path.Combine(Path.GetFullPath(runtimeRoot), "scripts", "profiles");

    private static string BuildBackupHint(string path) => File.Exists(path + ".bak")
        ? $"检测到备份 {Path.GetFileName(path)}.bak；为避免套用过期参数，未自动恢复。"
        : "未检测到对应备份。";
}
