using System.Globalization;
using System.Net;
using System.Text;
using BcTelemetryProxy.Configuration;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.Support;

/// <summary>
/// <c>GET /support/errors[?hours=24]</c>: error kinds, most users affected first (JSON).<br/>
/// <c>GET /support/errors/{fingerprint}</c>: support report in Markdown, ready to
/// send to the partner (CSP) or Microsoft: versions, instances, counts, message,
/// AL stack, AL object / extension, SQL statement, operation and session ids.
/// </summary>
public static class SupportEndpoints
{
    /// <summary>Details listed first in a report sample, in this order.</summary>
    private static readonly string[] KeyDetails =
    {
        "bc.event.id", "bc.company.name", "bc.client_type", "alObjectType", "alObjectId", "alObjectName",
        "extensionName", "extensionPublisher", "extensionVersion", "extensionId", "failureReason",
        "bc.operation.id", "bc.session.id", "sqlServerSessionId", "enduser.id",
    };

    /// <summary>Shown in their own blocks or not at all.</summary>
    private static readonly HashSet<string> SkippedDetails = new(StringComparer.Ordinal)
    {
        "alStackTrace", "alErrorMessage", "exception.stacktrace", "exception.message", "exception.type",
        "sqlStatement", "db.query.text", "sqlserver.deadlock.graph",
    };

    public static void MapSupport(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<SupportOptions>>().Value;
        if (!options.Enabled)
        {
            return;
        }

        var group = app.MapGroup("/support").AddEndpointFilter(async (context, next) =>
            options.LocalOnly && !IsLocal(context.HttpContext.Connection.RemoteIpAddress)
                ? Results.StatusCode(StatusCodes.Status403Forbidden)
                : await next(context));

        group.MapGet("/errors", (ErrorRegistry registry, double? hours) =>
            Results.Json(registry.List(TimeSpan.FromHours(Math.Clamp(hours ?? 24, 0.1, 24 * 30)))
                .Select(e => new
                {
                    e.Fingerprint,
                    e.Type,
                    Message = Shorten(e.Message, 300),
                    e.EventId,
                    e.ServiceName,
                    e.Count,
                    e.Users,
                    e.Sessions,
                    e.FirstSeenUtc,
                    e.LastSeenUtc,
                    e.Instances,
                    e.Versions,
                    Report = $"/support/errors/{e.Fingerprint}",
                })));

        group.MapGet("/errors/{fingerprint}", (ErrorRegistry registry, string fingerprint) =>
            registry.Find(fingerprint) is { } error
                ? Results.Text(Report(error, options.TraceUrlTemplate), "text/markdown; charset=utf-8")
                : Results.NotFound());
    }

    private static bool IsLocal(IPAddress? address)
        => address is null || IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    /// <summary>Markdown support report for one error kind.</summary>
    public static string Report(ErrorSummary e, string? traceUrlTemplate)
    {
        var sb = new StringBuilder();
        var latest = e.Samples.FirstOrDefault();
        var d = latest?.Details ?? new Dictionary<string, string>();

        sb.Append("# Error report: ").AppendLine(e.Type).AppendLine();
        sb.AppendLine("| | |").AppendLine("|---|---|");
        Row(sb, "Error", e.Type);
        Row(sb, "Telemetry event", e.EventId);
        Row(sb, "Service", e.ServiceName);
        Row(sb, "Server instances", string.Join(", ", e.Instances));
        Row(sb, "Version (build)", string.Join(", ", e.Versions));
        Row(sb, "Occurrences", e.Count.ToString(CultureInfo.InvariantCulture));
        Row(sb, "Affected users / sessions", $"{e.Users} / {e.Sessions}");
        Row(sb, "First / last seen (UTC)", $"{Time(e.FirstSeenUtc)} / {Time(e.LastSeenUtc)}");
        Row(sb, "Extension", Join(d.GetValueOrDefault("extensionName"), d.GetValueOrDefault("extensionPublisher"), d.GetValueOrDefault("extensionVersion")));
        Row(sb, "AL object", Join(d.GetValueOrDefault("alObjectType"), d.GetValueOrDefault("alObjectId"), d.GetValueOrDefault("alObjectName")));
        Row(sb, "Fingerprint", e.Fingerprint);
        sb.AppendLine();

        Block(sb, "Message", e.Message);
        Block(sb, "AL stack trace", e.StackTrace ?? d.GetValueOrDefault("alStackTrace"));
        Block(sb, "SQL statement", d.GetValueOrDefault("sqlStatement") ?? d.GetValueOrDefault("db.query.text"));

        sb.AppendLine("## Latest occurrences").AppendLine();
        foreach (var sample in e.Samples)
        {
            sb.Append("### ").Append(Time(sample.TimeUtc)).Append(" UTC, ").AppendLine(sample.ServiceInstanceId).AppendLine();
            if (sample.TraceId is { } trace)
            {
                var link = string.IsNullOrWhiteSpace(traceUrlTemplate) ? null : traceUrlTemplate.Replace("{traceId}", trace, StringComparison.Ordinal);
                sb.Append("- Trace: `").Append(trace).Append('`').AppendLine(link is null ? string.Empty : $" ({link})");
            }

            foreach (var key in KeyDetails.Where(sample.Details.ContainsKey))
            {
                sb.Append("- ").Append(key).Append(": `").Append(sample.Details[key]).AppendLine("`");
            }

            foreach (var (key, value) in sample.Details.Where(x => !KeyDetails.Contains(x.Key) && !SkippedDetails.Contains(x.Key)))
            {
                sb.Append("- ").Append(key).Append(": `").Append(Shorten(value, 500)).AppendLine("`");
            }

            sb.AppendLine();
        }

        if (d.GetValueOrDefault("sqlserver.deadlock.graph") is { } graph)
        {
            sb.AppendLine("## Deadlock graph").AppendLine().AppendLine("```xml").AppendLine(graph).AppendLine("```");
        }

        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.Append("| ").Append(name).Append(" | ").Append(value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ")).AppendLine(" |");
        }
    }

    private static void Block(StringBuilder sb, string title, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            sb.Append("## ").AppendLine(title).AppendLine().AppendLine("```").AppendLine(text.Trim()).AppendLine("```").AppendLine();
        }
    }

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return present.Length == 0 ? null : string.Join(" ", present);
    }

    private static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string? Shorten(string? text, int max) => text is { } t && t.Length > max ? t[..max] + "…" : text;
}
