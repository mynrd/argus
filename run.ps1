<#
.SYNOPSIS
    Builds and runs everything Argus needs.

.DESCRIPTION
    Argus is a single server process: it captures the desktop windows, serves the web app, and
    hosts both the SignalR control hub and the binary frame socket. There is no separate agent to
    start - the capture code has to live in your interactive desktop session, so it runs here.

    Default (production-ish): builds the Angular app into Argus.Server\wwwroot and runs the server,
    which serves the built UI on one port.

    -Dev: runs the .NET server AND the Angular dev server, so UI edits hot-reload. The Angular dev
    server proxies /hubs, /ws and /api through to the .NET server (see proxy.conf.json).

.PARAMETER Dev
    Run with hot reload. Opens the UI on http://localhost:4200 instead of the server port.

.PARAMETER Port
    Port for the .NET server. Default 5227.

.PARAMETER SkipBuild
    Skip the build steps and just run what is already compiled.

.PARAMETER Urls
    Override the listen addresses entirely, e.g. "http://0.0.0.0:5227".
    Read the security note below before using this.

.EXAMPLE
    .\run.ps1
.EXAMPLE
    .\run.ps1 -Dev
.EXAMPLE
    .\run.ps1 -Port 8080

.NOTES
    SECURITY: Argus injects keystrokes into your desktop and has no authentication. By default it
    binds to loopback plus your Tailscale address only, so it is reachable from your tailnet and
    nowhere else. Passing -Urls http://0.0.0.0:... exposes desktop control to every network this
    machine ever joins. Do not do that on untrusted wifi.

    Argus must run as a normal user process, NOT as a Windows service. Services run in session 0,
    which has no access to the interactive desktop - window enumeration, capture and input
    injection all fail there.
#>
[CmdletBinding()]
param(
    [switch]$Dev,
    [int]$Port = 5227,
    [switch]$SkipBuild,
    [string]$Urls
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$serverProject = Join-Path $root 'src\Argus.Server\Argus.Server.csproj'
$webDir = Join-Path $root 'src\Argus.Web'

function Write-Step { param([string]$Text) Write-Host "`n=== $Text" -ForegroundColor Cyan }
function Write-Info { param([string]$Text) Write-Host "    $Text" -ForegroundColor DarkGray }

# ---------------------------------------------------------------- preflight

Write-Step 'Checking prerequisites'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK not found on PATH. Install the .NET 10 SDK.'
}
Write-Info "dotnet $(dotnet --version)"

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw 'node not found on PATH. Install Node.js.'
}
Write-Info "node $(node --version)"

if (-not (Test-Path (Join-Path $webDir 'node_modules'))) {
    Write-Step 'Installing web dependencies (first run)'
    Push-Location $webDir
    try { npm install } finally { Pop-Location }
}

# -------------------------------------------------------------------- build

if (-not $SkipBuild) {
    Write-Step 'Building server'
    dotnet build $serverProject -c Debug --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }

    if (-not $Dev) {
        Write-Step 'Building web app into Argus.Server\wwwroot'
        Push-Location $webDir
        try {
            npx ng build
            if ($LASTEXITCODE -ne 0) { throw 'Web build failed.' }
        }
        finally { Pop-Location }
    }
}

# ------------------------------------------------------------------ address

$tailscaleIp = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -like '100.*' -and $_.PrefixOrigin -ne 'WellKnown' } |
    Select-Object -First 1 -ExpandProperty IPAddress)

Write-Step 'Starting Argus'
Write-Info "server   http://127.0.0.1:$Port"
if ($tailscaleIp) {
    Write-Info "tailnet  http://${tailscaleIp}:$Port"
}
else {
    Write-Info 'tailnet  (no Tailscale address detected - localhost only)'
}

$env:Argus__Port = $Port
if ($Urls) {
    $env:ARGUS_URLS = $Urls
    Write-Host "    WARNING: overriding bind addresses with '$Urls'." -ForegroundColor Yellow
    Write-Host '    Argus has no authentication and can type into your desktop.' -ForegroundColor Yellow
}

# --------------------------------------------------------------------- run

$serverArgs = @('run', '--project', $serverProject, '--no-launch-profile')
if ($SkipBuild) { $serverArgs += '--no-build' }

if (-not $Dev) {
    Write-Info 'Open the URL above. Ctrl+C to stop.'
    & dotnet @serverArgs
    exit $LASTEXITCODE
}

# Dev mode: server in the background, Angular dev server in the foreground so its output is what
# you watch while editing the UI.
Write-Info 'dev UI   http://localhost:4200 (proxies /hubs, /ws and /api to the server)'

$server = Start-Process -FilePath 'dotnet' -ArgumentList $serverArgs -PassThru -NoNewWindow

try {
    Push-Location $webDir
    try { npx ng serve } finally { Pop-Location }
}
finally {
    if ($server -and -not $server.HasExited) {
        Write-Step 'Stopping server'
        $server.Kill($true)
    }
}
