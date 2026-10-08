"""Run every shipped developer suite and capture fresh exact-input provenance.

No successful result from this tool is a live Windows/Unity/Mono certificate.
Source changes during execution make the whole report fail, even if every
individual runner exited successfully. Independent suite logs are retained.
"""
import argparse
import ast
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time

from TestEnvironment import resolve, sha256
from FixtureEvidence import inspect as inspect_fixture_evidence

CORE_SUITES = {"engine", "native", "save", "rest-native", "effects-native", "effects-parity", "await", "ui",
               "ability-native", "await/integration", "ui-render", "plugin-lifecycle", "ui-input-boundary",
               "network-protocol", "network-transport", "network-native", "network-native/runtime", "network-descriptor", "network-end-to-end", "coverage", "shove-native", "sync-sites"}
SOURCE_SUFFIXES = {".cs", ".csproj", ".py", ".ps1", ".md", ".json"}


def source_hashes(project):
    result = {}
    for directory in ("src", "tools", "tests"):
        for path in sorted((project / directory).rglob("*")):
            if not path.is_file() or path.suffix.lower() not in SOURCE_SUFFIXES:
                continue
            if any(part in {"build", "bin", "obj", "__pycache__", ".git"} for part in path.relative_to(project).parts):
                continue
            if path.name in {"source-manifest.json", "results.json"}:
                continue  # Evidence produced by a runner is not compiler input.
            result[path.relative_to(project).as_posix()] = sha256(path)
    # These hand-authored review inputs are shipped with the sources. Native
    # decompiler dumps and generated qualification output are not inputs.
    for relative in ("audit/effects/Inventory.cs","audit/effects/EFFECT-COVERAGE.md","audit/coverage/COVERAGE-REVIEW.md",
                     "audit/network/NATIVE-NETWORK-REVIEW.md"):
        path = project / relative
        if path.is_file():
            result[relative] = sha256(path)
    return result


def suite_runners(project):
    return {p.parent.relative_to(project / "tests").as_posix(): p
            for p in sorted((project / "tests").rglob("run.py"))
            if not any(part in {"build", "bin", "obj", "__pycache__"} for part in p.relative_to(project / "tests").parts)}


def execute(command, cwd, environment, log, timeout):
    started = time.monotonic()
    try:
        result = subprocess.run([str(p) for p in command], cwd=cwd, env=environment,
                                text=True, errors="replace", capture_output=True, timeout=timeout)
        output, code = result.stdout + result.stderr, result.returncode
        timed_out = False
    except subprocess.TimeoutExpired as error:
        def decode(value):
            return value.decode("utf-8", "replace") if isinstance(value, bytes) else value or ""
        output = decode(error.stdout) + decode(error.stderr) + "\nRunner timed out.\n"
        code, timed_out = -1, True
    log.write_text(output, encoding="utf-8")
    return {"command": [str(p) for p in command], "exit_code": code, "timed_out": timed_out,
            "seconds": round(time.monotonic() - started, 3), "log": log.name,
            "log_sha256": sha256(log), "status": "pass" if code == 0 else "fail"}


