param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$IsccPath,
    [string]$LogDirectory = ''
)
$ErrorActionPreference = 'Stop'
$beamRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$beamBuilder = Join-Path $beamRepo 'scripts/Publish-BeamInstaller.ps1'
if (-not (Test-Path -LiteralPath $beamBuilder)) { throw 'Beam installer builder is not implemented.' }
$beamId = [Guid]::NewGuid().ToString('N')
$beamTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$beamRoot = Join-Path $beamTemp ('Beam.InstallerTests-' + $beamId)
$beamInstall = Join-Path $beamRoot 'Installed app with spaces'
$beamRunRoot = 'Software\Beam\InstallerTests\' + $beamId
$beamRunKey = $beamRunRoot + '\Run'
$beamUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Beam.InstallerTest.' + $beamId + '_is1'
$beamCommand = '"' + (Join-Path $beamInstall 'Beam.App.exe') + '" --startup'
if (-not $LogDirectory) { $LogDirectory = Join-Path $beamRepo ('artifacts/installer-test-logs/' + $beamId) }
New-Item -ItemType Directory -Path $beamRoot,$LogDirectory -Force | Out-Null

function Require-BeamTest([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}
function Run-BeamProcess([string]$file, [string[]]$arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($file)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = 'Hidden'
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true) # This is only the installer test process we started.
            throw 'Installer test process timed out.'
        }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
