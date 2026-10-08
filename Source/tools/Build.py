"""Compile a genuine UMM plugin against pinned pristine Solasta Mono assemblies.

The supplied game/reference files are read-only inputs. No game-DLL rebasing,
replacement, donor import, or GoDice integration occurs in this build.
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
from pathlib import Path

BASELINE = "a87e45cbd187e42e9d64a610850b39edba4896168b14d71ca71c5d32cf9e96ee"
UMM = "1387468bc3af41c50fe51859a3bb7af4922891aa8f13a6187e7a348ceaabfd88"
HARMONY = "df043f05c6e43602fa8e8f38f52a3a2cf962aab7a65a9d85573ea25f01a63e80"
NET8_HARMONY = "9514a5c06980b2d2b84e6b6429453eb31b7d904832f14d645e69754e0aaf902e"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def tool_inputs(root):
    return {p.name: p.read_bytes() for p in sorted((root / "tools").iterdir())
            if p.is_file() and p.suffix in (".cs", ".py", ".ps1", ".json", ".md") and not p.name.endswith("-reference.cs")}


def run(command, log, verbose=True):
    result = subprocess.run([str(x) for x in command], text=True, capture_output=True)
    log.write_text(result.stdout + result.stderr, encoding="utf-8")
    if verbose or result.returncode:
        print(result.stdout + result.stderr, end="")
    result.check_returncode()


def compile_cs(dotnet, compiler, sources, refs, output, *, tool=False):
    flags = ["-nologo", "-nostdlib+", "-optimize+", "-deterministic+", "-langversion:7.3",
             "-target:" + ("exe" if tool else "library"), '-out:"' + str(output) + '"']
    source_root = Path(os.path.commonpath([str(p.resolve().parent) for p in sources]))
    flags.append('-pathmap:"' + str(source_root) + '=/source"')
    flags += ['-r:"' + str(p.resolve()) + '"' for p in refs]
    flags += ['"' + str(p.resolve()) + '"' for p in sources]
    response = output.with_suffix(".rsp")
    response.write_text("\n".join(flags), encoding="utf-8")
    run([dotnet, compiler, "@" + str(response)], output.with_suffix(".compile.txt"))
    if tool:
        output.with_suffix(".runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {
            "tfm": "net8.0", "framework": {"name": "Microsoft.NETCore.App", "version": "8.0.0"}}}), encoding="utf-8")


def main():
    from TestEnvironment import resolve
    root = Path(__file__).resolve().parent.parent
    workspace = root.parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--managed", type=Path)
    parser.add_argument("--references", type=Path)
    parser.add_argument("--sdk-root", type=Path)
    parser.add_argument("--dotnet", type=Path)
    parser.add_argument("--cecil", type=Path)
    parser.add_argument("--source", type=Path, default=root / "src")
    parser.add_argument("--output", type=Path, default=root / "build")
    parser.add_argument("--smoke-only", action="store_true", help="Only compile the real-dependency ABI smoke probe, never a release stub")
    args = parser.parse_args()
    try:
        environment = resolve(root, managed=args.managed, references=args.references, sdk_root=args.sdk_root,
                              dotnet=args.dotnet, cecil=args.cecil)
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        parser.error(str(error))
    for key in ("managed", "references", "sdk_root", "dotnet", "cecil"):
        setattr(args, key, getattr(environment, key))
    managed, references, source, output = [x.resolve() for x in (args.managed, args.references, args.source, args.output)]
    for path, expected in ((managed / "Assembly-CSharp.dll", BASELINE), (references / "UnityModManager.dll", UMM), (references / "0Harmony.dll", HARMONY)):
        if not path.is_file() or digest(path) != expected:
            parser.error("Pinned input is missing or different: " + str(path))
    managed_hashes = {p.name: digest(p) for p in sorted(managed.glob("*.dll"))}
    environment_before = environment.report()
    for protected in (managed, references, source):
        if output == protected or protected in output.parents or output in protected.parents:
            parser.error("Build output must be separate from managed inputs, references and sources")
    compiler = args.sdk_root.resolve() / "Roslyn/bincore/csc.dll"
    output.mkdir(parents=True, exist_ok=True)
    tool_bytes = tool_inputs(root)
    tool_snapshot = output / "tool-source-snapshot"
    tool_snapshot.mkdir(exist_ok=True)
    for name, data in tool_bytes.items():
        (tool_snapshot / name).write_bytes(data)
    tool_hashes = {name: hashlib.sha256(data).hexdigest() for name, data in tool_bytes.items()}
    tool_output = output / "Tools"
    tool_output.mkdir(exist_ok=True)
    net_refs = environment.net_refs
    shutil.copyfile(args.cecil, tool_output / "Mono.Cecil.dll")
    dependency_tool = tool_output / "InspectDependencies.dll"
    compile_cs(args.dotnet, compiler, [tool_snapshot / "InspectDependencies.cs"], net_refs + [args.cecil], dependency_tool, tool=True)
    from TestEnvironment import NETWORK_SHA256
    run([args.dotnet, dependency_tool, managed / "Assembly-CSharp.dll", managed / "mscorlib.dll", references / "UnityModManager.dll", references / "0Harmony.dll"] +
        [managed / name for name in NETWORK_SHA256], output / "dependencies.txt", verbose=False)
    network_abi = tool_output / "AuditNetworkAbi.dll"
    compile_cs(args.dotnet, compiler, [tool_snapshot / "AuditNetworkAbi.cs"], net_refs + [args.cecil], network_abi, tool=True)
    run([args.dotnet, network_abi, managed], output / "network-abi.txt")
    # All game framework references are pristine; the SDK framework is used
    # only by audit tooling, never by the shipped plugin compilation.
    game_refs = sorted(managed.glob("*.dll"))
    runtime_refs = game_refs + [references / "UnityModManager.dll", references / "0Harmony.dll"]
    smoke = tool_output / "BuildSmoke.dll"
    compile_cs(args.dotnet, compiler, [tool_snapshot / "BuildSmoke.cs"], runtime_refs, smoke)
    net_harmony = references / "net8-harmony/0Harmony.dll"
    if not net_harmony.is_file() or digest(net_harmony) != NET8_HARMONY:
        parser.error("The pinned .NET8 Harmony generator-test assembly is unavailable")
    shutil.copyfile(net_harmony, tool_output / "0Harmony.dll")
    generator = tool_output / "HarmonyRuntimeProbe.dll"
    compile_cs(args.dotnet, compiler, [tool_snapshot / "HarmonyRuntimeProbe.cs", tool_snapshot / "HarmonyFixtureHost.cs"], net_refs + [net_harmony], generator, tool=True)
    run([args.dotnet, generator], output / "harmony-generator-probe.txt")
    if args.smoke_only:
        print("Compiled genuine UMM/Harmony ABI smoke probe; no plugin package created.")
        return
    sources = sorted(source.rglob("*.cs"))
    if not sources:
        parser.error("No plugin sources found; no stub will be packaged")
    # Compile an immutable captured source set. A worker/editor changing a
    # file during compilation must not make the manifest describe different
    # bytes from those Roslyn actually compiled.
    snapshot = output / "source-snapshot"
    source_bytes = {str(p.relative_to(source)): p.read_bytes() for p in sources}
    compiled_sources = []
    for relative, data in source_bytes.items():
        captured = snapshot / relative
        captured.parent.mkdir(parents=True, exist_ok=True)
        captured.write_bytes(data)
        compiled_sources.append(captured)
    source_hashes = {relative: hashlib.sha256(data).hexdigest() for relative, data in source_bytes.items()}
    plugin_dir = output / "ManualDiceRoller"
    plugin_dir.mkdir(exist_ok=True)
    plugin = plugin_dir / "ManualDiceRoller.dll"
    compile_cs(args.dotnet, compiler, compiled_sources, runtime_refs, plugin)
    audit = tool_output / "AuditPlugin.dll"
    compile_cs(args.dotnet, compiler, [tool_snapshot / "AuditPlugin.cs"], net_refs + [args.cecil], audit, tool=True)
    run([args.dotnet, audit, plugin, managed, references], output / "assembly-audit.txt")
    bindings = tool_output / "AuditHarmonyBindings.dll"
    compile_cs(args.dotnet, compiler, [tool_snapshot / "AuditHarmonyBindings.cs"], net_refs + [args.cecil], bindings, tool=True)
    run([args.dotnet, bindings, plugin, managed, references, snapshot], output / "harmony-bindings.txt", verbose=False)
    for negative in ("name", "type"):
        run([args.dotnet, bindings, plugin, managed, references, snapshot, "--negative-" + negative], output / ("harmony-bindings-negative-" + negative + ".txt"), verbose=False)
    native_read = tool_output / "AuditHarmonyRuntime.dll"
    compile_cs(args.dotnet, compiler, [tool_snapshot / "AuditHarmonyRuntime.cs"], net_refs + [net_harmony], native_read, tool=True)
    run([args.dotnet, native_read, managed, references, output / "harmony-bindings.txt", snapshot / "NativeSavePlanner.cs", plugin], output / "harmony-native-read.txt", verbose=False)
    if {str(p.relative_to(source)): digest(p) for p in sorted(source.rglob("*.cs"))} != source_hashes:
        parser.error("Runtime sources changed during the audited build; rebuild the frozen source set")
    if {name: hashlib.sha256(data).hexdigest() for name, data in tool_inputs(root).items()} != tool_hashes:
        parser.error("Build/audit/tool sources changed during compilation; rebuild the frozen tool set")
    if managed_hashes != {p.name: digest(p) for p in sorted(managed.glob("*.dll"))} or environment_before != environment.report():
        parser.error("Managed/tool/dependency inputs changed during compilation; rebuild against unchanged inputs")
    info = source / "Info.json"
    if not info.is_file():
        info = source / "info.template.json"
    if info.is_file():
        metadata = json.loads(info.read_text(encoding="utf-8-sig"))
    else:
        metadata = {"Id": "ManualDiceRoller", "DisplayName": "Manual Dice Roller", "Version": "0.8.4.2",
                    "ManagerVersion": "0.32.4", "AssemblyName": "ManualDiceRoller.dll", "EntryMethod": "ManualDiceRoller.Plugin.Load"}
    if metadata.get("AssemblyName") != plugin.name or metadata.get("EntryMethod") != "ManualDiceRoller.Plugin.Load" or metadata.get("ManagerVersion") != "0.32.4":
        parser.error("Info.json must declare the actual pinned standalone assembly, loader and entry point")
    (plugin_dir / "Info.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    report = {"game_sha256": BASELINE, "plugin_sha256": digest(plugin), "umm_sha256": UMM, "harmony_sha256": HARMONY,
              "info_sha256": digest(plugin_dir / "Info.json"), "metadata": metadata,
              "tools": tool_hashes,
              "managed_reference_count": len(game_refs), "target": "supplied Unity/Mono .NET Framework profile",
              "developer_environment": environment_before, "live_windows_mono_qualified": False, "live_multiplayer_qualified": False,
              "sources": source_hashes, "managed_references": managed_hashes,
              "audits": {name: digest(output / name) for name in
                         ("dependencies.txt", "network-abi.txt", "harmony-generator-probe.txt", "assembly-audit.txt", "harmony-bindings.txt",
                          "harmony-bindings-negative-name.txt", "harmony-bindings-negative-type.txt", "harmony-native-read.txt")},
              "runtime_dependencies_bundled": False, "game_dll_bundled": False, "godice_included": False}
    (output / "build-manifest.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("Built " + str(plugin) + "; game inputs unchanged.")


if __name__ == "__main__":
    main()
