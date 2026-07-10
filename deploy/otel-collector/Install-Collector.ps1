#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the OpenTelemetry Collector (contrib) as a Windows service using the
    config.yaml in this folder. The contrib build is required for the
    windowseventlog receiver.

.PARAMETER GitLabOtlpEndpoint
    OTLP/HTTP endpoint of the self-managed GitLab Observability instance,
    e.g. http://gitlab-observability.example.com:4318

.PARAMETER Version
    otelcol-contrib release to download.

.EXAMPLE
    .\Install-Collector.ps1 -GitLabOtlpEndpoint "http://gitlab-observability.example.com:4318"
#>
param(
    [Parameter(Mandatory = $true)][string]$GitLabOtlpEndpoint,
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

# Persist the GitLab endpoint as a machine environment variable the service reads.
[Environment]::SetEnvironmentVariable("GITLAB_OTLP_ENDPOINT", $GitLabOtlpEndpoint, "Machine")

$serviceName = "otelcol-contrib"
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service $serviceName -Force
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

sc.exe create $serviceName binPath= "`"$exe`" --config `"$configTarget`"" start= auto | Out-Null
sc.exe description $serviceName "OpenTelemetry Collector - Business Central telemetry to GitLab" | Out-Null
Start-Service $serviceName

Write-Host "Collector installed and started. Endpoint: $GitLabOtlpEndpoint"
