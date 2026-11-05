#!/usr/bin/env bash
# Final Phase 1: NuGet restores the .NET reader; no Java/JAR download is needed.
# One-time local set-up: a random local SQL password (server/.env) and
# src/HousekeepingParser/local.settings.json. Both generated files are git-ignored.
set -euo pipefail
SERVER="$(cd "$(dirname "$0")/.." && pwd)"

if [ ! -f "$SERVER/.env" ]; then
  echo "HSK_SQL_SA_PASSWORD=Hsk_$(head -c 18 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 20)_1" > "$SERVER/.env"
  echo "Created server/.env"
fi
# shellcheck disable=SC1091
source "$SERVER/.env"

SETTINGS="$SERVER/src/HousekeepingParser/local.settings.json"
if [ ! -f "$SETTINGS" ]; then
  DEMO="${HSK_DEMO_PASSWORD:-demo}"
  python3 - "$SERVER" "$HSK_SQL_SA_PASSWORD" "$DEMO" <<'PY'
import json, sys
server, sa, demo = sys.argv[1:]
path = f"{server}/src/HousekeepingParser/local.settings.example.json"
s = json.load(open(path))
v = s["Values"]
v["Database__ConnectionString"] = v["Database__ConnectionString"].replace("CHANGE_ME_LOCAL_ONLY", sa)
v["Auth__SeedUsers__0__Password"] = demo
v["Auth__SeedUsers__1__Password"] = demo
json.dump(s, open(f"{server}/src/HousekeepingParser/local.settings.json", "w"), indent=2)
PY
  echo "Created src/HousekeepingParser/local.settings.json (sign-in: director / operations, password: ${DEMO})"
fi
echo "Next: cd server && docker compose up -d && cd src/HousekeepingParser && func start"
