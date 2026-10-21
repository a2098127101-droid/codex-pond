[CmdletBinding()]
param(
    [string]$AppId = $env:STEAM_APP_ID,
    [string]$DepotId = $env:STEAM_DEPOT_ID,
    [string]$NuGetSource = 'https://api.nuget.org/v3/index.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$steamPipeRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $steamPipeRoot '..'))
$projectFile = Join-Path $projectRoot 'CodexPond.csproj'
if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
    throw "WPF project not found: $projectFile"
}

$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture) + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$stageRoot = [System.IO.Path]::GetFullPath((Join-Path $steamPipeRoot "staging\win-x64-$stamp"))
$buildOutput = [System.IO.Path]::GetFullPath((Join-Path $steamPipeRoot "build-output\$stamp"))
$generatedRoot = [System.IO.Path]::GetFullPath((Join-Path $steamPipeRoot "generated\$stamp"))

foreach ($path in @($stageRoot, $buildOutput, $generatedRoot)) {
    $relative = [System.IO.Path]::GetRelativePath($steamPipeRoot, $path)
    if ($relative.StartsWith('..') -or [System.IO.Path]::IsPathRooted($relative)) {
        throw "Refusing to write outside SteamPipe folder: $path"
    }
}

New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
$restoreArgs = @('restore', $projectFile, '-r', 'win-x64', "-p:RestoreSources=$NuGetSource", '-p:NuGetAudit=false')
& dotnet @restoreArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

$publishArgs = @('publish', $projectFile, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-o', $stageRoot)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$steamApi64Dll = $env:STEAM_API64_DLL
if (-not [string]::IsNullOrWhiteSpace($steamApi64Dll)) {
    $nativeDllPath = [System.IO.Path]::GetFullPath($steamApi64Dll)
    if (-not (Test-Path -LiteralPath $nativeDllPath -PathType Leaf)) { throw "STEAM_API64_DLL does not exist: $nativeDllPath" }
    if ([System.IO.Path]::GetFileName($nativeDllPath) -ine 'steam_api64.dll') { throw 'STEAM_API64_DLL must point to Valve steam_api64.dll.' }
    Copy-Item -LiteralPath $nativeDllPath -Destination (Join-Path $stageRoot 'steam_api64.dll')
} else {
    Write-Warning 'Valve steam_api64.dll was not supplied; this package remains usable offline but Steamworks runtime initialization will be unavailable.'
}

if ([string]::IsNullOrWhiteSpace($AppId) -and [string]::IsNullOrWhiteSpace($DepotId)) {
    Write-Output "Self-contained Windows x64 package: $stageRoot"
    Write-Output 'SteamPipe VDF not generated because STEAM_APP_ID and STEAM_DEPOT_ID are unset.'
    exit 0
}

if ($AppId -notmatch '^[1-9]\d{0,11}$' -or $DepotId -notmatch '^[1-9]\d{0,11}$') {
    throw 'Set both STEAM_APP_ID and STEAM_DEPOT_ID to the numeric IDs assigned in Steamworks.'
}

New-Item -ItemType Directory -Path $buildOutput -Force | Out-Null
New-Item -ItemType Directory -Path $generatedRoot -Force | Out-Null
$contentRootVdf = $stageRoot.Replace('\', '/')
$buildOutputVdf = $buildOutput.Replace('\', '/')
$appTemplate = Get-Content -LiteralPath (Join-Path $steamPipeRoot 'app_build.vdf.template') -Raw
$depotTemplate = Get-Content -LiteralPath (Join-Path $steamPipeRoot 'depot_build_windows.vdf.template') -Raw
$appVdf = $appTemplate.Replace('@APP_ID@', $AppId).Replace('@DEPOT_ID@', $DepotId).Replace('@BUILD_OUTPUT@', $buildOutputVdf).Replace('@CONTENT_ROOT@', $contentRootVdf)
$depotVdf = $depotTemplate.Replace('@DEPOT_ID@', $DepotId).Replace('@CONTENT_ROOT@', $contentRootVdf)
[System.IO.File]::WriteAllText((Join-Path $generatedRoot 'app_build.vdf'), $appVdf, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText((Join-Path $generatedRoot 'depot_build_windows.vdf'), $depotVdf, [System.Text.UTF8Encoding]::new($false))

Write-Output "Self-contained Windows x64 package: $stageRoot"
Write-Output "Local SteamPipe app configuration: $(Join-Path $generatedRoot 'app_build.vdf')"
Write-Output 'No upload or live-branch operation was started.'
