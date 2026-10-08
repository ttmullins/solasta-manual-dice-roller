"""Parse all shipped PowerShell tools with checksum-pinned official grammar.

This invokes the managed language parser only. It never executes a script and
does not certify Windows PowerShell 5.1 or live game behavior.
"""
from pathlib import Path
import hashlib
import json
import os
import subprocess
from zipfile import ZipFile

from Build import compile_cs
from TestEnvironment import resolve

PARSER_PACKAGE = ("system.management.automation.7.4.6.nupkg","a3ae82271d0cab7758506c89aec0228f3dc08712ce6f17c57b785d6d4f1f77e3")
MMI_PACKAGE = ("microsoft.management.infrastructure.runtime.unix.3.0.0.nupkg","b4f959bad08df9a6e680c33200f5cefe7c1b8222a26eb134d338de0b565425ab")


def extract(references, package, member, expected, target):
    path = references / package[0]
    if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != package[1]:
        raise ValueError("Official parser dependency missing/different; run tools/FetchDependencies.py: " + package[0])
    with ZipFile(path) as archive:
        data = archive.read(member)
    if hashlib.sha256(data).hexdigest() != expected:
        raise ValueError("Official parser assembly differs: " + member)
    target.write_bytes(data)


def main():
    root = Path(__file__).resolve().parents[1]
    env = resolve(root)
    output = root / "build/powershell-grammar"
    output.mkdir(parents=True,exist_ok=True)
    platform = "win" if os.name == "nt" else "unix"
    parser = output / "System.Management.Automation.dll"
    parser_sha = ("4ee93931215d7f6fc8cdd9df0f16bd0e0c3fa5233ad7bf52a8935fea133b6006" if os.name == "nt" else
                  "5eab90b3fea1183b40aecdd7b5a8f1cc8a86342af966b6057ea97d477aec7e94")
    extract(env.references,PARSER_PACKAGE,"runtimes/" + platform + "/lib/net8.0/System.Management.Automation.dll",parser_sha,parser)
    # Parsing does not call the native CIM/OS operations in this managed
    # dependency. This runtime assembly supplies parser type metadata only.
    extract(env.references,MMI_PACKAGE,"runtimes/unix/lib/netstandard1.6/Microsoft.Management.Infrastructure.dll",
            "e600e2473e2404859789c76529671c5ea12b66dca552cb3f43e960bda13b9434",output / "Microsoft.Management.Infrastructure.dll")
    executable = output / "AuditPowerShellSyntax.dll"
    compile_cs(env.dotnet,env.compiler,[root / "tools/AuditPowerShellSyntax.cs"],env.net_refs + [parser],executable,tool=True)
    scripts = sorted((root / "tools").glob("*.ps1"))
    result = subprocess.run([str(env.dotnet),str(executable)] + [str(path) for path in scripts],text=True,capture_output=True)
    print(result.stdout + result.stderr,end="")
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
