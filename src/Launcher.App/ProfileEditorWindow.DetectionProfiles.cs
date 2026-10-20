using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Launcher.Models.Profiles;
using TextBox = System.Windows.Controls.TextBox;

namespace Launcher.App;

public partial class ProfileEditorWindow
{
    private bool TryBuildContextShiftCandidate(out ModelProfile candidate)
    {
        candidate = _originalProfile;
        if (!TryBuildReasoningCandidate(out var loading, validateTemplate: false)) return false;
        if (VisionEnableCheckBox.IsChecked == true)
            return Fail(AppLanguageManager.Text("ContextShiftVisionConflictError"));
        if (!TryGetSelectedContextSize(out var context, out var error)
            || !TryGetBatchSizes(out var batch, out var micro, out error)) return Fail(error);
        var extra = loading.ExtraArguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        AddOptional(extra, "keep", KeepTokensTextBox.Text);
        AddOptional(extra, "ctx-checkpoints", CtxCheckpointsTextBox.Text);
        if (SelectedValue<string>(SwaFullComboBox) == "on") extra["swa-full"] = null;
        AddOptional(extra, "rope-scaling", SelectedValue<string>(RopeScalingComboBox));
        AddOptional(extra, "rope-scale", RopeScaleTextBox.Text);
        AddOptional(extra, "rope-freq-base", RopeFreqBaseTextBox.Text);
        AddOptional(extra, "rope-freq-scale", RopeFreqScaleTextBox.Text);
        if (!TryAddYarnArguments(extra, YarnArgumentsTextBox.Text, out error)) return Fail(error);
        loading = loading with
        {
            ContextSize = context,
            BatchSize = batch,
            MicroBatchSize = micro,
            Parallel = SelectedValue<int>(ParallelComboBox),
            ExtraArguments = extra,
            MtpEnabled = MtpEnableCheckBox.IsChecked == true,
        };
        if (loading.MtpEnabled)
        {
            if (!TryBuildMtpSettings(out var mtp, out error)) return Fail(error);
            loading = loading with
            {
                MtpSource = mtp.Source,
                MtpCapabilityStatus = mtp.CapabilityStatus,
                MtpDraftModelRelativePath = mtp.DraftModelRelativePath,
                MtpDraftMaxTokens = mtp.DraftMaxTokens,
                MtpDraftMinTokens = mtp.DraftMinTokens,
                MtpDraftMinimumProbability = mtp.DraftMinimumProbability,
                MtpDraftSplitProbability = mtp.DraftSplitProbability,
                MtpBackendSampling = mtp.BackendSampling,
                MtpDraftGpuLayers = mtp.DraftGpuLayers,
                MtpDraftDevice = mtp.DraftDevice,
                MtpDraftCacheTypeK = mtp.DraftCacheTypeK,
                MtpDraftCacheTypeV = mtp.DraftCacheTypeV,
                MtpDraftThreads = mtp.DraftThreads,
                MtpDraftBatchThreads = mtp.DraftBatchThreads,
            };
        }
        var numbers = new Dictionary<string, int?>();
        foreach (var key in new[] { "moe-cache-mib", "checkpoint-min-step", "spec-draft-n-ubatch" })
        {
            if (key == "moe-cache-mib" && loading.ModelType != ModelType.MoE || key == "spec-draft-n-ubatch" && !loading.MtpEnabled)
            { numbers[key] = null; continue; }
            var text = ((TextBox)_runtimeInputs[key]).Text.Trim();
            if (text.Length == 0) numbers[key] = null;
            else if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) numbers[key] = value;
            else return Fail("--" + key + AppLanguageManager.Choose("：请输入非负整数或留空。", ": enter a non-negative integer or leave blank."));
        }
        var unified = SelectedValue<string>((ComboBox)_runtimeInputs["kv-unified"]);
        loading = loading with
        {
            RuntimeParameters = new()
            {
                KvUnified = unified switch { "on" => true, "off" => false, _ => null },
                MoeCacheMiB = numbers["moe-cache-mib"],
                CheckpointMinimumStep = numbers["checkpoint-min-step"],
                DraftMicroBatchSize = numbers["spec-draft-n-ubatch"],
                DraftSampling = loading.MtpEnabled ? NullWhenWhiteSpace(SelectedValue<string>((ComboBox)_runtimeInputs["spec-draft-sampling"])) : null,
            }
        };
        loading = Launcher.Scripts.Templates.ModelResourceCatalog.Resolve(loading, _runtimeRoot);
        candidate = ContextShiftProbeProfile.Create(loading);
        var errors = ModelProfileValidator.Validate(candidate, _runtimeRoot);
        return errors.Count == 0 || Fail(string.Join(Environment.NewLine, errors));

        bool Fail(string message)
        {
            MessageBox.Show(this, message, AppLanguageManager.Text("ContextShiftErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
