"""Run the standalone rest collector against actual pristine native rest methods."""
from pathlib import Path
import hashlib
import argparse
import sys
import json
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[2]
WORKSPACE = ROOT.parent.parent
TEST = Path(__file__).resolve().parent
BUILD = TEST / "build"
BUILD.mkdir(exist_ok=True)
sys.path.insert(0, str(ROOT / "tools"))
from TestEnvironment import resolve
parser = argparse.ArgumentParser(description=__doc__)
for key in ("dotnet", "sdk-root", "managed", "references"):
    parser.add_argument("--" + key, type=Path)
args = parser.parse_args()
environment = resolve(ROOT, dotnet=args.dotnet, sdk_root=args.sdk_root, managed=args.managed,
                      references=args.references, require_cecil=False)
DOTNET, SDK, REFS = environment.dotnet, environment.sdk_root, environment.net_ref_root
MANAGED = environment.managed
HARMONY = environment.references / "net8-harmony/0Harmony.dll"
references = sorted(REFS.glob("*.dll")) + [p for p in sorted(MANAGED.glob("*.dll")) if not p.name.startswith("System") and p.name not in {"mscorlib.dll", "netstandard.dll"}] + [HARMONY]
sources = [TEST / "Environment.cs", TEST / "RestNativeTests.cs", TEST / "Bootstrap.cs", ROOT / "src/RestPatches.cs", ROOT / "src/RuntimeRolls.cs", ROOT / "src/RollText.cs", ROOT / "src/RollBroker.cs", ROOT / "src/NativeScratchScope.cs", ROOT / "tools/HarmonyFixtureHost.cs"]
sources += [ROOT / "tests/common/OfflineNetworkHost.cs", ROOT / "tests/common/OfflineBardicHost.cs", ROOT / "tests/common/OfflineShoveHost.cs"]
def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def label(path):
    return str(path.relative_to(ROOT))


tracked_sources = sources + [Path(__file__).resolve(), ROOT / "tools/TestEnvironment.py"]
source_bytes = {label(path): path.read_bytes() for path in tracked_sources}
snapshot_hashes = {name: hashlib.sha256(data).hexdigest() for name, data in source_bytes.items()}
input_paths = {"native_game_sha256": MANAGED / "Assembly-CSharp.dll", "test_harmony_sha256": HARMONY}
input_hashes = {name: sha256(path) for name, path in input_paths.items()}
snapshot = BUILD / "source-snapshot"
compiled_sources = []
for path in sources:
    copy = snapshot / path.relative_to(ROOT)
    copy.parent.mkdir(parents=True, exist_ok=True)
    copy.write_bytes(source_bytes[label(path)])
    compiled_sources.append(copy)


def source_hashes():
    return {label(path): sha256(path) for path in tracked_sources}


def verify_evidence():
    if source_hashes() != snapshot_hashes:
        raise RuntimeError("Fixture sources changed after the compiler snapshot; rerun against one unchanged source version.")
    for source, copy in zip(sources, compiled_sources):
        if sha256(copy) != snapshot_hashes[label(source)]:
            raise RuntimeError("A compiler source snapshot changed during the fixture run.")
    if {name: sha256(path) for name, path in input_paths.items()} != input_hashes:
        raise RuntimeError("Native game or Harmony input changed during the fixture run.")


evidence = {
    "status": "running",
    "compiled_from_source_snapshot": True,
    "sources": snapshot_hashes,
    **input_hashes,
}


def write_evidence(status, error=None):
    observed_sources = source_hashes()
    observed_inputs = {name: sha256(path) for name, path in input_paths.items()}
    if status == "passed" and (observed_sources != snapshot_hashes or observed_inputs != input_hashes):
        raise RuntimeError("Recorded post-run evidence differs from the compiler snapshot or native inputs.")
    evidence["status"] = status
    evidence["sources_after"] = observed_sources
    evidence["inputs_after"] = observed_inputs
    if (TEST / "results.txt").is_file():
        evidence["results_sha256"] = sha256(TEST / "results.txt")
    if error is not None:
        evidence["error"] = str(error)
    (TEST / "source-manifest.json").write_text(json.dumps(evidence, indent=2) + "\n")


write_evidence("running")
try:
    output = BUILD / "RestNativeTests.dll"
    flags = ["-nologo", "-nostdlib+", "-langversion:7.3", "-target:exe", "-main:RestNativeBootstrap", f'-out:"{output}"']
    flags += [f'-r:"{p}"' for p in references] + [f'"{p}"' for p in compiled_sources]
    response = BUILD / "RestNativeTests.rsp"
    response.write_text("\n".join(flags)+"\n")
    compile_result = subprocess.run([str(DOTNET), str(SDK / "Roslyn/bincore/csc.dll"), "@"+str(response)], cwd=ROOT, env=environment.child_environment(), text=True, capture_output=True)
    (BUILD / "compile.txt").write_text(compile_result.stdout+compile_result.stderr)
    compile_result.check_returncode()
    verify_evidence()
    evidence["sources_after_compile"] = source_hashes()
    output.with_suffix(".runtimeconfig.json").write_text(json.dumps({"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}))
    shutil.copyfile(HARMONY, BUILD / "0Harmony.dll")
    result = subprocess.run([str(DOTNET), str(output)], cwd=ROOT, env=environment.child_environment(), text=True, capture_output=True)
    (TEST / "results.txt").write_text(result.stdout+result.stderr)
    print(result.stdout+result.stderr, end="")
    result.check_returncode()
    verify_evidence()
    write_evidence("passed")
except Exception as error:
    write_evidence("failed", error)
    raise
