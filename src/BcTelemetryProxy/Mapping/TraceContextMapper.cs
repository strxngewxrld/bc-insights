using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace BcTelemetryProxy.Mapping;

/// <summary>
/// Derives W3C trace context from Business Central's <c>ai.operation.id</c> and
/// <c>ai.operation.parentId</c> without lossy padding or truncation.
/// </summary>
public static class TraceContextMapper
{
    private const int TraceIdHexLength = 32;
    private const int SpanIdHexLength = 16;

    /// <summary>
    /// Resolves the trace id for an operation:
    /// <list type="bullet">
    /// <item>a valid 32-hex W3C trace id is used unchanged,</item>
    /// <item>any other non-empty id is deterministically hashed (SHA-256, first
    /// 16 bytes) so equal ids always yield the same trace id and different ids
    /// effectively never collide,</item>
    /// <item>a missing id yields a random trace id.</item>
    /// </list>
    /// </summary>
    public static ActivityTraceId ResolveTraceId(string? operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return ActivityTraceId.CreateRandom();
        }

        if (IsValidTraceId(operationId))
        {
            return ActivityTraceId.CreateFromString(operationId.AsSpan());
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(operationId), hash);

        var traceIdBytes = hash[..16];
        EnsureNonZero(traceIdBytes);
        return ActivityTraceId.CreateFromBytes(traceIdBytes);
    }

    /// <summary>
    /// Returns the parent span id only when a valid 16-hex W3C span id is
    /// present; otherwise <c>null</c>. No artificial parent is invented, so a
    /// span without a valid parent becomes a root span.
    /// </summary>
    public static ActivitySpanId? ResolveParentSpanId(string? parentId)
    {
        if (!string.IsNullOrWhiteSpace(parentId) && IsValidSpanId(parentId))
        {
            return ActivitySpanId.CreateFromString(parentId.AsSpan());
        }

        return null;
    }

    /// <summary>
    /// Span id of a request/dependency: BC's own id when it is a valid 16-hex W3C
    /// span id (so child items referencing it as parent link up), otherwise random.
    /// </summary>
    public static ActivitySpanId ResolveSpanId(string? id)
        => !string.IsNullOrWhiteSpace(id) && IsValidSpanId(id)
            ? ActivitySpanId.CreateFromString(id.AsSpan())
            : ActivitySpanId.CreateRandom();

    private static bool IsValidTraceId(string value)
        => value.Length == TraceIdHexLength && IsHex(value) && !IsAllZero(value);

    private static bool IsValidSpanId(string value)
        => value.Length == SpanIdHexLength && IsHex(value) && !IsAllZero(value);

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllZero(string value)
    {
        foreach (var c in value)
        {
            if (c != '0')
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureNonZero(Span<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b != 0)
            {
                return;
            }
        }

        // Astronomically unlikely, but an all-zero trace id is invalid.
        bytes[^1] = 1;
    }
}
