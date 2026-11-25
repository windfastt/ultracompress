namespace UltraCompress.Core.Safety;

/// <summary>
/// Thrown when a mutating operation (<see cref="OperationKind.LossyOptimization"/>,
/// <see cref="OperationKind.FormatConversion"/>, or <see cref="OperationKind.DataRemoval"/>)
/// is attempted without valid explicit consent or violates original-preservation guarantees.
/// </summary>
public sealed class SafetyConsentRequiredException : InvalidOperationException
{
    public SafetyConsentRequiredException(OperationKind operationKind, string reasonCode, string message)
        : base(message)
    {
        OperationKind = operationKind;
        ReasonCode = reasonCode;
    }

    public OperationKind OperationKind { get; }

    public string ReasonCode { get; }
}

/// <summary>
/// Thrown when a <see cref="CompressionProfile"/> or lossless pipeline attempts to execute
/// a non-lossless transform, mutating <see cref="OperationKind"/>, or unverified output.
/// </summary>
public sealed class SafetyPolicyViolationException : InvalidOperationException
{
    public SafetyPolicyViolationException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

/// <summary>
/// Thrown when mandatory bit-exact roundtrip verification (byte sequence, SHA-256, or XXH64) fails.
/// </summary>
public sealed class BitExactVerificationException : IOException
{
    public BitExactVerificationException(string message, BitExactVerificationResult? result = null)
        : base(message)
    {
        VerificationResult = result;
    }

    public BitExactVerificationResult? VerificationResult { get; }
}
