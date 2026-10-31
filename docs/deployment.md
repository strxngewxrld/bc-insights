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
| `Mapping`   | spans derived from BC events with a duration (APM)                  |
| `HostTelemetry` | server metrics, BC counters/process, BC Event Log of this machine |

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

Run the proxy **on each BC server** to get that server's metrics, BC
performance counters and Event Log as well (`HostTelemetry`, on by default).
The app pool identity needs read access to performance counters and the event
log: `Install-IISSite.ps1` adds it to *Performance Monitor Users* (S-1-5-32-558)
and *Event Log Readers* (S-1-5-32-573), addressed by SID so localized Windows
works too. Manually:

```powershell
Add-LocalGroupMember -SID "S-1-5-32-558" -Member "IIS AppPool\BcTelemetryProxy"
Add-LocalGroupMember -SID "S-1-5-32-573" -Member "IIS AppPool\BcTelemetryProxy"
```

On a domain controller there are no local groups; use the domain's built-in
groups of the same SID instead.

## 4. What arrives in GitLab

- **Services:** `service.name` (default `business-central`), one
  `service.instance.id` per `<host>/<instance>`; filter by `host.name`,
  `bc.server_instance`, `deployment.environment`.
- **Logs:** every BC signal (RT0005 long-running SQL, RT0018 long-running AL,
  LC* extension lifecycle, …) with `event.name` = BC event id, original timestamp,
  severity, and all custom dimensions; numeric `*InMs` companions for durations.
- **Traces:** one trace per BC operation (client action, web service call,
  task) with all its events and logs; database events as client spans to `sql-server`.
- **Metrics:** `bc.server.*` counters and `process.*` per instance, `system.*`
  per host, `bcproxy.*` for the proxy itself, BC metric telemetry as gauges.
- **APM:** BC events with a duration as spans (latency, throughput, errors per operation).
- Dashboard and alert ideas: [`deploy/gitlab-o11y/`](../deploy/gitlab-o11y/).

## 5. Database server (2-tier)

The proxy runs on the BC server and finds the database server in each instance's
`CustomSettings.config`. To monitor it without an agent, allow the proxy's identity
(the IIS app pool runs as the BC server's computer account `DOMAIN\BCSERVER$` on the
network) to read server state:

```sql
CREATE LOGIN [DOMAIN\BCSERVER$] FROM WINDOWS;
GRANT VIEW SERVER STATE TO [DOMAIN\BCSERVER$];   -- SQL 2022: VIEW SERVER PERFORMANCE STATE
```

Further servers or SQL logins: `SqlMonitoring:Servers` (name -> connection string;
keep passwords in environment variables, e.g. `SqlMonitoring__Servers__SQL01=...`).
`GET /health` stays healthy if SQL is unreachable; the proxy logs one warning and
reports `sqlserver.up = 0`.

For CPU, disk and network of the database host in *Infrastructure*, run a second
proxy there as a Windows service with only host telemetry (no BC points at it):

```powershell
sc.exe create BcTelemetryProxy binPath= "C:\bcproxy\BcTelemetryProxy.exe" start= auto
setx /M Otlp__Endpoint https://<id>.otel.gitlab-o11y.com:14318
setx /M SqlMonitoring__Enabled false   # the BC server's proxy already monitors SQL
```

Turn on BC's own database telemetry on every instance (off by default on-premises):

```powershell
Set-NAVServerConfiguration BC290 -KeyName EnableDeadlockMonitoring -KeyValue true
Set-NAVServerConfiguration BC290 -KeyName EnableLockTimeoutMonitoring -KeyValue true
# SqlLongRunningThresholdForApplicationInsights (ms) controls RT0005
Restart-NAVServerInstance BC290
```

## 6. Finding errors and reporting them to the partner

1. **See:** GitLab *Observe → Exceptions* groups errors by type (BC: `failureReason`,
   lock timeout, deadlock; SQL Server: `SqlDeadlock`) with count and first / last
   seen. Open one for the trace: everything the user's action caused.
2. **Prioritise:** `GET /support/errors?hours=24` on the proxy lists error kinds
   with affected users and sessions. Errors with `extensionPublisher` of a partner
   app belong to that partner; `alObjectType = System` or Microsoft apps to the
   CSP / Microsoft.
3. **Report:** `GET /support/errors/<fingerprint>` gives a Markdown report with all
   that support asks for (build, time UTC, operation / session id, message, AL
   stack, SQL statement, deadlock graph). Attach it to the ticket. With
   `Support:TraceUrlTemplate` the report links to the trace.
4. **Be notified:** GitLab alert rules with a notification channel (e-mail /
   webhook to the CSP's support address), e.g. logs `event.name = bc.error.first_seen`
   (new error kind), `exception` count by type, `sqlserver.deadlock`, `sqlserver.up = 0`.
   See [alerting ideas](../deploy/gitlab-o11y/README.md#alerting-ideas).

## Performance analysis for DevOps and the CSP

On the proxy host, open `http://localhost:5140/support/performance?hours=24`
or `/support/performance/report` for Markdown. Filter a specific source with
`&instance=SRV01%2FBC290`. Routes use the existing `Support:LocalOnly` policy.
The top 100 operation groups are sorted by accumulated observed duration and
separate BC builds, instances, environments, extensions, AL objects and tenants.
Compare count, average, nearest-rank p95 and maximum together; follow the slowest
trace, then correlate its SQL session with blocking/waits and host metrics.
The `NextStep` field suggests an investigation, without asserting a root cause.

The memory bound is `Support:MaxPerformanceSamples` (20,000; clamped to
1–200,000). Samples reset on restart; `evictedSamples` counts capacity evictions
since startup. Time selection uses span completion timestamps. Only compact,
already-filtered fields are retained, without SQL text or stack traces. These
statistics describe observed positive-duration spans; BC thresholds/sampling and
overlapping spans prevent treating them as total user latency or complete workload
statistics. For historical comparisons, retain telemetry in GitLab.

Start with the highest accumulated duration, then investigate repeated SQL calls
and rows read, AL filters and loops, blocking/deadlocks, and external HTTP calls.
Use identical workloads and builds for before/after comparisons. Inspect execution
plans before making index changes; validate AL changes in a test environment.
Microsoft documents RT0005 and its duration/SQL dimensions in
[long-running SQL telemetry](https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/administration/telemetry-long-running-sql-query-trace).

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
- **Support reports** — `/support/*` answers local callers only
  (`Support:LocalOnly`); reports contain error messages and SQL statements.

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
