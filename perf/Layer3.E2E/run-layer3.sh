#!/usr/bin/env bash
# Layer 3 (e2e) entry point: builds baseline and candidate refs into real, jellyfin-web-included
# containers (overlaying this repo's build onto docker.io/jellyfin/jellyfin:preview — see
# Dockerfile), seeds each with the same tiny synthetic fixtures via the real API, measures real
# page loads with Playwright under a pinned CPU throttle, and writes the standardized
# perf-result.json with candidate/baseline ratios.
#
# Usage: run-layer3.sh --baseline <git-ref> --candidate <git-ref> [--out <path>] [--keep-images]
#
# Manual/local by design (see perf/README.md) — not wired into perf/run-perf.sh's run/diff
# subcommands, which only drive Layer 1. Requires: podman, ffmpeg, node (with playwright
# installed — run `npm install` in this directory first).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
RESULTS_DIR="$SCRIPT_DIR/../results"
RUNTIME="${PERF_CONTAINER_RUNTIME:-podman}"

baseline_ref=""
candidate_ref=""
out_path=""
keep_images="false"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --baseline) baseline_ref="$2"; shift 2 ;;
    --candidate) candidate_ref="$2"; shift 2 ;;
    --out) out_path="$2"; shift 2 ;;
    --keep-images) keep_images="true"; shift ;;
    *) echo "Unknown arg: $1" >&2; exit 1 ;;
  esac
done

if [[ -z "$baseline_ref" || -z "$candidate_ref" ]]; then
  echo "Usage: run-layer3.sh --baseline <git-ref> --candidate <git-ref> [--out <path>] [--keep-images]" >&2
  exit 1
fi
[[ -z "$out_path" ]] && out_path="$RESULTS_DIR/layer3-e2e.json"
mkdir -p "$(dirname "$out_path")"

sanitize() { echo "$1" | tr '/' '-' | tr -c 'a-zA-Z0-9-' '_'; }

"$SCRIPT_DIR/generate-fixtures.sh"
EXPECTED_COUNT="$(cat "$SCRIPT_DIR/fixtures/.expected-count")"

if [[ ! -d "$SCRIPT_DIR/node_modules/playwright" ]]; then
  echo "playwright not installed — run 'npm install' in $SCRIPT_DIR first" >&2
  exit 1
fi

declare -A WORKTREES
declare -A IMAGES
declare -A CONTAINERS
declare -A PORTS
PORTS[baseline]=18196
PORTS[candidate]=18197

cleanup() {
  for role in baseline candidate; do
    [[ -n "${CONTAINERS[$role]:-}" ]] && "$RUNTIME" rm -f "${CONTAINERS[$role]}" >/dev/null 2>&1 || true
    if [[ "$keep_images" != "true" && -n "${IMAGES[$role]:-}" ]]; then
      "$RUNTIME" rmi "${IMAGES[$role]}" >/dev/null 2>&1 || true
    fi
    if [[ -n "${WORKTREES[$role]:-}" ]]; then
      git -C "$REPO_ROOT" worktree remove --force "${WORKTREES[$role]}" >/dev/null 2>&1 || true
    fi
  done
}
trap cleanup EXIT

build_and_run() {
  local role="$1" ref="$2" port="$3"
  local current_ref
  current_ref="$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD 2>/dev/null || echo "")"

  local build_dir="$REPO_ROOT"
  if [[ "$ref" != "$current_ref" && "$ref" != "HEAD" ]]; then
    local worktree="$(mktemp -d)/perf-layer3-worktree-$(sanitize "$ref")"
    echo "[$role] checking out $ref into disposable worktree: $worktree" >&2
    git -C "$REPO_ROOT" worktree add --detach "$worktree" "$ref" >&2
    WORKTREES[$role]="$worktree"
    build_dir="$worktree"
  fi
  # perf/Layer3.E2E may not exist on every ref (e.g. upstream/master, which this harness measures
  # against but never depends on) — always use the current working tree's copy, same pattern
  # perf/run-perf.sh uses for Layer 1.
  mkdir -p "$build_dir/perf"
  cp -r "$SCRIPT_DIR" "$build_dir/perf/Layer3.E2E"

  local image="localhost/jellyfin-perf-layer3:$(sanitize "$ref")-$$"
  echo "[$role] building $image ..." >&2
  "$RUNTIME" build -f "$build_dir/perf/Layer3.E2E/Dockerfile" -t "$image" "$build_dir" >&2
  IMAGES[$role]="$image"

  local name="jellyfin-perf-layer3-$role-$$"
  "$RUNTIME" run -d --name "$name" -p "127.0.0.1:${port}:8096" \
    -v "$SCRIPT_DIR/fixtures:/fixtures:ro,Z" \
    "$image" >&2
  CONTAINERS[$role]="$name"

  echo "[$role] waiting for http://localhost:${port}/health ..." >&2
  local deadline=$((SECONDS + 60))
  until curl -sf -o /dev/null "http://localhost:${port}/health"; do
    if [[ $SECONDS -ge $deadline ]]; then
      echo "[$role] server did not become healthy in time" >&2
      "$RUNTIME" logs "$name" >&2 || true
      exit 1
    fi
    sleep 1
  done
}

build_and_run baseline "$baseline_ref" "${PORTS[baseline]}"
build_and_run candidate "$candidate_ref" "${PORTS[candidate]}"

WORKDIR="$(mktemp -d)"
for role in baseline candidate; do
  port="${PORTS[$role]}"
  echo "[$role] seeding ..." >&2
  node "$SCRIPT_DIR/seed.js" "http://localhost:${port}" "$EXPECTED_COUNT" > "$WORKDIR/$role-seed.json"
  token="$(node -e "console.log(JSON.parse(require('fs').readFileSync('$WORKDIR/$role-seed.json','utf8')).accessToken)")"
  user_id="$(node -e "console.log(JSON.parse(require('fs').readFileSync('$WORKDIR/$role-seed.json','utf8')).userId)")"
  server_id="$(node -e "console.log(JSON.parse(require('fs').readFileSync('$WORKDIR/$role-seed.json','utf8')).serverId)")"
  echo "[$role] measuring ..." >&2
  node "$SCRIPT_DIR/measure.js" "http://localhost:${port}" "$token" "$user_id" "$server_id" > "$WORKDIR/$role-measure.json"
done

candidate_sha="$(git -C "$REPO_ROOT" rev-parse "$candidate_ref" 2>/dev/null || echo null)"
node "$SCRIPT_DIR/write-result.js" "$WORKDIR/baseline-measure.json" "$WORKDIR/candidate-measure.json" "$candidate_ref" "$candidate_sha" "$out_path"
