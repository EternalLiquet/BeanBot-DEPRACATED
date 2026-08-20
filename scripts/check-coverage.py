#!/usr/bin/env python3
"""Validate BeanBot Cobertura coverage and create human-readable reports."""

from __future__ import annotations

import html
import json
from pathlib import Path
import shutil
import sys
import xml.etree.ElementTree as ElementTree


def load_rate(value: object, name: str) -> float:
    try:
        rate = float(value)
    except (TypeError, ValueError) as error:
        raise ValueError(f"{name} must be a number") from error
    if not 0 <= rate <= 1:
        raise ValueError(f"{name} must be between 0 and 1")
    return rate


def format_percent(rate: float) -> str:
    return f"{rate * 100:.2f}%"


def main() -> int:
    if len(sys.argv) != 4:
        print(
            f"Usage: {Path(sys.argv[0]).name} RESULTS_DIRECTORY BASELINE.json OUTPUT_DIRECTORY",
            file=sys.stderr,
        )
        return 2

    results_directory = Path(sys.argv[1])
    baseline_path = Path(sys.argv[2])
    output_directory = Path(sys.argv[3])
    reports = sorted(results_directory.rglob("*.cobertura.xml"))
    if len(reports) != 1:
        print(
            f"Expected exactly one Cobertura report under {results_directory}, found {len(reports)}.",
            file=sys.stderr,
        )
        return 1

    try:
        root = ElementTree.parse(reports[0]).getroot()
        packages = [
            package
            for package in root.findall("./packages/package")
            if package.get("name") == "BeanBot"
        ]
        if len(packages) != 1:
            raise ValueError(f"expected one BeanBot package, found {len(packages)}")
        line_rate = load_rate(packages[0].get("line-rate"), "BeanBot line rate")
        branch_rate = load_rate(packages[0].get("branch-rate"), "BeanBot branch rate")
        with baseline_path.open(encoding="utf-8") as stream:
            baseline = json.load(stream)
        baseline_line_rate = load_rate(baseline.get("lineRate"), "baseline lineRate")
        baseline_branch_rate = load_rate(baseline.get("branchRate"), "baseline branchRate")
        tolerance = load_rate(baseline.get("tolerance"), "baseline tolerance")
    except (ElementTree.ParseError, OSError, ValueError, json.JSONDecodeError) as error:
        print(f"Coverage report validation failed: {error}", file=sys.stderr)
        return 1

    minimum_line_rate = max(0, baseline_line_rate - tolerance)
    minimum_branch_rate = max(0, baseline_branch_rate - tolerance)
    passed = line_rate >= minimum_line_rate and branch_rate >= minimum_branch_rate
    status = "PASS" if passed else "FAIL"
    summary = (
        "# BeanBot coverage\n\n"
        "| Metric | Current | Baseline | Minimum |\n"
        "| --- | ---: | ---: | ---: |\n"
        f"| Lines | {format_percent(line_rate)} | {format_percent(baseline_line_rate)} | "
        f"{format_percent(minimum_line_rate)} |\n"
        f"| Branches | {format_percent(branch_rate)} | {format_percent(baseline_branch_rate)} | "
        f"{format_percent(minimum_branch_rate)} |\n\n"
        f"Result: **{status}**\n"
    )

    output_directory.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(reports[0], output_directory / "coverage.cobertura.xml")
    (output_directory / "coverage-summary.md").write_text(summary, encoding="utf-8")
    escaped_summary = html.escape(summary)
    (output_directory / "index.html").write_text(
        "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\">"
        "<title>BeanBot coverage</title><style>body{font-family:system-ui;max-width:50rem;"
        "margin:2rem auto;padding:0 1rem}pre{white-space:pre-wrap}</style>"
        f"<h1>BeanBot coverage</h1><pre>{escaped_summary}</pre></html>\n",
        encoding="utf-8",
    )
    print(summary, end="")
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
