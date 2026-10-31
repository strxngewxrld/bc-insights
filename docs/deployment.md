# Deployment guide

This document covers deploying the Business Central telemetry proxy on-premises
and connecting it to GitLab Observability (or any other OTLP/HTTP backend).

## Components

1. **BcTelemetryProxy** — ASP.NET Core app hosted in IIS (in-process). Receives
   App Insights telemetry from any number of BC server instances and exports
   OTLP/HTTP directly to the backend.
2. **Observability backend** — GitLab Observability or any OTLP/HTTP endpoint.
3. *(optional)* **OpenTelemetry Collector** — only if you also want Windows
   Event Logs or fan-out to several backends. Then set `Otlp:Endpoint` to the
   Collector (`http://localhost:4318`) and let the Collector forward.

### Topology for several servers

Every BC server instance names itself in the URL path, so data from different
hosts and instances stays separate in the backend, whether they share a proxy or not.

**Prefer one proxy per BC server** (IIS on the same machine, `IngestionEndpoint`
on `localhost`): BC does **not** buffer or resend telemetry it cannot deliver, so
anything sent while the proxy is unreachable is lost. A local, always-on proxy
removes the network hop as a failure point; the proxy itself buffers and retries
towards the backend (`Otlp:MaxQueueSize`, `Otlp:MaxRetrySeconds`). A shared proxy
per site works too, but then make it highly available.

## 1. Backend endpoint

**GitLab Observability (GitLab.com):** the group's observability settings show
the OTLP endpoints, e.g.

```
HTTPS  https://<id>.otel.gitlab-o11y.com:14318   ← use this
HTTP   http://<id>.otel.gitlab-o11y.com:4318     (unencrypted, lab only)
```

Set `Otlp:Endpoint` to the HTTPS base URL; the proxy appends `/v1/logs`,
`/v1/traces` and `/v1/metrics`. The per-group endpoint currently accepts data
without an auth header. If your backend requires one, add it under
`Otlp:Headers` (e.g. env var `Otlp__Headers__Authorization`), never in source control.

## 2. Proxy on IIS

Prerequisites on the proxy host:

- **ASP.NET Core 8 Hosting Bundle** — <https://dotnet.microsoft.com/download/dotnet/8.0>
- IIS with the ASP.NET Core Module V2 (installed by the Hosting Bundle).
- Outbound HTTPS from the proxy host to the backend (port 14318 for GitLab).

```powershell
# Run as Administrator
.\deploy\iis\Install-IISSite.ps1 -SiteName "BcTelemetryProxy" -Port 8888 `
    -OtlpEndpoint "https://<id>.otel.gitlab-o11y.com:14318"
```

Configuration lives in `appsettings.json`, overridable via environment
variables (`Section__Key`):

| Section     | Purpose                                                             |
| ----------- | ------------------------------------------------------------------- |
| `Otlp`      | endpoint, headers, gzip, timeout, queue size, batch size/delay, retry window |
| `Resource`  | service name/namespace, environment, fallback host/instance, per-source overrides |
| `Ingestion` | request size / count limits                                         |
| `Telemetry` | attribute allow/deny/redact/hash lists                              |
| `Debug`     | optional capture of raw envelopes / exported OTLP (troubleshooting) |

## 3. Point Business Central at the proxy

Per BC server instance, set the Application Insights connection string so the
`IngestionEndpoint` targets the proxy **and names host and instance**. The
instrumentation key can be any GUID.

```powershell
$host_ = $env:COMPUTERNAME; $instance = "BC290"
Set-NAVServerConfiguration -ServerInstance $instance `
    -KeyName ApplicationInsightsConnectionString `
    -KeyValue "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://bcproxy.example.com:8888/$host_/$instance/"
