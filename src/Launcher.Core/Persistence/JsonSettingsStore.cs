using System.Text.Json;
using Launcher.Core.Configuration;
using Launcher.Core.Security;

namespace Launcher.Core.Persistence;

public sealed class JsonSettingsStore : ISettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public async Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new LauncherSettings();
            }

            await using var stream = new FileStream(
                _settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var settings = await JsonSerializer.DeserializeAsync<LauncherSettings>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);

            return Validate(settings);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"设置文件不是有效 JSON：{_settingsPath}。{BuildBackupRecoveryHint()}",
                exception);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                $"设置文件未通过校验：{_settingsPath}。{BuildBackupRecoveryHint()}",
                exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;

        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)
                ?? throw new InvalidOperationException("设置文件必须位于一个目录中。");

            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            PrivateFilePermissions.HardenFile(temporaryPath);

            if (File.Exists(_settingsPath))
            {
                File.Copy(_settingsPath, _settingsPath + ".bak", overwrite: true);
                PrivateFilePermissions.HardenFile(_settingsPath + ".bak");
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Preserve the original settings result; temp files are never loaded.
                }
            }

            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private string BuildBackupRecoveryHint() => File.Exists(_settingsPath + ".bak")
        ? $"检测到备份 {_settingsPath}.bak；它可能属于上一次模式，Launcher 不会自动套用，请确认模式后再手动恢复。"
        : "未检测到可用的 .bak 备份。";

    private static LauncherSettings Validate(LauncherSettings? settings)
    {
        if (settings is null)
        {
            throw new InvalidDataException("设置文件为空。");
        }

        if (settings.SchemaVersion != LauncherSettings.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"不支持的设置版本 {settings.SchemaVersion}，当前版本为 {LauncherSettings.CurrentSchemaVersion}。");
        }

        if (!Enum.IsDefined(settings.SelectedMode))
        {
            throw new InvalidDataException($"设置中的模式值无效：{settings.SelectedMode}。");
        }

        if (settings.RouterPort is < 1 or > 65535)
        {
            throw new InvalidDataException("设置中的 Router 端口必须在 1 到 65535 之间。");
        }

        if (!string.IsNullOrWhiteSpace(settings.LlamaRoot)
            && !Path.IsPathFullyQualified(settings.LlamaRoot))
        {
            throw new InvalidDataException("设置中的 llama.cpp Runtime Root 必须是绝对路径。");
        }

        if (settings.SelectedMode == ProviderMode.Local)
        {
            if (string.IsNullOrWhiteSpace(settings.SelectedModelId))
            {
                throw new InvalidDataException("Local 模式缺少已选择的模型 ID。");
            }

            if (string.IsNullOrWhiteSpace(settings.LlamaRoot))
            {
                throw new InvalidDataException("Local 模式缺少 llama.cpp Runtime Root。");
            }
        }

        return settings;
    }
}
