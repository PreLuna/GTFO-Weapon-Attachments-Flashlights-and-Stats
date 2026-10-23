param(
 [Parameter(Mandatory=$true)][string]$GameRoot,
 [Parameter(Mandatory=$true)][string]$RuntimeRoot,
 [string]$BuildToolsRoot=(Join-Path (Split-Path $PSScriptRoot -Parent) 'GTFO-Mod-Build-and-Verification-Tools'),
 [string]$DependencyRoot='', [string]$CecilPath=''
)
$ErrorActionPreference='Stop'
& (Join-Path $BuildToolsRoot 'tools/Build-Repository.ps1') -RepositoryRoot $PSScriptRoot -GameRoot $GameRoot -RuntimeRoot $RuntimeRoot -DependencyRoot $DependencyRoot -CecilPath $CecilPath
