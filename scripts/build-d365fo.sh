#!/usr/bin/env bash
# Clones d365fo-cli at the commit fo-artifacts is tested against, builds it, and prints the d365fo path.
set -euo pipefail
COMMIT=b60ffdd65fd182d378bce396fbc9f14849f8ce62
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIR="$ROOT/.d365fo-cli"
# A fork of dynamics365ninja/d365fo-cli carrying fixes fo-artifacts needs (see README).
REPO=https://github.com/dizx/d365fo-cli
if [ ! -d "$DIR/.git" ]; then
  git clone --quiet "$REPO" "$DIR"
fi
git -C "$DIR" remote set-url origin "$REPO"
git -C "$DIR" fetch --quiet --depth 1 origin "$COMMIT"
git -C "$DIR" checkout --quiet "$COMMIT"
dotnet build "$DIR/src/D365FO.Cli" -c Release --nologo -v quiet >&2
echo "$DIR/src/D365FO.Cli/bin/Release/net10.0/d365fo"
