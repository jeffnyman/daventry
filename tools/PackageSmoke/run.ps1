#!/usr/bin/env pwsh
# Packs every Daventry package into artifacts/packages and checks them the way a consumer would:
#   - a console app restores Daventry.Agi and Daventry.Sci from the local feed and plays a game headless
#   - the Daventry.Player tool is installed from the feed and takes a screenshot
#   - a Blazor WebAssembly app restores Daventry.Host.Browser and publishes; with -BrowserGame, Node and
#     Chrome, headless Chrome plays the game on that page
# Usage: tools/PackageSmoke/run.ps1 -Game <folder> [-BrowserGame <folder>] [-SkipPack] [-VersionSuffix alpha.2]
param(
    [Parameter(Mandatory)] [string] $Game,
    [string] $BrowserGame,
    [string] $VersionSuffix,
    [switch] $SkipPack
)

$ErrorActionPreference = 'Stop'
$smoke = $PSScriptRoot
$root = (Resolve-Path (Join-Path $smoke '../..')).Path
$artifacts = Join-Path $root 'artifacts'
$feed = Join-Path $artifacts 'packages'
$cache = Join-Path $artifacts 'nuget-cache'
$toolDir = Join-Path $artifacts 'tool'
$blazorOut = Join-Path $artifacts 'blazor'

function Step($name) { Write-Host "`n=== $name" -ForegroundColor Cyan }
# node may be a version manager's shim, so the server is stopped by the port it listens on
function Stop-Listener($port) {
    if ($IsWindows) {
        Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
            ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    }
    else {
        $pids = & lsof -ti ":$port" 2>$null
        if ($pids) { $pids | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue } }
    }
}
function Run($file, [string[]] $arguments) {
    Write-Host "> $file $($arguments -join ' ')" -ForegroundColor DarkGray
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$file exited with $LASTEXITCODE" }
}

foreach ($dir in $cache, $toolDir, $blazorOut) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}
if (-not $SkipPack) {
    Step 'Pack'
    if (Test-Path $feed) { Remove-Item -Recurse -Force $feed }
    $packArgs = @($root, '-c', 'Release', '-o', $feed)
    if ($VersionSuffix) { $packArgs += "-p:VersionSuffix=$VersionSuffix" }
    Run dotnet (@('pack') + $packArgs)
}

$core = Get-ChildItem $feed -Filter 'Daventry.Core.*.nupkg' | Where-Object Name -NotLike '*.symbols.nupkg' | Select-Object -First 1
if (-not $core) { throw "no Daventry.Core package in $feed" }
$version = $core.Name -replace '^Daventry\.Core\.(.+)\.nupkg$', '$1'
Write-Host "Package version $version"
Get-ChildItem $feed | ForEach-Object { Write-Host ("  {0,10:N0}  {1}" -f $_.Length, $_.Name) }

# packages restored in this run come from the feed, never from a stale copy in the global cache
$env:NUGET_PACKAGES = $cache
Push-Location $smoke
try {
    Step 'Console app from the packages'
    Run dotnet @('run', '--project', 'ConsoleSmoke', '-c', 'Release', "--property:DaventryVersion=$version", '--',
        $Game, '600', (Join-Path $artifacts 'console.png'))

    Step 'Daventry.Player as a dotnet tool'
    Run dotnet @('tool', 'install', 'Daventry.Player', '--tool-path', $toolDir, '--version', $version, '--add-source', $feed)
    $daventry = Join-Path $toolDir ($IsWindows ? 'daventry.exe' : 'daventry')
    Run $daventry @($Game, '--list')
    Run $daventry @($Game, '--screenshot', (Join-Path $artifacts 'tool.png'), '--frames', '300')

    Step 'Blazor WebAssembly app from the packages'
    Run dotnet @('publish', 'BlazorSmoke', '-c', 'Release', '-o', $blazorOut, "--property:DaventryVersion=$version")
    $module = Join-Path $blazorOut 'wwwroot/_content/Daventry.Host.Browser/daventry.js'
    if (-not (Test-Path $module)) { throw "the browser host's module is missing from the published site: $module" }
    Write-Host "published with $module"

    if ($BrowserGame) {
        $node = Get-Command node -ErrorAction SilentlyContinue
        $chrome = if ($env:CHROME) { $env:CHROME } else { 'C:/Program Files/Google/Chrome/Application/chrome.exe' }
        if (-not $node -or -not (Test-Path $chrome)) {
            Write-Warning 'Node or Chrome not found; skipping the headless browser run'
        }
        else {
            Step 'Headless Chrome plays on the published page'
            $gameDir = Join-Path $blazorOut 'wwwroot/games/g'
            New-Item -ItemType Directory -Force $gameDir | Out-Null
            $files = Get-ChildItem -File $BrowserGame
            $files | Copy-Item -Destination $gameDir
            @{ name = 'g'; files = @($files | ForEach-Object Name) } | ConvertTo-Json | Set-Content (Join-Path $gameDir 'manifest.json')

            $port = 8126
            Stop-Listener $port
            $server = Start-Process node -ArgumentList @("`"$smoke/../BrowserSmoke/server.mjs`"", "`"$blazorOut/wwwroot`"", $port) -PassThru -NoNewWindow
            try {
                Start-Sleep -Seconds 1
                $steps = "wait:25000;eval:document.querySelector('.status')?.textContent;eval:JSON.stringify(daventryAudio);" +
                    "eval:[...document.querySelector('canvas').getContext('2d').getImageData(0,0,320,200).data].filter((v,i)=>i%4<3&&v).length;" +
                    "shot:$artifacts/browser.png"
                $out = & node "$smoke/../BrowserSmoke/cdp.mjs" "http://127.0.0.1:$port/?game=games/g/manifest.json" $steps 2>&1
                $out | ForEach-Object { Write-Host $_ }
                if (-not ($out -match 'Playing ')) { throw 'the page did not report Playing' }
            }
            finally {
                Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
                Stop-Listener $port
            }
        }
    }
}
finally {
    Pop-Location
}

Step 'Done'
Write-Host "packages in $feed; screenshots in $artifacts"
