# bc-insights — Business Central Telemetry Proxy

A C# proxy that makes on-premises **Business Central** server instances believe
they are talking to **Azure Application Insights**, translates the telemetry into
**OpenTelemetry (OTLP)** and exports it **directly** to an OTLP backend such as
**GitLab Observability**. One proxy serves any number of BC server instances on
any number of hosts; each keeps its own identity in the backend.

## Architecture

```
BC server SRV01 / BC290 ──┐  IngestionEndpoint = https://bcproxy:8443/SRV01/BC290/
BC server SRV01 / BC250 ──┤  IngestionEndpoint = https://bcproxy:8443/SRV01/BC250/
BC server SRV02 / BC290 ──┘  IngestionEndpoint = https://bcproxy:8443/SRV02/BC290/
          │  POST /<host>/<instance>/v2/track  (gzip, NDJSON envelopes)
          ▼
BcTelemetryProxy  (ASP.NET Core, IIS in-process)
   • fakes the App Insights ingestion endpoint (/v2/track, /v2.1/track)
   • resolves the source (host + BC instance) → one OTLP resource per source
   • maps envelopes to OTLP logs / spans / metrics (semantic conventions + bc.*)
   • filters / redacts / hashes attributes before export
   • bounded queue → batched OTLP/HTTP JSON (gzip), retry + backoff,
     503 backpressure to BC when the queue is full
          ▼
GitLab Observability (or any OTLP/HTTP endpoint, optionally via a Collector)
```

No Collector is required. A Collector remains an option when you also want to
ship Windows Event Logs or fan out to several backends
([`deploy/otel-collector/`](deploy/otel-collector/)); point `Otlp:Endpoint` at it.

## Sources: several instances and servers

The OTLP resource of every item is derived per envelope:

| Resource attribute                                    | Source                                                                    |
| ----------------------------------------------------- | ------------------------------------------------------------------------- |
| `host.name`                                           | URL `/<host>/…` › `ai.cloud.roleInstance` › `ai.internal.nodeName` › `Resource:HostName` › caller IP |
| `bc.server_instance`                                  | URL `/…/<instance>/` › `Resource:BcServerInstance`                         |
| `service.instance.id`                                 | `<host>/<instance>`                                                       |
| `service.name`, `service.namespace`                   | `Resource` defaults, overridable per source                               |
| `deployment.environment.name` (+ legacy `deployment.environment`) | `Resource:DeploymentEnvironment`, overridable per source      |

Accepted URL forms: `/v2/track`, `/<instance>/v2/track`, `/<host>/<instance>/v2/track`
(also `v2.1`). Segments are limited to `[A-Za-z0-9._-]`, max. 64 characters.
Per-source overrides live in `Resource:Sources`, keyed by `host/instance`,
`instance` or `host` (most specific wins):

```json
"Resource": {
  "DeploymentEnvironment": "production",
  "Sources": {
    "BC290-TEST": { "DeploymentEnvironment": "test" },
    "SRV02/BC290": { "ServiceName": "business-central-sales" }
  }
}
```

Tenant, company and environment are **record attributes** (`bc.tenant.id`,
`bc.company.name`, `bc.environment`), since one instance can serve many tenants.

## Telemetry mapping

| AI `baseType`          | OTLP signal | Notes                                                        |
| ---------------------- | ----------- | ------------------------------------------------------------ |
| `MessageData`          | Log         | `severityLevel` (name or number)→severity, `message`→body, `eventId`→`event.name` |
| `EventData`            | Log (Info)  | `name`→body                                                  |
| `ExceptionData`        | Log (Error) | `exception.type` / `.message` / `.stacktrace`                |
| `RequestData`          | Span/Server | duration, `http.response.status_code`, `url.full`, status    |
| `RemoteDependencyData` | Span/Client | `bc.dependency.type` / `.target` / `.data` / `.result_code`  |
| `MetricData`           | Gauge       | value at original time; `bc.metric.count/min/max/stddev`     |
| *(anything else)*      | Log (Warn)  | flagged `telemetry.mapping.status=unsupported`, counted      |

- **Original timestamps** are kept for every signal (logs included).
- **Log ↔ trace correlation:** a log carries `traceId`/`spanId` only when it
  belongs to a span (valid `ai.operation.parentId`). BC on-premises reuses one
  `ai.operation.id` across sessions, which would otherwise merge unrelated logs
  into one trace; the id is always available as `bc.operation.id`.
