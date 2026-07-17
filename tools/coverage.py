#!/usr/bin/env python3
"""Merge cobertura coverage from all test projects and report core line coverage + gaps.

Usage:
    for p in Celly.Tests Celly.Protobuf.Tests Celly.Conformance; do
        dotnet test tests/$p --collect:"XPlat Code Coverage" --results-directory /tmp/cov/$p
    done
    python3 tools/coverage.py /tmp/cov
"""
import sys, glob, re, xml.etree.ElementTree as ET
from collections import defaultdict

root = sys.argv[1] if len(sys.argv) > 1 else "/tmp/cov"
covered, alllines = defaultdict(set), defaultdict(set)


def norm(fn):
    return re.sub(r"^Celly(\.Protobuf)?/", "", fn)  # merge the two path roots the runs emit


for f in glob.glob(f"{root}/**/coverage.cobertura.xml", recursive=True):
    for cls in ET.parse(f).getroot().iter("class"):
        fn = cls.get("filename") or ""
        if fn.endswith(".g.cs") or "obj/" in fn or "Cel/Expr" in fn or fn.startswith("/"):
            continue  # skip generated proto code
        k = norm(fn)
        for line in cls.iter("line"):
            ln = int(line.get("number"))
            alllines[k].add(ln)
            if int(line.get("hits")) > 0:
                covered[k].add(ln)

tot_all = sum(len(v) for v in alllines.values())
tot_cov = sum(len(covered[k] & alllines[k]) for k in alllines)
print(f"Core line coverage: {tot_cov}/{tot_all} = {100 * tot_cov / tot_all:.1f}%\n")
print("Lowest-covered files:")
rows = [
    (len(covered[k] & alllines[k]) / len(alllines[k]), len(covered[k] & alllines[k]), len(alllines[k]), k)
    for k in alllines if len(alllines[k]) >= 8
]
for pct, c, a, k in sorted(rows)[:15]:
    print(f"  {100 * pct:5.1f}%  {c:4}/{a:<4}  {k}")
