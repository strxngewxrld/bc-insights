# Deployment guide

This document covers deploying the Business Central telemetry proxy on-premises
and connecting it to an OTLP-capable observability backend.

## Components

1. **BcTelemetryProxy** — ASP.NET Core app hosted in IIS (in-process).
2. **OpenTelemetry Collector** (`otelcol-contrib`) — Windows service, central and
   backend-neutral egress; also reads selected Windows Event Log providers.
3. **Observability backend** — any OTLP/HTTP endpoint.

The proxy is backend-agnostic. It always exports to the local Collector; the
Collector decides where telemetry ultimately goes.

## 1. Choose a backend (Collector exporter)

Set `OTLP_EXPORT_ENDPOINT` to your backend's OTLP/HTTP endpoint. The exporter
appends `/v1/traces`, `/v1/logs` and `/v1/metrics`. Examples:

- **GitLab Observability (self-managed, GitLab 18.1+)** — exposes OTLP on
  `:4318` (HTTP) and `:4317` (gRPC), UI on `:8080`. Set
  `OTLP_EXPORT_ENDPOINT=http://gitlab-observability.example.com:4318`.
  If your deployment requires project-scoped ingestion with a token, add the
  header it expects in `config.yaml` (`headers: { PRIVATE-TOKEN: ${env:OTLP_EXPORT_TOKEN} }`)
  and set `OTLP_EXPORT_TOKEN`.
- **Grafana stack / Jaeger / vendor OTLP** — point `OTLP_EXPORT_ENDPOINT` at the
  collector/gateway and add auth headers if needed.

GitLab is a *supported target*, not a hard dependency: switching backends is a
one-line change to the exporter endpoint.

## 2. OpenTelemetry Collector

```powershell
# Run as Administrator
.\deploy\otel-collector\Install-Collector.ps1 `
    -OtlpExportEndpoint "https://observability.example.com:4318"
```

This downloads `otelcol-contrib` (needed for the `windowseventlog` receiver),
installs [`config.yaml`](../deploy/otel-collector/config.yaml), and registers an
auto-start Windows service. The config uses **batching, retry-on-failure and a
sending queue**, and keeps **TLS verification on** (`insecure: false`).

### Windows Event Log scope

By default the Collector does **not** ship the entire System/Application
channels. It is scoped to BC-relevant providers (BC Server, .NET Runtime,
ASP.NET Core Module, `BcTelemetryProxy`) and, for the System channel, to
warning/error levels. Adjust the `query` filters in `config.yaml` to add specific
providers or event ids.

Validate locally first by temporarily switching the pipeline exporters to `debug`
and watching the console output.

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
at the local Collector. Ingestion limits, attribute privacy and resource
attributes are configured in `appsettings.json`.

## 4. Point Business Central at the proxy

On the BC Server instance, set the Application Insights connection string so the
`IngestionEndpoint` targets the proxy. The instrumentation key can be any GUID.

```powershell
Set-NAVServerConfiguration -ServerInstance BC `
    -KeyName ApplicationInsightsConnectionString `
    -KeyValue "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://bcproxy.example.com/"
Restart-NAVServerInstance -ServerInstance BC
```

For multitenant instances use
`Mount-NAVTenant -ApplicationInsightsConnectionString "..."` per tenant.

## Security model

This proxy is a translation layer on a trusted on-prem network. Its security
posture:

- **TLS on the ingestion endpoint** — Business Central expects HTTPS. Bind a
  certificate to the IIS site (`New-WebBinding -Name BcTelemetryProxy -Protocol https -Port 443`
  plus a certificate in the machine store). Plain HTTP is a **development/lab
  profile only** (`appsettings.Development.json`).
- **TLS on the egress** — the Collector exports with TLS verification enabled by
  default; do not set `insecure: true` in production.
- **Endpoint protection** — restrict who can reach `/v2/track` via network rules
  (firewall / host allowlist) and/or **mTLS** at IIS. Note that Business Central's
  Application Insights client cannot add custom auth headers, so header/API-key
  auth is not usable for the BC→proxy hop; network isolation and mTLS are the
  appropriate controls.
- **No instrumentation-key validation — by design.** The proxy accepts any
  instrumentation key: it is a single-tenant, network-trusted compatibility
  shim, and the key is not a secret in this topology. Do not rely on the key for
  authentication; rely on network controls instead.
- **Attribute privacy** — sensitive `properties`/context values are redacted or
  hashed before export (`Telemetry` config). Review the allow/deny/redact/hash
  lists for your data.

## Ingestion limits

`appsettings.json` → `Ingestion`:

| Setting                       | Default | Effect                                    |
| ----------------------------- | ------- | ----------------------------------------- |
| `MaxCompressedRequestBytes`   | 10 MiB  | rejects oversized compressed bodies (413) |
| `MaxDecompressedRequestBytes` | 100 MiB | bounds decompressed payload (413)         |
| `MaxEnvelopeBytes`            | 1 MiB   | oversized single envelope → per-item error |
| `MaxEnvelopesPerRequest`      | 10 000  | rejects batches with too many items (413) |

## Verification

1. **Health** — `GET https://bcproxy.example.com/health` → `{"status":"ok"}`.
2. **Replay** — post `samples/sample-track-payload.ndjson` (gzipped) to
   `/v2/track`; expect `{"itemsReceived":6,"itemsAccepted":6,...}`.
3. **Partial success** — post a batch with one malformed line; expect `206` with
   `itemsAccepted < itemsReceived` and a populated `errors[]`.
4. **Backend** — trigger a BC action and confirm traces/logs/metrics appear in
   the backend UI. Filter by `bc.server_instance` or `eventId`.
5. **Eventlog** — write a test entry and confirm it reaches the backend.

## Open items

- Confirm the exact backend OTLP path/auth for your deployment (esp. token-scoped
  GitLab ingestion).
- Pre-aggregated BC metrics are recorded via the .NET Meter API (no synchronous
  gauge instrument exists in .NET 8; gauge-like values are recorded loss-light
  with their aggregate stats as attributes).
