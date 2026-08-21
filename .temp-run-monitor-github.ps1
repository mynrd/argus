<#
.SYNOPSIS
    Watches origin/main for new commits, pulls them, and restarts run.ps1.

.DESCRIPTION
    Local-only supervisor. It does NOT reimplement the build -
    it launches run.ps1 as a child process and manages that child's lifecycle.

    Every -IntervalSeconds it asks GitHub for the current tip of main with `git ls-remote`, which
    is a single network round trip and never touches the working tree. When that SHA differs from
    local HEAD it stops the server, runs `git pull --ff-only`, and starts run.ps1 again.

    Stop order matters: dotnet holds a write lock on src\Argus.Server\bin and `ng build` rewrites
    src\Argus.Server\wwwroot, so the old process has to die before the new build starts.

    A failed pull (dirty tree, or local commits that diverged from origin) is logged and that SHA
    is not retried. The server is restarted on the old commit rather than left down. This script
    never runs `git reset --hard` - resolve the divergence yourself.

.PARAMETER IntervalSeconds
    How often to poll GitHub. Default 30.

.PARAMETER Port
    Passed through to run.ps1. Default 5227.

.PARAMETER Dev
    Passed through to run.ps1.

.PARAMETER Urls
    Passed through to run.ps1. Read the security note in run.ps1 before using it.

.PARAMETER Password
    Fallback viewer password, passed through to run.ps1. The server only uses it when
    Argus:Password in src\Argus.Server\appsettings.json is empty; with both empty there is no lock.
    `--password value` and `--password=value` are accepted as well, for callers used to that style.

.EXAMPLE
    .\.temp-run-monitor-github.ps1
.EXAMPLE
    .\.temp-run-monitor-github.ps1 -IntervalSeconds 60 -Port 8080
.EXAMPLE
    .\.temp-run-monitor-github.ps1 -Password 123455
.EXAMPLE
    .\.temp-run-monitor-github.ps1 --password=123455
#>
# PositionalBinding is off so an unnamed `--password=x` lands in $ExtraArgs instead of being
# coerced into -IntervalSeconds, which is what happens with the default binder.
[CmdletBinding(PositionalBinding = $false)]
# The password has to reach the server as a plain env var, so SecureString would only be theatre.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'Password')]
param(
    [int]$IntervalSeconds = 30,
    [int]$Port = 5227,
    [switch]$Dev,
    [string]$Urls,
    [string]$Password,
    # PowerShell's binder does not understand `--password=x`, so catch it here rather than let it
    # bind silently to some other positional parameter.
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$ExtraArgs
)

for ($i = 0; $i -lt $ExtraArgs.Count; $i++) {
    $arg = $ExtraArgs[$i]
    if ($arg -match '^--password=(.*)$') { $Password = $Matches[1]; continue }
    if ($arg -eq '--password') {
        if ($i + 1 -ge $ExtraArgs.Count) { throw "--password needs a value." }
        $Password = $ExtraArgs[++$i]
        continue
    }
    throw "Unrecognized argument '$arg'."
}

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$runScript = Join-Path $root 'run.ps1'
$branch = 'main'

if (-not (Test-Path $runScript)) { throw "run.ps1 not found next to this script ($runScript)." }

# Never let git block the loop on an interactive credential prompt - fail the poll instead.
$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'Never'

function Write-Watch { param([string]$Text, [string]$Color = 'DarkCyan')
    Write-Host "[watch $(Get-Date -Format 'HH:mm:ss')] $Text" -ForegroundColor $Color
}

function Invoke-Git {
    # `2>&1` turns a native command's stderr into ErrorRecords on the success stream, and under
    # $ErrorActionPreference='Stop' that is a *terminating* error - so git's routine chatter
    # ("From https://github.com/...", which pull always writes to stderr) would kill the watcher.
    # Relax the preference for the duration of the call and judge success by $LASTEXITCODE only.
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArgs)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git -C $root @GitArgs 2>&1
        return [pscustomobject]@{
            Ok   = ($LASTEXITCODE -eq 0)
            Text = ($out | ForEach-Object { $_.ToString() })
        }
    }
    finally { $ErrorActionPreference = $prev }
}

