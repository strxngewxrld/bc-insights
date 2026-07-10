#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes the BC telemetry proxy and hosts it as an IIS website
    (ASP.NET Core Module V2, in-process).

.DESCRIPTION
    Business Central's Application Insights connection string should then point
    its IngestionEndpoint at this site, e.g.:
        InstrumentationKey=<any-guid>;IngestionEndpoint=https://bcproxy.example.com/

.PARAMETER SiteName
    IIS site name to create.

.PARAMETER Port
    HTTP port for the site binding. Use a certificate binding for HTTPS, which BC
    expects for the ingestion endpoint.

.PARAMETER OtlpEndpoint
    OTLP/HTTP endpoint of the local OpenTelemetry Collector.

.EXAMPLE
    .\Install-IISSite.ps1 -PhysicalPath "C:\inetpub\bcproxy" -Port 8888
#>
param(
    [string]$SiteName = "BcTelemetryProxy",
    [string]$PhysicalPath = "C:\inetpub\bcproxy",
    [int]$Port = 8888,
    [string]$OtlpEndpoint = "http://localhost:4318",
    [string]$ProjectPath = "$PSScriptRoot\..\..\src\BcTelemetryProxy\BcTelemetryProxy.csproj"
)

$ErrorActionPreference = "Stop"
Import-Module WebAdministration

# Prerequisite: install the ASP.NET Core 8 Hosting Bundle on this server first:
#   https://dotnet.microsoft.com/download/dotnet/8.0 (Hosting Bundle)

Write-Host "Publishing proxy to $PhysicalPath"
dotnet publish $ProjectPath -c Release -o $PhysicalPath

# Inject the Collector endpoint into appsettings via an environment variable
# override (ASP.NET Core config: Otlp__Endpoint).
$appPool = $SiteName
if (-not (Test-Path "IIS:\AppPools\$appPool")) {
    New-WebAppPool -Name $appPool | Out-Null
}
# ASP.NET Core runs out-of-managed-runtime; "No Managed Code" is correct.
Set-ItemProperty "IIS:\AppPools\$appPool" -Name managedRuntimeVersion -Value ""
Set-ItemProperty "IIS:\AppPools\$appPool" -Name processModel.identityType -Value ApplicationPoolIdentity

if (Test-Path "IIS:\Sites\$SiteName") {
    Remove-Website -Name $SiteName
}
New-Website -Name $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $appPool -Port $Port | Out-Null

# Set the OTLP endpoint for the app pool environment.
$env:Otlp__Endpoint = $OtlpEndpoint
[Environment]::SetEnvironmentVariable("Otlp__Endpoint", $OtlpEndpoint, "Machine")

Write-Host "Site '$SiteName' is running on port $Port, exporting OTLP to $OtlpEndpoint"
Write-Host "Point Business Central's IngestionEndpoint at this site (HTTPS recommended)."
