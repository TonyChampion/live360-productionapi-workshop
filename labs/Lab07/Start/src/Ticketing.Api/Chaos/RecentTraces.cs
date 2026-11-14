using System.Collections.Concurrent;
using System.Diagnostics;

namespace Ticketing.Api.Chaos;

/// <summary>
/// Lab infrastructure, not lab content: records recent activities (spans) in memory so the lab
/// client can draw them, with no Docker or tracing backend required. Production apps export the
/// same spans with OpenTelemetry instead (Lab 6).
/// </summary>
public sealed class RecentTraces
{
    public static readonly RecentTraces Instance = new();

    private const int MaxSpans = 1000;
    private readonly ConcurrentQueue<Span> _spans = new();

    public record Span(
        string TraceId, string SpanId, string? ParentSpanId, string Source, string Name,
        DateTime StartUtc, double DurationMs, string Status,
        Dictionary<string, string?> Tags, List<string> Events);

    private RecentTraces()
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Microsoft.AspNetCore" or "Ticketing.Api",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Record
        });
    }

    private void Record(Activity activity)
    {
        _spans.Enqueue(new Span(
            activity.TraceId.ToString(),
            activity.SpanId.ToString(),
            activity.ParentSpanId == default ? null : activity.ParentSpanId.ToString(),
            activity.Source.Name,
            activity.DisplayName,
            activity.StartTimeUtc,
            activity.Duration.TotalMilliseconds,
            activity.Status.ToString(),
            activity.TagObjects.ToDictionary(t => t.Key, t => t.Value?.ToString()),
            activity.Events.Select(e => e.Name).ToList()));

        while (_spans.Count > MaxSpans && _spans.TryDequeue(out _)) { }
    }

    /// <summary>The most recent API requests, each with its spans in start order.</summary>
    public IEnumerable<object> Recent(int count = 15) =>
        _spans
            .GroupBy(s => s.TraceId)
            .Select(g => new
            {
                Root = g.FirstOrDefault(s => s.Source == "Microsoft.AspNetCore"),
                Spans = g.OrderBy(s => s.StartUtc).ToList()
            })
            .Where(t => t.Root is not null && t.Root.Name.Contains("/api/"))
            .OrderByDescending(t => t.Root!.StartUtc)
            .Take(count)
            .Select(t => new
            {
                traceId = t.Root!.TraceId,
                name = t.Root.Name,
                startUtc = t.Root.StartUtc,
                durationMs = Math.Round(t.Root.DurationMs, 1),
                spans = t.Spans.Select(s => new
                {
                    s.SpanId, s.ParentSpanId, s.Source, s.Name, s.Status, s.Tags, s.Events,
                    offsetMs = Math.Round((s.StartUtc - t.Root.StartUtc).TotalMilliseconds, 1),
                    durationMs = Math.Round(s.DurationMs, 1)
                })
            });
}
