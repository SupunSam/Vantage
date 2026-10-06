#!/bin/bash
# Cloud sessions only: install the .NET 10 SDK and SQL Server for Linux so `dotnet test` runs before pushing.
# Idempotent. Local (Docker) use is untouched.
set -uo pipefail

[ "${CLAUDE_CODE_REMOTE:-}" = "true" ] || exit 0

SA_PASSWORD="Ci_Only#Passw0rd"   # throwaway local SQL Server, not a real secret
CONN="Server=localhost,1433;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=True"
REPO="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "$0")/../.." && pwd)}"
SUDO=""; [ "$(id -u)" -ne 0 ] && SUDO="sudo"
log() { echo "[session-start] $*" >&2; }

# 1. .NET 10 SDK (Ubuntu's own feed; builds.dotnet.microsoft.com is not reachable from cloud sessions)
if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  log "installing .NET 10 SDK"
  $SUDO apt-get update -qq && $SUDO apt-get install -y -qq dotnet-sdk-10.0 >&2 || log "dotnet install failed"
fi

# 2. SQL Server 2022 from the same MCR image CI uses. Cloud sessions can reach mcr.microsoft.com, but not the
#    apt package's download host, so we run the image with a local Docker daemon.
if ! (exec 3<>/dev/tcp/127.0.0.1/1433) 2>/dev/null; then
  if ! docker info >/dev/null 2>&1; then
    log "starting Docker daemon"
    nohup $SUDO dockerd >/tmp/dockerd.log 2>&1 &
    for _ in $(seq 1 30); do docker info >/dev/null 2>&1 && break; sleep 1; done
  fi
  if docker ps -a --format '{{.Names}}' | grep -qx vantage-sql; then
    log "starting SQL Server container"
    docker start vantage-sql >/dev/null
  else
    log "pulling and starting SQL Server 2022"
    docker run -d --name vantage-sql -p 1433:1433 -e ACCEPT_EULA=Y -e MSSQL_PID=Developer \
      -e "MSSQL_SA_PASSWORD=${SA_PASSWORD}" mcr.microsoft.com/mssql/server:2022-latest >/dev/null || log "SQL Server container failed"
  fi
  for _ in $(seq 1 60); do (exec 3<>/dev/tcp/127.0.0.1/1433) 2>/dev/null && break; sleep 2; done
fi

# 4. Restore packages so build and test start fast
(cd "$REPO/backend" && dotnet restore Vantage.slnx -v q >&2) || log "dotnet restore failed"
(cd "$REPO/frontend" && npm ci --no-audit --no-fund --silent >&2) || log "npm ci failed"

# 5. Tell the session where the test database is
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  echo "export VANTAGE_TEST_SQL='${CONN}'" >> "$CLAUDE_ENV_FILE"
  echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1" >> "$CLAUDE_ENV_FILE"
fi
log "ready"
