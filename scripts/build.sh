#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
game_dir="${1:-${DV_GAME_PATH:-/home/deck/.local/share/Steam/steamapps/common/Derail Valley}}"
dotnet_bin="${DOTNET:-$(command -v dotnet || true)}"
if [[ -z "$dotnet_bin" && -x /tmp/dv-dotnet/dotnet ]]; then dotnet_bin=/tmp/dv-dotnet/dotnet; fi
if [[ -z "$dotnet_bin" ]]; then echo 'Install a .NET 8 SDK or set DOTNET to its executable.' >&2; exit 1; fi
if [[ ! -f "$game_dir/DerailValley_Data/Managed/UnityModManager/UnityModManager.dll" ]]; then
    echo "Unity Mod Manager was not found in: $game_dir" >&2; exit 1
fi
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-1}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$project_dir/.tools/dotnet-home}"
"$dotnet_bin" build "$project_dir/DVTrainSpeedLimit.csproj" -c Release --nologo -p:GamePath="$game_dir"
"$dotnet_bin" run --project "$project_dir/tests/SpeedLimit.Tests.csproj" -c Release
package_dir="$project_dir/dist/DVTrainSpeedLimit"
mkdir -p -- "$package_dir"
cp -- "$project_dir/bin/Release/net472/DVTrainSpeedLimit.dll" "$project_dir/Info.json" "$project_dir/README.md" "$package_dir/"
if command -v zip >/dev/null; then
    (cd "$project_dir/dist" && zip -q -r -FS DVTrainSpeedLimit-0.3.2.zip DVTrainSpeedLimit)
fi
echo "Built and tested: $package_dir"
