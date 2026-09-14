using System.IO;
using System.Windows;
using System.Windows.Controls;
using Launcher.Models.Profiles;

namespace Launcher.App;

public partial class ProfileEditorWindow : Window
{
    private static readonly IReadOnlyList<Choice<int>> ContextChoices =
    [
        new("尚未设置", 0),
        new("4K (4,096)", 4096),
        new("8K (8,192)", 8192),
        new("16K (16,384)", 16384),
        new("32K (32,768)", 32768),
        new("64K (65,536)", 65536),
        new("80K (81,920)", 81920),
        new("128K (131,072)", 131072),
    ];

    private static readonly IReadOnlyList<Choice<int>> CompactionSafetyReserveChoices =
    [
        new("1K (1,024)", 1024),
        new("2K (2,048)", 2048),
        new("4K (4,096)", 4096),
        new("8K (8,192)", 8192),
        new("12K (12,288)", 12288),
        new("16K (16,384)", 16384),
        new("24K (24,576)", 24576),
        new("32K (32,768)", 32768),
    ];

    private static readonly IReadOnlyList<Choice<string>> GpuLayerChoices =
    [
        new("Auto（由 llama.cpp 决定）", "auto"),
        new("All", "all"),
        new("0（仅 CPU）", "0"),
    ];

    private static readonly IReadOnlyList<Choice<string>> FlashAttentionChoices =
    [
        new("Auto（由 llama.cpp 决定）", "auto"),
        new("On", "on"),
        new("Off", "off"),
    ];

    private static readonly IReadOnlyList<Choice<string>> CacheTypeChoices =
    [
        new("F16（默认）", "f16"),
        new("BF16", "bf16"),
        new("Q8_0", "q8_0"),
        new("Q4_0", "q4_0"),
        new("Q4_1", "q4_1"),
        new("IQ4_NL", "iq4_nl"),
        new("Q5_0", "q5_0"),
        new("Q5_1", "q5_1"),
        new("F32", "f32"),
    ];

    private static readonly IReadOnlyList<Choice<int>> ParallelChoices =
    [
        new("1", 1),
        new("2", 2),
        new("4", 4),
    ];

    private static readonly IReadOnlyList<Choice<int>> BatchChoices =
    [
        new("Default", 0),
        new("128", 128),
        new("256", 256),
        new("512", 512),
        new("1,024", 1024),
        new("2,048", 2048),
    ];

    private static readonly IReadOnlyList<Choice<int>> MicroBatchChoices =
    [
        new("Default", 0),
        new("64", 64),
        new("128", 128),
        new("256", 256),
        new("512", 512),
    ];

    private static readonly IReadOnlyList<Choice<int>> IdleSleepChoices =
    [
        new("30 秒", 30),
        new("1 分钟", 60),
        new("5 分钟", 300),
        new("15 分钟", 900),
        new("禁用", -1),
    ];

    private readonly string _runtimeRoot;
    private readonly ModelProfile _originalProfile;

    public ProfileEditorWindow(ModelProfile profile, string runtimeRoot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        InitializeComponent();
        _originalProfile = profile;
        _runtimeRoot = Path.GetFullPath(runtimeRoot);
        UpdatedProfile = profile;
        RestoreModelDefaultsButton.IsEnabled = profile.DefaultParameters is not null;
        Populate(profile);
    }

    public ModelProfile UpdatedProfile { get; private set; }

    public bool SavedAsModelDefault { get; private set; }

    private void RestoreModelDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = ModelProfileFactory.RestoreModelDefaults(_originalProfile) with
        {
            DisplayName = DisplayNameTextBox.Text.Trim(),
        };
        Populate(defaults);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
        => CompleteSave(saveAsModelDefault: false);

    private void SaveAsModelDefaultButton_Click(object sender, RoutedEventArgs e)
        => CompleteSave(saveAsModelDefault: true);

