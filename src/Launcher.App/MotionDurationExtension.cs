using System.Windows;
using System.Windows.Markup;
using System.Windows.Media.Animation;

namespace Launcher.App;

[MarkupExtensionReturnType(typeof(Duration))]
public sealed class MotionDurationExtension : MarkupExtension
{
    public double Milliseconds { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Duration(TimeSpan.FromMilliseconds(
            SystemParameters.ClientAreaAnimation ? Math.Max(0, Milliseconds) : 0));
}
