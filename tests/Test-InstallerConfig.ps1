<#
Runs the uninstall path against a disposable GnuPG home and fake gpgconf.
No primary GnuPG configuration or agent is touched.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root = Join-Path $repo 'artifacts' ('installer-test-' + [Guid]::NewGuid().ToString('N'))
$fakeBin = Join-Path $root 'fake-bin'
$installed = Join-Path $root 'installed'
$other = Join-Path $root 'other'
$gpgHome = Join-Path $root 'gpg-home'
$oldPath = $env:PATH
$oldBindir = $env:PFW_TEST_BINDIR
$oldHomedir = $env:PFW_TEST_HOMEDIR

try {
  foreach ($directory in @($fakeBin, $installed, $other, $gpgHome)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
  }

  [System.IO.File]::WriteAllLines((Join-Path $fakeBin 'gpgconf.cmd'), @(
    '@echo off'
    'if "%1" == "--list-dirs" ('
    '  if "%2" == "bindir" echo %PFW_TEST_BINDIR%'
    '  if "%2" == "homedir" echo %PFW_TEST_HOMEDIR%'
    '  exit /b 0'
    ')'
    'exit /b 1'
  ))

  $env:PATH = "$fakeBin;$oldPath"
  $env:PFW_TEST_BINDIR = $installed
  $env:PFW_TEST_HOMEDIR = $gpgHome

  $ownedPath = Join-Path $installed 'pfw.x64.exe'
  $externalPath = Join-Path $other 'pfw.x64.exe'
  [System.IO.File]::WriteAllText($ownedPath, 'disposable installed binary')
  [System.IO.File]::WriteAllText($externalPath, 'user-owned binary')

  $config = Join-Path $gpgHome 'gpg-agent.conf'
  $original = "default-cache-ttl 300`npinentry-program $externalPath`n"
  [System.IO.File]::WriteAllText($config, $original)

  foreach ($iteration in 1..2) {
    $output = & pwsh -NoProfile -File (Join-Path $repo 'install.ps1') `
      -Uninstall -NoRestartAgent -InstallDirectory $installed 2>&1
    if ($LASTEXITCODE -ne 0) {
      throw "Uninstall run $iteration failed: $($output -join ' | ')"
    }
    if ([System.IO.File]::ReadAllText($config) -cne $original) {
      throw 'The unrelated gpg-agent.conf setting changed.'
    }
    if (-not (Test-Path -LiteralPath $externalPath)) {
      throw 'An executable outside the install directory was removed.'
    }
    if (Test-Path -LiteralPath $ownedPath) {
      throw 'The disposable executable inside the install directory was not removed.'
    }
  }

  [System.IO.File]::WriteAllText($ownedPath, 'disposable installed binary')
  $mixed = "pinentry-program $externalPath`npinentry-program $ownedPath`n"
  [System.IO.File]::WriteAllText($config, $mixed)
  $output = & pwsh -NoProfile -File (Join-Path $repo 'install.ps1') `
    -Uninstall -NoRestartAgent -InstallDirectory $installed 2>&1
  if ($LASTEXITCODE -eq 0 -or
      [System.IO.File]::ReadAllText($config) -cne $mixed -or
      -not (Test-Path -LiteralPath $ownedPath) -or
      -not (Test-Path -LiteralPath $externalPath)) {
    throw 'Mixed pinentry-program settings were not preserved for review.'
  }

  # Load the actual installer functions without executing its entry point. Stub
  # only release discovery/download so installation stays entirely offline.
  $installer = Join-Path $repo 'install.ps1'
  $source = [System.IO.File]::ReadAllText($installer)
  $tokens = $null
  $parseErrors = $null
  $ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $installer, [ref]$tokens, [ref]$parseErrors)
  if ($parseErrors.Count -ne 0) { throw 'Installer syntax validation failed.' }
  $functions = @($ast.EndBlock.Statements | Where-Object {
      $_ -is [System.Management.Automation.Language.FunctionDefinitionAst]
    })
  . ([scriptblock]::Create($source.Substring(0, $functions[0].Extent.StartOffset)))
  foreach ($definition in $functions) {
    . ([scriptblock]::Create($definition.Extent.Text))
  }

  $payload = [System.Text.Encoding]::UTF8.GetBytes('disposable release executable')
  $expectedHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($payload))
  function Get-LatestRelease {
    [pscustomobject]@{ tag_name = 'test'; assets = @(
        [pscustomobject]@{ name = 'pfw.x64.exe' },
        [pscustomobject]@{ name = 'SHA256SUMS.txt' }
      ) }
  }
  function Download-Asset {
    param($Asset, [string]$DestinationPath)
    if ($Asset.name -eq 'pfw.x64.exe') {
      [System.IO.File]::WriteAllBytes($DestinationPath, $payload)
    }
    else {
      [System.IO.File]::WriteAllText($DestinationPath, "$expectedHash  pfw.x64.exe`n")
    }
  }
  function Confirm-ReplacePinentryProgram { return $false }
  $NoRestartAgent = $true
  [System.IO.File]::WriteAllText($config, $original)
  Install-LatestRelease -GpgConfPath (Join-Path $fakeBin 'gpgconf.cmd') `
    -HomeDirectory $gpgHome -InstallDirectory $installed -IsUpdate $false
  if ([System.IO.File]::ReadAllText($config) -cne $original -or
      -not (Test-Path -LiteralPath $externalPath) -or
      [System.IO.File]::ReadAllText($ownedPath) -cne 'disposable release executable' -or
      @(Get-ChildItem -LiteralPath $installed -Filter '.pfw-download-*').Count -ne 0) {
    throw 'Offline installation changed an unapproved setting or left temporary assets.'
  }

  function Confirm-ReplacePinentryProgram { return $true }
  Install-LatestRelease -GpgConfPath (Join-Path $fakeBin 'gpgconf.cmd') `
    -HomeDirectory $gpgHome -InstallDirectory $installed -IsUpdate $true
  $configured = @(Get-Content -LiteralPath $config)
  $ownedGpgPath = $ownedPath.Replace('\', '/')
  if (@($configured | Where-Object { $_ -like '*previous-pinentry-program:*' }).Count -ne 1 -or
      @($configured | Where-Object { $_ -eq "pinentry-program $ownedGpgPath" }).Count -ne 1 -or
      @(Get-ChildItem -LiteralPath $gpgHome -Filter 'gpg-agent.conf.*.bak').Count -lt 1) {
    throw "Approved replacement did not record the previous setting and a backup: $($configured -join ' | ')"
  }

  function Confirm-RestorePreviousPinentryProgram { return 'Restore' }
  Uninstall-PinentryForWindows -GpgConfPath (Join-Path $fakeBin 'gpgconf.cmd') `
    -HomeDirectory $gpgHome -InstallDirectory $installed
  $restored = @(Get-Content -LiteralPath $config)
  if ($restored.Count -ne 2 -or $restored[0] -cne 'default-cache-ttl 300' -or
      $restored[1] -cne "pinentry-program $externalPath" -or
      -not (Test-Path -LiteralPath $externalPath) -or
      (Test-Path -LiteralPath $ownedPath) -or
      @(Get-ChildItem -LiteralPath $gpgHome -Filter 'gpg-agent.conf.*.bak').Count -ne 2) {
    throw 'Uninstall did not restore the previous pinentry and remove only the installed binary.'
  }

  Write-Host 'Installer offline install, approval, restore, and uninstall isolation: PASS'
}
finally {
  $env:PATH = $oldPath
  $env:PFW_TEST_BINDIR = $oldBindir
  $env:PFW_TEST_HOMEDIR = $oldHomedir
  if (Test-Path -LiteralPath $root) {
    Remove-Item -LiteralPath $root -Recurse -Force
  }
}
