param(
    [string]$ExePath = (Join-Path $PSScriptRoot "../artifacts/win-x64/P2PRD.Service.exe"),
    [string]$ServiceName = "P2PRD",
    [switch]$NoStart
)

function Ensure-Administrator {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($current)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Error "Administrator privileges are required to install the service."
        exit 1
    }
}

function Invoke-Sc {
    param([string]$Arguments)
    $process = Start-Process sc.exe -ArgumentList $Arguments -NoNewWindow -PassThru -Wait
    if ($process.ExitCode -ne 0) {
        throw "sc.exe failed with exit code $($process.ExitCode): sc $Arguments"
    }
}

if (-not $IsWindows) {
    Write-Error "This install script must be run on Windows."
    exit 1
}

Ensure-Administrator

$fullPath = Resolve-Path -Path $ExePath -ErrorAction Stop

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -eq 'Running') {
        Write-Host "Stopping existing service $ServiceName..."
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
    }

    Write-Host "Removing existing service $ServiceName..."
    Invoke-Sc "delete $ServiceName"
}

Write-Host "Creating service $ServiceName using $fullPath..."
Invoke-Sc "create $ServiceName binPath= \"$fullPath\" DisplayName= \"P2P Remote Desktop\" start= auto"
Invoke-Sc "description $ServiceName \"P2P Remote Desktop host service\""

if (-not $NoStart.IsPresent) {
    Write-Host "Starting service $ServiceName..."
    Start-Service -Name $ServiceName -ErrorAction Stop
}

Write-Host "Service $ServiceName installed successfully."
