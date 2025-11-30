[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SelfContained,
    [switch]$SkipSignalingServer
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$artifactRoot = Join-Path $repoRoot "artifacts/$Runtime"

if (-not (Test-Path $artifactRoot)) {
    New-Item -ItemType Directory -Path $artifactRoot | Out-Null
}

Write-Host "Publishing projects for runtime '$Runtime' (Configuration=$Configuration, SelfContained=$($SelfContained.IsPresent))"

dotnet restore $repoRoot

function Publish-Project {
    param(
        [Parameter(Mandatory)] [string]$Project,
        [Parameter(Mandatory)] [string]$Name
    )

    $outputPath = Join-Path $artifactRoot $Name
    if (Test-Path $outputPath) {
        Remove-Item $outputPath -Recurse -Force
    }

    dotnet publish $Project `
        -c $Configuration `
        -r $Runtime `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishReadyToRun=true `
        -p:SelfContained=$($SelfContained.IsPresent) `
        -o $outputPath
}

Publish-Project -Project "$repoRoot/src/Service/P2PRD.Service.csproj" -Name "Service"
Publish-Project -Project "$repoRoot/src/OperatorConsole/OperatorConsole.csproj" -Name "OperatorConsole"
Publish-Project -Project "$repoRoot/src/Configurator/Configurator.csproj" -Name "Configurator"

if (-not $SkipSignalingServer) {
    Publish-Project -Project "$repoRoot/src/SignalingServer/SignalingServer.csproj" -Name "SignalingServer"
}

Write-Host "Done. Artifacts under $artifactRoot"
