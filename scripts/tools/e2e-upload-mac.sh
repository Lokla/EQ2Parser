#!/usr/bin/env bash
# macOS counterpart of e2e-upload.ps1: upload real fights from this working
# tree to a LOCAL copy of the site and read back what it stored.
#
#   scripts/tools/e2e-upload-mac.sh                      # site checkout beside this repo
#   scripts/tools/e2e-upload-mac.sh --lexicon <path>     # elsewhere
#   scripts/tools/e2e-upload-mac.sh --log <eq2log_X.txt> # another log (default: the golden one)
#   scripts/tools/e2e-upload-mac.sh --keep               # leave the scratch folder for a look
#
# Everything stays on this machine:
#   1. a throwaway PostgreSQL cluster (initdb) in the scratch folder, TCP on
#      127.0.0.1 only, no unix socket; the site's helper refuses anything
#      that is not local anyway;
#   2. the site from its checkout against that database (e2e_upload_helper.py,
#      shared with the Windows script), with a minted throwaway token;
#   3. eq2parser-cli on a scratch data folder with EQ2PARSER_LOOPBACK_ONLY=1,
#      so the app cannot reach any host but this one whatever its settings
#      say; the token sits in that data folder's own Keychain entry;
#   4. two copies of the log: logs/Wuoshi (on the allow-list) and
#      logs/Kaladim (refused with X-Lexicon-Reject);
#   5. readback and checks; then stop the site, delete the Keychain entry,
#      stop and delete the cluster. Always.
#
# Needs: uv, PostgreSQL 17 binaries (brew install uv postgresql@17), the
# EQ2Lexicon checkout, the .NET SDK. One PASS/FAIL line per check, then
# "e2e-upload: OK" (exit 0), "FAIL" (1) or "INCOMPLETE" (2).
#
# ASCII only, like the PowerShell scripts.
set -u

repo="$(cd "$(dirname "$0")/../.." && pwd)"
helper="$repo/scripts/tools/e2e_upload_helper.py"
lexicon="$(dirname "$repo")/EQ2Lexicon"
log="$repo/tests/logs/eq2log_Menludiir.txt"
port=8765
pgport=54329
keep=0
while [ $# -gt 0 ]; do
    case "$1" in
        --lexicon) lexicon="$2"; shift 2 ;;
        --log) log="$2"; shift 2 ;;
        --port) port="$2"; shift 2 ;;
        --keep) keep=1; shift ;;
        *) echo "unknown option $1"; exit 2 ;;
    esac
done

failed=0
pass() { echo "PASS $1"; }
fail() { failed=1; echo "FAIL $1"; }
incomplete() { echo "SKIP all ($1)"; echo "e2e-upload: INCOMPLETE"; exit 2; }

pgbin=""
for d in /opt/homebrew/opt/postgresql@17/bin /usr/local/opt/postgresql@17/bin; do
    [ -x "$d/initdb" ] && pgbin="$d" && break
done
[ -n "$pgbin" ] || incomplete "no PostgreSQL 17 binaries (brew install postgresql@17)"
command -v uv >/dev/null || incomplete "uv is not on PATH (brew install uv)"
[ -f "$lexicon/main.py" ] || incomplete "no EQ2Lexicon checkout at $lexicon; pass --lexicon"
[ -f "$log" ] || incomplete "no such log: $log"
for p in "$port" "$pgport"; do
    if lsof -nP -iTCP:"$p" -sTCP:LISTEN >/dev/null 2>&1; then incomplete "port $p is already in use"; fi
done
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$dotnet" ] || dotnet="$(command -v dotnet || true)"
[ -n "$dotnet" ] || incomplete "no dotnet"
export DOTNET_ROOT="$(dirname "$dotnet")" DOTNET_CLI_TELEMETRY_OPTOUT=1

work="$repo/artifacts/e2e-upload-mac"
rm -rf "$work"
mkdir -p "$work"
data="$work/data"
cli="$work/cli/eq2parser-cli"

