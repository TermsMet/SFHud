<#
.SYNOPSIS
    Builds a SFHud release: a portable win-x64 folder, a .zip and its SHA-256.

.DESCRIPTION
    By default makes a self-contained single-file SFHud.exe that runs on any 64-bit Windows 10/11
    PC without installing .NET. The local AI model is NOT bundled; SFHud downloads it from the
    AI Core page on demand (it lives in %LOCALAPPDATA%\SFHud, not next to the exe).

    Output:
      publish\SFHud-<Edition>-v<version>-win-x64\SFHud.exe   (+ README.md, CHANGELOG.md)
      dist\SFHud-<Edition>-Setup-<version>.exe     installer (same exe; the name starts setup mode)
      dist\SFHud-<Edition>-v<version>-win-x64.zip  portable   (Edition = Free or Pro)
      dist\*.sha256

.EXAMPLE
    .\build.ps1                      # release build, self-contained single file, zipped
.EXAMPLE
    .\build.ps1 -FrameworkDependent  # small build that needs the .NET 10 Desktop Runtime
.EXAMPLE
    .\build.ps1 -StopRunning -Run    # close a running published SFHud first, launch the new one after
.EXAMPLE
    .\build.ps1 -Edition Pro         # the paid, license-activated build (SFHud-Pro-...)
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # Free (open source) or Pro (license-activated, needs src\SFHud.App\Pro).
    [ValidateSet('Free', 'Pro')]
    [string]$Edition = 'Free',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    # Needs the .NET 10 Desktop Runtime on the target PC, but is ~1/10 the size.
    [switch]$FrameworkDependent,

    # Ship a folder of DLLs instead of one SFHud.exe.
    [switch]$NoSingleFile,

    # Precompile for faster startup (bigger exe).
    [switch]$ReadyToRun,

    # Skip the .zip.
    [switch]$NoZip,

    # Regenerate the icon from assets\icon.png even if it looks up to date.
    [switch]$Icon,

    # Close SFHud instances started from the output folder instead of failing.
    [switch]$StopRunning,

    # Launch the built exe when done.
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\SFHud.App\SFHud.App.csproj'

function Step([string]$text) { Write-Host "`n> $text" -ForegroundColor Cyan }
function Ok([string]$text) { Write-Host "  $text" -ForegroundColor Green }
function Fail([string]$text) { Write-Host "  $text" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------- toolchain
Step 'Checking .NET SDK'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Fail 'dotnet was not found. Install the .NET 10 SDK: https://dotnet.microsoft.com/download' }
$sdks = & dotnet --list-sdks
$has10 = $sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 10 }
if (-not $has10) { Fail ".NET 10 SDK or newer is required. Found:`n$($sdks -join "`n")" }
Ok ($has10 | Select-Object -Last 1)

# ---------------------------------------------------------------- version
[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { $version = '0.0.0' }
if ($Edition -eq 'Pro' -and -not (Test-Path (Join-Path $root 'src\SFHud.App\Pro\LicenseService.cs'))) { Fail 'The Pro edition needs src\SFHud.App\Pro (not part of the open-source repo).' }
$name = "SFHud-$Edition-v$version-$Runtime"
if ($FrameworkDependent) { $name += '-fxdep' }
$outDir = Join-Path $root "publish\$name"
$distDir = Join-Path $root 'dist'
Ok "SFHud $version  ->  $name"

# ---------------------------------------------------------------- icon
Step 'Icon'
$iconSrc = Join-Path $root 'assets\icon.png'
$iconOut = Join-Path $root 'src\SFHud.App\Assets\sfhud.ico'
if (-not (Test-Path $iconSrc)) {
    if (-not (Test-Path $iconOut)) { Fail 'assets\icon.png and src\SFHud.App\Assets\sfhud.ico are both missing.' }
    Ok 'assets\icon.png missing; using the existing sfhud.ico'
}
elseif ($Icon -or -not (Test-Path $iconOut) -or (Get-Item $iconSrc).LastWriteTime -gt (Get-Item $iconOut).LastWriteTime) {
    & (Join-Path $root 'tools\make-icon.ps1') | Out-Null
    Ok 'Regenerated sfhud.ico from assets\icon.png'
}
else { Ok 'Up to date' }

# ---------------------------------------------------------------- running copies
Step 'Checking for a running SFHud in the output folder'
$running = Get-Process -Name SFHud -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and $_.Path.StartsWith($outDir, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
}
if ($running) {
    if ($StopRunning) {
        $running | ForEach-Object { $_.CloseMainWindow() | Out-Null }
        Start-Sleep -Milliseconds 1500
        $running | Where-Object { -not $_.HasExited } | Stop-Process -Force
        Ok "Closed $($running.Count) running copy(ies)"
    }
    else { Fail "SFHud is running from $outDir. Close it or pass -StopRunning." }
}
else { Ok 'None' }

# ---------------------------------------------------------------- publish
Step "Publishing ($Configuration, $Runtime)"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$singleFile = if ($NoSingleFile) { 'false' } else { 'true' }
$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '-o', $outDir,
    '--self-contained', $selfContained,
    "-p:PublishSingleFile=$singleFile",
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    "-p:EnableCompressionInSingleFile=$selfContained",
    '-p:DebugType=none',
    '-p:SatelliteResourceLanguages=en',
    "-p:PublishReadyToRun=$(if ($ReadyToRun) { 'true' } else { 'false' })",
    "-p:Edition=$Edition",
    '-nologo'
)
$sw = [Diagnostics.Stopwatch]::StartNew()
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed (exit $LASTEXITCODE)." }
Ok ("Published in {0:N1} s" -f $sw.Elapsed.TotalSeconds)

$exe = Join-Path $outDir 'SFHud.exe'
if (-not (Test-Path $exe)) { Fail "SFHud.exe was not produced in $outDir" }
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'CHANGELOG.md') $outDir -Force

# ---------------------------------------------------------------- zip
$zip = $null
if (-not $NoZip) {
    Step 'Packaging'
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
    $zip = Join-Path $distDir "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name.zip" | Set-Content -Path "$zip.sha256" -Encoding ascii
    Ok "$zip"
    Ok "SHA-256 $hash"
}

# ---------------------------------------------------------------- installer
# SFHud.exe is its own installer: named *Setup*, it opens the setup window (folder choice, shortcuts,
# Apps & Features uninstall entry). The single-file build is self-contained, so nothing else is needed.
$setup = $null
if (-not $NoSingleFile -and -not $FrameworkDependent) {
    Step 'Installer'
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
    $setup = Join-Path $distDir "SFHud-$Edition-Setup-$version.exe"
    Copy-Item $exe $setup -Force
    $sh = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    "$sh  SFHud-$Edition-Setup-$version.exe" | Set-Content -Path "$setup.sha256" -Encoding ascii
    Ok "$setup"
    Ok "SHA-256 $sh"
}

# ---------------------------------------------------------------- summary
Step 'Done'
$exeMb = (Get-Item $exe).Length / 1MB
$folderMb = (Get-ChildItem $outDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Ok ("SFHud.exe     {0,8:N1} MB   {1}" -f $exeMb, $exe)
Ok ("folder       {0,8:N1} MB" -f $folderMb)
if ($zip) { Ok ("zip          {0,8:N1} MB" -f ((Get-Item $zip).Length / 1MB)) }
if ($setup) { Ok ("installer    {0,8:N1} MB   {1}" -f ((Get-Item $setup).Length / 1MB), $setup) }
if ($FrameworkDependent) { Write-Host '  Needs the .NET 10 Desktop Runtime on the target PC.' -ForegroundColor Yellow }

if ($Run) { Start-Process $exe }
