"""Fetch exact official runtime references and developer parsers; never install in Solasta."""
from pathlib import Path
from urllib.request import urlopen
from zipfile import ZipFile
import hashlib
import os
import tempfile


def atomic_write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix=path.name + ".", suffix=".tmp", delete=False) as handle:
        temporary = Path(handle.name)
        handle.write(data)
    try:
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def main():
    references = Path(__file__).resolve().parent.parent / "references"
    references.mkdir(parents=True, exist_ok=True)
    packages = [
        ("unitymodmanager.0.32.4.nupkg", "a8438e921ca7b0e5805f5a31c196ae65c90f236543885bea0146457d082c9ec9", {
            "lib/net35/UnityModManager.dll": ("UnityModManager.dll", "1387468bc3af41c50fe51859a3bb7af4922891aa8f13a6187e7a348ceaabfd88")}),
        ("lib.harmony.2.3.6.nupkg", "498f99c5ca97cfec0e6cb277d721c242071ea9d9f019312cf9eaf015762de075", {
            "lib/net48/0Harmony.dll": ("0Harmony.dll", "df043f05c6e43602fa8e8f38f52a3a2cf962aab7a65a9d85573ea25f01a63e80"),
            "lib/net8.0/0Harmony.dll": ("net8-harmony/0Harmony.dll", "9514a5c06980b2d2b84e6b6429453eb31b7d904832f14d645e69754e0aaf902e")}),
        ("mono.cecil.0.10.4.nupkg", "3451a4a112a81be0bf76b8dc2bca396acdf0455d80c8961e7903ce1fb35c2a9a", {
            "lib/net35/Mono.Cecil.dll": ("Mono.Cecil.dll", "7ae470288fff4a402899c254d0a76cefef55877f5c54f96e83c797cc5bb6e2f6")}),
        ("system.management.automation.7.4.6.nupkg", "a3ae82271d0cab7758506c89aec0228f3dc08712ce6f17c57b785d6d4f1f77e3", {}),
        ("microsoft.management.infrastructure.runtime.unix.3.0.0.nupkg", "b4f959bad08df9a6e680c33200f5cefe7c1b8222a26eb134d338de0b565425ab", {})]
    for filename, expected, members in packages:
        name, version = (("unitymodmanager", "0.32.4") if filename.startswith("unity") else
                         ("lib.harmony", "2.3.6") if filename.startswith("lib.harmony") else
                         ("mono.cecil", "0.10.4") if filename.startswith("mono.cecil") else
                         ("system.management.automation", "7.4.6") if filename.startswith("system.management") else
                         ("microsoft.management.infrastructure.runtime.unix", "3.0.0"))
        path = references / filename
        if not path.is_file():
            url = "https://api.nuget.org/v3-flatcontainer/{}/{}/{}".format(name, version, filename)
            data = urlopen(url, timeout=30).read()
            if hashlib.sha256(data).hexdigest() != expected:
                raise RuntimeError("Official package checksum differs: " + filename)
            atomic_write(path, data)
        if hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            raise RuntimeError("Existing package checksum differs: " + filename)
        with ZipFile(path) as archive:
            if archive.testzip() is not None:
                raise RuntimeError("Corrupt official dependency package")
            for member, (target_name, checksum) in members.items():
                data = archive.read(member)
                if hashlib.sha256(data).hexdigest() != checksum:
                    raise RuntimeError("Official assembly checksum differs: " + member)
                target = references / target_name
                target.parent.mkdir(parents=True, exist_ok=True)
                atomic_write(target, data)
        print("Verified official " + name + " " + version)


if __name__ == "__main__":
    main()
