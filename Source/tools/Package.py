"""Package only a verified standalone plugin; exclude game/loader/Harmony binaries."""
import argparse
import hashlib
import json
import os
import tempfile
from pathlib import Path, PurePosixPath
from zipfile import ZipFile, ZIP_DEFLATED, ZipInfo
from FixtureEvidence import inspect as inspect_fixture_evidence


def digest(data):
    return hashlib.sha256(data).hexdigest()


def add_file(files, name, data):
    """Reject ZIP paths that Windows extracts differently from their identity."""
    path = PurePosixPath(name)
    reserved = {"con", "prn", "aux", "nul"} | {"com" + str(i) for i in range(1,10)} | {"lpt" + str(i) for i in range(1,10)}
    if (not name or "\\" in name or path.is_absolute() or any(part in ("", ".", "..") or
            part.endswith(("."," ")) or any(c in part for c in '<>:"|?*') or
            any(ord(c)<32 for c in part) or part.split(".",1)[0].lower() in reserved for part in name.split("/"))):
        raise ValueError("Unsafe Windows ZIP path: " + name)
    for existing in files:
        if existing.lower() == name.lower() and existing != name:
            raise ValueError("Windows case collision in package: " + name + " and " + existing)
    if name in files and files[name] != data:
        raise ValueError("Conflicting package source identity: " + name)
    files[name] = data


def capture_acceptance(files, report, prefix):
    from ValidateRelease import local_file
    acceptance = json.loads(report.read_text(encoding="utf-8-sig"))
    add_file(files,prefix + report.name,report.read_bytes())
    diagnostic = local_file(report.parent,acceptance["diagnostic_report"])
    add_file(files,prefix + acceptance["diagnostic_report"],diagnostic.read_bytes())
    diagnostics = json.loads(diagnostic.read_text(encoding="utf-8-sig"))
    relative_files = {item["relative_path"] for item in diagnostics.get("logs",[])}
    relative_files.update(relative for case in acceptance.get("cases",[]) for relative in case.get("evidence",[]))
    for relative in sorted(relative_files):
        add_file(files,prefix + relative,local_file(report.parent,relative).read_bytes())


