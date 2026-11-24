using UltraCompress.Core;

namespace UltraCompress.Engine;

/// <summary>
/// Engine-facing entry for static file analysis before advisor/recipe selection.
/// </summary>
public sealed class FileAnalysisService
{
    public Task<FileFingerprint> ProbeAsync(
        string path,
        FileProbeOptions? options = null,
        CancellationToken cancellationToken = default) =>
        FileProbe.ProbeAsync(path, options, cancellationToken);
}