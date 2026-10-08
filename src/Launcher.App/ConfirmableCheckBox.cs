using System.ComponentModel;
using CheckBox = System.Windows.Controls.CheckBox;

namespace Launcher.App;

/// <summary>Allows an operation to be declined before the checked state or its animations change.</summary>
public sealed class ConfirmableCheckBox : CheckBox
{
    public event EventHandler<CancelEventArgs>? ToggleRequested;

    protected override void OnToggle()
    {
        var request = new CancelEventArgs();
        ToggleRequested?.Invoke(this, request);
        if (!request.Cancel) base.OnToggle();
    }
}
