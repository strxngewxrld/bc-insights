# bc-insights — Business Central Telemetry Proxy

A C# proxy that makes an on-premises **Business Central** server believe it is
talking to **Azure Application Insights**, translates the telemetry into
**OpenTelemetry (OTLP)**, and forwards it to a **self-managed GitLab
Observability** instance (GitLab 18.1+). Windows Event Log entries are shipped
alongside it, so both BC telemetry and eventlog show up in GitLab.

## Architecture

```
BC on-prem  (App Insights ConnectionString: IngestionEndpoint = https://bcproxy.example.com/)
   │  POST /v2/track  (gzip, NDJSON envelopes)
   ▼
BcTelemetryProxy  (ASP.NET Core, hosted in IIS, in-process)
   • fakes the App Insights ingestion endpoint (/v2/track, /v2.1/track)
   • parses envelopes and maps them to OTLP logs / spans / metrics
   • answers in the AI response format so BC sees a healthy endpoint
   • exports OTLP/HTTP to the local OpenTelemetry Collector (localhost:4318)
   ▼
OpenTelemetry Collector  (Windows service, otelcol-contrib)
   • otlp receiver (from the proxy)
   • windowseventlog receiver (Windows Event Log)
   • otlphttp exporter → GitLab Observability
   ▼
Self-managed GitLab 18.1+ Observability  →  Traces / Logs / Metrics UI
```

The Collector is the single component that talks to GitLab — it also handles the
eventlog, so GitLab connectivity (endpoint, batching, retry) lives in one place.

## Telemetry mapping

| AI `baseType`          | OTLP signal | Notes                                                        |
| ---------------------- | ----------- | ------------------------------------------------------------ |
| `MessageData`          | Log         | `severityLevel`→level, `message`→body, `eventId`→event name  |
| `EventData`            | Log (Info)  | `name`→body                                                  |
| `ExceptionData`        | Log (Error) | `exception.type` / `.message` / `.stacktrace` attributes     |
| `RequestData`          | Span/Server | duration, `responseCode`, `success`, `url.full`              |
| `RemoteDependencyData` | Span/Client | `bc.dependency.type` / `.target` / `.data` / `.result_code`  |
| `MetricData`           | Metric      | histogram record per metric point                            |

All AI context tags (`ai.cloud.role`, `ai.operation.id`, `ai.user.id`, …) and
custom dimensions (`customDimensions`) are carried through as attributes. The BC
`ai.operation.id` becomes the OTLP trace id so related telemetry correlates into
a single trace in GitLab.

> **Note:** log timestamps use the proxy's receive time rather than the original
> envelope time (the public OpenTelemetry logging API does not expose a settable
> timestamp). For a near-real-time proxy the difference is seconds.

## Repository layout

```
src/BcTelemetryProxy/           ASP.NET Core proxy (net8.0)
tests/BcTelemetryProxy.Tests/   xUnit unit + integration tests
deploy/iis/                     IIS publish/host script + web.config
deploy/otel-collector/          Collector config + Windows service install script
samples/                        Example BC /v2/track payload (NDJSON)
.github/workflows/ci.yml        Build + test
```

## Build & test

```bash
dotnet build
dotnet test
```

## Run locally

```bash
# 1. Start a Collector that just prints what it receives (debug exporter).
#    (Any otelcol-contrib with the provided config; set GITLAB_OTLP_ENDPOINT.)
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

1. **Collector** — `deploy/otel-collector/Install-Collector.ps1 -GitLabOtlpEndpoint "http://gitlab-observability.example.com:4318"`
2. **Proxy on IIS** — install the ASP.NET Core 8 Hosting Bundle, then
   `deploy/iis/Install-IISSite.ps1 -Port 8888`
3. **Point BC at the proxy** — set the Application Insights connection string on
   the BC server instance:
   ```
   InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://bcproxy.example.com/
   ```
   (`Set-NAVServerConfiguration`/`Mount-NAVTenant -ApplicationInsightsConnectionString`).

See [`docs/deployment.md`](docs/deployment.md) for details and the open items to
verify against your GitLab version.