export E2E_LEXICON="$lexicon"
# The site refuses to start without a session secret (normally in its .env);
# a throwaway one per run.
export SESSION_SECRET="$(python3 -I -c 'import secrets; print(secrets.token_hex(32))')"
unset VIRTUAL_ENV
export TEST_DATABASE_URL="postgresql://postgres@127.0.0.1:$pgport/eq2lexicon_test"
helper_run() { uv run --frozen --project "$lexicon" python "$helper" "$@"; }

cleanup() {
    if [ -x "$cli" ]; then
        EQ2PARSER_LOOPBACK_ONLY=1 "$cli" --data-dir "$data" --forget-token >/dev/null 2>&1
    fi
    pkill -f "uvicorn backend.server.app:app --port $port" >/dev/null 2>&1
    pkill -f "e2e_upload_helper.py serve $port" >/dev/null 2>&1
    if [ -f "$work/pg/PG_VERSION" ]; then
        helper_run db-drop >/dev/null 2>&1
        "$pgbin/pg_ctl" -D "$work/pg" -m fast stop >/dev/null 2>&1
    fi
    if [ "$keep" -eq 0 ]; then rm -rf "$work"; else echo "NOTE kept $work"; fi
}
trap cleanup EXIT

# --- 0. the app -------------------------------------------------------------
if ! "$dotnet" build "$repo/src/EQ2Parser.Mac.Cli" -c Release -o "$work/cli" >"$work/build.log" 2>&1; then
    fail "build (see $work/build.log)"; keep=1; echo "e2e-upload: FAIL"; exit 1
fi

# --- 1. database --------------------------------------------------------------
"$pgbin/initdb" -D "$work/pg" -U postgres --auth=trust -E UTF8 --locale=C >"$work/initdb.log" 2>&1 \
    || incomplete "initdb failed (see $work/initdb.log)"
"$pgbin/pg_ctl" -D "$work/pg" -l "$work/pg.log" -w \
    -o "-p $pgport -c listen_addresses=127.0.0.1 -c unix_socket_directories=''" start >/dev/null \
    || incomplete "the scratch PostgreSQL did not start (see $work/pg.log)"
helper_run db-create >"$work/helper.log" 2>&1 || incomplete "could not create the scratch database"
if out="$(helper_run migrate 2>&1)"; then
    pass "migrate ($(echo "$out" | grep '^migrations applied' | tail -1))"
else
    fail "migrate"; echo "$out" | tail -6 | sed 's/^/  /'; echo "e2e-upload: FAIL"; exit 1
fi

# --- 2. site ----------------------------------------------------------------
helper_run serve "$port" >"$work/server.log" 2>&1 &
# Every probe has its own timeout: with --reload the parent keeps the port
# open even when the app inside it has crashed, and a probe without one
# waits forever. A crash in the log ends the wait at once.
up=0
for _ in $(seq 1 30); do
    sleep 3
    if curl -fsS -m 3 -o /dev/null "http://127.0.0.1:$port/api/zones/raid-bosses"; then up=1; break; fi
    if grep -qE '^(Traceback|[A-Za-z]+Error:)' "$work/server.log"; then break; fi
done
if [ "$up" -eq 1 ]; then pass "site start"; else
    fail "site start (it did not answer on 127.0.0.1:$port)"; grep -v '://' "$work/server.log" | tail -8 | sed 's/^/  /'
    echo "e2e-upload: FAIL"; exit 1
fi

# --- 3. token -----------------------------------------------------------------
if ! helper_run token "$work/token.txt" >>"$work/helper.log" 2>&1 || [ ! -s "$work/token.txt" ]; then
    fail "token (could not mint one in the scratch database)"; echo "e2e-upload: FAIL"; exit 1
fi

