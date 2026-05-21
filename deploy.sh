#!/usr/bin/env bash
# Deploy the hardware test suite to a Raspberry Pi.
#
#   ./deploy.sh                              # build + sync
#   ./deploy.sh run                          # build + sync + run "all" (visual y/n prompts)
#   ./deploy.sh run baseline                 # build + sync + run a single test
#   ./deploy.sh run --no-prompt all          # CI / smoke: assertions only
#   ./deploy.sh run image ./pic.png          # build + sync + ad-hoc image render
#
# Test names (see tests/Epd4in26.Hardware/VISUAL.md for the visual checklist per test):
#   clear   baseline   noop   partial-single   partial-bbox
#   ghost-budget   auto-promote   promote-large   sleep-wake
#
# Configure via env vars (defaults shown):
#   PI_HOST=raspberrypi.local
#   PI_USER=pi
#   PI_DEST=epd4in26               # path on the Pi (relative to $HOME)
#   RID=linux-arm64                # use linux-arm for 32-bit Raspberry Pi OS
#   EPD_TEST_INTERVAL=180          # forwarded to the Pi; lower at your own risk
#   NO_COLOR=1                     # suppress ANSI color output

set -euo pipefail

PI_HOST="${PI_HOST:-raspberrypi.local}"
PI_USER="${PI_USER:-pi}"
PI_DEST="${PI_DEST:-epd4in26}"
RID="${RID:-linux-arm64}"
PROJECT="tests/Epd4in26.Hardware"
ASSEMBLY="EpdTest"

if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
    BOLD='\033[1m'; DIM='\033[2m'; GREEN='\033[32m'; RED='\033[31m'; CYAN='\033[36m'; YELLOW='\033[33m'; RESET='\033[0m'
else
    BOLD=''; DIM=''; GREEN=''; RED=''; CYAN=''; YELLOW=''; RESET=''
fi

step()  { printf "${CYAN}==>${RESET} ${BOLD}%s${RESET}\n" "$*"; }
ok()    { printf "    ${GREEN}\xE2\x9C\x93${RESET} %s\n" "$*"; }
warn()  { printf "    ${YELLOW}\xE2\x9A\xA0${RESET} %s\n" "$*"; }
fail()  { printf "${RED}\xE2\x9C\x97${RESET} %s\n" "$*" >&2; exit 1; }

cd "$(dirname "$0")"

# Track TFM in Directory.Build.props so a future bump doesn't desync the publish path.
TFM=$(grep -oE '<TargetFramework>[^<]+' Directory.Build.props 2>/dev/null | head -1 | cut -d'>' -f2)
TFM="${TFM:-net10.0}"

step "preflight: SSH to ${PI_USER}@${PI_HOST}"
if ! ssh -o BatchMode=yes -o ConnectTimeout=5 "${PI_USER}@${PI_HOST}" 'true' 2>/dev/null; then
    fail "cannot reach ${PI_USER}@${PI_HOST} -- check network, hostname, and SSH key (try: ssh-copy-id ${PI_USER}@${PI_HOST})"
fi
ok "reachable"

step "publish ${PROJECT} (${RID}, self-contained)"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true --nologo -v quiet

PUBLISH_DIR="$PROJECT/bin/Release/$TFM/$RID/publish"
[ -d "$PUBLISH_DIR" ] || fail "publish output not found at $PUBLISH_DIR"
SIZE=$(du -sh "$PUBLISH_DIR" | cut -f1)
ok "$SIZE -> $PUBLISH_DIR"

step "sync to ${PI_USER}@${PI_HOST}:${PI_DEST}"
ssh "${PI_USER}@${PI_HOST}" "mkdir -p ${PI_DEST}"
rsync -az --delete "$PUBLISH_DIR/" "${PI_USER}@${PI_HOST}:${PI_DEST}/"
ssh "${PI_USER}@${PI_HOST}" "chmod +x ${PI_DEST}/${ASSEMBLY}"
ok "synced"

if [ "${1:-}" = "run" ]; then
    shift
    TEST_ARGS="${*:-all}"
    step "running on Pi: ${ASSEMBLY} ${TEST_ARGS}"
    echo
    # -t allocates a TTY so Ctrl-C interrupts the remote process cleanly.
    ssh -t "${PI_USER}@${PI_HOST}" \
        "cd ${PI_DEST} && EPD_TEST_INTERVAL=${EPD_TEST_INTERVAL:-180} ./${ASSEMBLY} ${TEST_ARGS}"
else
    step "deployed"
    printf "    run with: ${DIM}ssh %s@%s 'cd %s && ./%s all'${RESET}\n" \
        "$PI_USER" "$PI_HOST" "$PI_DEST" "$ASSEMBLY"
fi
