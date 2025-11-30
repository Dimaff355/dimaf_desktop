#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNTIME="${RUNTIME:-win-x64}"
CONFIGURATION="${CONFIGURATION:-Release}"
SELF_CONTAINED="${SELF_CONTAINED:-false}"
SKIP_SIGNALING_SERVER="${SKIP_SIGNALING_SERVER:-false}"
ARTIFACT_ROOT="$ROOT/artifacts/$RUNTIME"

mkdir -p "$ARTIFACT_ROOT"

echo "Publishing projects for runtime '$RUNTIME' (Configuration=$CONFIGURATION, SelfContained=$SELF_CONTAINED)"

dotnet restore "$ROOT"

publish() {
  local project="$1"; shift
  local name="$1"; shift
  local output="$ARTIFACT_ROOT/$name"
  rm -rf "$output"

  dotnet publish "$project" \
    -c "$CONFIGURATION" \
    -r "$RUNTIME" \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PublishReadyToRun=true \
    -p:SelfContained="$SELF_CONTAINED" \
    -o "$output"
}

publish "$ROOT/src/Service/P2PRD.Service.csproj" "Service"
publish "$ROOT/src/OperatorConsole/OperatorConsole.csproj" "OperatorConsole"
publish "$ROOT/src/Configurator/Configurator.csproj" "Configurator"

if [[ "$SKIP_SIGNALING_SERVER" != "true" ]]; then
  publish "$ROOT/src/SignalingServer/SignalingServer.csproj" "SignalingServer"
fi

echo "Done. Artifacts under $ARTIFACT_ROOT"