def main():
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build", type=Path, default=root / "build")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--readme", type=Path, help="Final installation/limitations README")
    parser.add_argument("--test-results", type=Path, help="Final tested-build results with live-test qualifications")
    parser.add_argument("--qualification", choices=("candidate", "production"), default="candidate")
    parser.add_argument("--regressions", type=Path, help="Fresh complete developer regression report")
    parser.add_argument("--windows", type=Path, help="Actual Windows acceptance report for production qualification")
    parser.add_argument("--multiplayer", type=Path, help="Actual coordinated host/client Windows acceptance report")
    args = parser.parse_args()
    build = args.build.resolve()
    manifest = json.loads((build / "build-manifest.json").read_text(encoding="utf-8"))
    for name, expected in manifest.get("audits", {}).items():
        if Path(name).name != name or digest((build / name).read_bytes()) != expected:
            parser.error("Audited build evidence changed: " + name)
    plugin = build / "ManualDiceRoller/ManualDiceRoller.dll"
    if digest(plugin.read_bytes()) != manifest["plugin_sha256"]:
        parser.error("Plugin differs from the audited build")
    current = {str(p.relative_to(root / "src")): digest(p.read_bytes()) for p in sorted((root / "src").rglob("*.cs"))}
    if current != manifest["sources"]:
        parser.error("Source has changed since the audited compilation")
    current_tools = {p.name: digest(p.read_bytes()) for p in sorted((root / "tools").iterdir())
                     if p.is_file() and p.suffix in (".cs", ".py", ".ps1", ".json", ".md") and not p.name.endswith("-reference.cs")}
    if current_tools != manifest["tools"]:
        parser.error("Build/audit/package tools have changed since the audited build")
    audit = (build / "assembly-audit.txt").read_text(encoding="utf-8")
    if not audit.rstrip().endswith("0 errors."):
        parser.error("Standalone assembly audit did not pass")
    if not (build / "harmony-bindings.txt").read_text(encoding="utf-8").rstrip().endswith("0 errors."):
        parser.error("Real-metadata Harmony binding audit did not pass")
    if "; 0 errors." not in (build / "harmony-native-read.txt").read_text(encoding="utf-8").splitlines()[-1]:
        parser.error("Actual Harmony pristine IL read audit did not pass")
    for negative in ("name", "type"):
        if "Negative audit self-test --negative-" + negative + ": PASS" not in (build / ("harmony-bindings-negative-" + negative + ".txt")).read_text(encoding="utf-8"):
            parser.error("Harmony binding audit negative self-test did not pass")
    info = json.loads((build / "ManualDiceRoller/Info.json").read_text(encoding="utf-8"))
    if digest((build / "ManualDiceRoller/Info.json").read_bytes()) != manifest["info_sha256"] or info != manifest["metadata"]:
        parser.error("Info.json differs from the audited build metadata")
    if info.get("AssemblyName") != "ManualDiceRoller.dll" or info.get("EntryMethod") != "ManualDiceRoller.Plugin.Load":
        parser.error("Package entry point differs from the audited plugin")
    template = root / "src/info.template.json"
    if template.is_file() and json.loads(template.read_text(encoding="utf-8-sig")) != info:
        parser.error("Source metadata differs from the audited package metadata")
    fixture_evidence = inspect_fixture_evidence(root, manifest.get("managed_references", {}))
    if fixture_evidence["status"] != "pass":
        parser.error("Stale or invalid shipped fixture evidence: " + "; ".join(fixture_evidence["errors"]))
    qualification = {"level": "candidate", "live_windows_mono_qualified": False, "live_multiplayer_qualified": False,
                     "basis": "audited_build_only; full developer and actual Windows evidence not supplied"}
    if args.qualification == "production" and args.regressions is None:
        parser.error("Production packaging requires complete developer and actual Windows acceptance reports")
    if args.regressions:
        from ValidateRelease import validate
        try:
            qualification = validate(root, build, args.regressions.resolve(), args.qualification,
                                     args.windows.resolve() if args.windows else None,
                                     args.multiplayer.resolve() if args.multiplayer else None)
        except (ValueError, KeyError, OSError, TypeError, AttributeError, OverflowError) as error:
            parser.error("Invalid release qualification evidence: " + str(error))
        if qualification["status"] != "pass":
            parser.error("Release qualification failed: " + "; ".join(qualification["errors"]))
    if args.readme is None or args.test_results is None:
        parser.error("Final installation README and qualified test results are required")
    files = {"ManualDiceRoller/ManualDiceRoller.dll": plugin.read_bytes(),
             "ManualDiceRoller/Info.json": (build / "ManualDiceRoller/Info.json").read_bytes(),
             "ManualDiceRoller/README.md": args.readme.read_bytes(),
             "ManualDiceRoller/TestResults.md": args.test_results.read_bytes(),
             "ManualDiceRoller/build-manifest.json": (build / "build-manifest.json").read_bytes(),
             "ManualDiceRoller/release-qualification.json": (json.dumps(qualification, indent=2) + "\n").encode("utf-8"),
             "ManualDiceRoller/Source/src/info.template.json": (build / "ManualDiceRoller/Info.json").read_bytes()}
    for directory in ("src", "tools", "tests"):
        for path in sorted((root / directory).rglob("*")):
            if not path.is_file() or path.suffix.lower() not in (".cs", ".csproj", ".py", ".ps1", ".md", ".txt", ".json"):
                continue
            relative = path.relative_to(root)
            if any(part in ("build", "bin", "obj", "__pycache__", ".git") for part in relative.parts):
                continue
            if (path.name == "Info.json" or path.name.endswith("-reference.cs") or path.name.startswith("UMM-official-")
                    or path.name.lower().startswith("native-") and path.suffix.lower() in (".cs", ".txt")):
                continue
            add_file(files,"ManualDiceRoller/Source/" + relative.as_posix(),path.read_bytes())
    # Include only hand-authored audit/reproduction files from this directory.
    # Native decompiler dumps and all development binaries remain excluded.
    for name in ("Inventory.cs", "EFFECT-COVERAGE.md", "inventory.txt"):
        path = root / "audit/effects" / name
        if path.is_file():
            add_file(files,"ManualDiceRoller/Source/audit/effects/" + name,path.read_bytes())
    for name in ("COVERAGE-REVIEW.md","native-roll-boundaries.tsv","await-qualification.txt","behavior-qualification.txt"):
        path = root / "audit/coverage" / name
        if path.is_file():
            add_file(files,"ManualDiceRoller/Source/audit/coverage/" + name,path.read_bytes())
    native_network_review = root / "audit/network/NATIVE-NETWORK-REVIEW.md"
    if native_network_review.is_file():
        add_file(files,"ManualDiceRoller/Source/audit/network/NATIVE-NETWORK-REVIEW.md",native_network_review.read_bytes())
    for name in ("dependencies.txt", "network-abi.txt", "harmony-generator-probe.txt", "assembly-audit.txt", "harmony-bindings.txt",
                 "harmony-bindings-negative-name.txt", "harmony-bindings-negative-type.txt", "harmony-native-read.txt"):
        add_file(files,"ManualDiceRoller/Audit/" + name,(build / name).read_bytes())
    if (build / "package-checks.txt").is_file():
        files["ManualDiceRoller/Audit/package-checks.txt"] = (build / "package-checks.txt").read_bytes()
    if args.regressions:
        files["ManualDiceRoller/Audit/regression-report.json"] = args.regressions.read_bytes()
        regressions = json.loads(args.regressions.read_text(encoding="utf-8-sig"))
        operations = list(regressions["suites"].values()) + [regressions[name] for name in ("tool_regressions", "powershell_grammar", "build", "package_regressions") if name in regressions]
        for operation in operations:
            name = operation["log"]
            log = (args.regressions.resolve().parent / name).resolve()
            if log.parent != args.regressions.resolve().parent or digest(log.read_bytes()) != operation["log_sha256"]:
                parser.error("Regression log changed or escapes its report directory: " + name)
            add_file(files,"ManualDiceRoller/Audit/Regression/" + name,log.read_bytes())
    if args.qualification == "production":
        from ValidateRelease import local_file
        capture_acceptance(files,args.windows.resolve(),"ManualDiceRoller/Audit/Acceptance/single-player/")
        mp_path = args.multiplayer.resolve()
        mp = json.loads(mp_path.read_text(encoding="utf-8-sig"))
        prefix = "ManualDiceRoller/Audit/Acceptance/multiplayer/"
        add_file(files,prefix + mp_path.name,mp_path.read_bytes())
        for peer in mp["peers"]:
            relative = peer["acceptance_report"]
            parent = PurePosixPath(relative).parent.as_posix()
            capture_acceptance(files,local_file(mp_path.parent,relative),prefix + (parent + "/" if parent != "." else ""))
        for relative in sorted({r for case in mp["cases"] for r in case.get("evidence",[])} |
                               {r for roll in mp.get("recorded_rolls",[]) for r in roll.get("baseline_evidence",[])}):
            add_file(files,prefix + relative,local_file(mp_path.parent,relative).read_bytes())
    packed_sources = {name: digest(files["ManualDiceRoller/Source/src/" + name]) for name in manifest["sources"]}
    packed_tools = {name: digest(files["ManualDiceRoller/Source/tools/" + name]) for name in manifest["tools"]}
    if packed_sources != manifest["sources"] or packed_tools != manifest["tools"]:
        parser.error("Source/tool bytes changed while capturing package contents")
    for name, expected in {**fixture_evidence["manifest_sha256"], **fixture_evidence["project_input_sha256"]}.items():
        archive_name = "ManualDiceRoller/Source/" + name
        if archive_name not in files or digest(files[archive_name]) != expected:
            parser.error("Fixture source/result/manifest bytes changed while capturing package contents: " + name)
    if args.regressions:
        for name, expected in regressions["sources_after"].items():
            archive_name = "ManualDiceRoller/Source/" + name
            if archive_name not in files or digest(files[archive_name]) != expected:
                parser.error("Test/compiler inputs changed while capturing package contents: " + name)
    binaries = [name for name in files if name.lower().endswith((".dll", ".exe"))]
    if binaries != ["ManualDiceRoller/ManualDiceRoller.dll"]:
        parser.error("An excluded dependency/game/development binary reached the package")
    verified = {}
    for name,data in files.items():
        add_file(verified,name,data)
    checksums = {name: digest(data) for name, data in sorted(files.items())}
    files["ManualDiceRoller/CHECKSUMS.sha256"] = "".join(value + "  " + name + "\n" for name, value in checksums.items()).encode("utf-8")
    output = args.output.resolve()
    if output.suffix.lower() != ".zip":
        parser.error("Package output must be a ZIP file")
    for protected in [root / name for name in ("src","tools","tests","references")] + [build / "ManualDiceRoller"]:
        if output == protected or protected in output.parents or output in protected.parents:
            parser.error("Package output must be separate from runtime/source/tool/test/reference inputs")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=output.parent, prefix=output.name + ".", suffix=".tmp", delete=False) as handle:
        temporary = Path(handle.name)
    try:
        with ZipFile(temporary, "w", compression=ZIP_DEFLATED, compresslevel=9) as archive:
            for name, data in sorted(files.items()):
                entry = ZipInfo(name, (2026, 10, 6, 0, 0, 0))
                entry.compress_type = ZIP_DEFLATED
                entry.create_system = 0
                entry.external_attr = 0
                archive.writestr(entry, data)
        with ZipFile(temporary) as archive:
            if archive.testzip() is not None:
                raise RuntimeError("Final ZIP CRC verification failed")
            for name, value in checksums.items():
                if digest(archive.read(name)) != value:
                    raise RuntimeError("Final ZIP checksum verification failed: " + name)
        os.replace(temporary, output)
    finally:
        if temporary.exists():
            temporary.unlink()
    print(str(args.output.resolve()) + "\nSHA256 " + digest(args.output.read_bytes()) + "\nBytes " + str(args.output.stat().st_size))


if __name__ == "__main__":
    main()
