param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $repositoryRoot "Server/$Runtime"
dotnet publish (Join-Path $repositoryRoot 'src/Eota.Server.Host/Eota.Server.Host.csproj') -c Release -r $Runtime --self-contained true -o $publishDirectory --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }
Set-Content -LiteralPath (Join-Path $repositoryRoot 'Server/.gdignore') -Value ''