def main():
    project = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("dotnet", "sdk-root", "managed", "references", "cecil"):
        parser.add_argument("--" + key, type=Path)
    parser.add_argument("--output", type=Path, default=project / "build/regression")
    parser.add_argument("--suite", action="append", help="Select suites for a partial developer run; cannot satisfy the full release gate")
    parser.add_argument("--timeout", type=int, default=900, help="Maximum seconds per independent process")
    parser.add_argument("--build", type=Path, help="After suites pass, compile and audit the full runtime plugin into this separate directory")
    args = parser.parse_args()
    for path in sorted(project.rglob("*.py")):
        if any(part in {"build", "bin", "obj", "__pycache__", "references"} for part in path.relative_to(project).parts):
            continue
        try:
            ast.parse(path.read_text(encoding="utf-8-sig"), filename=str(path))
        except SyntaxError as error:
            parser.error("Source Python syntax is invalid: " + str(error))
    try:
        env = resolve(project, dotnet=args.dotnet, sdk_root=args.sdk_root, managed=args.managed,
                      references=args.references, cecil=args.cecil)
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        parser.error(str(error))
    runners = suite_runners(project)
    if not CORE_SUITES.issubset(runners):
        parser.error("Required suite runners missing: " + ", ".join(sorted(CORE_SUITES - runners.keys())))
    selected = sorted(set(args.suite) if args.suite else runners)
    if set(selected) - runners.keys():
        parser.error("Unknown selected suite")
    if args.timeout < 1:
        parser.error("Timeout must be positive")
    output = args.output.resolve()
    for protected in (project / "src", project / "tools", project / "tests", env.managed, env.references):
        if output == protected or protected in output.parents or output in protected.parents:
            parser.error("Regression output must be separate from sources, tests and protected inputs")
    output.mkdir(parents=True, exist_ok=True)
    before = source_hashes(project)
    inputs_before = {p.name: sha256(p) for p in sorted(env.managed.glob("*.dll"))}
    environment_before = env.report()
    report = {"schema_version": 1, "started_utc": datetime.now(timezone.utc).isoformat(),
              "project_version": json.loads((project / "src/info.template.json").read_text())["Version"],
              "python": sys.version, "platform": sys.platform, "environment": environment_before,
              "suite_inventory": sorted(runners), "selected_suites": selected, "full_inventory_run": selected == sorted(runners),
              "sources_before": before, "managed_inputs_before": inputs_before, "suites": {},
              "live_windows_mono_qualified": False, "live_multiplayer_qualified": False}
    child_env = env.child_environment()
    # Do not inherit a cached Python helper from a different source revision.
    child_env["PYTHONDONTWRITEBYTECODE"] = "1"
    for name in selected:
        print("Running " + name + "...", flush=True)
        result = execute([sys.executable, runners[name]], project, child_env, output / (name.replace("/", "-") + ".txt"), args.timeout)
        report["suites"][name] = result
        print(name + ": " + result["status"] + " (" + str(result["seconds"]) + "s)", flush=True)
    tool_test = project / "tools/TestReleaseTools.py"
    if tool_test.is_file():
        report["tool_regressions"] = execute([sys.executable, tool_test], project, child_env, output / "release-tools.txt", args.timeout)
    report["powershell_grammar"] = execute([sys.executable,project / "tools/RunPowerShellGrammar.py"],project,child_env,
                                          output / "powershell-grammar.txt",args.timeout)
    # Generated manifests are validated after their runners have had a chance
    # to refresh successful evidence. Their bytes and every recorded project
    # input are then pinned separately from compiler-input source hashes.
    report["fixture_evidence"] = inspect_fixture_evidence(project, inputs_before)
    for error in report["fixture_evidence"]["errors"]:
        print("Fixture evidence: " + error, flush=True)
    suite_pass = all(s["status"] == "pass" for s in report["suites"].values())
    if args.build and suite_pass and report["fixture_evidence"]["status"] == "pass":
        report["build"] = execute([sys.executable, project / "tools/Build.py", "--output", args.build.resolve()],
                                  project, child_env, output / "build.txt", args.timeout)
        if report["build"]["status"] == "pass":
            report["build_manifest"] = json.loads((args.build / "build-manifest.json").read_text())
            report["package_regressions"] = execute([sys.executable, project / "tools/TestPackage.py", "--build", args.build.resolve(),
                                                    "--log", args.build.resolve() / "package-checks.txt"],
                                                   project, child_env, output / "package.txt", args.timeout)
    after = source_hashes(project)
    inputs_after = {p.name: sha256(p) for p in sorted(env.managed.glob("*.dll"))}
    report["sources_after"] = after
    report["managed_inputs_after"] = inputs_after
    report["sources_unchanged"] = before == after
    report["managed_inputs_unchanged"] = inputs_before == inputs_after
    report["environment_inputs_unchanged"] = environment_before == env.report()
    report["fixture_evidence_unchanged"] = report["fixture_evidence"] == inspect_fixture_evidence(project, inputs_after)
    report["finished_utc"] = datetime.now(timezone.utc).isoformat()
    operations = list(report["suites"].values()) + [report[name] for name in ("tool_regressions", "powershell_grammar", "build", "package_regressions") if name in report]
    passed = (all(r["status"] == "pass" for r in operations) and report["sources_unchanged"] and
              report["managed_inputs_unchanged"] and report["environment_inputs_unchanged"] and
              report["fixture_evidence"]["status"] == "pass" and report["fixture_evidence_unchanged"])
    report["status"] = "pass" if passed else "fail"
    report["qualification"] = "developer_regressions_only; actual Windows Unity/Mono acceptance required"
    temporary = output / "regression-report.json.pending"
    temporary.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, output / "regression-report.json")
    print("Regression report: " + str(output / "regression-report.json"), flush=True)
    print("No live Windows/Unity/Mono qualification is asserted.", flush=True)
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
