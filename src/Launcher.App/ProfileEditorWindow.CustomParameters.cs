using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Launcher.ChatGPT.Configuration;
using Launcher.Models.Profiles;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Panel = System.Windows.Controls.Panel;

namespace Launcher.App;

public partial class ProfileEditorWindow
{
    private sealed record CustomRow(bool Codex, Grid Container, TextBox Name, TextBox Value, CheckBox? NoValue);
    private readonly List<CustomRow> _customRows = [];
    private readonly Dictionary<string, FrameworkElement> _runtimeInputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _runtimeInputTooltips = new(StringComparer.Ordinal);
    private IReadOnlySet<string>? _additionalRuntimeCapabilities;

    private void InitializeAdditionalParameterControls()
    {
        AddRuntimeNumber(MtpAdditionalRoot, "spec-draft-n-ubatch", "Draft 物理批量", "Draft physical batch", "0：继承主模型物理批量。", "0: inherit the main model's physical batch.");
        AddRuntimeChoice(MtpAdditionalRoot, "spec-draft-sampling", "Draft 采样方式", "Draft sampling", ["greedy", "probabilistic"]);
        AddRuntimeNumber(MoeAdditionalRoot, "moe-cache-mib", "MoE GPU 专家缓存（MiB）", "MoE GPU expert cache (MiB)", "用于缓存放在 CPU 的专家。0：关闭。", "Cache CPU-resident experts on the GPU. 0: disabled.");
        AddRuntimeNumber(CacheAdditionalRoot, "cache-ram", "提示词缓存容量（MiB）", "Prompt cache capacity (MiB)", "-1：无限制；0：关闭。", "-1: unlimited; 0: disabled.");
        AddRuntimeChoice(CacheAdditionalRoot, "cache-prompt", "提示词缓存", "Prompt cache");
        AddRuntimeChoice(CacheAdditionalRoot, "cache-idle-slots", "缓存空闲槽", "Cache idle slots");
        AddRuntimeChoice(CacheAdditionalRoot, "kv-unified", "统一 KV 缓存", "Unified KV cache");
        AddRuntimeNumber(CheckpointAdditionalRoot, "checkpoint-min-step", "检查点最小间隔", "Minimum checkpoint step", "上下文检查点的最小 token 间隔。", "Minimum token interval between context checkpoints.");
        AddRuntimeChoice(ComputeAdditionalRoot, "backend-sampling", "主模型后端采样（实验性）", "Main-model backend sampling (experimental)", ["on"]);

        AddCustomSection(false);
        AddCustomSection(true);
    }

