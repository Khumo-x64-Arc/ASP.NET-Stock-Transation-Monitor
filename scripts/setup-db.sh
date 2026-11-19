#!/usr/bin/env bash
# Applies database/*.sql in order. Requires sqlcmd.
set -euo pipefail
SERVER="${SERVER:-localhost,1433}"; USER="${SQL_USER:-sa}"; PASS="${SA_PASSWORD:-YourStrong!Passw0rd}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
for f in "$ROOT"/database/*.sql; do
  echo "== $(basename "$f")"
  sqlcmd -S "$SERVER" -U "$USER" -P "$PASS" -C -b -i "$f"
done
echo "Database ready."
