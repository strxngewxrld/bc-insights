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
   • on Windows also collects its own machine: system.* metrics, BC performance
     counters and process metrics per instance, BC Event Log → logs
          ▼
GitLab Observability (or any OTLP/HTTP endpoint, optionally via a Collector)
```

No Collector is required: the proxy also covers server metrics and the BC
Event Log. A Collector ([`deploy/otel-collector/`](deploy/otel-collector/))
remains an option to fan out to several backends; point `Otlp:Endpoint` at it.

What this gives you in GitLab Observability (APM views, dashboard, alert ideas):
[`deploy/gitlab-o11y/`](deploy/gitlab-o11y/).

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
| `MessageData` with a duration | + Span | sign-in, SQL (client, `db.system.name`), AL, web service (client), report … → APM |
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

### Host telemetry (Windows, `HostTelemetry`)

The proxy samples the machine it runs on every `IntervalSeconds` (60):

| Metrics                | Resource                 | Content                                                       |
| ---------------------- | ------------------------ | ------------------------------------------------------------- |
| `system.*`             | `<host>`                 | CPU (`utilization`, `time` per mode), memory, file systems, disk utilization / queue / throughput, network, uptime |
| `bc.server.*`          | `<host>/<instance>`      | every counter of `Microsoft Dynamics NAV(<instance>)`: sessions, operations/s, operation time, SQL connections, caches, tasks, throttling, … |
| `process.*`            | `<host>/<instance>`      | BC server process memory, CPU time, threads, handles          |
| `bcproxy.*`            | `<host>`                 | export queue length, exported / failed / dropped / rejected items, degraded flag |

New Event Log entries of `EventLogChannels` (default: the BC server Admin
channel, warnings and errors) become logs on the instance they name.

### Database tier (`SqlMonitoring`)

The proxy reads each local BC instance's `CustomSettings.config`
(`DatabaseServer`, `DatabaseInstance`, `DatabaseName`). The instance's resource
gets `bc.database.server` / `bc.database.name`, and database events (RT0005 long
running SQL, RT0012 lock timeout, RT0028 deadlock) become client spans to
`peer.service = sql-server` with `server.address` and `db.namespace`.

Without an agent on the database server, the proxy also queries the SQL Server's
dynamic management views (works for a remote server in a 2-tier setup). The data
goes to resource `service.name = sql-server`, `service.instance.id = <host>/<sqlInstance>`:

| Signal | Content |
| ------ | ------- |
| `sqlserver.*` metrics | reachable (`up`), batch requests, connections, blocked processes, page life expectancy, buffer cache hit ratio, lock waits / timeouts / deadlocks, waits by category, file I/O and stall per database (data/log), transaction log size / used, memory, CPU and memory of the database host |
| `sqlserver.blocking` logs | each request blocked ≥ `BlockingThresholdSeconds` (10), logged once: both session ids, wait type / resource, both statements, client app and host |
| `sqlserver.deadlock` span + log | each deadlock from the `system_health` session: failed span with `exception` event (`SqlDeadlock`), victim and other statements, deadlock graph |

The proxy's identity needs `VIEW SERVER STATE` (SQL Server 2022:
`VIEW SERVER PERFORMANCE STATE`), see [deployment](docs/deployment.md#5-database-server-2-tier).

### Errors and support reports (`Support`)

Every error (failed BC event, SQL deadlock) gets `bc.error.fingerprint`: event,
type, AL object, extension and message without ids/numbers. The first time a
fingerprint occurs, the proxy logs `bc.error.first_seen` (for a "new error" alert).
The proxy keeps counts, affected users / sessions and the latest samples in memory:

- `GET /support/errors?hours=24` lists error kinds, most affected users first.
- `GET /support/errors/<fingerprint>` returns a Markdown report for the partner (CSP)
  or Microsoft: BC build, instances, counts, first / last seen, message, AL stack
  trace, AL object, extension and publisher, SQL statement, operation, session and
  trace ids. Local callers only by default (`Support:LocalOnly`).

### Trace context

`ai.operation.id` becomes the trace id (see `Mapping/TraceContextMapper.cs`): a
valid 32-hex W3C id is used unchanged, a GUID as its 32 hex digits, any other id
is deterministically hashed (SHA-256, first 16 bytes); a missing id yields a random
trace id for spans and no trace context for logs. A valid `ai.operation.parentId`
is preserved as parent; otherwise the span is a root span. A request's own 16-hex
`id` becomes its span id.

BC on-premises sets one `ai.operation.id` per client action (a click in the web
client, a web service call, a task) and keeps it within the session
(`ai.session.id`, exported as `bc.session.id`). With `Mapping:CorrelateOperations`
(default on), all spans and logs of one action share its trace: sign-in, AL, long
SQL statements, lock timeouts and the error dialog appear together in one trace.

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
  HostTelemetry/  Windows metrics, BC counters/process, Event Log forwarder,
                  BC instance catalog, SQL Server monitoring
  Ingestion/      endpoints, envelope reader, limits, SourceResolver
  Mapping/        AiToOtelMapper + TraceContextMapper + AttributeMapper + MetricMapper
  Export/         OtlpExporter (queue, batching, retry) + OtlpJsonSerializer
  Model/          AI envelope + translated telemetry items
  Support/        error registry (fingerprints) + /support/errors reports
tests/BcTelemetryProxy.Tests/   xUnit unit + integration tests
deploy/iis/                     IIS publish/host script
deploy/gitlab-o11y/             GitLab Observability dashboard + guide
deploy/otel-collector/          optional Collector (fan-out to several backends)
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
