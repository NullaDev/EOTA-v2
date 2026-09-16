param([string]$OutputFile = '')
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputFile) { $OutputFile = Join-Path $repositoryRoot 'Docs/CardTable.zh-CN.md' }
dotnet run --project (Join-Path $PSScriptRoot 'Eota.ContentCli') -c Release -- table $repositoryRoot $OutputFile
if ($LASTEXITCODE -ne 0) { throw 'Card table generation failed.' }
