namespace Launcher.Models.Profiles;

/// <summary>All external chat templates live directly in the Runtime's scripts/templates directory.</summary>
public static class ChatTemplateFile
{
    public static string GetDirectory(string root) => Path.Combine(Path.GetFullPath(root), "scripts", "templates");

    public static string ResolveRelative(string relative, string root)
    {
        if (Path.IsPathRooted(relative)) throw OutsideDirectory(root);
        return ValidatePath(Path.GetFullPath(Path.Combine(root, relative)), root);
    }

    public static string ValidatePath(string path, string root)
    {
        if (!Path.IsPathFullyQualified(path)) throw OutsideDirectory(root);
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), GetDirectory(root), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(name), ".jinja", StringComparison.OrdinalIgnoreCase)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name)))
            throw OutsideDirectory(root);

        var runtime = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var current = full; !string.Equals(current, runtime, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw OutsideDirectory(root);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    private static InvalidDataException OutsideDirectory(string root) => new(
        "Chat Template 路径不允许逃逸或存放在 templates 目录外；模板只能使用以下目录中的直属 .jinja 文件 / "
        + "Use .jinja files directly in this template directory only:" + Environment.NewLine + GetDirectory(root));
}
