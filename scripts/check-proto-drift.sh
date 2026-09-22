#!/usr/bin/env bash
set -euo pipefail

# Fails if the vendored .proto files under Protos/ differ from their upstream source. Run in CI on
# every PR that touches Protos/; on failure, copy the updated files from a local checkout, commit
# them, and update Protos/VENDORED_COMMIT with the new source commits.
#
# Both refs default to the branch rather than the pinned commit on purpose: the point of this check
# is to notice when upstream moves, not to confirm that a pin still equals itself.

LITD_REPO="${LITD_REPO:-https://github.com/lightninglabs/lightning-terminal.git}"
LITD_REF="${LITD_REF:-master}"
LND_REPO="${LND_REPO:-https://github.com/lightningnetwork/lnd.git}"
LND_REF="${LND_REF:-master}"

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT_DIR/scripts/plugin-env.sh"
PROTOS_DIR="$ROOT_DIR/$PROJECT/Protos"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

drifted=0

check() {
  local checkout="$1" upstream_rel="$2" vendored_rel="$3"
  if ! diff -q "$checkout/$upstream_rel" "$PROTOS_DIR/$vendored_rel" > /dev/null 2>&1; then
    echo ""
    echo "DRIFT: $PROTOS_DIR/$vendored_rel is stale relative to $upstream_rel"
    echo "  cp \$CHECKOUT/$upstream_rel $PROTOS_DIR/$vendored_rel"
    drifted=1
  fi
}

echo "Fetching $LITD_REPO@$LITD_REF..."
git clone --depth 1 --branch "$LITD_REF" "$LITD_REPO" "$WORK_DIR/litd" --quiet
check "$WORK_DIR/litd" "litrpc/lit-sessions.proto" "litrpc/lit-sessions.proto"
check "$WORK_DIR/litd" "litrpc/lit-status.proto" "litrpc/lit-status.proto"

echo "Fetching $LND_REPO@$LND_REF..."
git clone --depth 1 --branch "$LND_REF" "$LND_REPO" "$WORK_DIR/lnd" --quiet
check "$WORK_DIR/lnd" "lnrpc/stateservice.proto" "lnrpc/stateservice.proto"

if [ "$drifted" -ne 0 ]; then
  echo ""
  echo "After copying, record the new source commits in $PROTOS_DIR/VENDORED_COMMIT:"
  echo "  git -C \$CHECKOUT rev-parse HEAD"
  exit 1
fi

echo "Vendored protos match their upstreams."
