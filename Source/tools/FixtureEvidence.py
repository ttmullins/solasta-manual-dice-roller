"""Check shipped fixture evidence against its recorded exact source inputs.

This validates provenance, not the truth of simulated/live gameplay claims.
Legacy manifests retain their original declared scope; no unused inputs are
invented to make them look like full-runtime execution.
"""
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re

from TestEnvironment import CECIL_SHA256, GAME_SHA256, NETWORK_SHA256, REFERENCE_SHA256, sha256

FIXTURE_MANIFESTS = {
    "tests/" + suite + "/source-manifest.json" for suite in (
        "ability-native", "await", "await/integration", "coverage", "effects-native",
        "effects-parity", "native", "network-descriptor", "network-native",
        "network-native/runtime", "network-protocol", "network-transport", "network-end-to-end",
        "plugin-lifecycle", "rest-native", "save", "shove-native", "sync-sites",
        "ui-input-boundary", "ui-render")
}
SOURCE_MAPS = {"production_sources", "fixture_sources", "sources", "sources_before",
               "sources_after", "sources_after_compile", "boundaries"}
SOURCE_SUFFIXES = {".cs", ".csproj", ".py", ".ps1", ".md", ".json"}
HASH = re.compile(r"[0-9a-f]{64}\Z")


def digest(data):
    return hashlib.sha256(data).hexdigest()


def _object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON identity: " + key)
        result[key] = value
    return result


def _local(project, base, relative):
    if (not isinstance(relative, str) or "\\" in relative or
            any(part in ("", ".", "..") for part in relative.split("/")) or
            PurePosixPath(relative).is_absolute() or ":" in relative):
        raise ValueError("Noncanonical or escaping fixture input: " + str(relative))
    path = (base / relative).resolve()
    if project not in path.parents or not path.is_file():
        raise ValueError("Missing or escaping fixture input: " + relative)
    return path


def _generated_text(text):
    # Match the runners' Path.write_text: universal newline input, host newline
    # output. The captured class itself remains a declared fixture boundary.
    return text.replace("\n", os.linesep).encode("utf-8")


