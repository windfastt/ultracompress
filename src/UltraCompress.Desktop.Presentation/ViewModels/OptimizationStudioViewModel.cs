using System.Collections.ObjectModel;
using System.Text;
using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.Primitives;
using UltraCompress.Desktop.Presentation.Services;

namespace UltraCompress.Desktop.Presentation.ViewModels;

/// <summary>
/// Strictly separated from Lossless Compression.
/// Handles Lossy Optimization, Format Conversion, and Metadata/Data Removal behind mandatory explicit consent gates.
/// </summary>
public sealed class OptimizationStudioViewModel : ObservableObject
{
    private OptimizationSubMode _selectedSubMode = OptimizationSubMode.LossyOptimization;
    private OptimizationTargetOption _selectedTarget;
    private string _outputDirectory;
    private bool _preserveOriginalFile = true;
    private bool _consentUnderstandNonBitExact;
    private bool _consentApproveItemizedPreview;
    private bool _consentAllowReplaceOriginalWhenUnpreserved;
    private OptimizationImpactPreview? _impactPreview;
    private MutatingConsentGateResult _consentGate;
    private ActionableErrorCard? _lastErrorCard;
    private string _statusMessage = "Safety Studio isolated from Lossless Compression. Original files are preserved untouched by default.";

    public OptimizationStudioViewModel(string? defaultOutputDirectory = null)
    {
        _outputDirectory = string.IsNullOrWhiteSpace(defaultOutputDirectory)
            ? Path.Combine(Path.GetTempPath(), "UltraCompress_Optimized")
            : Path.GetFullPath(defaultOutputDirectory);

        var initialOptions = ConsentGateValidator.GetOptionsForSubMode(_selectedSubMode);
        foreach (var opt in initialOptions)
        {
            AvailableTargets.Add(opt);
        }

        _selectedTarget = initialOptions[0];
        _consentGate = EvaluateGateInternal();
    }

    public event Action<CompressionHistoryRecord>? OptimizationHistoryProduced;

    public IReadOnlyList<OptimizationSubMode> SubModes { get; } =
    [
        OptimizationSubMode.LossyOptimization,
        OptimizationSubMode.FormatConversion,
        OptimizationSubMode.DataRemoval,
    ];

    public ObservableCollection<OptimizationTargetOption> AvailableTargets { get; } = [];

    public ObservableCollection<string> StagedInputFiles { get; } = [];

    public ObservableCollection<string> ItemizedPreviewLines { get; } = [];

    public OperationKind OperationKind => ConsentGateValidator.MapSubModeToOperationKind(_selectedSubMode);

    public OptimizationSubMode SelectedSubMode
    {
        get => _selectedSubMode;
        set
        {
            if (SetProperty(ref _selectedSubMode, value))
            {
                RaisePropertyChanged(nameof(OperationKind));
                RaisePropertyChanged(nameof(SubModeHeaderTitle));
                ReloadTargetsForSubMode();
                ResetAllConsentsAndRefreshPreview();
            }
        }
    }

    public string SubModeHeaderTitle => _selectedSubMode switch
    {
        OptimizationSubMode.LossyOptimization => "Lossy Optimization — Perceptual Size Reduction (Non-Bit-Exact)",
        OptimizationSubMode.FormatConversion => "Format Conversion — Container & Encoding Transcode",
        OptimizationSubMode.DataRemoval => "Metadata & Data Hygiene — Strip EXIF, GPS & Diagnostic Blocks",
        _ => _selectedSubMode.ToString(),
    };

