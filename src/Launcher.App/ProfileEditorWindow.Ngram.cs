using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Launcher.Models.Profiles;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace Launcher.App;

public partial class ProfileEditorWindow
{
    private readonly Dictionary<string, CheckBox> _ngramTypeControls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBox> _ngramParameterControls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StackPanel> _ngramTypePanels = new(StringComparer.Ordinal);
    private IReadOnlySet<string>? _ngramRuntimeCapabilities;
    private CheckBox _ngramEnabledControl = null!;
    private TextBlock _ngramRuntimeStatus = null!;
    private TextBox _ngramStaticCache = null!;
    private TextBox _ngramDynamicCache = null!;

    private void InitializeNgramControls()
    {
        _ngramEnabledControl = new CheckBox
        {
            Content = AppLanguageManager.Choose("启用 n-gram 加速", "Enable n-gram acceleration"),
            Style = (Style)FindResource("StartupToggleStyle"),
            ToolTip = AppLanguageManager.Text("ParameterHelp_ngram-enabled"),
        };
        ToolTipService.SetShowOnDisabled(_ngramEnabledControl, true);
        _ngramEnabledControl.Checked += (_, _) => UpdateNgramUiState();
        _ngramEnabledControl.Unchecked += (_, _) => UpdateNgramUiState();
        NgramRoot.Children.Add(_ngramEnabledControl);
        NgramRoot.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 8),
            TextWrapping = TextWrapping.Wrap,
            Text = AppLanguageManager.Choose("可多选；运行库决定调用顺序，选择更多策略不一定更快。参数留空表示沿用运行库默认。", "Select one or more. The runtime determines priority; more strategies may not be faster. Blank parameters inherit runtime defaults."),
        });
        _ngramRuntimeStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        _ngramRuntimeStatus.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
        NgramRoot.Children.Add(_ngramRuntimeStatus);
        foreach (var type in NgramOptions.Types)
        {
            var panel = new StackPanel();
            var check = new CheckBox
            {
                Content = type == "ngram-map-k4v" ? type + AppLanguageManager.Choose("（实验性）", " (Experimental)") : type,
                Style = (Style)FindResource("StartupToggleStyle"),
                Margin = new Thickness(0, 0, 0, 10),
            };
            ToolTipService.SetShowOnDisabled(check, true);
            var fields = new StackPanel();
            check.Checked += (_, _) => UpdateNgramUiState();
            check.Unchecked += (_, _) => UpdateNgramUiState();
            _ngramTypeControls.Add(type, check);
            _ngramTypePanels.Add(type, fields);
            panel.Children.Add(check);
            panel.Children.Add(fields);
            var parameterPanel = new ProfileParameterPanel();
            foreach (var definition in NgramOptions.Parameters.Where(p => p.Type == type))
            {
                var column = new StackPanel();
                column.Children.Add(new TextBlock
                {
                    Text = AppLanguageManager.Choose(definition.Chinese, definition.English),
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                });
                var input = new TextBox
                {
                    Margin = new Thickness(0, 5, 0, 0),
                    ToolTip = AppLanguageManager.Text("ParameterHelp_" + definition.Key) + $"\n{definition.Minimum} ~ {definition.Maximum}\n"
                        + AppLanguageManager.Choose("留空：沿用运行库默认值。", "Blank: inherit runtime default."),
                };
                ToolTipService.SetShowOnDisabled(input, true);
                _ngramParameterControls.Add(definition.Key, input);
                column.Children.Add(input);
                parameterPanel.Children.Add(column);
            }
            fields.Children.Add(parameterPanel);
            if (type == "ngram-simple") fields.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Text = AppLanguageManager.Choose("最小命中次数的实际作用取决于运行库实现；部分版本仅解析此参数。", "The effect of minimum hits depends on the runtime; some versions only parse this option."),
            });
            if (type == "ngram-cache")
            {
                _ngramStaticCache = AddNgramCacheField(fields, true);
                _ngramDynamicCache = AddNgramCacheField(fields, false);
                fields.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = AppLanguageManager.Choose("缓存文件可不选。仅使用与当前模型分词器匹配的统计缓存；不是 JSON 或 GGUF。动态缓存是否写回取决于运行库，本启动器不会创建或覆盖缓存文件。", "Cache files are optional. Use statistics compatible with this model's tokenizer, not JSON or GGUF. Dynamic cache persistence depends on the runtime; the launcher does not create or overwrite caches."),
                });
            }
            var border = new Border
            {
                Child = panel,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 12),
                BorderThickness = new Thickness(1),
            };
            border.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            NgramRoot.Children.Add(border);
        }
    }

    private TextBox AddNgramCacheField(StackPanel panel, bool isStatic)
    {
        panel.Children.Add(new TextBlock
        {
            Text = isStatic ? AppLanguageManager.Choose("静态缓存文件（可选）", "Static cache file (optional)") : AppLanguageManager.Choose("动态缓存文件（可选）", "Dynamic cache file (optional)"),
            FontWeight = FontWeights.SemiBold,
        });
        var row = new DockPanel { Margin = new Thickness(0, 5, 0, 12), LastChildFill = true };
        var browse = new Button { Content = AppLanguageManager.Choose("选择…", "Browse…"), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        var input = new TextBox { ToolTip = AppLanguageManager.Text(isStatic ? "ParameterHelp_lookup-cache-static" : "ParameterHelp_lookup-cache-dynamic") };
        ToolTipService.SetShowOnDisabled(input, true);
        row.Children.Add(input);
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { CheckFileExists = true, Filter = AppLanguageManager.Choose("统计缓存文件|*.*", "Statistics cache files|*.*"), InitialDirectory = _runtimeRoot };
            if (dialog.ShowDialog(this) == true) input.Text = dialog.FileName;
        };
        panel.Children.Add(row);
        return input;
    }

    private void PopulateNgram(NgramSettings settings)
    {
        _ngramEnabledControl.IsChecked = settings.Enabled;
        foreach (var pair in _ngramTypeControls) pair.Value.IsChecked = settings.Types.Contains(pair.Key, StringComparer.Ordinal);
        foreach (var pair in _ngramParameterControls)
            pair.Value.Text = settings.Parameters.TryGetValue(pair.Key, out var value) ? value?.ToString(CultureInfo.InvariantCulture) ?? "" : "";
        _ngramStaticCache.Text = settings.StaticCachePath ?? "";
        _ngramDynamicCache.Text = settings.DynamicCachePath ?? "";
        UpdateNgramUiState();
    }

    private void UpdateNgramUiState()
    {
        if (_ngramEnabledControl is null) return;
        _ngramEnabledControl.IsEnabled = _ngramEnabledControl.IsChecked == true
            || NgramOptions.Types.Any(t => _ngramRuntimeCapabilities?.Contains("spec-type:" + t) == true);
        foreach (var pair in _ngramTypeControls)
        {
            var supported = _ngramRuntimeCapabilities?.Contains("spec-type:" + pair.Key) == true;
            pair.Value.IsEnabled = pair.Value.IsChecked == true || (_ngramEnabledControl.IsChecked == true && supported);
            pair.Value.ToolTip = AppLanguageManager.Text("ParameterHelp_" + pair.Key) + (supported ? "" : Environment.NewLine
                + AppLanguageManager.Choose("当前运行库无法确认支持此策略。已保存的选择会保留，启动时重新检查。", "Runtime support is unconfirmed. Saved selections are retained and checked again at startup."));
            _ngramTypePanels[pair.Key].Visibility = pair.Value.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            // Keep saved values repairable, including on an older runtime. Never clear disabled fields.
            _ngramTypePanels[pair.Key].IsEnabled = _ngramEnabledControl.IsChecked == true;
        }
        _ngramRuntimeStatus.Text = _ngramRuntimeCapabilities is null
            ? AppLanguageManager.Choose("未取得运行库支持信息，启动前必须确认所选策略可用。", "Runtime support information is unavailable; selected strategies must be confirmed before startup.")
            : NgramOptions.Types.Any(t => _ngramRuntimeCapabilities.Contains("spec-type:" + t))
                ? AppLanguageManager.Choose("支持：", "Supported: ") + string.Join(", ", NgramOptions.Types.Where(t => _ngramRuntimeCapabilities.Contains("spec-type:" + t)))
                : AppLanguageManager.Choose("当前运行库未报告可用的 n-gram 策略；已保存的设置会保留。", "The runtime reports no available n-gram strategies. Saved settings are retained.");
    }

    private bool TryBuildNgramSettings(out NgramSettings settings, out string error)
    {
        var values = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var parameter in NgramOptions.Parameters)
        {
            var text = _ngramParameterControls[parameter.Key].Text.Trim();
            if (text.Length == 0) values[parameter.Key] = null;
            else if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) values[parameter.Key] = value;
            else
            {
                settings = new();
                error = AppLanguageManager.Choose("请输入整数或留空：", "Enter an integer or leave blank: ") + parameter.Key;
                return false;
            }
        }
        settings = new NgramSettings
        {
            Enabled = _ngramEnabledControl.IsChecked == true,
            Types = NgramOptions.Types.Where(t => _ngramTypeControls[t].IsChecked == true).ToArray(),
            Parameters = values,
            StaticCachePath = NullWhenWhiteSpace(_ngramStaticCache.Text),
            DynamicCachePath = NullWhenWhiteSpace(_ngramDynamicCache.Text),
        };
        var errors = NgramOptions.Validate(settings, _runtimeRoot, checkFiles: false);
        error = string.Join(Environment.NewLine, errors);
        return errors.Count == 0;
    }
}
