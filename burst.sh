#!/usr/bin/env bash
# Fires the burst tool (tools/Burst, lld §12) at a running service.
#
#   ./burst.sh <BASE_URL> [options]          e.g. ./burst.sh https://seatres-api.onrender.com
#   ./burst.sh --help                        every option and its default
#
# Uses the .NET 8 SDK when it is on PATH, otherwise builds and runs tools/Burst/Dockerfile.
#   BURST_USE_DOCKER=1        force the Docker path even when dotnet is present
#   BURST_DOCKER_NETWORK=...  network for the container (default: host, so http://localhost:8080 works on Linux;
#                             use the compose network, e.g. bookingsystem_default, to reach http://api:8080)
# The exit code is the tool's: 0 only if every scenario passes.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

has_sdk() {
  command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -Eq '^([89]|[1-9][0-9])\.'
}

if [[ "${BURST_USE_DOCKER:-0}" != "1" ]] && has_sdk; then
  exec dotnet run --project "$root/tools/Burst" -c Release -- "$@"
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "burst.sh: needs the .NET 8 SDK or Docker on PATH." >&2
  exit 2
fi

image="seatres-burst"
echo "burst.sh: no .NET 8 SDK, using Docker (building $image)..." >&2
docker build -q -t "$image" -f "$root/tools/Burst/Dockerfile" "$root" >/dev/null

# The current directory is mounted at /work, so `--json report.json` is written here, owned by the caller.
workdir="$(pwd -W 2>/dev/null || pwd)"   # Git Bash on Windows: hand Docker a Windows path
MSYS_NO_PATHCONV=1 exec docker run --rm \
  --network "${BURST_DOCKER_NETWORK:-host}" \
  --user "$(id -u):$(id -g)" -e HOME=/tmp \
  -v "$workdir:/work" \
  "$image" "$@"