Restart-NAVServerInstance -ServerInstance $instance
```

For multitenant instances use `Mount-NAVTenant -ApplicationInsightsConnectionString "..."`
per tenant with the same URL; tenants are distinguished by the `bc.tenant.id` attribute.

## 4. What arrives in GitLab

- **Services:** `service.name` (default `business-central`), one
  `service.instance.id` per `<host>/<instance>`; filter by `host.name`,
  `bc.server_instance`, `deployment.environment`.
- **Logs:** every BC signal (RT0005 long-running SQL, RT0018 long-running AL,
  LC* extension lifecycle, …) with `event.name` = BC event id, original timestamp,
  severity, and all custom dimensions; numeric `*InMs` companions for durations.
- **Traces:** requests (server spans) and dependencies (client spans), correlated
  with logs via trace id.
- **Metrics:** BC metric telemetry as gauges.

## Security model

- **TLS on the ingestion endpoint** — bind a certificate to the IIS site for
  production. Plain HTTP is acceptable only on a trusted network / lab.
- **TLS on the egress** — use the backend's HTTPS endpoint.
- **Endpoint protection** — restrict who can reach the proxy via firewall /
  host allowlist and/or mTLS at IIS. BC's Application Insights client cannot add
  auth headers, so network controls are the appropriate protection.
- **No instrumentation-key validation — by design.** The key is not a secret in
  this topology; sources are identified by URL path.
- **Attribute privacy** — review the `Telemetry` allow/deny/redact/hash lists.

## Ingestion and export limits

| Setting                                  | Default | Effect                                        |
| ---------------------------------------- | ------- | --------------------------------------------- |
| `Ingestion:MaxCompressedRequestBytes`    | 10 MiB  | oversized compressed bodies → 413             |
| `Ingestion:MaxDecompressedRequestBytes`  | 100 MiB | bounds decompressed payload → 413             |
| `Ingestion:MaxEnvelopeBytes`             | 1 MiB   | oversized single envelope → per-item error    |
| `Ingestion:MaxEnvelopesPerRequest`       | 10 000  | too many items → 413                          |
| `Otlp:MaxQueueSize`                      | 100 000 | queue full → 503 to BC (BC retries)           |
| `Otlp:MaxBatchSize`                      | 2 000   | items per export request                      |
| `Otlp:BatchDelayMilliseconds`            | 2 000   | wait for more items before sending            |
| `Otlp:MaxRetrySeconds`                   | 300     | retry window before a batch is dropped        |

## Verification

1. **Health** — `GET /health` → `status`, `queueLength`, `exported`, `failed`,
   `rejected`, `lastSuccessUtc`, `lastError`.
2. **Replay** — post `samples/sample-track-payload.ndjson` to
   `/SRV01/BC290/v2/track`; expect `{"itemsReceived":6,"itemsAccepted":6,...}` and
   `exported` counts rising on `/health`.
3. **Backend** — in GitLab Observability, filter logs by
   `service.instance.id = SRV01/BC290`.
4. **BC** — sign in to BC and open a few pages; BC sends telemetry in batches
   about every 30 seconds. A sign-in produces `RT0004`, an error dialog `RT0030`
   (severity ERROR).

## Troubleshooting

- `GET /health` → `status: degraded` and `lastError` show export problems
  (unreachable backend, TLS, HTTP 4xx); `failed` counts items given up after
  `Otlp:MaxRetrySeconds`.
- BC gets `503` when the export queue is full (backend down for a long time);
  BC then drops that batch.
- **Mapping check:** set `Debug:CaptureDirectory` (e.g. env
  `Debug__CaptureDirectory=C:\bcproxy\capture`) and restart. Every received
  envelope (`kind: ingest`, with source and mapping status) and every OTLP request
  (`kind: export`) is appended to `capture-yyyyMMdd.ndjson` until
  `Debug:MaxCaptureBytes` (100 MiB) is reached. The capture is unfiltered — it may
  contain personal data; switch it off and delete the files afterwards.
- `Logging:LogLevel:BcTelemetryProxy=Debug` logs one line per ingestion request
  (source, received, accepted, errors).
