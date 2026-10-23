#!/usr/bin/env python3
"""SPEC-96: query live SonarCloud issue counts per rule vs local board."""
import json, sys, urllib.request

BASE = ("https://sonarcloud.io/api/issues/search"
        "?componentKeys=afonsoft_OpenHandsNET&types=CODE_SMELL"
        "&ps=1&facets=rules&severities=")


def fetch(sev):
    with urllib.request.urlopen(BASE + sev, timeout=30) as r:
        d = json.load(r)
    return d.get("total", 0), {
        v["val"]: v["count"] for v in d.get("facets", [{}])[0].get("values", [])}


def main():
    out = {}
    for sev in ["BLOCKER", "CRITICAL", "MAJOR", "MINOR", "INFO"]:
        total, rules = fetch(sev)
        out[sev] = {"total": total, "rules": rules}
        print(f"{sev}: {total}")
        for k, v in sorted(rules.items(), key=lambda x: -x[1]):
            print(f"  {k} {v}")
    for t in ["VULNERABILITY", "BUG"]:
        with urllib.request.urlopen(
                BASE.replace("CODE_SMELL", t).replace("&severities=", ""),
                timeout=30) as r:
            print(f"{t}: {json.load(r).get('total', 0)}")
    json.dump(out, open("tools/sonar-live.json", "w"), indent=1)


if __name__ == "__main__":
    sys.exit(main())
