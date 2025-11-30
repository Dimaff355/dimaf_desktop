param(
    [string]$ServiceName = "P2PRD"
)

function Ensure-Administrator {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($current)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Error "Administrator privileges are required to uninstall the service."
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
    Write-Error "This uninstall script must be run on Windows."
    exit 1
}

Ensure-Administrator

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service $ServiceName is not installed."
    exit 0
}

if ($existing.Status -eq 'Running') {
    Write-Host "Stopping service $ServiceName..."
    Stop-Service -Name $ServiceName -Force -ErrorAction Stop
}

Write-Host "Removing service $ServiceName..."
Invoke-Sc "delete $ServiceName"
Write-Host "Service $ServiceName removed."