def inspect(project, managed_references=None):
    project = Path(project).resolve()
    manifests = {p.relative_to(project).as_posix(): p
                 for p in sorted((project / "tests").rglob("source-manifest.json"))
                 if not any(part in {"build", "bin", "obj", "__pycache__"}
                            for part in p.relative_to(project).parts)}
    errors = ["Required fixture manifest missing: " + name
              for name in sorted(FIXTURE_MANIFESTS - manifests.keys())]
    manifest_hashes = {}
    file_hashes = {}
    assertions = 0
    for name, manifest in manifests.items():
        local_assertions = 0

        def check_file(path, expected, label):
            nonlocal assertions, local_assertions
            if not isinstance(expected, str) or not HASH.fullmatch(expected):
                raise ValueError("Malformed fixture input hash: " + label)
            actual = sha256(path)
            file_hashes[path.relative_to(project).as_posix()] = actual
            assertions += 1
            local_assertions += 1
            if actual != expected:
                errors.append(name + ": stale fixture input hash: " + label)

        try:
            data = json.loads(manifest.read_text(encoding="utf-8-sig"), object_pairs_hook=_object)
            if not isinstance(data, dict):
                raise ValueError("Fixture manifest must be a JSON object")
            manifest_hashes[name] = sha256(manifest)
            if "status" in data and data["status"] not in ("pass", "passed"):
                errors.append(name + ": fixture status did not pass")
            if name == "tests/save/source-manifest.json" and (
                    data.get("status") != "passed" or not isinstance(data.get("sources"), dict)):
                errors.append(name + ": successful save fixture source snapshot/status is required")

            def walk(value, context=""):
                if not isinstance(value, dict):
                    return
                for key, child in value.items():
                    if isinstance(child, dict):
                        walk(child, key)
                    elif (key.startswith(("src/", "tools/", "tests/")) or
                          context in SOURCE_MAPS and Path(key).suffix.lower() in SOURCE_SUFFIXES):
                        base = manifest.parent if context == "boundaries" and "/" not in key else project
                        check_file(_local(project, base, key), child, key)
                    elif key in ("game_sha256", "pristine_game_sha256", "native_game_sha256"):
                        if child != GAME_SHA256:
                            errors.append(name + ": fixture identifies a different native game")
                    elif key in ("net8_harmony_sha256", "test_harmony_sha256", "harmony_sha256"):
                        if child != REFERENCE_SHA256["net8-harmony/0Harmony.dll"]:
                            errors.append(name + ": fixture identifies a different development Harmony")
                    elif key == "cecil_sha256" and child != CECIL_SHA256:
                        errors.append(name + ": fixture identifies a different native IL reader")
                    elif key == "unity_core_sha256" and managed_references is not None:
                        if child != managed_references.get("UnityEngine.CoreModule.dll"):
                            errors.append(name + ": fixture identifies a different Unity core input")
                    elif context in ("inputs_before", "inputs_after") and key.startswith("managed/") and managed_references is not None:
                        if child != managed_references.get(key.removeprefix("managed/")):
                            errors.append(name + ": stale fixture managed dependency hash: " + key)
                    elif context in ("inputs_before", "inputs_after") and key.startswith("dependency/"):
                        expected = {"Mono.Cecil.dll": CECIL_SHA256, **REFERENCE_SHA256}.get(key.removeprefix("dependency/"))
                        if expected is None or child != expected:
                            errors.append(name + ": stale fixture compiler dependency hash: " + key)
                    elif context == "photon_assembly_hashes":
                        expected = NETWORK_SHA256.get(key, (managed_references or {}).get(key))
                        if expected is not None and child != expected:
                            errors.append(name + ": fixture identifies a different native dependency: " + key)

            walk(data)
            for key, relative in (("production_plugin_sha256", "src/Plugin.cs"),
                                  ("native_patches_compiled_snapshot_sha256", "src/NativePatches.cs")):
                if key in data:
                    check_file(_local(project, project, relative), data[key], key)
            captures = {
                "captured_plugin_class_sha256": lambda source: source[:source.index("    // These guards leave Unity")] + "}\n",
                "captured_input_guards_sha256": lambda source:
                    "using System;\nusing System.Collections.Generic;\nusing System.Linq;\n"
                    "using System.Reflection;\nusing HarmonyLib;\nusing UnityEngine;\n"
                    "using UnityEngine.EventSystems;\nnamespace ManualDiceRoller\n{\n" +
                    source[source.index("    internal static class InputGuards"):],
                "generated_native_invocation_sha256": lambda source:
                    source[:source.index("    public sealed class NativeRollPlan")] + "}\n"
            }
            for key, capture in captures.items():
                if key not in data:
                    continue
                relative = "src/RuntimeRolls.cs" if key == "generated_native_invocation_sha256" else "src/Plugin.cs"
                source = _local(project, project, relative).read_text(encoding="utf-8")
                expected = data[key]
                if not isinstance(expected, str) or not HASH.fullmatch(expected) or digest(_generated_text(capture(source))) != expected:
                    errors.append(name + ": stale captured source hash: " + key)
                assertions += 1
            for key, filename in (("results_sha256", "results.txt"), ("extracted_methods_sha256", "extracted-methods.txt")):
                if key in data:
                    check_file(_local(project, manifest.parent, filename), data[key], filename)
            for key in ("sources", "production_sources", "fixture_sources"):
                if key in data and (not isinstance(data[key], dict) or not data[key]):
                    errors.append(name + ": fixture source map is empty or malformed: " + key)
            for before, after in (("sources_before", "sources_after"), ("inputs_before", "inputs_after")):
                if before in data and after in data and data[before] != data[after]:
                    errors.append(name + ": fixture input bytes changed during execution: " + before)
            if not local_assertions:
                errors.append(name + ": fixture contains no verifiable project source input hashes")
        except (ValueError, OSError, TypeError, AttributeError, KeyError) as error:
            errors.append(name + ": invalid fixture evidence: " + str(error))
    return {"schema_version": 1, "status": "fail" if errors else "pass",
            "manifest_sha256": manifest_hashes, "project_input_sha256": file_hashes,
            "tracked_hash_assertions": assertions, "errors": errors}
