# Builds dist\Sniffer-Setup-<version>.exe: publishes a self-contained app, then compiles the Inno Setup script.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = ([xml](Get-Content "$root\Sniffer.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

Remove-Item "$root\dist\app" -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\Sniffer.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -o "$root\dist\app"
if ($LASTEXITCODE) { throw "dotnet publish failed" }

& $iscc "/DAppVersion=$version" "$root\installer\Sniffer.iss"
if ($LASTEXITCODE) { throw "Inno Setup failed" }
Write-Host "Installer: $root\dist\Sniffer-Setup-$version.exe"
