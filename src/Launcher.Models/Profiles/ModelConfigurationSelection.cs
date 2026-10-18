namespace Launcher.Models.Profiles;

public sealed record PreferredConfigurationState(string? Path, bool Exists, string? Error = null)
{
    public string? Name => Path is not null && (Exists || Error is not null)
        ? System.IO.Path.GetFileNameWithoutExtension(Path) : null;
}

public sealed record ModelConfigurationChoice(string Path)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>File selection only: never parses parameters, probes capabilities or changes files.</summary>
public static class ModelConfigurationSelection
{
    public static PreferredConfigurationState Inspect(ModelProfile model, string root)
    {
        if (string.IsNullOrWhiteSpace(model.PreferredConfigurationPath)) return new(null, false);
        try
        {
            var path = RequireFile(model.PreferredConfigurationPath, model, root);
            return new(path, true);
        }
        catch (FileNotFoundException) { return new(model.PreferredConfigurationPath, false); }
        catch (DirectoryNotFoundException) { return new(model.PreferredConfigurationPath, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            // An unreadable or invalid selection is not evidence that the user deleted it.
            return new(model.PreferredConfigurationPath, false, ex.Message);
        }
    }

    public static string RequireFile(string path, ModelProfile model, string root)
    {
        var full = ModelConfigurationFile.ValidatePath(path, model, Path.GetFullPath(root));
        if ((File.GetAttributes(full) & FileAttributes.Directory) != 0)
            throw new InvalidDataException("所选配置不是文件 / The selected configuration is not a file.");
        return full;
    }

    public static IReadOnlyList<ModelConfigurationChoice> ListFiles(ModelProfile model, string root)
    {
        root = Path.GetFullPath(root);
        var directory = ModelConfigurationFile.GetDirectory(model, root);
        ModelConfigurationFile.ValidatePath(Path.Combine(directory, ".selection.json"), model, root);
        string[] files;
        try { files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly); }
        catch (DirectoryNotFoundException) { return []; }
        var choices = new List<ModelConfigurationChoice>();
        foreach (var path in files.Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)))
        {
            try { choices.Add(new(RequireFile(path, model, root))); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (InvalidDataException) { /* Links and out-of-directory entries are not selectable. */ }
        }
        return choices.OrderBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
