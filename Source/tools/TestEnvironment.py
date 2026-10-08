"""Resolve developer-tool inputs consistently without changing game files.

Runners may pass their command-line values to resolve(). Explicit arguments
override MANUALROLL_TEST_* variables, which override discovered defaults.
This resolver locates .NET 8 tools; it makes no claim about live Unity/Mono.
"""
import hashlib
import os
import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path

GAME_SHA256 = "a87e45cbd187e42e9d64a610850b39edba4896168b14d71ca71c5d32cf9e96ee"
CECIL_SHA256 = "7ae470288fff4a402899c254d0a76cefef55877f5c54f96e83c797cc5bb6e2f6"
NETWORK_SHA256 = {
    "PhotonUnityNetworking.dll": "f1efa0cebf758bd79d43e202757d39b00e27384c65084ed14a9697af8bc3e32b",
    "PhotonRealtime.dll": "1cb825bb0f955fef65a29b2981797214d08fa2137b3738c4924b709b7d28a29d",
    "Photon3Unity3D.dll": "5619e0aec897562522ce9adc87b5b80e3e5e9b1b137cbae866f8812e874e7fbe",
}
REFERENCE_SHA256 = {
    "UnityModManager.dll": "1387468bc3af41c50fe51859a3bb7af4922891aa8f13a6187e7a348ceaabfd88",
    "0Harmony.dll": "df043f05c6e43602fa8e8f38f52a3a2cf962aab7a65a9d85573ea25f01a63e80",
    "net8-harmony/0Harmony.dll": "9514a5c06980b2d2b84e6b6429453eb31b7d904832f14d645e69754e0aaf902e",
}


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def version_key(value):
    # SDK prereleases are accepted only after a stable major8 SDK exists;
    # numeric comparison avoids lexicographic 8.0.9 > 8.0.31 mistakes.
    version = str(value).split("-", 1)[0]
    return tuple(int(part) for part in version.split("."))


def chosen(explicit, variable, fallback=None):
    value = explicit if explicit is not None else os.environ.get(variable)
    return Path(value if value else fallback).expanduser().resolve() if value or fallback else None


@dataclass(frozen=True)
class Environment:
    project: Path
    dotnet: Path
    sdk_root: Path
    compiler: Path
    net_ref_root: Path
    managed: Path
    references: Path
    cecil: Path

    @property
    def net_refs(self):
        return sorted(self.net_ref_root.glob("*.dll"))

    def child_environment(self):
        result = os.environ.copy()
        for key in ("dotnet", "sdk_root", "managed", "references", "cecil"):
            result["MANUALROLL_TEST_" + key.upper()] = str(getattr(self, key))
        return result

    def report(self):
        result = {key: str(getattr(self, key)) for key in
                  ("dotnet", "sdk_root", "compiler", "net_ref_root", "managed", "references", "cecil")}
        result["inputs"] = {str(path): sha256(path) for path in
                            (self.managed / "Assembly-CSharp.dll", self.references / "UnityModManager.dll",
                             self.references / "0Harmony.dll", self.references / "net8-harmony/0Harmony.dll", self.cecil)
                            if path.is_file()}
        result["tool_host"] = "dotnet8_development_only"
        result["network_inputs"] = {name: sha256(self.managed / name) for name in NETWORK_SHA256
                                    if (self.managed / name).is_file()}
        result["parser_package_inputs"] = {name:sha256(self.references / name) for name in
                                           ("system.management.automation.7.4.6.nupkg","microsoft.management.infrastructure.runtime.unix.3.0.0.nupkg")
                                           if (self.references / name).is_file()}
        return result


def resolve(project, *, dotnet=None, sdk_root=None, managed=None, references=None, cecil=None,
            require_managed=True, require_references=True, require_cecil=True):
    project = Path(project).resolve()
    workspace = project.parent.parent
    local_dotnet = workspace / "work/tooling/dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet")
    fallback_dotnet = local_dotnet if local_dotnet.is_file() else shutil.which("dotnet")
    dotnet = chosen(dotnet, "MANUALROLL_TEST_DOTNET", fallback_dotnet)
    if dotnet is None or not dotnet.is_file():
        raise ValueError("Install a .NET 8 SDK or set MANUALROLL_TEST_DOTNET / --dotnet")
    sdk_root = chosen(sdk_root, "MANUALROLL_TEST_SDK_ROOT")
    if sdk_root is None:
        completed = subprocess.run([str(dotnet), "--list-sdks"], text=True, capture_output=True, check=True)
        sdk_candidates = []
        for line in completed.stdout.splitlines():
            if "[" not in line or "]" not in line:
                continue
            version, directory = line.split("[", 1)
            version = version.strip()
            if version.split(".", 1)[0] == "8" and "-" not in version:
                sdk_candidates.append(Path(directory.rsplit("]", 1)[0]) / version)
        if not sdk_candidates:
            raise ValueError("A stable .NET 8 SDK is required; use MANUALROLL_TEST_SDK_ROOT / --sdk-root")
        sdk_root = max(sdk_candidates, key=lambda path: version_key(path.name)).resolve()
    compiler = sdk_root / "Roslyn/bincore/csc.dll"
    packs = sdk_root.parent.parent / "packs/Microsoft.NETCore.App.Ref"
    candidates = [p / "ref/net8.0" for p in packs.glob("8.*") if (p / "ref/net8.0").is_dir()]
    if not compiler.is_file() or not candidates:
        raise ValueError("The specified SDK is missing Roslyn or its .NET 8 reference pack")
    net_ref_root = max(candidates, key=lambda path: version_key(path.parents[1].name))
    managed = chosen(managed, "MANUALROLL_TEST_MANAGED", workspace / "work/managed-input")
    references = chosen(references, "MANUALROLL_TEST_REFERENCES", project / "references")
    cecil = chosen(cecil, "MANUALROLL_TEST_CECIL", references / "Mono.Cecil.dll")
    if require_managed and (not (managed / "Assembly-CSharp.dll").is_file() or sha256(managed / "Assembly-CSharp.dll") != GAME_SHA256):
        raise ValueError("The exact analyzed pristine Assembly-CSharp.dll is required: " + str(managed))
    if require_managed:
        for name, expected in NETWORK_SHA256.items():
            if not (managed / name).is_file() or sha256(managed / name) != expected:
                raise ValueError("Missing/different pristine Solasta Photon input: " + name)
    if require_references:
        for name, expected in REFERENCE_SHA256.items():
            if not (references / name).is_file() or sha256(references / name) != expected:
                raise ValueError("Missing/different official reference; run tools/FetchDependencies.py: " + name)
    if require_cecil and (not cecil.is_file() or sha256(cecil) != CECIL_SHA256):
        raise ValueError("Pinned Mono.Cecil 0.10.4 net35 is required; run tools/FetchDependencies.py")
    return Environment(project, dotnet, sdk_root, compiler, net_ref_root, managed, references, cecil)