    private StackPanel RuntimeField(Panel parent, string key, string chinese, string english)
    {
        var field = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        field.Children.Add(new TextBlock { Text = AppLanguageManager.Choose(chinese, english), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        parent.Children.Add(field);
        return field;
    }

    private void AddRuntimeNumber(Panel parent, string key, string chinese, string english, string zhHint, string enHint)
    {
        var field = RuntimeField(parent, key, chinese, english);
        var input = new TextBox { Margin = new Thickness(0, 5, 0, 0), ToolTip = AppLanguageManager.Text("ParameterHelp_" + key) };
        ToolTipService.SetShowOnDisabled(input, true);
        field.Children.Add(input);
        _runtimeInputs.Add(key, input);
        _runtimeInputTooltips.Add(key, input.ToolTip);
    }

    private void AddRuntimeChoice(Panel parent, string key, string chinese, string english, string[]? choices = null)
    {
        var field = RuntimeField(parent, key, chinese, english);
        var input = new ComboBox { Margin = new Thickness(0, 5, 0, 0), DisplayMemberPath = "Label", SelectedValuePath = "Value", ToolTip = AppLanguageManager.Text("ParameterHelp_" + key) };
        ToolTipService.SetShowOnDisabled(input, true);
        input.ItemsSource = choices is null ? DefaultOnOffChoices : new[] { new Choice<string>(AppLanguageManager.Choose("默认", "Default"), "") }
            .Concat(choices.Select(value => new Choice<string>(value == "on" ? AppLanguageManager.Choose("开启", "On") : value, value))).ToArray();
        field.Children.Add(input);
        _runtimeInputs.Add(key, input);
        _runtimeInputTooltips.Add(key, input.ToolTip);
    }

    private void PopulateAdditionalParameters(ModelProfile profile)
    {
        var values = profile.RuntimeParameters;
        SetNumber("spec-draft-n-ubatch", values.DraftMicroBatchSize);
        SetNumber("moe-cache-mib", values.MoeCacheMiB);
        SetNumber("cache-ram", values.CacheRamMiB);
        SetNumber("checkpoint-min-step", values.CheckpointMinimumStep);
        SetChoice("spec-draft-sampling", values.DraftSampling ?? "");
        SetToggle("cache-prompt", values.CachePrompt);
        SetToggle("cache-idle-slots", values.CacheIdleSlots);
        SetToggle("kv-unified", values.KvUnified);
        SetToggle("backend-sampling", values.BackendSampling);
        MoeAdditionalRoot.Visibility = profile.ModelType == ModelType.MoE ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in _customRows.ToArray()) RemoveCustomRow(row, markDirty: false);
        foreach (var row in profile.CustomParameters.Llama) AddCustomRow(false, row);
        // Previously hand-written unknown ExtraArguments must remain visible and repairable.
        // Moving them into rows occurs only when the user explicitly saves the page.
        foreach (var pair in profile.ExtraArguments.Where(p => !ManagedAdvancedArgumentKeys.Contains(p.Key, StringComparer.OrdinalIgnoreCase)))
            AddCustomRow(false, new() { Name = "--" + pair.Key, Value = pair.Value ?? "", NoValue = pair.Value is null });
        foreach (var row in profile.CustomParameters.Codex) AddCustomRow(true, row);
        UpdateAdditionalCapabilities();

        void SetNumber(string key, int? number) => ((TextBox)_runtimeInputs[key]).Text = number?.ToString(CultureInfo.InvariantCulture) ?? "";
        void SetChoice(string key, string value) => ((ComboBox)_runtimeInputs[key]).SelectedValue = value;
        void SetToggle(string key, bool? value) => SetChoice(key, value is true ? "on" : value is false ? "off" : "");
    }

    private bool TryBuildAdditionalParameters(out RuntimeParameterSettings settings, out string error)
    {
        settings = new();
        error = "";
        var values = new Dictionary<string, int?>();
        foreach (var key in new[] { "spec-draft-n-ubatch", "moe-cache-mib", "cache-ram", "checkpoint-min-step" })
        {
            var text = ((TextBox)_runtimeInputs[key]).Text.Trim();
            if (text.Length == 0) values[key] = null;
            else if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) values[key] = number;
            else { error = "--" + key + AppLanguageManager.Choose("：请输入整数或留空。", ": enter an integer or leave blank."); return false; }
        }
        settings = new()
        {
            DraftMicroBatchSize = values["spec-draft-n-ubatch"],
            DraftSampling = Choice("spec-draft-sampling"),
            MoeCacheMiB = values["moe-cache-mib"],
            CacheRamMiB = values["cache-ram"],
            CheckpointMinimumStep = values["checkpoint-min-step"],
            CachePrompt = Toggle("cache-prompt"),
            CacheIdleSlots = Toggle("cache-idle-slots"),
            KvUnified = Toggle("kv-unified"),
            BackendSampling = Toggle("backend-sampling"),
        };
        error = string.Join(Environment.NewLine, settings.Validate());
        return error.Length == 0;
        string? Choice(string key) => NullWhenWhiteSpace(SelectedValue<string>((ComboBox)_runtimeInputs[key]));
        bool? Toggle(string key) => Choice(key) switch { "on" => true, "off" => false, _ => null };
    }

    private void UpdateAdditionalCapabilities()
    {
        foreach (var pair in _runtimeInputs)
        {
            var hasValue = pair.Value is TextBox text ? !string.IsNullOrWhiteSpace(text.Text) : ((ComboBox)pair.Value).SelectedIndex > 0;
            var supported = _additionalRuntimeCapabilities?.Contains(pair.Key);
            // Detection is intentionally unavailable while another Local service runs.
            // Unknown support must not block ordinary editing of a stopped model.
            pair.Value.IsEnabled = supported is not false || hasValue;
            pair.Value.ToolTip = supported switch
            {
                true => _runtimeInputTooltips[pair.Key],
                null => _runtimeInputTooltips[pair.Key] + Environment.NewLine + AppLanguageManager.Choose(
                    "当前运行库支持情况尚未确认，可编辑并保存；启动前仍会检查支持情况。",
                    "Runtime support is unconfirmed. You can edit and save; support is still checked before startup."),
                false => _runtimeInputTooltips[pair.Key] + Environment.NewLine + AppLanguageManager.Choose(
                    "当前运行库未报告支持。已有值会保留，可清空以恢复默认。",
                    "The runtime does not report support. Saved values are retained and can be cleared to inherit defaults."),
            };
        }
    }

