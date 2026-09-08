using System.Diagnostics;

namespace DbStudio.Core;

/// <summary>比对阶段耗时，仅包含阶段名和毫秒数，不记录连接凭证或 SQL。</summary>
public sealed record ComparisonTiming(string Stage, double Milliseconds);

/// <summary>记录本次比对的分阶段耗时，并在阶段边界响应取消与报告进度。</summary>
internal sealed class ComparisonMeasurement(IProgress<string>? progress, CancellationToken cancellationToken)
{
    internal List<ComparisonTiming> Timings { get; } = [];

    internal T Run<T>(string stage, Func<T> action)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(stage);
        var watch = Stopwatch.StartNew();
        var result = action();
        Timings.Add(new(stage, watch.Elapsed.TotalMilliseconds));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
