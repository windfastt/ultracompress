using System.Globalization;

namespace UltraCompress.Desktop.Presentation.Primitives;

public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return $"-{FormatBytes(-bytes)}";
        }

        if (bytes == 0)
        {
            return "0 B";
        }

        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024.0 && unitIndex < Units.Length - 1)
        {
            value /= 1024.0;
            unitIndex++;
        }

        return unitIndex == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:F1} {Units[unitIndex]}");
    }

    public static string FormatPercent(double percent) =>
        string.Create(CultureInfo.InvariantCulture, $"{percent:F1}%");

    public static string FormatSpeed(double mbPerSecond)
    {
        if (mbPerSecond <= 0.001)
        {
            return "0.0 MB/s";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{mbPerSecond:F1} MB/s");
    }

    public static string FormatEta(TimeSpan? eta)
    {
        if (eta is null || eta.Value < TimeSpan.Zero || eta.Value.TotalHours >= 99)
        {
            return "--:--";
        }

        var span = eta.Value;
        if (span.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h {span.Minutes:D2}m");
        }

        if (span.TotalMinutes >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}m {span.Seconds:D2}s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, span.Seconds)}s");
    }
}
