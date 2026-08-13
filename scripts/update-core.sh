#!/usr/bin/env bash
# Pull (or push) the Radio_Web_Control_Core git subtree at core/.
#
# A plain clone already contains core/ and builds with no extra steps. Run this
# when you want the latest shared code from the core repo — not as part of
# every build. `git subtree pull --squash` creates a commit in this repo.
#
# Usage (from anywhere; the script cds to the repo root):
#   ./scripts/update-core.sh          # pull --squash from core main
#   ./scripts/update-core.sh pull
#   ./scripts/update-core.sh push     # push local core/ changes back up
#
# Keep PREFIX / URL / BRANCH in sync with scripts/update-core.ps1.

set -euo pipefail

PREFIX="core"
REMOTE_NAME="radio-core"
URL="https://github.com/mm5agm/Radio_Web_Control_Core.git"
BRANCH="main"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

action="${1:-pull}"
case "$action" in
  pull|push) ;;
  -h|--help|help)
    sed -n '2,14p' "$0"
    exit 0
    ;;
  *)
    echo "error: unknown action '$action' (use pull or push)" >&2
    exit 1
    ;;
esac

if ! command -v git >/dev/null 2>&1; then
  echo "error: git is not on PATH" >&2
  exit 1
fi
if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo "error: not a git repository" >&2
  exit 1
fi
if [[ ! -x "$(git --exec-path)/git-subtree" ]]; then
  echo "error: git subtree is not available. Install a full Git distribution (not a minimal git)." >&2
  exit 1
fi
if [[ ! -d "$PREFIX" ]]; then
  echo "error: $PREFIX/ is missing. First-time add (once, from a clean tree):" >&2
  echo "  git subtree add --prefix=$PREFIX $URL $BRANCH --squash" >&2
  exit 1
fi

if [[ -n "$(git status --porcelain)" ]]; then
  echo "error: working tree is not clean. Commit or stash before a subtree $action." >&2
  git status --short
  exit 1
fi

if ! git remote get-url "$REMOTE_NAME" >/dev/null 2>&1; then
  echo "Adding git remote $REMOTE_NAME -> $URL"
  git remote add "$REMOTE_NAME" "$URL"
fi

if [[ "$action" = pull ]]; then
  echo "git subtree pull --prefix=$PREFIX $REMOTE_NAME $BRANCH --squash"
  git fetch "$REMOTE_NAME" "$BRANCH"
  git subtree pull --prefix="$PREFIX" "$REMOTE_NAME" "$BRANCH" --squash
  echo "core/ is up to date. Review the squash commit git just created, then push this branch when ready."
else
  echo "git subtree push --prefix=$PREFIX $REMOTE_NAME $BRANCH"
  git subtree push --prefix="$PREFIX" "$REMOTE_NAME" "$BRANCH"
  echo "Pushed core/ to $URL ($BRANCH)."
fi
