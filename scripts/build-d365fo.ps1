# Clones d365fo-cli at the commit fo-batchgen is tested against, builds it, and prints the d365fo path.
$ErrorActionPreference = 'Stop'
$Commit = '640406b357b5fc9bc33ba499c8ec325c5b8a5c5f'
$Root = Split-Path -Parent $PSScriptRoot
$Dir = Join-Path $Root '.d365fo-cli'
if (-not (Test-Path (Join-Path $Dir '.git'))) {
    git clone --quiet https://github.com/dynamics365ninja/d365fo-cli $Dir
}
git -C $Dir fetch --quiet --depth 1 origin $Commit
git -C $Dir checkout --quiet $Commit
dotnet build (Join-Path $Dir 'src/D365FO.Cli') -c Release --nologo -v quiet | Out-Host
Join-Path $Dir 'src/D365FO.Cli/bin/Release/net10.0/d365fo.exe'
