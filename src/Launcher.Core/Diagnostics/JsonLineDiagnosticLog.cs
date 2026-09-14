using System.Text;
using System.Text.Json;
using Launcher.Core.Security;

namespace Launcher.Core.Diagnostics;

public sealed class JsonLineDiagnosticLog
{
    private readonly string _path;
    private readonly long _maximumBytes;
    private readonly int _retainedFiles;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _fileHardened;

    public JsonLineDiagnosticLog(
        string path,
        long maximumBytes = 4 * 1024 * 1024,
        int retainedFiles = 2)
    {
        _path = System.IO.Path.GetFullPath(
            string.IsNullOrWhiteSpace(path)
                ? throw new ArgumentException("A diagnostic log path is required.", nameof(path))
                : path);
        _maximumBytes = maximumBytes > 0
            ? maximumBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _retainedFiles = retainedFiles >= 0
            ? retainedFiles
            : throw new ArgumentOutOfRangeException(nameof(retainedFiles));
    }

    public string Path => _path;

    public async Task AppendAsync(
        string level,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentNullException.ThrowIfNull(message);

        var line = JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            level,
            message,
        });

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("The diagnostic log path has no parent directory.");
            Directory.CreateDirectory(directory);
            RotateIfNeeded();
            var shouldHarden = !_fileHardened || !File.Exists(_path);

            await using var stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);
            if (shouldHarden)
            {
                PrivateFilePermissions.HardenFile(_path);
                _fileHardened = true;
            }
            await using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < _maximumBytes)
        {
            return;
        }

        if (_retainedFiles == 0)
        {
            File.Delete(_path);
            _fileHardened = false;
            return;
        }

        var oldest = $"{_path}.{_retainedFiles}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = _retainedFiles - 1; index >= 1; index--)
        {
            var source = $"{_path}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{_path}.{index + 1}", overwrite: true);
            }
        }

        File.Move(_path, $"{_path}.1", overwrite: true);
        _fileHardened = false;
    }
}
