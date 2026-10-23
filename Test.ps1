param([Parameter(Mandatory=$true)][string]$RuntimeRoot,[string]$BuildToolsRoot=(Join-Path (Split-Path $PSScriptRoot -Parent) 'GTFO-Mod-Build-and-Verification-Tools'))
$ErrorActionPreference='Stop'
& (Join-Path $BuildToolsRoot 'tools/Test-Recipes.ps1') -RepositoryRoot $PSScriptRoot -RuntimeRoot $RuntimeRoot
