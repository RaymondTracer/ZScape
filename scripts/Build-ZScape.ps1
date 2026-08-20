[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release', 'Both')]
    [string]$Configuration = 'Both',

    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot 'ZScape.csproj'
$documentsPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)

if ([string]::IsNullOrWhiteSpace($documentsPath)) {
    throw 'Windows did not provide a Documents directory for the build archive.'
}

$configurations = if ($Configuration -eq 'Both') {
    @('Debug', 'Release')
}
else {
    @($Configuration)
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$archiveRoot = Join-Path $documentsPath (Join-Path 'ZScape\Builds' $timestamp)

foreach ($currentConfiguration in $configurations) {
    $buildArguments = @('build', $projectPath, '-c', $currentConfiguration, '-v:minimal')
    if ($NoRestore) {
        $buildArguments += '--no-restore'
    }

    Write-Host "Building ZScape ($currentConfiguration)..."
    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) {
        throw "The $currentConfiguration build failed; no archive was created for that configuration."
    }

    $configurationOutputRoot = Join-Path $projectRoot (Join-Path 'bin' $currentConfiguration)
    $executable = Get-ChildItem -LiteralPath $configurationOutputRoot -Filter 'ZScape.exe' -File -Recurse |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -eq $executable) {
        throw "The $currentConfiguration build completed but ZScape.exe was not found under $configurationOutputRoot."
    }

    $sourceDirectory = $executable.Directory.FullName
    $destinationDirectory = Join-Path $archiveRoot $currentConfiguration
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null

    Get-ChildItem -LiteralPath $sourceDirectory -Force |
        Copy-Item -Destination $destinationDirectory -Recurse -Force

    Write-Host "Archived $currentConfiguration output to $destinationDirectory"
}

Write-Host "Complete. Build archives are in $archiveRoot"
