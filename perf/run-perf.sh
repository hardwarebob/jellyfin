#!/usr/bin/env bash
# Single entry point for the perf-regression harness.
#
# Currently drives Layer 1 (query-plan gates) only. Layer 2 (Layer2.Integration/) is built but
# not yet wired in here — it needs `dotnet test` run from its own directory specifically (see
# Layer2.Integration/Core/SeededHostFixture.cs), a different invocation shape than Layer 1's
# `dotnet run`, and isn't ref-switchable via git worktree the same way yet. Run it directly:
#   dotnet test perf/Layer2.Integration -c Release --filter FullyQualifiedName~RunAllAndWriteResults
#   (PERF_OUT=<path> PERF_GIT_REF=<ref> env vars control output path / recorded ref)
#
#   run-perf.sh run  [--ref <git-ref>] [--out <path>]
#       Runs Layer 1 against the given ref (default: current worktree, uncommitted changes
#       included) and writes the standardized JSON result to --out (default: results/<ref>.json).
#       When --ref differs from the current checkout, builds and runs from a disposable git
#       worktree so your actual working tree is left untouched.
#
#   run-perf.sh diff --baseline <ref> --candidate <ref> [--fail-on-regression]
#       Runs twice (baseline ref, candidate ref) and pipes both results into compare/compare.py.
#
# Requires: dotnet SDK (or set PERF_DOTNET_IMAGE to a container image with one, e.g.
# mcr.microsoft.com/dotnet/sdk:10.0 — the harness will then run `dotnet` via `podman run`/`docker run`).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
RUNNER_PROJECT="perf/Layer1.QueryPlan/Runner/Jellyfin.PerfTests.QueryPlan.Runner.csproj"
RESULTS_DIR="$SCRIPT_DIR/results"

dotnet_cmd() {
  local workdir="$1"
  shift
  if [[ -n "${PERF_DOTNET_IMAGE:-}" ]]; then
    local runtime="${PERF_CONTAINER_RUNTIME:-podman}"
    "$runtime" run --rm -v "$workdir":/src:Z -w "/src" "$PERF_DOTNET_IMAGE" dotnet "$@"
  else
    (cd "$workdir" && dotnet "$@")
  fi
}

sanitize_ref() {
  echo "$1" | tr '/' '-'
}

run_layer1() {
  local git_ref="$1"
  local out_path="$2"
  local workdir="$REPO_ROOT"
  local worktree=""

  local current_ref
  current_ref="$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD 2>/dev/null || echo "")"

  if [[ -n "$git_ref" && "$git_ref" != "$current_ref" && "$git_ref" != "HEAD" ]]; then
    worktree="$(mktemp -d)/perf-worktree-$(sanitize_ref "$git_ref")"
    echo "Checking out $git_ref into a disposable worktree: $worktree" >&2
    git -C "$REPO_ROOT" worktree add --detach "$worktree" "$git_ref" >&2
    cp -r "$SCRIPT_DIR" "$worktree/perf"
    workdir="$worktree"
  fi

  mkdir -p "$(dirname "$out_path")"

  # Written relative to $workdir, not as the final absolute $out_path: in container mode only
  # $workdir is mounted (at /src) — an absolute host path wouldn't resolve inside the container
  # and the file would be written into the ephemeral container instead, then lost on --rm.
  local relative_out="perf-result.json"
  # Gating failures are an expected, meaningful result here (that's the whole point of
  # measuring a baseline) — don't let the runner's own exit code (1 on gating failure, used when
  # it's run standalone for CI) abort this orchestrator via set -e.
  PERF_GIT_REF="$git_ref" dotnet_cmd "$workdir" run -c Release --project "$RUNNER_PROJECT" -- --ref "$git_ref" --out "$relative_out" || true
  cp "$workdir/$relative_out" "$out_path"

  if [[ -n "$worktree" ]]; then
    git -C "$REPO_ROOT" worktree remove --force "$worktree" >&2 || true
  fi
}

cmd="${1:-}"
shift || true

case "$cmd" in
  run)
    ref="HEAD"
    out=""
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --ref) ref="$2"; shift 2 ;;
        --out) out="$2"; shift 2 ;;
        *) echo "Unknown arg: $1" >&2; exit 1 ;;
      esac
    done
    [[ -z "$out" ]] && out="$RESULTS_DIR/$(sanitize_ref "$ref").json"
    run_layer1 "$ref" "$out"
    ;;

  diff)
    baseline_ref=""
    candidate_ref=""
    fail_on_regression=""
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --baseline) baseline_ref="$2"; shift 2 ;;
        --candidate) candidate_ref="$2"; shift 2 ;;
        --fail-on-regression) fail_on_regression="--fail-on-regression"; shift ;;
        *) echo "Unknown arg: $1" >&2; exit 1 ;;
      esac
    done
    if [[ -z "$baseline_ref" || -z "$candidate_ref" ]]; then
      echo "Usage: run-perf.sh diff --baseline <ref> --candidate <ref> [--fail-on-regression]" >&2
      exit 1
    fi

    baseline_out="$RESULTS_DIR/$(sanitize_ref "$baseline_ref").json"
    candidate_out="$RESULTS_DIR/$(sanitize_ref "$candidate_ref").json"
    run_layer1 "$baseline_ref" "$baseline_out"
    run_layer1 "$candidate_ref" "$candidate_out"

    python3 "$SCRIPT_DIR/compare/compare.py" "$baseline_out" "$candidate_out" $fail_on_regression
    ;;

  *)
    echo "Usage: run-perf.sh run [--ref <git-ref>] [--out <path>]" >&2
    echo "       run-perf.sh diff --baseline <ref> --candidate <ref> [--fail-on-regression]" >&2
    exit 1
    ;;
esac
