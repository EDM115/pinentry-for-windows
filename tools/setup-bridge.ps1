param(
  [Parameter(Mandatory = $true)][string]$ExecutablePath,
  [string]$BrokerDirectory = (Join-Path $env:LOCALAPPDATA 'PinentryForWindows\broker'),
  [string]$Distribution,
  [string]$RelayCertificatePath,
  [switch]$NoAutostart,
  [switch]$RemoveAutostart
)

$ErrorActionPreference = 'Stop'
$ServiceId = '0000AFBA-FACB-11E6-BD58-64006A7986D3'
$RunPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunName = 'PinentryForWindowsBroker'
$ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)
$BrokerDirectory = [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($BrokerDirectory))

if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf))
{
  throw "Pinentry executable not found: $ExecutablePath"
}

if ($RemoveAutostart)
{
  if (Test-Path $RunPath)
  {
    $current = (Get-ItemProperty -Path $RunPath -Name $RunName -ErrorAction SilentlyContinue).$RunName
    if ($current -and $current.StartsWith('"' + $ExecutablePath + '" --broker ', [StringComparison]::OrdinalIgnoreCase))
    {
      Remove-ItemProperty -Path $RunPath -Name $RunName
      Write-Host 'Broker autostart removed.'
    }
  }

  exit 0
}

& $ExecutablePath --init-broker $BrokerDirectory
if ($LASTEXITCODE -ne 0)
{
  throw 'Broker identity initialization failed.'
}

if ($RelayCertificatePath)
{
  if (-not $Distribution -or $Distribution -notmatch '^[A-Za-z0-9._-]+$')
  {
    throw 'A distribution name containing only letters, digits, dot, underscore or hyphen is required.'
  }

  $RelayCertificatePath = [System.IO.Path]::GetFullPath($RelayCertificatePath)
  if (-not (Test-Path -LiteralPath $RelayCertificatePath -PathType Leaf))
  {
    throw "Relay public certificate not found: $RelayCertificatePath"
  }

  & $ExecutablePath --approve-client $BrokerDirectory $RelayCertificatePath "wsl:$Distribution"
  if ($LASTEXITCODE -ne 0)
  {
    throw 'Relay pairing failed.'
  }
}

if (-not $NoAutostart)
{
  if (-not (Test-Path $RunPath))
  {
    New-Item -Path $RunPath -Force | Out-Null
  }
  $value = '"' + $ExecutablePath + '" --broker "' + $BrokerDirectory + '"'
  $current = (Get-ItemProperty -Path $RunPath -Name $RunName -ErrorAction SilentlyContinue).$RunName
  if ($current -and $current -ne $value)
  {
    throw 'An existing broker autostart points elsewhere. Review it before replacement.'
  }

  Set-ItemProperty -Path $RunPath -Name $RunName -Value $value
  Write-Host 'Broker autostart configured for this Windows user.'
}

Write-Host 'Run the following command in an elevated Command Prompt to register the Hyper-V service:'
Write-Host ('reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\' +
  $ServiceId + '" /v ElementName /t REG_SZ /d "PinentryForWindows Broker" /f')
Write-Host ('Broker public certificate for the WSL relay: ' + (Join-Path $BrokerDirectory 'broker.cer'))
Write-Host ('Start the broker for this session with: "' + $ExecutablePath + '" --broker "' + $BrokerDirectory + '"')
