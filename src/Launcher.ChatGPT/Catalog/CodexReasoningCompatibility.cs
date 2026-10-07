using System.Diagnostics;
using System.Text.Json;
using Launcher.ChatGPT.Processes;

namespace Launcher.ChatGPT.Catalog;

public sealed record CodexReasoningCompatibilityResult(bool? Compatible, string? ExecutablePath, string Detail);

public static class CodexReasoningCompatibility
{
    public static async Task<CodexReasoningCompatibilityResult> CheckAsync(IReadOnlyList<string> levels, CancellationToken token)
    {
        string executable;
        try { executable = await CodexDesktopCliResolver.ResolveAsync(token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new(null, null, "未找到当前 Desktop 配套的 CLI，客户端兼容性未确认。" + exception.Message);
        }
        var directory = Path.Combine(Path.GetTempPath(), "EggLauncher.CodexSchema", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var stamp = new FileInfo(executable);
            var initialStamp = (stamp.Length, stamp.LastWriteTimeUtc);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "app-server", "generate-json-schema", "--out", directory }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); } }
            await output.ConfigureAwait(false); await error.ConfigureAwait(false);
            if (process.ExitCode != 0) return new(null, executable, "当前 Codex 未提供协议 schema，客户端档位兼容性未确认。");
            var path = Path.Combine(directory, "v2", "ModelListResponse.json");
            if (!File.Exists(path)) return new(null, executable, "Codex 模型协议 schema 不可用，客户端档位兼容性未确认。");
            using var schema = JsonDocument.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
            stamp.Refresh();
            if (!stamp.Exists || (stamp.Length, stamp.LastWriteTimeUtc) != initialStamp)
                return new(null, executable, "检测期间 Codex 已更新，请重新检测客户端兼容性。");
            var compatible = Accepts(schema.RootElement, levels);
            return new(compatible, executable, $"Codex {FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "当前安装版"}："
                + (compatible == true ? "协议确认支持这些原始档位值。" : compatible == false ? "协议不支持全部已确认档位，不开放调节。" : "协议能力未确认，不开放调节。"));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(null, executable, "读取 Codex 协议超时，兼容性未确认。"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        { return new(null, executable, "读取 Codex 协议失败，兼容性未确认：" + exception.Message); }
        finally { try { Directory.Delete(directory, recursive: true); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }

    public static bool? Accepts(JsonElement schema, IReadOnlyList<string> levels)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !(schema.TryGetProperty("definitions", out var definitions) || schema.TryGetProperty("$defs", out definitions))
            || definitions.ValueKind != JsonValueKind.Object
            || !definitions.TryGetProperty("ReasoningEffort", out var effort) || effort.ValueKind != JsonValueKind.Object) return null;
        if (effort.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
            return levels.All(level => values.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == level));
        if (effort.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "string"
            && effort.EnumerateObject().All(property => property.Name is "type" or "description" or "title" or "minLength" or "maxLength"))
        {
            var minimum = 0; var maximum = int.MaxValue;
            if (effort.TryGetProperty("minLength", out var min) && (min.ValueKind != JsonValueKind.Number || !min.TryGetInt32(out minimum) || minimum < 0)) return null;
            if (effort.TryGetProperty("maxLength", out var max) && (max.ValueKind != JsonValueKind.Number || !max.TryGetInt32(out maximum) || maximum < minimum)) return null;
            return levels.All(level => CodexReasoningLevels.IsRecognized(level) && level.Length >= minimum && level.Length <= maximum);
        }
        return null;
    }
}
