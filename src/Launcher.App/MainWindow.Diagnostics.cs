using Launcher.Core.Diagnostics;
using Launcher.Models.Profiles;

namespace Launcher.App;

public partial class MainWindow
{
    private DiagnosticJournal _diagnostics = DiagnosticJournal.None;

    private void InitializeDiagnostics()
    {
        _diagnostics = new DiagnosticJournal(new ModeAwareJsonLineDiagnosticLog(
            _paths.LauncherConciseLogFile, _paths.LauncherFullLogFile, _settings.DetailedDiagnosticsEnabled));
        _diagnostics.SetDetailed(_settings.DetailedDiagnosticsEnabled);
    }

    private static Dictionary<string, object?> ModelDiagnosticFields(ModelProfile model, string? configuration = null) => new()
    {
        ["modelId"] = model.Id,
        ["configuration"] = System.IO.Path.GetFileName(configuration ?? model.PreferredConfigurationPath),
    };
}