    private void AddCustomSection(bool codex)
    {
        var panel = codex ? CodexCustomRoot : LlamaCustomRoot;
        panel.Children.Add(new TextBlock { FontWeight = FontWeights.Bold, FontSize = 15, Text = codex ? "Codex" : "llama.cpp" });
        panel.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 10),
            TextWrapping = TextWrapping.Wrap,
            Text = codex ? AppLanguageManager.Choose("左侧填写配置键；右侧填写单个 TOML 值。字符串需要引号，例如 \"value\"。仅本地启动时写入。", "Enter a configuration key and one TOML literal. Quote strings, e.g. \"value\". Applied only on Local launch.")
                : AppLanguageManager.Choose("左侧填写 --参数名；右侧填写一个参数值，无需添加外层引号。无值命令请勾选“无值开关”。值暂不支持引号、#、;、换行和首尾空格。", "Enter --option and one value without outer quotes. Select ‘No value’ for a valueless switch. Quotes, #, ;, line breaks and surrounding whitespace are not supported in values."),
        });
        var add = new Button { HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Content = AppLanguageManager.Choose("添加参数", "Add parameter") };
        add.Click += (_, _) => { AddCustomRow(codex, new()); MarkConfigurationDirty(); };
        panel.Children.Add(add);
    }

    private void AddCustomRow(bool codex, CustomParameter parameter)
    {
        var container = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBox { Text = parameter.Name, Margin = new Thickness(0, 0, 8, 0), ToolTip = AppLanguageManager.Text(codex ? "ParameterHelp_CustomCodexName" : "ParameterHelp_CustomLlamaName") };
        var value = new TextBox { Text = parameter.Value, Margin = new Thickness(0, 0, 8, 0), ToolTip = AppLanguageManager.Text(codex ? "ParameterHelp_CustomCodexValue" : "ParameterHelp_CustomLlamaValue") };
        var noValue = codex ? null : new CheckBox { Content = AppLanguageManager.Choose("无值开关", "No value"), IsChecked = parameter.NoValue, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = AppLanguageManager.Text("ParameterHelp_CustomNoValue") };
        ToolTipService.SetShowOnDisabled(name, true);
        ToolTipService.SetShowOnDisabled(value, true);
        // Keep text intact when toggling a flag; invalid combinations require explicit correction.
        var remove = new Button { Content = AppLanguageManager.Choose("删除", "Remove"), Padding = new Thickness(10, 5, 10, 5) };
        Grid.SetColumn(value, 1); Grid.SetColumn(remove, 3);
        container.Children.Add(name); container.Children.Add(value); container.Children.Add(remove);
        if (noValue is not null) { ToolTipService.SetShowOnDisabled(noValue, true); Grid.SetColumn(noValue, 2); container.Children.Add(noValue); }
        var row = new CustomRow(codex, container, name, value, noValue);
        _customRows.Add(row);
        remove.Click += (_, _) => RemoveCustomRow(row, true);
        var panel = codex ? CodexCustomRoot : LlamaCustomRoot;
        panel.Children.Insert(panel.Children.Count - 1, container);
    }

    private void RemoveCustomRow(CustomRow row, bool markDirty)
    {
        (row.Codex ? CodexCustomRoot : LlamaCustomRoot).Children.Remove(row.Container);
        _customRows.Remove(row);
        if (markDirty) MarkConfigurationDirty();
    }

    private CustomParameterSettings ReadCustomParameters() => new()
    {
        Llama = Rows(false),
        Codex = Rows(true),
    };

    private CustomParameter[] Rows(bool codex) => _customRows.Where(r => r.Codex == codex).Select(r => new CustomParameter
    {
        Name = r.Name.Text,
        Value = r.Value.Text,
        NoValue = r.NoValue?.IsChecked == true,
    }).ToArray();

    private IReadOnlyList<string> ValidateCustomParametersForSave(ModelProfile profile) =>
        CustomParameterRules.Validate(profile.CustomParameters, profile.ExtraArguments.Keys)
            .Concat(CodexCustomParameterValidator.Validate(profile.CustomParameters.Codex)).Distinct().ToArray();
}
