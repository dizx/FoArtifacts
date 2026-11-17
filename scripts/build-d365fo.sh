#!/usr/bin/env bash
# Clones d365fo-cli at the commit fo-batchgen is tested against, builds it, and prints the d365fo path.
set -euo pipefail
COMMIT=640406b357b5fc9bc33ba499c8ec325c5b8a5c5f
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIR="$ROOT/.d365fo-cli"
if [ ! -d "$DIR/.git" ]; then
  git clone --quiet https://github.com/dynamics365ninja/d365fo-cli "$DIR"
fi
git -C "$DIR" fetch --quiet --depth 1 origin "$COMMIT"
git -C "$DIR" checkout --quiet "$COMMIT"
dotnet build "$DIR/src/D365FO.Cli" -c Release --nologo -v quiet >&2
echo "$DIR/src/D365FO.Cli/bin/Release/net10.0/d365fo"
