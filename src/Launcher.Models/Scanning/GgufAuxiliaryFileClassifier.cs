namespace Launcher.Models.Scanning;

public static class GgufAuxiliaryFileClassifier
{
    private static readonly string[] SpeculativePrefixes = ["mtp-", "eagle3-", "dflash-", "dspark-"];

    public static bool IsMtpCompanion(string path)
    {
        var normalized = path.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                   .Any(segment => string.Equals(segment, "egg-launcher-mtp", StringComparison.OrdinalIgnoreCase))
               || fileName.StartsWith("mtp-", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains(".mtp.", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAuxiliaryModel(string path)
    {
        var fileName = Path.GetFileName(path);
        return IsMtpCompanion(path)
               || fileName.StartsWith("mmproj-", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains(".mmproj.", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains("imatrix", StringComparison.OrdinalIgnoreCase)
               || SpeculativePrefixes.Any(prefix =>
                   fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static string Describe(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith("mmproj-", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".mmproj.", StringComparison.OrdinalIgnoreCase))
        {
            return "mmproj 不是可独立加载的主模型。";
        }

        return IsMtpCompanion(path)
            ? "MTP 辅助 GGUF 不能作为独立主模型添加。"
            : "辅助 GGUF 不能作为独立主模型添加。";
    }
}