- **Numeric dimensions:** BC durations (`"00:00:01.25"`) are exported as numeric
  `<key>InMs` (BC's own `*InMs` value wins); known counters (`sqlExecutes`,
  `sqlRowsRead`, …) as integers and `True`/`False` as booleans, so they can be
  aggregated and filtered in the backend.
- **One key per value:** well-known dimensions are renamed (`companyName` →
  `bc.company.name`, `aadTenantId` → `bc.tenant.id`, `clientType` →
  `bc.client_type`, `componentVersion` → `bc.version`, `environmentType` →
  `bc.environment.type`, `httpStatusCode` → `http.response.status_code`);
  `eventId` is `event.name`. All other dimensions keep their BC names. Privacy
  rules match the BC name as well as the exported name.
- **Placeholders dropped:** BC's `"N/A"` (tags) and `"NaN"` (dimensions) are
  treated as missing.

### Trace context

`ai.operation.id` becomes the trace id (see `Mapping/TraceContextMapper.cs`): a
valid 32-hex W3C id is used unchanged; any other id is deterministically hashed
(SHA-256, first 16 bytes); a missing id yields a random trace id for spans and no
trace context for logs. A valid `ai.operation.parentId` is preserved as parent;
otherwise the span is a root span. A request's own 16-hex `id` becomes its span id.

## Hardening at a glance

- **Ingestion limits** (`Ingestion`): compressed / decompressed / per-envelope
  size and envelope count; over-limit requests get `413`.
- **Partial success**: per-envelope evaluation, `206` with indexed `errors[]`.
- **Backpressure**: if the export queue (`Otlp:MaxQueueSize`) cannot take a
  batch, the proxy answers `503`, which the BC telemetry client retries.
- **Export reliability**: batching, gzip, exponential backoff with `Retry-After`
  for 408/429/502/503/504 and network errors (`Otlp:MaxRetrySeconds`); OTLP
  partial-success rejections are logged and counted.
- **Attribute privacy** (`Telemetry`): allowlist / denylist / redaction / hashing.
- **Observability of the proxy**: `GET /health` reports `status` (`degraded`
  while the latest export failed), queue length, exported / dropped / failed /
  rejected counts, last success, last error and its time.
- **Debug capture** (`Debug:CaptureDirectory`, off by default): writes raw BC
  envelopes and the exported OTLP JSON to `capture-yyyyMMdd.ndjson` for mapping
  checks; size-limited, unfiltered (may contain personal data).

> **Availability matters:** BC does not buffer telemetry it cannot deliver. While
> the proxy is unreachable, telemetry is lost (verified with BC 29). Run the proxy
> close to BC (e.g. IIS on each BC server, `localhost`) and keep it always on.

## Repository layout

```
src/BcTelemetryProxy/
  Configuration/  options (Ingestion, Otlp, Resource, Telemetry)
  Diagnostics/    DebugCapture (optional raw/OTLP capture)
  Ingestion/      endpoints, envelope reader, limits, SourceResolver
  Mapping/        AiToOtelMapper + TraceContextMapper + AttributeMapper + MetricMapper
  Export/         OtlpExporter (queue, batching, retry) + OtlpJsonSerializer
  Model/          AI envelope + translated telemetry items
tests/BcTelemetryProxy.Tests/   xUnit unit + integration tests
deploy/iis/                     IIS publish/host script
deploy/otel-collector/          optional Collector (Windows Event Log, fan-out)
samples/                        example /v2/track payload + real (sanitised) BC 29 envelope
```

## Build & test

```bash
dotnet build
dotnet test
```

## Run locally against GitLab Observability

```powershell
$env:Otlp__Endpoint = "https://<id>.otel.gitlab-o11y.com:14318"
dotnet run --project src/BcTelemetryProxy
# replay the sample as host SRV01, instance BC290:
Invoke-WebRequest http://localhost:5140/SRV01/BC290/v2/track -Method Post `
  -InFile samples/sample-track-payload.ndjson -ContentType application/x-json-stream
# -> {"itemsReceived":6,"itemsAccepted":6,"errors":[]}
Invoke-RestMethod http://localhost:5140/health
```

See [`docs/deployment.md`](docs/deployment.md) for production deployment.