function Read-BeamRun {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($beamRunKey)
    try { if ($key) { return $key.GetValue('Beam') } } finally { if ($key) { $key.Dispose() } }
}
function Write-BeamRun([string]$command) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($beamRunKey)
    try { $key.SetValue('Beam', $command, [Microsoft.Win32.RegistryValueKind]::String) } finally { $key.Dispose() }
}
function Remove-BeamRun {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($beamRunKey, $true)
    try { if ($key) { $key.DeleteValue('Beam', $false) } } finally { if ($key) { $key.Dispose() } }
}
function Install-BeamFixture([string]$setup, [string]$name, [string[]]$extra = @()) {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR=' + $beamInstall),('/LOG=' + (Join-Path $LogDirectory ($name + '.log')))) + $extra
    return Run-BeamProcess $setup $arguments
}
function Get-BeamUninstaller {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($beamUninstallKey)
    try { if (-not $key) { return $null }; $command = [string]$key.GetValue('UninstallString') }
    finally { if ($key) { $key.Dispose() } }
    if ($command -match '^"([^"]+)"') { $candidate = $Matches[1] } else { $candidate = $command }
    $resolved = [IO.Path]::GetFullPath($candidate)
    if (-not $resolved.StartsWith($beamInstall + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^unins\d+\.exe$') {
        throw 'Unexpected installer test uninstaller path.'
    }
    return $resolved
}
function Uninstall-BeamFixture([string]$name) {
    $uninstaller = Get-BeamUninstaller
    if (-not $uninstaller) { throw 'Installer test uninstaller is missing.' }
    return Run-BeamProcess $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG=' + (Join-Path $LogDirectory ($name + '.log'))))
}
$beamData = Join-Path $beamRoot 'Documents/Beam'
New-Item -ItemType Directory -Path $beamData -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $beamData 'token.txt'), 'synthetic-install-token')
[IO.File]::WriteAllText((Join-Path $beamData 'history.json'), '[{"synthetic":"retained"}]')
$beamOriginalData = @{}
Get-ChildItem -LiteralPath $beamData -File | ForEach-Object { $beamOriginalData[$_.Name] = (Get-FileHash -LiteralPath $_.FullName).Hash }
function Check-BeamData {
    foreach ($name in $beamOriginalData.Keys) {
        Require-BeamTest ((Get-FileHash -LiteralPath (Join-Path $beamData $name)).Hash -eq $beamOriginalData[$name]) 'Installer changed synthetic user data.'
    }
}
try {
    $beamInvalidPayload = Join-Path $beamRoot 'Invalid payload'
    foreach ($relative in @('Beam.App.exe','Beam.App.dll','coreclr.dll','hostfxr.dll','wwwroot/index.html','wwwroot/app.js','wwwroot/vault.js','wwwroot/motion.js','wwwroot/styles.css','wwwroot/lucide.min.js','wwwroot/lucide.LICENSE','START-HERE.txt')) {
        $file = Join-Path $beamInvalidPayload $relative
        New-Item -ItemType Directory -Path (Split-Path $file -Parent) -Force | Out-Null
        [IO.File]::WriteAllText($file, 'synthetic-invalid-payload')
    }
    $beamNestedOutput = Join-Path $beamInvalidPayload 'Nested output'
    $beamRejected = $false
    try {
        $null = & $beamBuilder -PublishDirectory $beamInvalidPayload -OutputDirectory $beamNestedOutput -Version '0.1.0-installertest.1' -IsccPath (Join-Path $beamRoot 'missing-compiler.exe') -TestIdentity $beamId -TestRoot $beamRoot
    } catch { $beamRejected = $_.Exception.Message -like 'Output directory must be outside*' }
    Require-BeamTest $beamRejected 'Builder did not reject output inside its own payload before creating files.'
    Require-BeamTest (-not (Test-Path -LiteralPath $beamNestedOutput)) 'Rejected nested output created files in the payload.'

    [IO.File]::WriteAllText((Join-Path $beamInvalidPayload 'history.json'), 'synthetic-private-data')
    [IO.File]::SetAttributes((Join-Path $beamInvalidPayload 'history.json'), [IO.FileAttributes]::Hidden)
    $beamRejected = $false
    try {
        $null = & $beamBuilder -PublishDirectory $beamInvalidPayload -OutputDirectory (Join-Path $beamRoot 'Rejected private output') -Version '0.1.0-installertest.1' -IsccPath $IsccPath
    } catch { $beamRejected = $_.Exception.Message -like 'Refusing an installer payload containing personal data*' }
    Require-BeamTest $beamRejected 'Builder did not reject personal data before compiling.'
    $beamRejected = $false
    try {
        $null = & $beamBuilder -PublishDirectory $PublishDirectory -OutputDirectory (Join-Path $beamRoot 'Rejected version output') -Version '0.0.0-mismatch' -IsccPath $IsccPath
    } catch { $beamRejected = $_.Exception.Message -like 'Installer version must match*' }
    Require-BeamTest $beamRejected 'Builder did not reject a mismatched application version.'
    Write-Output 'PASS: packaging rejects nested output, personal data and mismatched versions before compiling.'

    $beamSetup1 = & $beamBuilder -PublishDirectory $PublishDirectory -OutputDirectory (Join-Path $beamRoot 'Build1') -Version '0.1.0-installertest.1' -IsccPath $IsccPath -TestIdentity $beamId -TestRoot $beamRoot
    $beamSetup2 = & $beamBuilder -PublishDirectory $PublishDirectory -OutputDirectory (Join-Path $beamRoot 'Build2') -Version '0.1.0-installertest.2' -IsccPath $IsccPath -TestIdentity $beamId -TestRoot $beamRoot
    $beamMutex = [Threading.Mutex]::new($false, ('Local\Beam.InstallerTest.' + $beamId))
    try {
        Require-BeamTest ((Install-BeamFixture $beamSetup1 'blocked-install') -ne 0) 'Setup ignored a running-app mutex.'
        Require-BeamTest (-not (Test-Path -LiteralPath (Join-Path $beamInstall 'Beam.App.exe'))) 'Blocked setup wrote application files.'
    } finally { $beamMutex.Dispose() }
    Write-Output 'PASS: setup refuses a running application without force-closing it.'

    Require-BeamTest ((Install-BeamFixture $beamSetup1 'fresh') -eq 0) 'Fresh installation failed.'
    Require-BeamTest ($null -eq (Read-BeamRun)) 'Fresh installation enabled startup without opting in.'
    foreach ($relative in @('Beam.App.exe','Beam.App.dll','wwwroot/app.js','wwwroot/vault.js','START-HERE.txt')) {
        Require-BeamTest ((Get-FileHash -LiteralPath (Join-Path $beamInstall $relative)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $PublishDirectory $relative)).Hash) "Installed payload differs: $relative"
    }
    Require-BeamTest (Test-Path -LiteralPath (Join-Path $beamRoot 'Shortcuts/Start Menu/Beam.lnk')) 'Start-menu shortcut was not installed.'
    Require-BeamTest (-not (Test-Path -LiteralPath (Join-Path $beamRoot 'Shortcuts/Desktop/Beam.lnk'))) 'Desktop shortcut was not optional.'
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($beamUninstallKey)
    try { Require-BeamTest ($key -and $key.GetValue('DisplayVersion') -eq '0.1.0-installertest.1') 'Installed-app metadata missing.' }
    finally { if ($key) { $key.Dispose() } }
    Write-Output 'PASS: per-user install defaults startup off, installs matching payload/Start menu, and registers an uninstaller.'

    Require-BeamTest ((Install-BeamFixture $beamSetup2 'opt-in-upgrade' @('/TASKS=startup,desktopicon')) -eq 0) 'Opt-in upgrade failed.'
    Require-BeamTest ((Read-BeamRun) -eq $beamCommand) 'Startup command is not the quoted installed executable plus --startup.'
    Require-BeamTest (Test-Path -LiteralPath (Join-Path $beamRoot 'Shortcuts/Desktop/Beam.lnk')) 'Selected desktop shortcut missing.'
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($beamRunKey, $true)
    try { $key.SetValue('Unrelated', 'keep') } finally { $key.Dispose() }
    Remove-BeamRun # Simulate turning the option off in Beam after setup.
    Require-BeamTest ((Install-BeamFixture $beamSetup2 'preserve-opt-out') -eq 0) 'Upgrade after app-side opt-out failed.'
    Require-BeamTest ($null -eq (Read-BeamRun)) 'Upgrade re-enabled the stale setup preference.'
    Write-BeamRun $beamCommand
    Require-BeamTest ((Install-BeamFixture $beamSetup2 'preserve-opt-in') -eq 0) 'Upgrade with enabled startup failed.'
    Require-BeamTest ((Read-BeamRun) -eq $beamCommand) 'Upgrade discarded the current startup preference.'
    Require-BeamTest ((Install-BeamFixture $beamSetup2 'merge-shortcut-only' @('/MERGETASKS=desktopicon')) -eq 0) 'Shortcut merge upgrade failed.'
    Require-BeamTest ((Read-BeamRun) -eq $beamCommand) 'An unrelated task merge discarded enabled startup.'
    Require-BeamTest (Test-Path -LiteralPath (Join-Path $beamRoot 'Shortcuts/Desktop/Beam.lnk')) 'Unrelated merge lost its explicit shortcut choice.'
    Require-BeamTest ((Install-BeamFixture $beamSetup2 'merge-explicit-off' @('/MERGETASKS=!startup')) -eq 0) 'Merged startup opt-out failed.'
    Require-BeamTest ($null -eq (Read-BeamRun)) 'Merged explicit startup opt-out was ignored.'
    Require-BeamTest ((Install-BeamFixture $beamSetup2 'merge-shortcut-while-off' @('/MERGETASKS=!desktopicon')) -eq 0) 'Shortcut-only merge while off failed.'
    Require-BeamTest ($null -eq (Read-BeamRun)) 'An unrelated task merge re-enabled disabled startup.'
    Require-BeamTest ((Install-BeamFixture $beamSetup2 'merge-explicit-on' @('/MERGETASKS=startup')) -eq 0) 'Merged startup opt-in failed.'
    Require-BeamTest ((Read-BeamRun) -eq $beamCommand) 'Merged explicit startup opt-in was ignored.'
    Require-BeamTest ((Install-BeamFixture $beamSetup2 'explicit-opt-out' @('/TASKS=')) -eq 0) 'Explicit startup opt-out failed.'
    Require-BeamTest ($null -eq (Read-BeamRun)) 'Explicit task selection was overwritten by saved startup state.'
    Write-Output 'PASS: upgrades retain app-side opt-out/opt-in, honor explicit tasks, and quote paths with spaces.'

    Write-BeamRun $beamCommand
    [IO.File]::WriteAllText((Join-Path $beamInstall 'keep-user-file.txt'), 'synthetic-unowned-file')
    $beamMutex = [Threading.Mutex]::new($false, ('Local\Beam.InstallerTest.' + $beamId))
    try {
        Require-BeamTest ((Uninstall-BeamFixture 'blocked-uninstall') -ne 0) 'Uninstall ignored the running-app mutex.'
        Require-BeamTest (Test-Path -LiteralPath (Join-Path $beamInstall 'Beam.App.exe')) 'Blocked uninstall removed application files.'
    } finally { $beamMutex.Dispose() }
    Require-BeamTest ((Uninstall-BeamFixture 'owned-uninstall') -eq 0) 'Uninstall failed.'
    Require-BeamTest ($null -eq (Read-BeamRun)) 'Uninstall left its owned startup command.'
    Require-BeamTest (-not (Test-Path -LiteralPath (Join-Path $beamInstall 'Beam.App.exe'))) 'Uninstall left installed application files.'
    Require-BeamTest (Test-Path -LiteralPath (Join-Path $beamInstall 'keep-user-file.txt')) 'Uninstall removed an unowned file.'
    Require-BeamTest (-not (Test-Path -LiteralPath (Join-Path $beamRoot 'Shortcuts/Start Menu/Beam.lnk'))) 'Uninstall left its Start-menu shortcut.'
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($beamUninstallKey)
    try { Require-BeamTest ($null -eq $key) 'Uninstall left its installed-app entry.' } finally { if ($key) { $key.Dispose() } }
    Check-BeamData
    Write-Output 'PASS: uninstall is blocked while running, removes owned files/startup/shortcuts and preserves data and unowned files.'

    Require-BeamTest ((Install-BeamFixture $beamSetup2 'foreign-command-install' @('/TASKS=startup')) -eq 0) 'Foreign-command fixture install failed.'
    $beamForeignCommand = '"C:\Another Beam copy\Beam.App.exe" --startup'
    Write-BeamRun $beamForeignCommand
    Require-BeamTest ((Uninstall-BeamFixture 'foreign-command-uninstall') -eq 0) 'Foreign-command fixture uninstall failed.'
    Require-BeamTest ((Read-BeamRun) -eq $beamForeignCommand) 'Uninstall deleted another copy''s startup command.'
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($beamRunKey)
    try { Require-BeamTest ($key.GetValue('Unrelated') -eq 'keep') 'Uninstall changed an unrelated registry value.' } finally { $key.Dispose() }
    Check-BeamData
    Write-Output 'PASS: uninstall preserves another copy''s startup command and unrelated registry values.'
}
finally {
    $uninstaller = Get-BeamUninstaller
    if ($uninstaller -and (Test-Path -LiteralPath $uninstaller)) { try { $null = Uninstall-BeamFixture 'cleanup' } catch { Write-Warning $_.Exception.Message } }
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($beamRunRoot, $false)
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($beamUninstallKey, $false)
    $resolved = [IO.Path]::GetFullPath($beamRoot)
    if (-not $resolved.StartsWith($beamTemp, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -ne ('Beam.InstallerTests-' + $beamId)) { throw 'Refusing cleanup outside the installer test workspace.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
