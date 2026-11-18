# Clones d365fo-cli at the commit fo-artifacts is tested against, builds it, and prints the d365fo path.
$ErrorActionPreference = 'Stop'
$Commit = 'c1761561269709657484e596a5c3d378ba9a5b18'
$Root = Split-Path -Parent $PSScriptRoot
$Dir = Join-Path $Root '.d365fo-cli'
# A fork of dynamics365ninja/d365fo-cli carrying fixes fo-artifacts needs (see README).
$Repo = 'https://github.com/dizx/d365fo-cli'
if (-not (Test-Path (Join-Path $Dir '.git'))) {
    git clone --quiet $Repo $Dir
}
git -C $Dir remote set-url origin $Repo
git -C $Dir fetch --quiet --depth 1 origin $Commit
git -C $Dir checkout --quiet $Commit
dotnet build (Join-Path $Dir 'src/D365FO.Cli') -c Release --nologo -v quiet | Out-Host
Join-Path $Dir 'src/D365FO.Cli/bin/Release/net10.0/d365fo.exe'
