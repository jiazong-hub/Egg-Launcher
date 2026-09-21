using System.Windows;
using Launcher.Models.Profiles;

namespace Launcher.App;

public partial class ModelTypeSelectionWindow : Window
{
    public ModelTypeSelectionWindow(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        InitializeComponent();
        UiMotion.AttachWindowEntrance(this);
        ModelNameText.Text = profile.DisplayName;
        DenseRadioButton.IsChecked = profile.ModelType == ModelType.Dense;
        MoeRadioButton.IsChecked = profile.ModelType == ModelType.MoE;
    }

    public ModelType? SelectedModelType { get; private set; }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedModelType = DenseRadioButton.IsChecked == true
            ? ModelType.Dense
            : MoeRadioButton.IsChecked == true
                ? ModelType.MoE
                : null;
        if (SelectedModelType is null)
        {
            MessageBox.Show(
                this,
                AppLanguageManager.Choose("请选择稠密模型或 MoE 模型。", "Select Dense Model or MoE Model."),
                AppLanguageManager.Choose("尚未选择模型类型", "Model Type Not Selected"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
