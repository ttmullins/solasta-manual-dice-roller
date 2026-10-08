#!/usr/bin/env python3
"""Exercise verbatim production InputGuards at deterministic event boundaries."""
from pathlib import Path
import argparse
import hashlib
import json
import subprocess
import sys

here = Path(__file__).resolve().parent
project = here.parents[1]
sys.path.insert(0, str(project / 'tools'))
from TestEnvironment import resolve
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--dotnet', type=Path)
parser.add_argument('--sdk-root', type=Path)
args = parser.parse_args()
try:
    environment = resolve(project, dotnet=args.dotnet, sdk_root=args.sdk_root,
                          require_managed=False, require_references=False, require_cecil=False)
except (ValueError, OSError, subprocess.SubprocessError) as error:
    parser.error(str(error))
output = here / 'build'
output.mkdir(exist_ok=True)
production = project / 'src/Plugin.cs'
source = production.read_text()
begin = source.index('    internal static class InputGuards')
captured = output / 'InputGuards.cs'
captured.write_text('using System;\nusing System.Collections.Generic;\nusing System.Linq;\nusing System.Reflection;\nusing HarmonyLib;\nusing UnityEngine;\nusing UnityEngine.EventSystems;\nnamespace ManualDiceRoller\n{\n' + source[begin:])
sources = [captured, here / 'BoundaryStubs.cs', here / 'InputBoundaryTests.cs']
(here / 'source-manifest.json').write_text(json.dumps({'production_plugin_sha256': hashlib.sha256(production.read_bytes()).hexdigest(), 'captured_input_guards_sha256': hashlib.sha256(captured.read_bytes()).hexdigest(), 'boundaries': {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sources[1:]}}, indent=2) + '\n')
response = output / 'InputBoundaryTests.rsp'
response.write_text('\n'.join(['-nologo', '-nostdlib+', '-langversion:7.3', '-target:exe', '-out:"' + str(output / 'InputBoundaryTests.dll') + '"'] + ['-r:"' + str(p) + '"' for p in environment.net_refs] + ['"' + str(p) + '"' for p in sources]))
result = subprocess.run([str(environment.dotnet), str(environment.compiler), '@' + str(response)], text=True, capture_output=True)
(output / 'compile.txt').write_text(result.stdout + result.stderr)
print(result.stdout + result.stderr, end='')
if result.returncode: raise SystemExit(result.returncode)
(output / 'InputBoundaryTests.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': '8.0.0'}}}))
result = subprocess.run([str(environment.dotnet), str(output / 'InputBoundaryTests.dll')], text=True, capture_output=True)
(here / 'results.txt').write_text(result.stdout + result.stderr)
print(result.stdout + result.stderr, end='')
raise SystemExit(result.returncode)
