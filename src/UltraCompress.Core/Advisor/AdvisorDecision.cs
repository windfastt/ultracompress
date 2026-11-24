namespace UltraCompress.Core.Advisor;

/// <summary>Audit entry for which rule influenced a suggestion.</summary>
public sealed record AdvisorDecision(string RuleId, string Summary);