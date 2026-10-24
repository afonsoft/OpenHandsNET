#!/usr/bin/env python3
"""SPEC-96: query live SonarCloud issue counts per rule vs local board.

Usage:
    sonar-rescan.py            # print + save per-severity rule counts
    sonar-rescan.py --board    # regenerate .sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md
                               # from live open issues (paginated dump)
"""
import datetime
import itertools
import json
import sys
import urllib.request

BASE = ("https://sonarcloud.io/api/issues/search"
        "?componentKeys=afonsoft_OpenHandsNET&types=CODE_SMELL"
        "&resolved=false&ps=1&facets=rules&severities=")
BOARD = ".sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md"


def get(url):
    with urllib.request.urlopen(url, timeout=30) as r:
        return json.load(r)


def fetch(sev):
    d = get(BASE + sev)
    return d.get("total", 0), {
        v["val"]: v["count"] for v in d.get("facets", [{}])[0].get("values", [])}


def fetch_all(itype):
    """Paginate api/issues/search (ps<=500, p<=20) for one issue type."""
    out, page = [], 1
    while True:
        d = get("https://sonarcloud.io/api/issues/search"
                "?componentKeys=afonsoft_OpenHandsNET&resolved=false"
                f"&types={itype}&ps=500&p={page}")
        out.extend(d.get("issues", []))
        if page * 500 >= d.get("total", 0) or page >= 20:
            return out
        page += 1


def write_board():
    issues = list(itertools.chain.from_iterable(
        fetch_all(t) for t in ("VULNERABILITY", "BUG", "CODE_SMELL")))
    issues.sort(key=lambda i: (i["type"], i["rule"], i["component"], i.get("line") or 0))
    groups = itertools.groupby(issues, key=lambda i: (i["type"], i["rule"]))
    lines = [
        "# SonarQube Review ToDo Board",
        "",
        f"Gerado: {datetime.date.today():%Y-%m-%d} (live rescan) · "
        "projeto `afonsoft_OpenHandsNET` · branch `main`",
        f"Total: {len(issues)} issues abertas",
        "",
        "## SonarQube Issues Checklist",
        "",
    ]
    for (itype, rule), items in groups:
        items = list(items)
        lines += ["", f"### {itype} · `{rule}`", ""]
        for i in items:
            path = i["component"].split(":", 1)[-1]
            sev = i["severity"]
            line = i.get("line") or i.get("textRange", {}).get("startLine", "?")
            msg = i["message"].splitlines()[0]
            lines += [
                f"- [ ] Issue {i['key']} — Severity: {sev} — "
                f"File: `{path}` — Line: {line}",
                f"      Summary: {msg}",
            ]
    with open(BOARD, "w") as f:
        f.write("\n".join(lines) + "\n")
    print(f"{BOARD}: {len(issues)} issues")


def main():
    if "--board" in sys.argv:
        return write_board()
    out = {}
    for sev in ["BLOCKER", "CRITICAL", "MAJOR", "MINOR", "INFO"]:
        total, rules = fetch(sev)
        out[sev] = {"total": total, "rules": rules}
        print(f"{sev}: {total}")
        for k, v in sorted(rules.items(), key=lambda x: -x[1]):
            print(f"  {k} {v}")
    for t in ["VULNERABILITY", "BUG"]:
        d = get(BASE.replace("CODE_SMELL", t).replace("&severities=", ""))
        print(f"{t}: {d.get('total', 0)}")
    json.dump(out, open("tools/sonar-live.json", "w"), indent=1)


if __name__ == "__main__":
    sys.exit(main())
