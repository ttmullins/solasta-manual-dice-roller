#!/usr/bin/env python3
"""Compile/run pure broker checks directly with .NET 8's C# compiler."""
from pathlib import Path
import argparse
import json
import subprocess

here = Path(__file__).resolve().parent
standalone = here.parents[1]
import sys
sys.path.insert(0, str(standalone / 'tools'))
from TestEnvironment import resolve
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--dotnet', type=Path)
parser.add_argument('--sdk-root', type=Path)
args = parser.parse_args()
environment = resolve(standalone, dotnet=args.dotnet, sdk_root=args.sdk_root,
                      require_managed=False, require_references=False, require_cecil=False)
dotnet, compiler, refs = environment.dotnet, environment.compiler, environment.net_ref_root
runtime = '8.0.0'
output = here / 'build'
output.mkdir(exist_ok=True)
response = output / 'EngineTests.rsp'
lines = ['-nologo', '-nostdlib+', '-target:exe', '-out:"' + str(output / 'EngineTests.dll') + '"']
lines += ['-r:"' + str(p) + '"' for p in sorted(refs.glob('*.dll'))]
lines += ['"' + str(here.parents[1] / 'src/RollBroker.cs') + '"', '"' + str(here / 'EngineTests.cs') + '"']
response.write_text('\n'.join(lines))
subprocess.run([str(dotnet), str(compiler), '@' + str(response)], check=True)
(output / 'EngineTests.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': runtime}}}))
result = subprocess.run([str(dotnet), str(output / 'EngineTests.dll')], check=True, text=True, capture_output=True)
(here / 'results.txt').write_text(result.stdout)
print(result.stdout, end='')
