#!/usr/bin/env bash
# Single-command seed. Pass overrides as --Section:Key=value, e.g.
#   ./seed.sh --Confluence:BaseUrl=http://localhost:8090 --BackdateViaPostgres=true
set -euo pipefail
cd "$(dirname "$0")"
exec dotnet run -c Release -- "$@"
