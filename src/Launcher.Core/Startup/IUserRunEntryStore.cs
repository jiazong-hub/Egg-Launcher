namespace Launcher.Core.Startup;

public interface IUserRunEntryStore
{
    string? Read(string valueName);

    void Write(string valueName, string command);

    void Delete(string valueName);
}
