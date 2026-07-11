# bc-insights — Business Central Telemetry Proxy

A C# proxy that makes an on-premises **Business Central** server believe it is
talking to **Azure Application Insights**, translates the telemetry into
**OpenTelemetry (OTLP)**, and forwards it — via an OpenTelemetry Collector — to
any OTLP-capable observability backend. The proxy is a pure compatibility and
translation layer; the choice of backend lives in the Collector, not in the
proxy. Windows Event Log entries are shipped alongside it.

## Architecture

```
BC on-prem  (App Insights ConnectionString: IngestionEndpoint = https://bcproxy.example.com/)
   │  POST /v2/track  (gzip, NDJSON envelopes)
   ▼
BcTelemetryProxy  (ASP.NET Core, hosted in IIS, in-process)
   • fakes the App Insights ingestion endpoint (/v2/track, /v2.1/track)
   • enforces request size/count limits and reports partial success
   • maps envelopes to OTLP logs / spans / metrics (semantic conventions + bc.*)
   • filters/redacts/hashes attributes before export
   • exports OTLP/HTTP to the local OpenTelemetry Collector (localhost:4318)
   ▼
OpenTelemetry Collector  (Windows service, otelcol-contrib) — central egress
   • otlp receiver (from the proxy)
   • windowseventlog receiver (scoped to BC-relevant providers)
   • batch + retry + sending queue, TLS secure by default
   • otlphttp/backend exporter → OTLP_EXPORT_ENDPOINT
   ▼
Observability backend (OTLP)  →  Traces / Logs / Metrics
```

The Collector is the single, backend-neutral egress component. GitLab
Observability is one supported target (see [`docs/deployment.md`](docs/deployment.md)),
not part of the proxy's core architecture.

## Telemetry mapping

| AI `baseType`          | OTLP signal | Notes                                                        |
| ---------------------- | ----------- | ------------------------------------------------------------ |
| `MessageData`          | Log         | `severityLevel`→level, `message`→body, `eventId`→event name  |
| `EventData`            | Log (Info)  | `name`→body                                                  |
| `ExceptionData`        | Log (Error) | `exception.type` / `.message` / `.stacktrace` attributes     |
| `RequestData`          | Span/Server | duration, `http.response.status_code`, `success`, `url.full` |
| `RemoteDependencyData` | Span/Client | `bc.dependency.type` / `.target` / `.data` / `.result_code`  |
| `MetricData`           | Metric      | classified gauge / counter / histogram (see below)           |
| *(anything else)*      | Log (Warn)  | flagged `telemetry.mapping.status=unsupported`, counted      |

Attributes use OpenTelemetry **semantic conventions** where possible
(`service.instance.id`, `enduser.id`, `http.response.status_code`, `url.full`, …)
and add BC-specific detail under a `bc.*` namespace
(`bc.server_instance`, `bc.operation.id`, `bc.company.name`, `bc.tenant.id`, …);
the two complement each other. Custom dimensions are carried through under their
original keys.

### Trace context

`ai.operation.id` becomes the OTLP trace id (see `Mapping/TraceContextMapper.cs`):
a valid 32-hex W3C id is used unchanged; any other id is deterministically hashed
(SHA-256, first 16 bytes) — never truncated or zero-padded; a missing id yields a
random trace id. A valid `ai.operation.parentId` is preserved; otherwise the span
is a **root span** (no artificial parent is invented).

> **Note:** log timestamps use the proxy's receive time rather than the original
> envelope time (the public OpenTelemetry logging API does not expose a settable
> timestamp). For a near-real-time proxy the difference is seconds.

## Hardening at a glance

- **Ingestion limits** (`appsettings.json` → `Ingestion`): compressed size,
  decompressed size, per-envelope size, envelope count. Over-limit requests are
  rejected with `413`; a faulty payload never crashes the process.
- **Partial success**: each envelope is evaluated individually; the AI response
  reports `itemsReceived` / `itemsAccepted` / `errors[]` with per-envelope index,
  and one bad element never discards the batch (`206 Partial Content`).
- **Attribute privacy** (`Telemetry`): allowlist / denylist / redaction / hashing,
  applied before export.
- **Bounded metrics** (`Metrics.MaxInstruments`): the number of dynamically
  created instruments is capped; excess points are forwarded loss-light as logs.
- **Secure defaults**: Collector TLS verification stays on; plain HTTP is a
  development/lab-only profile.

## Repository layout

```
src/BcTelemetryProxy/
  Configuration/    strongly-typed options (Ingestion, Metrics, Resource, Telemetry)
  Ingestion/        endpoints, envelope reader, request limits (LimitingStream)
  Mapping/          AiToOtelMapper (dispatch) + TraceContextMapper + AttributeMapper
                    + MetricMapper + JsonElementExtensions + OtelEmitter
tests/BcTelemetryProxy.Tests/   xUnit unit + integration tests
deploy/iis/                     IIS publish/host script + web.config
deploy/otel-collector/          Collector config + Windows service install script
samples/                        Example BC /v2/track payload (NDJSON)
.github/workflows/ci.yml        restore → format → build (warnaserror) → test → vuln scan
```

## Build & test

```bash
dotnet build
dotnet test
```

## Run locally

```bash
# 1. Start a Collector with the debug exporter (set OTLP_EXPORT_ENDPOINT to a
#    reachable backend, or swap exporters to `debug` to just print).
# 2. Run the proxy:
dotnet run --project src/BcTelemetryProxy
# 3. Replay the sample payload:
gzip -c samples/sample-track-payload.ndjson > /tmp/p.gz
curl -sS -X POST http://localhost:5000/v2/track \
     -H "Content-Type: application/x-json-stream" \
     -H "Content-Encoding: gzip" \
     --data-binary @/tmp/p.gz
# -> {"itemsReceived":6,"itemsAccepted":6,"errors":[]}
```

## Deploy (Windows / on-prem)

1. **Collector** — `deploy/otel-collector/Install-Collector.ps1 -OtlpExportEndpoint "https://observability.example.com:4318"`
2. **Proxy on IIS** — install the ASP.NET Core 8 Hosting Bundle, then
   `deploy/iis/Install-IISSite.ps1 -Port 8888`
3. **Point BC at the proxy** — set the Application Insights connection string on
   the BC server instance:
   ```
   InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://bcproxy.example.com/
   ```
   (`Set-NAVServerConfiguration` / `Mount-NAVTenant -ApplicationInsightsConnectionString`).

See [`docs/deployment.md`](docs/deployment.md) for backend targets (incl. GitLab),
the security model, and operational details.
