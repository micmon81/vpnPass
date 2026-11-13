param([string]$OutputDirectory = 'dist\vpnPass', [string]$PackageName = 'vpnPass-win-x64.zip')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'VpnPass\VpnPass.csproj'
$tests = Join-Path $PSScriptRoot 'VpnPass.Tests\VpnPass.Tests.csproj'
$output = Join-Path $PSScriptRoot $OutputDirectory

dotnet run --project $tests -c Release -- --inspect-network
if ($LASTEXITCODE -ne 0) { throw 'C# checks failed.' }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'VpnPass.Tests\Test-Routes.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Route lifecycle checks failed.' }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $output 'README.md') -Force
Compress-Archive -LiteralPath (Join-Path $output 'vpnPass.exe'), (Join-Path $output 'README.md') -DestinationPath (Join-Path $PSScriptRoot ('dist\' + $PackageName)) -Force
Write-Output "Ready: $output\vpnPass.exe"
