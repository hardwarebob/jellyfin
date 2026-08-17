#!/usr/bin/env python3
"""Diffs two perf-result.schema.json files, matching results by id.

Usage:
    compare.py baseline.json candidate.json [--fail-on-regression]

Prints a Markdown table (suitable for pasting into a PR description) and writes
results/diff.json (same matched-pairs shape, plus a "delta" block per result) next to
candidate.json. Exit code is only ever affected by --fail-on-regression, and only by
gating:true results — kind:timing is always informational, never affects it.
"""

import argparse
import json
import sys
from pathlib import Path


def load(path: str) -> dict:
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def index_by_id(run: dict) -> dict:
    return {r["id"]: r for r in run.get("results", [])}


def pct_change(baseline_val, candidate_val):
    if baseline_val in (None, 0):
        return None
    return (candidate_val - baseline_val) / baseline_val * 100


def diff_one(baseline: dict | None, candidate: dict | None) -> dict:
    result_id = (baseline or candidate)["id"]
    kind = (candidate or baseline)["kind"]
    gating = (candidate or baseline).get("gating", True)

    entry = {
        "id": result_id,
        "kind": kind,
        "gating": gating,
        "baselineStatus": baseline["status"] if baseline else None,
        "candidateStatus": candidate["status"] if candidate else None,
    }

    is_regression = False

    if kind == "queryplan":
        b_status = baseline["status"] if baseline else "not_applicable"
        c_status = candidate["status"] if candidate else "not_applicable"
        if b_status == "pass" and c_status == "fail":
            is_regression = True
        entry["summary"] = f"{b_status} -> {c_status}"
    elif kind in ("ratio", "operationcount"):
        b_metrics = (baseline or {}).get("metrics", {})
        c_metrics = (candidate or {}).get("metrics", {})
        # Compare every numeric metric key present on the candidate side.
        deltas = {}
        for key, c_val in c_metrics.items():
            b_val = b_metrics.get(key)
            if isinstance(c_val, (int, float)) and isinstance(b_val, (int, float)):
                deltas[key] = {
                    "baseline": b_val,
                    "candidate": c_val,
                    "percentChange": pct_change(b_val, c_val),
                }
        entry["deltas"] = deltas
        c_status = candidate["status"] if candidate else "not_applicable"
        if c_status == "fail" and (baseline is None or baseline.get("status") == "pass"):
            is_regression = True
        entry["summary"] = ", ".join(
            f"{k}: {v['baseline']} -> {v['candidate']}"
            + (f" ({v['percentChange']:+.1f}%)" if v["percentChange"] is not None else "")
            for k, v in deltas.items()
        ) or entry.get("summary", "")
    elif kind == "timing":
        entry["summary"] = "(informational only, not gating)"
    else:
        entry["summary"] = ""

    entry["isRegression"] = is_regression and gating
    return entry


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("baseline")
    parser.add_argument("candidate")
    parser.add_argument("--fail-on-regression", action="store_true")
    parser.add_argument("--out", default=None, help="Where to write diff.json (default: alongside candidate)")
    args = parser.parse_args()

    baseline_run = load(args.baseline)
    candidate_run = load(args.candidate)
    baseline_results = index_by_id(baseline_run)
    candidate_results = index_by_id(candidate_run)

    all_ids = list(dict.fromkeys(list(baseline_results) + list(candidate_results)))
    diffs = [diff_one(baseline_results.get(i), candidate_results.get(i)) for i in all_ids]

    out_path = Path(args.out) if args.out else Path(args.candidate).parent / "diff.json"
    out_path.write_text(
        json.dumps(
            {
                "baselineRef": baseline_run.get("run", {}).get("ref"),
                "candidateRef": candidate_run.get("run", {}).get("ref"),
                "diffs": diffs,
            },
            indent=2,
        ),
        encoding="utf-8",
    )

    print(f"# Perf diff: {baseline_run.get('run', {}).get('ref')} -> {candidate_run.get('run', {}).get('ref')}\n")
    print("| id | kind | gating | result |")
    print("|---|---|---|---|")
    for d in diffs:
        marker = "REGRESSION" if d["isRegression"] else ("info" if not d["gating"] else "ok")
        print(f"| {d['id']} | {d['kind']} | {d['gating']} | {marker}: {d['summary']} |")

    print(f"\nWrote {out_path}")

    regressions = [d for d in diffs if d["isRegression"]]
    if regressions:
        print(f"\n{len(regressions)} gating regression(s): {', '.join(d['id'] for d in regressions)}")

    if args.fail_on_regression and regressions:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
