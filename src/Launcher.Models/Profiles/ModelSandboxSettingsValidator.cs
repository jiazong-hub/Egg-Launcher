namespace Launcher.Models.Profiles;

public static class ModelSandboxSettingsValidator
{
    public static IReadOnlyList<string> Validate(ModelSandboxSettings? settings)
    {
        if (settings is null)
        {
            return Array.Empty<string>();
        }

        var errors = new List<string>();
        if (!Enum.IsDefined(settings.NetworkAccess))
        {
            errors.Add("沙箱联网权限选项无效。");
        }

        if (settings.NetworkCompatibilityEnabled && settings.NetworkAccess != SandboxNetworkAccess.Full)
        {
            errors.Add("联网兼容模式需要先开启允许命令联网。");
        }

        if (settings.AdditionalPaths is null)
        {
            errors.Add("沙箱路径权限列表无效。");
            return errors;
        }

        var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (settings.AdditionalPaths.Count(p => p?.UseAsGradleUserHome == true) > 1)
            errors.Add("只能指定一个 Gradle 用户目录。");
        foreach (var permission in settings.AdditionalPaths)
        {
            if (permission is null || string.IsNullOrWhiteSpace(permission.Path))
            {
                errors.Add("沙箱路径不能为空。");
                continue;
            }

            if (permission.Path.IndexOfAny(['\0', '\r', '\n']) >= 0)
            {
                errors.Add("沙箱路径包含不支持的字符。");
                continue;
            }

            try
            {
                if (permission.UseAsGradleUserHome && !permission.AllowWrite)
                    errors.Add("Gradle 用户目录必须允许读写。");
                if (!Path.IsPathFullyQualified(permission.Path))
                {
                    errors.Add($"沙箱路径必须是绝对路径：{permission.Path}");
                    continue;
                }

                var fullPath = Path.GetFullPath(permission.Path);
                var root = Path.GetPathRoot(fullPath);
                if (!string.IsNullOrEmpty(root)
                    && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"沙箱路径不能直接授权整个磁盘或共享根目录：{permission.Path}");
                    continue;
                }

                if (!normalizedPaths.Add(Path.TrimEndingDirectorySeparator(fullPath)))
                {
                    errors.Add($"沙箱路径重复：{permission.Path}");
                }
            }
            catch (Exception exception) when (exception is ArgumentException
                                               or NotSupportedException
                                               or IOException)
            {
                errors.Add($"沙箱路径格式无效：{permission.Path}");
            }
        }

        return errors;
    }
}
