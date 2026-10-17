param([string]$Destination = (Join-Path ([IO.Path]::GetTempPath()) 'BeamBuildTools/InnoSetup-6.7.3'))
$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
$beamCompiler = Join-Path $Destination 'ISCC.exe'
if (Test-Path -LiteralPath $beamCompiler) { return $beamCompiler }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$beamDownload = Join-Path $Destination 'innosetup-6.7.3.exe'
Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $beamDownload
$beamHash = (Get-FileHash -LiteralPath $beamDownload -Algorithm SHA256).Hash.ToLowerInvariant()
if ($beamHash -ne '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732') { throw 'Inno Setup download checksum mismatch.' }
$beamSignature = Get-AuthenticodeSignature -LiteralPath $beamDownload
if ($beamSignature.Status -ne 'Valid' -or $beamSignature.SignerCertificate.Subject -notmatch 'CN=Pyrsys B\.V\.') { throw 'Inno Setup publisher signature did not validate.' }
$beamSetup = [Diagnostics.ProcessStartInfo]::new($beamDownload)
$beamSetup.UseShellExecute = $false
$beamSetup.CreateNoWindow = $true
foreach ($beamArgument in @('/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS',('/DIR=' + $Destination))) { $beamSetup.ArgumentList.Add($beamArgument) }
$beamProcess = [Diagnostics.Process]::Start($beamSetup)
try {
    if (-not $beamProcess.WaitForExit(60000)) { $beamProcess.Kill($true); throw 'Inno Setup compiler installation timed out.' }
    if ($beamProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $beamCompiler)) { throw 'Inno Setup compiler installation failed.' }
} finally { $beamProcess.Dispose() }
return $beamCompiler
