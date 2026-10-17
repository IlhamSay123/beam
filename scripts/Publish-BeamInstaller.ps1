param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [string]$OutputDirectory = '',
    [string]$Version = '0.1.0-alpha.3.preview.1',
    [string]$IsccPath = '',
    [string]$TestIdentity = '',
    [string]$TestRoot = ''
)
$ErrorActionPreference = 'Stop'
$beamRepo = Split-Path $PSScriptRoot -Parent
if ($Version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.]+)?$') { throw 'Invalid installer version.' }
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
foreach ($beamRequired in @('Beam.App.exe','Beam.App.dll','coreclr.dll','hostfxr.dll','wwwroot/index.html','wwwroot/app.js','wwwroot/vault.js','wwwroot/motion.js','wwwroot/styles.css','wwwroot/lucide.min.js','wwwroot/lucide.LICENSE','START-HERE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $beamRequired) -PathType Leaf)) { throw "Publish a complete self-contained Beam folder first. Missing: $beamRequired" }
}
if (Get-ChildItem -LiteralPath $PublishDirectory -Recurse -Force | Where-Object Name -In @('token.txt','history.json','Inbox','Outbox')) { throw 'Refusing an installer payload containing personal data.' }
$beamTestBuild = -not [string]::IsNullOrEmpty($TestIdentity)
if ($beamTestBuild) {
    $beamGuid = [Guid]::Empty
    if (-not [Guid]::TryParse($TestIdentity, [ref]$beamGuid) -or -not [IO.Path]::IsPathFullyQualified($TestRoot)) { throw 'Installer tests require a unique GUID and absolute test root.' }
    $TestIdentity = $beamGuid.ToString('N')
}
else {
    $beamProductVersion = [string](Get-Item -LiteralPath (Join-Path $PublishDirectory 'Beam.App.exe')).VersionInfo.ProductVersion
    if ($beamProductVersion -ne $Version -and -not $beamProductVersion.StartsWith($Version + '+', [StringComparison]::Ordinal)) { throw 'Installer version must match the published Beam executable.' }
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $beamRepo 'artifacts/release' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$beamPayloadRoot = $PublishDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if ($OutputDirectory.Equals($beamPayloadRoot, [StringComparison]::OrdinalIgnoreCase) -or $OutputDirectory.StartsWith($beamPayloadRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output directory must be outside the published payload.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$beamOutput = Join-Path $OutputDirectory ("Beam-$Version-win-x64-Setup.exe")
if (Test-Path -LiteralPath $beamOutput) { throw 'Installer already exists. Choose a fresh output directory.' }
if (-not $IsccPath) { $IsccPath = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1') }
if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw 'Inno Setup compiler was not found.' }
$beamFileVersion = ($Version -split '-', 2)[0] + '.0'
$beamArguments = @('/Qp',("/DBeamVersion=$Version"),("/DBeamFileVersion=$beamFileVersion"),("/DPublishDirectory=$PublishDirectory"),("/O$OutputDirectory"))
if ($beamTestBuild) { $beamArguments += @(("/DBeamTestId=$TestIdentity"),("/DBeamTestRoot=$TestRoot")) }
$beamArguments += Join-Path $beamRepo 'packaging/Beam.iss'
$beamCompileLog = & $IsccPath @beamArguments 2>&1
$beamCompilerResult = $LASTEXITCODE
$beamCompileLog | Set-Content -LiteralPath (Join-Path $OutputDirectory 'installer-compile.log')
if ($beamCompilerResult -ne 0 -or -not (Test-Path -LiteralPath $beamOutput)) {
    $beamCompileLog | Select-Object -Last 15 | Write-Host
    throw 'Inno Setup compilation failed.'
}
$beamHash = (Get-FileHash -LiteralPath $beamOutput -Algorithm SHA256).Hash.ToLowerInvariant()
"$beamHash  $([IO.Path]::GetFileName($beamOutput))" | Set-Content -LiteralPath "$beamOutput.sha256" -Encoding ascii
Write-Host "Built installer: $beamOutput"
Write-Output $beamOutput