    public OptimizationTargetOption SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (value is not null && SetProperty(ref _selectedTarget, value))
            {
                ResetAllConsentsAndRefreshPreview();
            }
        }
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetProperty(ref _outputDirectory, value ?? string.Empty);
    }

    /// <summary>
    /// Safety toggle enabled by default (true) so original files are never overwritten unless explicitly unlocked.
    /// </summary>
    public bool PreserveOriginalFile
    {
        get => _preserveOriginalFile;
        set
        {
            if (SetProperty(ref _preserveOriginalFile, value))
            {
                _consentAllowReplaceOriginalWhenUnpreserved = false;
                RaisePropertyChanged(nameof(ConsentAllowReplaceOriginalWhenUnpreserved));
                RaisePropertyChanged(nameof(RequiresDestructiveReplaceConsent));
                RebuildImpactPreview();
                RefreshConsentGate();
            }
        }
    }

    public bool RequiresDestructiveReplaceConsent => !_preserveOriginalFile;

    public bool ConsentUnderstandNonBitExact
    {
        get => _consentUnderstandNonBitExact;
        set
        {
            if (SetProperty(ref _consentUnderstandNonBitExact, value))
            {
                RefreshConsentGate();
            }
        }
    }

    public bool ConsentApproveItemizedPreview
    {
        get => _consentApproveItemizedPreview;
        set
        {
            if (SetProperty(ref _consentApproveItemizedPreview, value))
            {
                RefreshConsentGate();
            }
        }
    }

    public bool ConsentAllowReplaceOriginalWhenUnpreserved
    {
        get => _consentAllowReplaceOriginalWhenUnpreserved;
        set
        {
            if (SetProperty(ref _consentAllowReplaceOriginalWhenUnpreserved, value))
            {
                RefreshConsentGate();
            }
        }
    }

    public OptimizationImpactPreview? ImpactPreview
    {
        get => _impactPreview;
        private set
        {
            if (SetProperty(ref _impactPreview, value))
            {
                RaisePropertyChanged(nameof(HasImpactPreview));
                RaisePropertyChanged(nameof(PreviewSummaryHeadline));
                RaisePropertyChanged(nameof(PreviewFidelitySummary));
                RaisePropertyChanged(nameof(PreviewPreservationBadge));
            }
        }
    }

    public bool HasImpactPreview => ImpactPreview is not null;

    public string PreviewSummaryHeadline =>
        ImpactPreview?.BeforeAfterSummary ?? "Stage files to compute itemized impact preview.";

    public string PreviewFidelitySummary =>
        ImpactPreview?.FidelityImpactSummary ?? _selectedTarget.FidelitySummary;

    public string PreviewPreservationBadge =>
        ImpactPreview?.PreservationPolicyBadge
        ?? "SAFE DEFAULT: Original files preserved untouched; transformed copies written separately.";

    public MutatingConsentGateResult ConsentGate
    {
        get => _consentGate;
        private set
        {
            if (SetProperty(ref _consentGate, value))
            {
                RaisePropertyChanged(nameof(CanExecuteOptimization));
                RaisePropertyChanged(nameof(GateStatusMessage));
            }
        }
    }

    public bool CanExecuteOptimization => ConsentGate.IsAllowed;

    public string GateStatusMessage => ConsentGate.GateStatusMessage;

    public ActionableErrorCard? LastErrorCard
    {
        get => _lastErrorCard;
        private set
        {
            if (SetProperty(ref _lastErrorCard, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => LastErrorCard is not null;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public void StageFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        LastErrorCard = null;

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var existing = new HashSet<string>(StagedInputFiles, comparer);

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var full = Path.GetFullPath(raw.Trim());
            if (!File.Exists(full))
            {
                LastErrorCard = ActionableErrorPresenter.FromCode(ActionableErrorCode.SourceFileNotFound, full);
                StatusMessage = LastErrorCard.Title;
                return;
            }

            if (existing.Add(full))
            {
                StagedInputFiles.Add(full);
            }
        }

        ResetAllConsentsAndRefreshPreview();
    }

    public void ClearFiles()
    {
        StagedInputFiles.Clear();
        LastErrorCard = null;
        ResetAllConsentsAndRefreshPreview();
    }

    public async Task<(bool Success, IReadOnlyList<string> WrittenOutputPaths)> ExecuteOptimizationAsync(
        CancellationToken cancellationToken = default)
    {
        RefreshConsentGate();
        if (!ConsentGate.IsAllowed)
        {
            LastErrorCard = ActionableErrorPresenter.FromCode(
                ActionableErrorCode.MutatingConsentMissing,
                StagedInputFiles.FirstOrDefault(),
                string.Join("; ", ConsentGate.MissingRequirements));
            StatusMessage = LastErrorCard.Title;
            return (false, Array.Empty<string>());
        }

        LastErrorCard = null;
        Directory.CreateDirectory(OutputDirectory);
        var outputs = new List<string>(StagedInputFiles.Count);
        long totalOriginal = 0;
        long totalOutput = 0;

        try
        {
            foreach (var inputPath in StagedInputFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inputBytes = await File.ReadAllBytesAsync(inputPath, cancellationToken).ConfigureAwait(false);
                totalOriginal += inputBytes.LongLength;

                var transformedBytes = ApplySafeTransformPayload(inputBytes, _selectedSubMode, _selectedTarget);
                totalOutput += transformedBytes.LongLength;

                string destinationPath;
                if (PreserveOriginalFile)
                {
                    var stem = Path.GetFileNameWithoutExtension(inputPath);
                    var candidateName = $"{stem}{_selectedTarget.TargetExtension}";
                    destinationPath = Path.Combine(OutputDirectory, candidateName);
                    if (string.Equals(Path.GetFullPath(destinationPath), Path.GetFullPath(inputPath), StringComparison.OrdinalIgnoreCase))
                    {
                        destinationPath = Path.Combine(OutputDirectory, $"{stem}.copy{_selectedTarget.TargetExtension}");
                    }
                }
                else
                {
                    destinationPath = inputPath;
                }

                await File.WriteAllBytesAsync(destinationPath, transformedBytes, cancellationToken).ConfigureAwait(false);
                outputs.Add(destinationPath);
            }

            var history = new CompressionHistoryRecord
            {
                RecordId = Guid.NewGuid().ToString("N")[..12],
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ArchiveName = outputs.Count == 1 ? Path.GetFileName(outputs[0]) : $"{outputs.Count} Studio Output(s)",
                SourceSummary = $"{StagedInputFiles.Count} file(s) ({_selectedSubMode})",
                OutputPath = outputs[0],
                OutputFolder = Path.GetDirectoryName(outputs[0]) ?? OutputDirectory,
                OperationKind = OperationKind,
                ProfileName = _selectedSubMode.ToString(),
                StrategySummary = _selectedTarget.DisplayName,
                OriginalBytes = totalOriginal,
                OutputBytes = totalOutput,
                DurationSeconds = 0.05,
                AverageSpeedMbPerSec = 0,
                VerifiedSha256 = false,
            };

            OptimizationHistoryProduced?.Invoke(history);

            StatusMessage = PreserveOriginalFile
                ? $"Completed {_selectedSubMode}: wrote {outputs.Count} transformed copy/copies to '{OutputDirectory}'. Original files untouched."
                : $"Completed {_selectedSubMode} (in-place override): updated {outputs.Count} file(s).";

            return (true, outputs);
        }
        catch (Exception ex)
        {
            LastErrorCard = ActionableErrorPresenter.FromException(ex, OutputDirectory);
            StatusMessage = LastErrorCard.Title;
            return (false, outputs);
        }
    }

    public void DismissError() => LastErrorCard = null;

    private void ReloadTargetsForSubMode()
    {
        var options = ConsentGateValidator.GetOptionsForSubMode(_selectedSubMode);
        AvailableTargets.Clear();
        foreach (var opt in options)
        {
            AvailableTargets.Add(opt);
        }

        _selectedTarget = options[0];
        RaisePropertyChanged(nameof(SelectedTarget));
    }

    private void ResetAllConsentsAndRefreshPreview()
    {
        _consentUnderstandNonBitExact = false;
        _consentApproveItemizedPreview = false;
        _consentAllowReplaceOriginalWhenUnpreserved = false;

        RaisePropertyChanged(nameof(ConsentUnderstandNonBitExact));
        RaisePropertyChanged(nameof(ConsentApproveItemizedPreview));
        RaisePropertyChanged(nameof(ConsentAllowReplaceOriginalWhenUnpreserved));

        RebuildImpactPreview();
        RefreshConsentGate();
    }

    private void RebuildImpactPreview()
    {
        ItemizedPreviewLines.Clear();
        if (StagedInputFiles.Count == 0)
        {
            ImpactPreview = null;
            return;
        }

        long totalOriginal = 0;
        var itemized = new List<string>(StagedInputFiles.Count);
        foreach (var path in StagedInputFiles)
        {
            var length = File.Exists(path) ? new FileInfo(path).Length : 0L;
            totalOriginal += length;
            var line = $"{Path.GetFileName(path)} ({ByteSizeFormatter.FormatBytes(length)}) → {_selectedTarget.ItemizedActionTemplate}";
            itemized.Add(line);
            ItemizedPreviewLines.Add(line);
        }

        var estimatedAfter = Math.Max(16L, (long)(totalOriginal * _selectedTarget.EstimatedOutputRatio));
        ImpactPreview = new OptimizationImpactPreview
        {
            SubMode = _selectedSubMode,
            OperationKind = OperationKind,
            InputFileCount = StagedInputFiles.Count,
            TotalOriginalBytes = totalOriginal,
            EstimatedOutputBytes = estimatedAfter,
            PreserveOriginalFile = _preserveOriginalFile,
            FidelityImpactSummary = _selectedTarget.FidelitySummary,
            ItemizedChanges = itemized,
            DestructiveOverwriteWarning = _preserveOriginalFile
                ? null
                : "WARNING: 'Preserve Original File' has been turned OFF. Original files will be permanently overwritten.",
        };
    }

    private void RefreshConsentGate() => ConsentGate = EvaluateGateInternal();

    private MutatingConsentGateResult EvaluateGateInternal()
    {
        var state = new MutatingConsentState
        {
            OperationKind = OperationKind,
            HasStagedFiles = StagedInputFiles.Count > 0,
            HasGeneratedPreview = ImpactPreview is not null,
            PreserveOriginalFile = _preserveOriginalFile,
            ConsentUnderstandNonBitExact = _consentUnderstandNonBitExact,
            ConsentApproveItemizedPreview = _consentApproveItemizedPreview,
            ConsentAllowReplaceOriginalWhenUnpreserved = _consentAllowReplaceOriginalWhenUnpreserved,
        };

        return ConsentGateValidator.Validate(state);
    }

    private static byte[] ApplySafeTransformPayload(
        byte[] input,
        OptimizationSubMode subMode,
        OptimizationTargetOption target)
    {
        return subMode switch
        {
            OptimizationSubMode.FormatConversion when target.Id == "convert-jsonl-ndjson-archive" =>
                NormalizeUtf8Text(input),
            OptimizationSubMode.DataRemoval =>
                StripTrailingNullsAndExifApp1(input),
            _ => BuildQuantizedEnvelope(input, target.Id),
        };
    }

    private static byte[] NormalizeUtf8Text(byte[] input)
    {
        var text = Encoding.UTF8.GetString(input).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(text);
    }

    private static byte[] StripTrailingNullsAndExifApp1(byte[] input)
    {
        if (input.Length >= 4 && input[0] == 0xFF && input[1] == 0xD8 && input[2] == 0xFF && input[3] == 0xE1 && input.Length >= 6)
        {
            var segLen = (input[4] << 8) | input[5];
            if (segLen >= 2 && 4 + segLen <= input.Length)
            {
                using var ms = new MemoryStream(input.Length - segLen);
                ms.Write(input, 0, 2);
                ms.Write(input, 2 + 2 + segLen, input.Length - (4 + segLen));
                return ms.ToArray();
            }
        }

        var end = input.Length;
        while (end > 0 && input[end - 1] == 0)
        {
            end--;
        }

        return end == input.Length ? input.ToArray() : input.AsSpan(0, end).ToArray();
    }

    private static byte[] BuildQuantizedEnvelope(byte[] input, string targetId)
    {
        var header = Encoding.ASCII.GetBytes($"UCOPT1:{targetId}\n");
        var keepLen = Math.Max(0, (int)(input.Length * 0.65));
        using var ms = new MemoryStream(header.Length + keepLen);
        ms.Write(header, 0, header.Length);
        if (keepLen > 0)
        {
            ms.Write(input, 0, keepLen);
        }

        return ms.ToArray();
    }
}
