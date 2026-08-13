#!/usr/bin/env python3
"""Fail when a dotnet package vulnerability JSON report contains findings."""

from __future__ import annotations

import json
from pathlib import Path
import sys


def package_findings(report: object) -> list[str]:
    if not isinstance(report, dict) or report.get("version") != 1:
        raise ValueError("unsupported vulnerability report schema")
    projects = report.get("projects")
    if not isinstance(projects, list):
        raise ValueError("vulnerability report has no projects list")

    findings: list[str] = []
    for project in projects:
        if not isinstance(project, dict):
            raise ValueError("vulnerability report contains an invalid project")
        project_path = str(project.get("path", "unknown project"))
        frameworks = project.get("frameworks", [])
        if not isinstance(frameworks, list):
            raise ValueError(f"{project_path} has an invalid frameworks list")
        for framework in frameworks:
            if not isinstance(framework, dict):
                raise ValueError(f"{project_path} contains an invalid framework")
            framework_name = str(framework.get("framework", "unknown framework"))
            for package_kind in ("topLevelPackages", "transitivePackages"):
                packages = framework.get(package_kind, [])
                if not isinstance(packages, list):
                    raise ValueError(f"{project_path} has an invalid {package_kind} list")
                for package in packages:
                    if not isinstance(package, dict):
                        raise ValueError(f"{project_path} contains an invalid package")
                    vulnerabilities = package.get("vulnerabilities", [])
                    if not vulnerabilities:
                        continue
                    package_id = str(package.get("id", "unknown package"))
                    resolved_version = str(package.get("resolvedVersion", "unknown version"))
                    findings.append(f"{project_path} [{framework_name}]: {package_id} {resolved_version}")
    return findings


def main() -> int:
    if len(sys.argv) != 2:
        print(f"Usage: {Path(sys.argv[0]).name} REPORT.json", file=sys.stderr)
        return 2
    try:
        with Path(sys.argv[1]).open(encoding="utf-8") as stream:
            report = json.load(stream)
        findings = package_findings(report)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"Vulnerability report validation failed: {error}", file=sys.stderr)
        return 1
    if findings:
        print("Known vulnerable NuGet packages were found:", file=sys.stderr)
        for finding in findings:
            print(f"- {finding}", file=sys.stderr)
        return 1
    print("No known vulnerable direct or transitive NuGet packages were found.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