    private void ContextSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContextRiskTextBlock is null)
        {
            return;
        }

        ContextRiskTextBlock.Visibility = ContextSizeComboBox.SelectedValue is int contextSize
            && contextSize > 0
            && contextSize < ModelProfile.RecommendedMinimumCodexContext
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void CompleteSave(bool saveAsModelDefault)
    {
        var updated = _originalProfile with
        {
            DisplayName = DisplayNameTextBox.Text.Trim(),
            ContextSize = SelectedValue<int>(ContextSizeComboBox),
            CompactionSafetyReserve = SelectedValue<int>(CompactionSafetyReserveComboBox),
            GpuLayers = SelectedValue<string>(GpuLayersComboBox),
            FlashAttention = SelectedValue<string>(FlashAttentionComboBox),
            CacheTypeK = SelectedValue<string>(CacheTypeKComboBox),
            CacheTypeV = SelectedValue<string>(CacheTypeVComboBox),
            Parallel = SelectedValue<int>(ParallelComboBox),
            BatchSize = NullWhenZero(SelectedValue<int>(BatchSizeComboBox)),
            MicroBatchSize = NullWhenZero(SelectedValue<int>(MicroBatchSizeComboBox)),
            IdleSleepSeconds = SelectedValue<int>(IdleSleepSecondsComboBox),
            Jinja = JinjaCheckBox.IsChecked == true,
            ChatTemplateRelativePath = NullWhenWhiteSpace(ChatTemplatePathTextBox.Text),
        };
        var errors = ModelProfileValidator.Validate(updated, _runtimeRoot);
        if (errors.Count > 0)
        {
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine, errors),
                "Profile 参数无效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (saveAsModelDefault)
        {
            var answer = MessageBox.Show(
                this,
                $"把当前参数保存为“{updated.DisplayName}”的专用默认值？\n\n"
                + "以后只有该模型执行“恢复此模型默认”时会使用；其他模型不会继承或改变。",
                "设置此模型的默认参数",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            updated = ModelProfileFactory.SaveCurrentParametersAsDefault(updated);
        }

        UpdatedProfile = updated;
        SavedAsModelDefault = saveAsModelDefault;
        DialogResult = true;
    }

    private void Populate(ModelProfile profile)
    {
        DisplayNameTextBox.Text = profile.DisplayName;
        AliasTextBox.Text = profile.Alias;
        ModelPathTextBox.Text = profile.ModelRelativePath;

        PopulateChoices(ContextSizeComboBox, ContextChoices, profile.ContextSize, profile.ContextSize.ToString("N0"));
        PopulateChoices(
            CompactionSafetyReserveComboBox,
            CompactionSafetyReserveChoices,
            profile.CompactionSafetyReserve,
            profile.CompactionSafetyReserve.ToString("N0"));
        PopulateChoices(GpuLayersComboBox, GpuLayerChoices, profile.GpuLayers, profile.GpuLayers);
        PopulateChoices(FlashAttentionComboBox, FlashAttentionChoices, profile.FlashAttention, profile.FlashAttention);
        PopulateChoices(CacheTypeKComboBox, CacheTypeChoices, profile.CacheTypeK, profile.CacheTypeK);
        PopulateChoices(CacheTypeVComboBox, CacheTypeChoices, profile.CacheTypeV, profile.CacheTypeV);
        PopulateChoices(ParallelComboBox, ParallelChoices, profile.Parallel, profile.Parallel.ToString());
        PopulateChoices(BatchSizeComboBox, BatchChoices, profile.BatchSize ?? 0, profile.BatchSize?.ToString("N0") ?? "Default");
        PopulateChoices(
            MicroBatchSizeComboBox,
            MicroBatchChoices,
            profile.MicroBatchSize ?? 0,
            profile.MicroBatchSize?.ToString("N0") ?? "Default");
        PopulateChoices(
            IdleSleepSecondsComboBox,
            IdleSleepChoices,
            profile.IdleSleepSeconds,
            profile.IdleSleepSeconds == -1 ? "禁用" : $"{profile.IdleSleepSeconds:N0} 秒");
        JinjaCheckBox.IsChecked = profile.Jinja;
        ChatTemplatePathTextBox.Text = profile.ChatTemplateRelativePath ?? string.Empty;
    }

    private static void PopulateChoices<T>(
        ComboBox comboBox,
        IReadOnlyList<Choice<T>> standardChoices,
        T selectedValue,
        string customLabel)
        where T : notnull
    {
        var choices = standardChoices.ToList();
        if (!choices.Any(choice => EqualityComparer<T>.Default.Equals(choice.Value, selectedValue)))
        {
            choices.Add(new Choice<T>($"Current ({customLabel})", selectedValue));
        }

        comboBox.ItemsSource = choices;
        comboBox.SelectedValue = selectedValue;
    }

    private static T SelectedValue<T>(ComboBox comboBox) where T : notnull =>
        comboBox.SelectedValue is T value
            ? value
            : throw new InvalidOperationException("请选择一个有效参数值。");

    private static int? NullWhenZero(int value) => value == 0 ? null : value;

    private static string? NullWhenWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record Choice<T>(string Label, T Value);
}
