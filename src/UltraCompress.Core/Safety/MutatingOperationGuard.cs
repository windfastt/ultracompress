namespace UltraCompress.Core.Safety;

/// <summary>
/// Runtime consent and original-preservation gate for mutating operations
/// (<see cref="OperationKind.LossyOptimization"/>, <see cref="OperationKind.FormatConversion"/>, <see cref="OperationKind.DataRemoval"/>).
/// </summary>
public static class MutatingOperationGuard
{
    /// <summary>
    /// Validates whether an operation of <paramref name="operationKind"/> is permitted to proceed.
    /// Throws <see cref="SafetyConsentRequiredException"/> for any mutating operation lacking valid explicit consent
    /// or attempting to overwrite the original file without explicit double-confirmation.
    /// </summary>
    public static void Enforce(
        OperationKind operationKind,
        OperationConsentToken? consentToken = null,
        string? sourcePath = null,
        string? destinationPath = null)
    {
        if (!Enum.IsDefined(operationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, "Undefined operation kind.");
        }

        var sameSourceAndDestination = PathsReferToSameLocation(sourcePath, destinationPath);

        if (operationKind == OperationKind.LosslessCompression)
        {
            if (sameSourceAndDestination)
            {
                throw new SafetyPolicyViolationException(
                    "LOSSLESS_IN_PLACE_OVERWRITE_FORBIDDEN",
                    "Lossless compression must write to a distinct archive/output path and never overwrite the source file in-place.");
            }

            return;
        }

        if (consentToken is null)
        {
            throw new SafetyConsentRequiredException(
                operationKind,
                "CONSENT_TOKEN_REQUIRED",
                $"Operation '{operationKind}' modifies or discards original bit-identity and requires an explicit OperationConsentToken.");
        }

        if (consentToken.OperationKind != operationKind)
        {
            throw new SafetyConsentRequiredException(
                operationKind,
                "CONSENT_KIND_MISMATCH",
                $"Consent token was issued for '{consentToken.OperationKind}', not '{operationKind}'.");
        }

        if (!consentToken.AcknowledgeNonReversible)
        {
            throw new SafetyConsentRequiredException(
                operationKind,
                "NON_REVERSIBILITY_NOT_ACKNOWLEDGED",
                $"Operation '{operationKind}' requires explicit acknowledgement of non-reversibility (AcknowledgeNonReversible = true).");
        }

        if (consentToken.TargetParameters is null
            || consentToken.TargetParameters.Count == 0
            || consentToken.TargetParameters.Any(kv => string.IsNullOrWhiteSpace(kv.Key)))
        {
            throw new SafetyConsentRequiredException(
                operationKind,
                "TARGET_PARAMETERS_REQUIRED",
                $"Operation '{operationKind}' requires explicit non-empty target parameters in OperationConsentToken.");
        }

        if (consentToken.ImpactPreview is null
            || consentToken.ImpactPreview.OperationKind != operationKind
            || string.IsNullOrWhiteSpace(consentToken.ImpactPreview.Summary)
            || consentToken.ImpactPreview.Items is null
            || consentToken.ImpactPreview.Items.Count == 0
            || consentToken.ImpactPreview.Items.Any(item =>
                item is null
                || string.IsNullOrWhiteSpace(item.TargetPath)
                || string.IsNullOrWhiteSpace(item.TransformId)
                || string.IsNullOrWhiteSpace(item.Description)))
        {
            throw new SafetyConsentRequiredException(
                operationKind,
                "IMPACT_PREVIEW_REQUIRED",
                $"Operation '{operationKind}' requires a valid itemized OperationImpactPreview before execution.");
        }

        if (consentToken.PreserveOriginal)
        {
            if (sameSourceAndDestination)
            {
                throw new SafetyConsentRequiredException(
                    operationKind,
                    "PRESERVE_ORIGINAL_VIOLATION",
                    $"Operation '{operationKind}' has PreserveOriginal = true and cannot overwrite the source path '{sourcePath}' in-place.");
            }

            return;
        }

        // PreserveOriginal == false requires explicit double-confirmation flags.
        if (!consentToken.AllowInPlaceOverwrite || !consentToken.ConfirmDestructiveInPlaceOverwrite)
        {
            throw new SafetyConsentRequiredException(
                operationKind,
                "DOUBLE_CONFIRMATION_REQUIRED",
                $"Overriding PreserveOriginal = false for '{operationKind}' requires both AllowInPlaceOverwrite = true and ConfirmDestructiveInPlaceOverwrite = true.");
        }
    }

    /// <summary>
    /// Rejects any attempt to run a mutating <see cref="OperationKind"/> inside a <see cref="CompressionProfile"/> workflow,
    /// even if a consent token is provided — compression profiles are strictly lossless.
    /// </summary>
    public static void EnforceProfileBoundary(CompressionProfile profile, OperationKind requestedKind)
    {
        var allowed = SafetyPolicy.GetOperationKind(profile);
        if (requestedKind != allowed)
        {
            throw new SafetyPolicyViolationException(
                "MUTATING_MODE_BANNED_IN_PROFILE",
                $"CompressionProfile '{profile}' is strictly bit-exact lossless and cannot execute '{requestedKind}'.");
        }
    }

    private static bool PathsReferToSameLocation(string? sourcePath, string? destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
        {
            return false;
        }

        var fullSource = Path.GetFullPath(sourcePath);
        var fullDest = Path.GetFullPath(destinationPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(fullSource, fullDest, comparison);
    }
}