# --- 4. the app -----------------------------------------------------------------
mkdir -p "$data" "$work/game/logs/Wuoshi" "$work/game/logs/Kaladim"
accepted="$work/game/logs/Wuoshi/$(basename "$log")"
refused="$work/game/logs/Kaladim/eq2log_Tester.txt"
cp "$log" "$accepted"
cp "$log" "$refused"
cat >"$data/settings.json" <<EOF
{
  "FirstRunShown": true,
  "LanguageCode": "en",
  "UploadEnabled": true,
  "UploadRaidOnly": false,
  "LexiconBaseUrl": "http://127.0.0.1:$port",
  "LexiconApiTokenProtected": "macos-keychain:v1",
  "LexiconTriggersEnabled": false,
  "Sources": [
    { "Path": "$refused", "ParseFromStart": false, "LastPosition": 0 },
    { "Path": "$accepted", "ParseFromStart": false, "LastPosition": 0 }
  ]
}
EOF
export EQ2PARSER_LOOPBACK_ONLY=1
"$cli" --data-dir "$data" --store-token "$work/token.txt" >/dev/null || { fail "storing the token in the Keychain"; echo "e2e-upload: FAIL"; exit 1; }
rm -f "$work/token.txt"
"$cli" --data-dir "$data" --from-settings --seconds 25 >"$work/app.log" 2>&1
grep -E '^  upload: ' "$work/app.log" | sed 's/^  upload: /NOTE status: /' | sort | uniq -c | sort -rn | head -8
if grep -q 'uploads ON' "$work/app.log"; then pass "uploads configured (token read back from the Keychain)"; else fail "uploads were not on in the app"; fi
if grep -q 'uploads paused: False' "$work/app.log"; then pass "queue not paused by the refused server"; else fail "the upload queue paused"; fi
if grep -q 'refused a request' "$data/session.log" 2>/dev/null; then fail "the app tried to reach a host other than this machine"; else pass "no request left this machine"; fi

# --- 5. what the site stored -----------------------------------------------------
json="$(helper_run readback 2>/dev/null | grep '^{' | tail -1)"
[ -n "$json" ] || { fail "readback"; echo "e2e-upload: FAIL"; exit 1; }
echo "$json" | python3 -I -c '
import json, sys
d = json.load(sys.stdin)
enc, rows = d["encounters"], d["attack_rows"]
ok = lambda m: print("PASS " + m)
bad = lambda m: (print("FAIL " + m), sys.exit(1))
failed = False
def check(cond, good, wrong):
    global failed
    if cond: print("PASS " + good)
    else:
        print("FAIL " + wrong); failed = True
mine = [e for e in enc if e["world"] == "Wuoshi"]
other = [e for e in enc if e["world"] != "Wuoshi"]
check(len(mine) >= 1, "fights stored for the accepted server: %d (%s)" % (len(mine), ", ".join(e["title"] for e in mine)),
      "no fight was stored for the accepted server")
check(not other, "nothing stored for the refused server", "fights stored under another world: %s" % sorted({e["world"] for e in other}))
check(d["quarantined"] == 0, "no upload quarantined by the plausibility gate", "%d upload(s) quarantined" % d["quarantined"])
types = sorted({int(r["swing_type"]) for r in rows})
print("NOTE swing types stored: %s across %d attack rows" % (types, len(rows)))
check(30 not in types, "no status-effect rows uploaded", "status-effect rows (swing type 30) were uploaded")
unl = [r for r in rows if int(r["swing_type"]) in (3, 13, 16) and not r["resist"]]
check(not unl, "heal, power and threat rows carry their resist label", "%d heal/power/threat rows without a resist label" % len(unl))
warnings = sorted({w for e in mine for w in (e["client_warnings"] or [])})
print("NOTE client_warnings on the stored fights: %s" % (warnings or "none"))
sys.exit(1 if failed else 0)
' || failed=1

if [ "$failed" -eq 0 ]; then echo "e2e-upload: OK"; exit 0; fi
keep=1
echo "e2e-upload: FAIL"
exit 1
