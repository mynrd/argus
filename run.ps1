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
    Not needed for LAN access - that is on by default. Read the security note below.

.PARAMETER Password
    Fallback viewer password, used only when Argus:Password in appsettings.json is empty.
    Passed to the server as ARGUS_PASSWORD. Leave both empty to run with no lock.

.EXAMPLE
    .\run.ps1
.EXAMPLE
    .\run.ps1 -Dev
.EXAMPLE
    .\run.ps1 -Port 8080

.NOTES
    SECURITY: Argus injects keystrokes into your desktop. By default it binds loopback, your
    Tailscale address, and your private LAN addresses, so it is reachable from your tailnet and
    from other machines on the same wifi or office network. Set -Password (or Argus:Password in
    appsettings.json) before using it on a network you do not control - with both empty the viewer
    is open to anyone who can reach the port. Passing -Urls http://0.0.0.0:... goes further still
    and binds every interface, including public ones.

    Argus must run as a normal user process, NOT as a Windows service. Services run in session 0,
    which has no access to the interactive desktop - window enumeration, capture and input
    injection all fail there.
#>
[CmdletBinding()]
# The password has to reach the server as a plain env var, so SecureString would only be theatre.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'Password')]
param(
    [switch]$Dev,
    [int]$Port = 5227,
    [switch]$SkipBuild,
    [string]$Urls,
    [string]$Password
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

# Mirrors NetworkBinding.Resolve on the server: loopback, Tailscale, and the private LAN.
$ipv4 = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.PrefixOrigin -ne 'WellKnown' }

$tailscaleIp = $ipv4 |
    Where-Object { $_.IPAddress -like '100.*' } |
    Select-Object -First 1 -ExpandProperty IPAddress

$lanIps = $ipv4 |
    Where-Object { $_.IPAddress -match '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)' } |
    Select-Object -ExpandProperty IPAddress

Write-Step 'Starting Argus'
Write-Info "server   http://127.0.0.1:$Port"
if ($tailscaleIp) {
    Write-Info "tailnet  http://${tailscaleIp}:$Port"
}
else {
    Write-Info 'tailnet  (no Tailscale address detected)'
}
foreach ($lan in $lanIps) {
    Write-Info "lan      http://${lan}:$Port"
}
if (-not $lanIps) {
    Write-Info 'lan      (no private LAN address detected)'
}

$env:Argus__Port = $Port

# Only a fallback: SessionGuard prefers Argus:Password from appsettings.json whenever that has a
# value, so this env var is what unlocks the viewer only when the config entry is left empty.
if ($Password) {
    $env:ARGUS_PASSWORD = $Password
    Write-Info 'password  from -Password (used only if Argus:Password in appsettings.json is empty)'
}
else {
    # Clear a value left over from an earlier run in this same shell.
    Remove-Item Env:\ARGUS_PASSWORD -ErrorAction SilentlyContinue
}

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
