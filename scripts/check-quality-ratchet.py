#!/usr/bin/env python3
"""Quality ratchet — blocks measured regressions vs a committed baseline.

Baseline: config/quality/quality-baseline.json
  { "metrics": { "coverageLines": { "value": 41.0, "slack": 0.5, "unit": "%" } } }

Current metrics file (JSON): { "coverageLines": 42.3, ... }

Semantics (same convention as OmniRoute):
  * missing metric in current file -> SKIP when --allow-missing, FAIL otherwise
    (an unmeasured metric is not a zero and not a pass);
  * current < baseline - slack  -> REGRESSION (exit 1);
  * current > baseline          -> improvement, reported (--require-tighten turns
    it into a failure so the gain is captured in the same PR);
  * writes a markdown table to --summary for $GITHUB_STEP_SUMMARY.
"""
import argparse
import json
import sys


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--current", required=True, help="JSON file with measured metrics")
    ap.add_argument("--baseline", default="config/quality/quality-baseline.json")
    ap.add_argument("--summary", default=".artifacts/quality-ratchet.md")
    ap.add_argument("--allow-missing", action="store_true")
    ap.add_argument("--require-tighten", action="store_true")
    args = ap.parse_args()

    try:
        baseline = json.load(open(args.baseline))["metrics"]
    except FileNotFoundError:
        print(f"::error::baseline not found: {args.baseline}")
        return 1
    try:
        current = json.load(open(args.current))
    except FileNotFoundError:
        print(f"::error::current metrics file not found: {args.current}")
        return 1

    rows = ["| Metric | Baseline | Current | Verdict |", "|---|---|---|---|"]
    failed, missing, improved = [], [], []
    for name, spec in baseline.items():
        target = float(spec["value"])
        slack = float(spec.get("slack", 0))
        unit = spec.get("unit", "")
        if name not in current:
            missing.append(name)
            rows.append(f"| {name} | {target}{unit} | — | SKIP (unmeasured) |")
            continue
        cur = float(current[name])
        if cur < target - slack:
            verdict = "REGRESSION"
            failed.append(name)
        elif cur > target:
            verdict = "improved"
            improved.append(name)
        else:
            verdict = "ok"
        rows.append(f"| {name} | {target}{unit} | {cur}{unit} | {verdict} |")

    report = "## Quality ratchet\n\n" + "\n".join(rows) + "\n"
    print(report)
    try:
        with open(args.summary, "w") as fh:
            fh.write(report)
    except OSError:
        pass  # summary is best-effort; the verdict is the exit code

    if missing and not args.allow_missing:
        print(f"::error::{len(missing)} baseline metric(s) not measured: {', '.join(missing)}")
        return 1
    if args.require_tighten and improved:
        print(f"::error::metric(s) improved without re-baseline: {', '.join(improved)} — "
              f"raise config/quality/quality-baseline.json in this PR")
        return 1
    if failed:
        print(f"::error::{len(failed)} metric(s) regressed vs baseline: {', '.join(failed)}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
