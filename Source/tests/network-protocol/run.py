#!/usr/bin/env python3
"""Compile the production pure multiplayer protocol and exercise multiparty schedules."""
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
env = resolve(project, dotnet=args.dotnet, sdk_root=args.sdk_root,
              require_managed=False, require_references=False, require_cecil=False)
output = here / 'build'
output.mkdir(exist_ok=True)
sources = [project / 'src/MultiplayerProtocol.cs', here / 'ProtocolTests.cs']
response = output / 'ProtocolTests.rsp'
response.write_text('\n'.join(['-nologo', '-nostdlib+', '-target:exe',
    '-out:"' + str(output / 'ProtocolTests.dll') + '"'] +
    ['-r:"' + str(p) + '"' for p in env.net_refs] + ['"' + str(p) + '"' for p in sources]))
subprocess.run([str(env.dotnet), str(env.compiler), '@' + str(response)], check=True)
(output / 'ProtocolTests.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {
    'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': '8.0.0'}}}))
result = subprocess.run([str(env.dotnet), str(output / 'ProtocolTests.dll')],
                        text=True, capture_output=True)
(here / 'results.txt').write_text(result.stdout + result.stderr)
if result.returncode:
    print(result.stdout + result.stderr, end='')
result.check_returncode()
(here / 'source-manifest.json').write_text(json.dumps({
    str(p.relative_to(project)): hashlib.sha256(p.read_bytes()).hexdigest()
    for p in sources}, sort_keys=True, indent=2) + '\n')
print(result.stdout, end='')