function Get-RemoteSha {
    $r = Invoke-Git ls-remote origin "refs/heads/$branch"
    if (-not $r.Ok) {
        Write-Watch "poll failed: $($r.Text -join ' ')" 'Yellow'
        return $null
    }
    # Match the SHA explicitly - stderr lines are interleaved in $r.Text.
    $sha = $r.Text | Where-Object { $_ -match '^([0-9a-f]{40})\s' } | Select-Object -First 1
    if (-not $sha) {
        Write-Watch "origin has no refs/heads/$branch" 'Yellow'
        return $null
    }
    return ($sha -split '\s+')[0]
}

function Get-LocalSha {
    $r = Invoke-Git rev-parse HEAD
    $sha = $r.Text | Where-Object { $_ -match '^[0-9a-f]{40}$' } | Select-Object -First 1
    if (-not $r.Ok -or -not $sha) { throw "git rev-parse HEAD failed: $($r.Text -join ' ')" }
    return $sha
}

function Start-Argus {
    $hostExe = (Get-Process -Id $PID).Path
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$runScript`"", '-Port', $Port)
    if ($Dev) { $argList += '-Dev' }
    if ($Urls) { $argList += @('-Urls', "`"$Urls`"") }
    if ($Password) { $argList += @('-Password', "`"$Password`"") }

    Write-Watch "starting run.ps1 at $((Get-LocalSha).Substring(0,7))"
    return Start-Process -FilePath $hostExe -ArgumentList $argList -PassThru -NoNewWindow
}

function Stop-Argus {
    param($Proc)
    if (-not $Proc -or $Proc.HasExited) { return }
    Write-Watch "stopping run.ps1 (pid $($Proc.Id)) and its children"
    # /T is the point: `dotnet run` spawns Argus.Server.exe as a grandchild, and killing only the
    # shell orphans it holding port $Port.
    # Same stderr-under-EAP=Stop trap as Invoke-Git: taskkill writes to stderr when the pid is
    # already gone, which is a benign race here, not a reason to tear down the watcher.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & taskkill /PID $Proc.Id /T /F 2>&1 | Out-Null }
    finally { $ErrorActionPreference = $prev }
    if (-not $Proc.WaitForExit(15000)) {
        Write-Watch "pid $($Proc.Id) did not exit within 15s" 'Yellow'
    }
}

function Invoke-Pull {
    Write-Watch "git pull --ff-only"
    $r = Invoke-Git pull --ff-only origin $branch
    $r.Text | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    return $r.Ok
}

# ---------------------------------------------------------------------- loop

Write-Watch "watching origin/$branch every ${IntervalSeconds}s - Ctrl+C stops everything" 'Cyan'

$server = Start-Argus
$skipSha = $null      # a remote SHA we already failed to pull; don't retry it every tick
$exitLogged = $false

try {
    while ($true) {
        Start-Sleep -Seconds $IntervalSeconds

        if ($server.HasExited -and -not $exitLogged) {
            Write-Watch "run.ps1 exited on its own (code $($server.ExitCode)). Not auto-restarting - waiting for a new commit." 'Yellow'
            $exitLogged = $true
        }

        $remote = Get-RemoteSha
        if (-not $remote) { continue }
        if ($remote -eq $skipSha) { continue }
        if ($remote -eq (Get-LocalSha)) { continue }

        Write-Watch "new commit on $branch : $($remote.Substring(0,7))" 'Green'

        Stop-Argus $server
        if (-not (Invoke-Pull)) {
            Write-Watch "pull failed - staying on the current commit and ignoring $($remote.Substring(0,7)) until origin moves again" 'Red'
            $skipSha = $remote
        }

        $server = Start-Argus
        $exitLogged = $false
    }
}
finally {
    Stop-Argus $server
}
