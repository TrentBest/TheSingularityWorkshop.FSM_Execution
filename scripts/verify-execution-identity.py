#!/usr/bin/env python3
"""Fail if the repository's implementation identity regresses to Foundry."""
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
tracked = subprocess.check_output(["git", "ls-files", "-z"], cwd=root).decode().split("\0")
tracked = [path for path in tracked if path]

stale_paths = [path for path in tracked if "Foundry" in path]
if stale_paths:
    print("Stale Foundry paths:")
    print("\n".join(f"  {path}" for path in stale_paths))
    sys.exit(1)

stale_code = []
for folder in ("src", "tests"):
    for path in (root / folder).rglob("*.cs"):
        content = path.read_text(encoding="utf-8")
        if "TheSingularityWorkshop.Foundry" in content:
            stale_code.append(str(path.relative_to(root)))

if stale_code:
    print("Stale Foundry namespaces/usings:")
    print("\n".join(f"  {path}" for path in stale_code))
    sys.exit(1)

stale_projects = []
for relative in tracked:
    if relative.endswith(".csproj"):
        path = root / relative
        content = path.read_text(encoding="utf-8")
        if "Foundry." in content:
            stale_projects.append(relative)

if stale_projects:
    print("Stale Foundry project identities/references:")
    print("\n".join(f"  {path}" for path in stale_projects))
    sys.exit(1)

required = (
    "src/Execution.Workflow/Execution.Workflow.csproj",
    "tests/Execution.Workflow.Tests/Execution.Workflow.Tests.csproj",
    ".github/workflows/execution-fsm-api-probe.yml",
)
missing = [path for path in required if not (root / path).is_file()]
if missing:
    print("Missing canonical Execution paths:")
    print("\n".join(f"  {path}" for path in missing))
    sys.exit(1)

print("PASS: source paths, C# namespaces, project references, and CI entry point use FSM_Execution identity.")
print("Legacy foundry.* protocol IDs and FOUNDRY_* compatibility aliases are intentionally outside this identity check.")
