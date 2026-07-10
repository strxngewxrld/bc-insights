# Deployment guide

This document covers deploying the Business Central telemetry proxy on-premises
and connecting it to a self-managed GitLab Observability instance.

## Components

1. **BcTelemetryProxy** — ASP.NET Core app hosted in IIS (in-process).
2. **OpenTelemetry Collector** (`otelcol-contrib`) — Windows service, central
   egress to GitLab, also reads the Windows Event Log.
3. **GitLab Observability** — self-managed, GitLab 18.1+.

## 1. GitLab Observability

Set up GitLab Observability on your self-managed instance following
<https://docs.gitlab.com/operations/observability/setup_self_managed/>.
It exposes OTLP on:

- `4317` — OTLP gRPC
- `4318` — OTLP HTTP
- `8080` — Observability UI

The Collector in this repo exports over **OTLP HTTP (4318)**.

> **Verify for your version:** whether your GitLab Observability deployment
> accepts plain OTLP on `:4318/v1/{traces,logs,metrics}` (standalone GOB) or
> requires a project-scoped path such as
> `/v3/<group-id>/<project-id>/ingest/logs` with a `PRIVATE-TOKEN` header. If the
> latter, uncomment the `headers` block in
> [`deploy/otel-collector/config.yaml`](../deploy/otel-collector/config.yaml) and
> set `GITLAB_TOKEN`.

## 2. OpenTelemetry Collector

```powershell
# Run as Administrator
.\deploy\otel-collector\Install-Collector.ps1 `
    -GitLabOtlpEndpoint "http://gitlab-observability.example.com:4318"
```

This downloads `otelcol-contrib` (needed for the `windowseventlog` receiver),
installs the config, and registers it as an auto-start Windows service. Validate
it locally first by temporarily switching the pipeline exporters to `debug` in
`config.yaml` and watching the console output.

## 3. Proxy on IIS

Prerequisites on the BC/IIS host:

- **ASP.NET Core 8 Hosting Bundle** — <https://dotnet.microsoft.com/download/dotnet/8.0>
- IIS with the ASP.NET Core Module V2 (installed by the Hosting Bundle).

```powershell
# Run as Administrator
.\deploy\iis\Install-IISSite.ps1 -SiteName "BcTelemetryProxy" -Port 8888 `
    -OtlpEndpoint "http://localhost:4318"
```

The [`web.config`](../src/BcTelemetryProxy/web.config) configures the in-process
ASP.NET Core Module. `Otlp:Endpoint` (env var `Otlp__Endpoint`) points the proxy
at the local Collector.

### TLS

Business Central expects an HTTPS ingestion endpoint. Bind a certificate to the
IIS site (e.g. `New-WebBinding -Name BcTelemetryProxy -Protocol https -Port 443`
plus a certificate in the machine store) and use that host in the BC connection
string. For a lab you can use HTTP, but production BC servers should use HTTPS.

## 4. Point Business Central at the proxy

On the BC Server instance, set the Application Insights connection string so the
`IngestionEndpoint` targets the proxy. The instrumentation key can be any GUID —
the proxy does not validate it.

```powershell
Set-NAVServerConfiguration -ServerInstance BC `
    -KeyName ApplicationInsightsConnectionString `
    -KeyValue "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://bcproxy.example.com/"
Restart-NAVServerInstance -ServerInstance BC
```

For multitenant instances use
`Mount-NAVTenant -ApplicationInsightsConnectionString "..."` per tenant.

## Verification

1. **Health** — `GET https://bcproxy.example.com/health` → `{"status":"ok"}`.
2. **Replay** — post `samples/sample-track-payload.ndjson` (gzipped) to
   `/v2/track`; expect `{"itemsReceived":6,"itemsAccepted":6,...}`.
3. **Collector** — with the `debug` exporter enabled, confirm translated logs,
   spans and metrics appear in the Collector console.
4. **BC → GitLab** — trigger a BC action (open a page, run a report) and confirm
   the trace/logs appear in the GitLab Observability UI. Filter by
   `bc.cloud.role` or `eventId`.
5. **Eventlog** — write a test entry
   (`Write-EventLog -LogName Application -Source BcTelemetryProxy -EventId 1000 -Message test`,
   after `New-EventLog -LogName Application -Source BcTelemetryProxy`) and confirm
   it reaches GitLab.

## Open items

- Confirm the exact GitLab OTLP path/auth for your version (see note above).
- Decide whether pre-aggregated BC metrics need a dedicated gauge mapping; the
  current implementation records them as OTLP histograms.
- Optional: forward a copy to real Azure Application Insights (pass-through) if
  you want to keep Azure in parallel — not implemented (translate-only).
