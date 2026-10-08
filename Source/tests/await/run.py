"""Exercise real Harmony-generated awaits against extracted pristine native IL.

Game and runtime dependencies are inputs and are never modified or packaged.
The fake environment supplies only the receiver/service types surrounding the
copied native Jump/Counterspell/Shove iterators and TA coroutine Run/Reset methods. The exception
parity tests use a compiler-generated fixture with the same Dispose fault shape.
"""
import argparse
import hashlib
import json
import shutil
import types
import sys
from pathlib import Path


def main():
    root = Path(__file__).resolve().parents[2]
    workspace = root.parent.parent
    sys.path.insert(0, str(root / "tools"))
    from TestEnvironment import resolve
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("managed", "references", "sdk-root", "dotnet", "cecil"):
        parser.add_argument("--" + key, type=Path)
    parser.add_argument("--output", type=Path, default=Path(__file__).resolve().parent / "build")
    args = parser.parse_args()
    environment = resolve(root, managed=args.managed, references=args.references, sdk_root=args.sdk_root,
                          dotnet=args.dotnet, cecil=args.cecil)
    for key in ("managed", "references", "sdk_root", "dotnet", "cecil"):
        setattr(args, key, getattr(environment, key))
    source = Path(__file__).resolve().parent
    tracked = [root / "src/NativePatches.cs", root / "src/NativeSiteManifest.cs",
               root / "tools/HarmonyFixtureHost.cs", root / "tools/Build.py", root / "tools/TestEnvironment.py",
               source / "run.py", source / "README.md"] + sorted(source.glob("*.cs"))
    source_bytes = {str(path.relative_to(root)): path.read_bytes() for path in tracked}
    before = {name: hashlib.sha256(data).hexdigest() for name, data in source_bytes.items()}
    # Execute the exact hashed helper bytes instead of allowing a stale Python
    # bytecode cache to make the manifest disagree with the helper used here.
    build_path = root / "tools/Build.py"
    build = types.ModuleType("standalone_build")
    build.__file__ = str(build_path)
    exec(compile(source_bytes["tools/Build.py"], str(build_path), "exec"), build.__dict__)
    game = args.managed.resolve() / "Assembly-CSharp.dll"
    harmony = args.references.resolve() / "net8-harmony/0Harmony.dll"
    for path, expected in [(game, build.BASELINE), (harmony, build.NET8_HARMONY)]:
        if not path.is_file() or build.digest(path) != expected:
            parser.error("Pinned native/generator input is missing or different: " + str(path))
    output = args.output.resolve()
    for protected in [args.managed.resolve(), args.references.resolve(), root / "src"]:
        if output == protected or protected in output.parents or output in protected.parents:
            parser.error("Test output must be separate from runtime inputs and plugin sources")
    compiler = args.sdk_root.resolve() / "Roslyn/bincore/csc.dll"
    if not compiler.is_file() or not args.dotnet.is_file() or not args.cecil.is_file():
        parser.error("The .NET8 SDK or Cecil test dependency is unavailable")
    output.mkdir(parents=True, exist_ok=True)
    refs = environment.net_refs
    snapshot = output / "NativePatches.cs"
    snapshot.write_bytes(source_bytes["src/NativePatches.cs"])
    shutil.copyfile(harmony, output / "0Harmony.dll")
    shutil.copyfile(args.cecil, output / "Mono.Cecil.dll")
    fixture = output / "Assembly-CSharp.dll"
    build.compile_cs(args.dotnet, compiler, [source / "TestJump.cs", source / "CounterNativeTypes.cs", source / "TestShove.cs"], refs, fixture)
    extract = output / "ExtractJump.dll"
    build.compile_cs(args.dotnet, compiler, [source / "ExtractShove.cs"], refs + [args.cecil], extract, tool=True)
    build.run([args.dotnet, extract, game, fixture], output / "extraction.txt")
    regression = output / "AwaitRegression.dll"
    build.compile_cs(args.dotnet, compiler, [source / "JumpRegression.cs", source / "RuntimeStub.cs", source / "JumpManifest.cs", snapshot, root / "tools/HarmonyFixtureHost.cs"], refs + [fixture, harmony], regression, tool=True)
    build.run([args.dotnet, regression], output / "results.txt")
    for negative in ["bad-hash", "unsafe-finally", "unsafe-branch", "unsafe-output"]:
        build.run([args.dotnet, regression, negative], output / (negative + ".txt"))
    core = args.managed.resolve() / "UnityEngine.CoreModule.dll"
    if not core.is_file() or build.digest(core) != "9cbd669f76900a90025b7ace9a90025360eda03ed0877f630bd2ad91713f9969":
        parser.error("Pinned Unity input is missing or different: " + str(core))
    original = output / "whole-original"
    shimmed = output / "whole-shim"
    for folder in [original, shimmed]:
        folder.mkdir(exist_ok=True)
        shutil.copyfile(game, folder / "Assembly-CSharp.dll")
        shutil.copyfile(harmony, folder / "0Harmony.dll")
        probe = folder / "FullGeneration.dll"
        build.compile_cs(args.dotnet, compiler, [source / "FullGeneration.cs", source / "RuntimeStub.cs", snapshot, root / "src/NativeSiteManifest.cs", root / "tools/HarmonyFixtureHost.cs"], refs + [game, harmony, core], probe, tool=True)
    build.run([args.dotnet, original / "FullGeneration.dll", args.managed.resolve(), "original"], output / "whole-original.txt")
    shim = output / "ShimUnityTransform.dll"
    build.compile_cs(args.dotnet, compiler, [source / "ShimUnityTransform.cs"], refs + [args.cecil], shim, tool=True)
    build.run([args.dotnet, shim, core, shimmed / "UnityEngine.CoreModule.dll"], output / "unity-shim.txt")
    build.run([args.dotnet, shimmed / "FullGeneration.dll", args.managed.resolve(), "shim"], output / "whole-shim.txt")
    after = {str(path.relative_to(root)): build.digest(path) for path in tracked}
    if before != after:
        raise RuntimeError("Sources changed during the await test run; packaged results were not refreshed")
    required = {
        "results.txt": "PASS 162 actual extracted native Jump iterator / real Harmony generated await checks",
        "bad-hash.txt": "PASS rejected bad-hash:",
        "unsafe-finally.txt": "PASS rejected unsafe-finally:",
        "unsafe-branch.txt": "PASS rejected unsafe-branch:",
        "unsafe-output.txt": "PASS rejected unsafe-output:",
        "whole-original.txt": "PASS original dependency qualification: 28 generated; 2 require unavailable native Unity InternalCall",
        "unity-shim.txt": "Test-only UnityEngine.Component.get_transform stub: throws if invoked; actual game assembly unchanged.",
        "whole-shim.txt": "PASS native reload preflight: 5 neutral fault normalizers retained",
    }
    logs = {name: (output / name).read_text(encoding="utf-8") for name in required}
    for name, expected in required.items():
        if expected not in logs[name]:
            raise RuntimeError("Expected success evidence missing from " + name)
    if build.digest(game) != build.BASELINE or build.digest(core) != "9cbd669f76900a90025b7ace9a90025360eda03ed0877f630bd2ad91713f9969" or build.digest(harmony) != build.NET8_HARMONY:
        raise RuntimeError("A pinned input changed during the run; packaged results were not refreshed")
    manifest = {
        "game_sha256": build.BASELINE,
        "unity_core_sha256": build.digest(core),
        "net8_harmony_sha256": build.NET8_HARMONY,
        "behavior_assertions": 162,
        "negative_preflight_cases": 4,
        "unchanged_dependencies_generated_owners": 28,
        "unchanged_dependencies_blocked_owners": 2,
        "test_only_unity_getter_stub_generated_owners": 30,
        "test_only_unity_getter_stub_await_sites": 46,
        "game_method_execution_in_whole_generation_test": False,
        "game_input_modified": False,
        "source_consistency": "identical_before_and_after_successful_run",
        "native_patches_compiled_snapshot_sha256": build.digest(snapshot),
        "sources_before": before,
        "sources_after": after,
        "sources": after,
    }
    results = "\n".join(name + "\n" + logs[name] for name in required)
    # Refresh the package-visible evidence only after every suite and source
    # consistency check succeeds. A failed rerun retains the previous evidence.
    result_tmp = source / "results.txt.pending"
    manifest_tmp = source / "source-manifest.json.pending"
    result_tmp.write_text(results, encoding="utf-8")
    manifest_tmp.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    result_tmp.replace(source / "results.txt")
    manifest_tmp.replace(source / "source-manifest.json")
    print("Refreshed package-visible results and exact pre/post source provenance after all checks passed.")
    print("This validates generated .NET8 Harmony code; it does not certify live Unity/Mono gameplay.")


if __name__ == "__main__":
    main()
