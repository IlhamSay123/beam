param(
    [string]$OutputDirectory = "",
    [string]$Version = "0.1.0-alpha.1"
)
$ErrorActionPreference = "Stop"
if ($Version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.]+)?$') { throw "Invalid release version." }
$root = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root "artifacts/release" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$package = Join-Path $OutputDirectory "Beam-$Version-win-x64"
if (Test-Path -LiteralPath $package) { throw "Package folder already exists: $package. Choose a new output folder." }
New-Item -ItemType Directory -Path $package -Force | Out-Null
dotnet publish (Join-Path $root "src/Beam.App/Beam.App.csproj") --configuration Release --runtime win-x64 --self-contained true --output $package "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
foreach ($required in @("Beam.App.exe", "wwwroot/index.html", "wwwroot/app.js", "wwwroot/styles.css", "wwwroot/vault.js", "wwwroot/motion.js", "wwwroot/lucide.min.js", "wwwroot/lucide.LICENSE")) {
    if (!(Test-Path -LiteralPath (Join-Path $package $required))) { throw "Missing published file: $required" }
}
Copy-Item -LiteralPath (Join-Path $root "packaging/START-HERE.txt") -Destination $package
$zip = "$package.zip"
if (Test-Path -LiteralPath $zip) { throw "Archive already exists: $zip" }
Compress-Archive -LiteralPath $package -DestinationPath $zip
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
Write-Output "Package: $zip"
Write-Output "SHA256:  $hash"
