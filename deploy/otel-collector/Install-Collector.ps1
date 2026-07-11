#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the OpenTelemetry Collector (contrib) as a Windows service using the
    config.yaml in this folder. The contrib build is required for the
    windowseventlog receiver.

.PARAMETER OtlpExportEndpoint
    OTLP/HTTP endpoint of the observability backend the Collector forwards to,
    e.g. https://observability.example.com:4318 (any OTLP-capable backend).

.PARAMETER OtlpExportToken
    Optional bearer/authorization token for the backend. When set, uncomment the
    matching headers block in config.yaml.

.PARAMETER Version
    otelcol-contrib release to download.

.EXAMPLE
    .\Install-Collector.ps1 -OtlpExportEndpoint "https://observability.example.com:4318"
#>
param(
    [Parameter(Mandatory = $true)][string]$OtlpExportEndpoint,
    [string]$OtlpExportToken,
    [string]$Version = "0.116.0",
    [string]$InstallDir = "C:\Program Files\otelcol-contrib"
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $InstallDir "otelcol-contrib.exe"
$configTarget = Join-Path $InstallDir "config.yaml"

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

if (-not (Test-Path $exe)) {
    $tar = Join-Path $env:TEMP "otelcol-contrib.tar.gz"
    $url = "https://github.com/open-telemetry/opentelemetry-collector-releases/releases/download/v$Version/otelcol-contrib_${Version}_windows_amd64.tar.gz"
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $tar
    tar -xzf $tar -C $InstallDir
    Remove-Item $tar
}

Copy-Item -Path (Join-Path $PSScriptRoot "config.yaml") -Destination $configTarget -Force

# Persist backend settings as machine environment variables the service reads.
[Environment]::SetEnvironmentVariable("OTLP_EXPORT_ENDPOINT", $OtlpExportEndpoint, "Machine")
if ($OtlpExportToken) {
    [Environment]::SetEnvironmentVariable("OTLP_EXPORT_TOKEN", $OtlpExportToken, "Machine")
}

$serviceName = "otelcol-contrib"
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service $serviceName -Force
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

sc.exe create $serviceName binPath= "`"$exe`" --config `"$configTarget`"" start= auto | Out-Null
sc.exe description $serviceName "OpenTelemetry Collector - Business Central telemetry egress" | Out-Null
Start-Service $serviceName

Write-Host "Collector installed and started. Export endpoint: $OtlpExportEndpoint"
