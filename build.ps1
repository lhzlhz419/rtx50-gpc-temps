# SPDX-License-Identifier: GPL-3.0-only
# Additional permission under GPLv3 section 7: see NVAPI-EXCEPTION.txt.
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/Rtx50Temps.csproj'
$dist = Join-Path $PSScriptRoot 'dist/win-x64'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$publish = Join-Path $artifacts 'single-file'
dotnet publish $project -c Release -r win-x64 --no-self-contained --artifacts-path $artifacts -o $publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$files = @(Get-ChildItem -LiteralPath $publish -File)
if ($files.Count -ne 1 -or $files[0].Name -ne 'Rtx50Temps.exe') { throw "Unexpected publish output in $publish" }
Copy-Item -LiteralPath $files[0].FullName -Destination $dist -Force -PassThru
