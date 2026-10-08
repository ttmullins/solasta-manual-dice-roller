"""Run effect descriptor parity against the exact pristine native game methods.

Only source, documentation and result manifests belong in the source package.
Generated binaries and absolute compiler response files stay under build/.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
TEST = Path(__file__).resolve().parent
WORKSPACE = ROOT.parent.parent
sys.path.insert(0, str(ROOT / "tools"))
from TestEnvironment import resolve, REFERENCE_SHA256
GAME_SHA256 = "a87e45cbd187e42e9d64a610850b39edba4896168b14d71ca71c5d32cf9e96ee"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def execute(command, log, *, cwd, env=None):
    result = subprocess.run([str(part) for part in command], cwd=cwd, env=env, text=True, capture_output=True)
    log.write_text(result.stdout + result.stderr, encoding="utf-8")
    print(result.stdout + result.stderr, end="")
    result.check_returncode()


def main():
    bundled_dotnet = WORKSPACE / "work/tooling/dotnet/dotnet"
    default_dotnet = bundled_dotnet if bundled_dotnet.is_file() else Path(shutil.which("dotnet") or "dotnet")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--managed", type=Path, help="Directory of the supplied pristine game's managed DLLs")
    parser.add_argument("--dotnet", type=Path, help=".NET 8 dotnet executable")
    parser.add_argument("--sdk-root", type=Path, help=".NET 8 SDK directory, for example .../sdk/8.0.425")
    parser.add_argument("--harmony", type=Path, help="Official Harmony 2.3.6.0 net8.0 test-host DLL")
    parser.add_argument("--output", type=Path, default=TEST / "build", help="Generated executable and compiler logs")
    args = parser.parse_args()
    environment = resolve(ROOT, managed=args.managed, dotnet=args.dotnet, sdk_root=args.sdk_root,
                          require_cecil=False)
    managed, dotnet, sdk = environment.managed, environment.dotnet, environment.sdk_root
    harmony = (args.harmony or environment.references / "net8-harmony/0Harmony.dll").expanduser().resolve()
    output = args.output.expanduser().resolve()
    game = managed / "Assembly-CSharp.dll"
    if not game.is_file() or digest(game) != GAME_SHA256:
        parser.error("--managed must contain the exact supplied pristine Assembly-CSharp.dll (SHA-256 " + GAME_SHA256 + ").")
    if not harmony.is_file() or digest(harmony) != REFERENCE_SHA256["net8-harmony/0Harmony.dll"]:
        parser.error("Supply --harmony pointing to the official Harmony 2.3.6.0 net8.0 DLL; external DLLs are not bundled in the source package.")
    for protected in (managed, ROOT / "src", TEST):
        if output == protected or output in protected.parents:
            parser.error("--output must not contain or replace source/input directories.")
    if managed in output.parents or ROOT / "src" in output.parents:
        parser.error("--output must be separate from native inputs and runtime source.")
    output.mkdir(parents=True, exist_ok=True)
    references = environment.net_refs + [p for p in sorted(managed.glob("*.dll")) if not p.name.startswith("System") and p.name not in {"mscorlib.dll", "netstandard.dll"}] + [harmony]
    sources = [TEST / "EffectParity.cs", ROOT / "src/EffectPatches.cs", ROOT / "src/RollText.cs", ROOT / "src/NativeScratchScope.cs", ROOT / "tools/HarmonyFixtureHost.cs"]
    tracked = sources + [TEST / "run.py", ROOT / "tools/TestEnvironment.py"]
    source_bytes = {str(path.relative_to(ROOT)): path.read_bytes() for path in tracked}
    snapshot_hashes = {name: hashlib.sha256(data).hexdigest() for name, data in source_bytes.items()}
    input_paths = {"native_game_sha256": game, "test_harmony_sha256": harmony}
    input_hashes = {name: digest(path) for name, path in input_paths.items()}
    compiled_sources = []
    for source in sources:
        copy = output / "source-snapshot" / source.relative_to(ROOT)
        copy.parent.mkdir(parents=True, exist_ok=True)
        copy.write_bytes(source_bytes[str(source.relative_to(ROOT))])
        compiled_sources.append(copy)

    def live_hashes():
        return {str(path.relative_to(ROOT)): digest(path) for path in tracked}

    def verify():
        if live_hashes() != snapshot_hashes:
            raise RuntimeError("Fixture sources changed after the compiler snapshot; rerun against one unchanged source version.")
        if any(digest(copy) != snapshot_hashes[str(source.relative_to(ROOT))] for source, copy in zip(sources, compiled_sources)):
            raise RuntimeError("A compiler source snapshot changed during the fixture run.")
        if {name: digest(path) for name, path in input_paths.items()} != input_hashes:
            raise RuntimeError("Native game or Harmony input changed during the fixture run.")

    manifest = {"status": "running", "compiled_from_source_snapshot": True, "sources": snapshot_hashes, **input_hashes,
                "coverage": "Native descriptor argument parity and helper lifetime; NativeRollPlan/RuntimeRolls are capture-only adapters, not production ownership tests."}

    def save(status, error=None):
        manifest["status"] = status
        manifest["sources_after"] = live_hashes()
        manifest["inputs_after"] = {name: digest(path) for name, path in input_paths.items()}
        if status == "passed":
            verify()
        if (TEST / "results.txt").is_file():
            manifest["results_sha256"] = digest(TEST / "results.txt")
        if error is not None:
            manifest["error"] = str(error)
        (TEST / "source-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    save("running")
    try:
        executable = output / "EffectParity.dll"
        flags = ["-nologo", "-nostdlib+", "-langversion:7.3", "-target:exe", "-main:Bootstrap", f'-out:"{executable}"']
        flags += [f'-r:"{path}"' for path in references] + [f'"{path}"' for path in compiled_sources]
        response = output / "EffectParity.rsp"
        response.write_text("\n".join(flags) + "\n", encoding="utf-8")
        execute([dotnet, sdk / "Roslyn/bincore/csc.dll", "@" + str(response)], output / "compile.txt", cwd=ROOT)
        verify()
        manifest["sources_after_compile"] = live_hashes()
        executable.with_suffix(".runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {"tfm": "net8.0", "framework": {"name": "Microsoft.NETCore.App", "version": "8.0.0"}}}) + "\n", encoding="utf-8")
        shutil.copyfile(harmony, output / "0Harmony.dll")
        child_env = environment.child_environment()
        child_env["MANUAL_DICE_PARITY_MANAGED"] = str(managed)
        execute([dotnet, executable], TEST / "results.txt", cwd=ROOT, env=child_env)
        verify()
        save("passed")
    except Exception as error:
        save("failed", error)
        raise



if __name__ == "__main__":
    main()
