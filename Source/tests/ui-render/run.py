#!/usr/bin/env python3
"""Exercise actual RollWindow.cs against a deterministic test-only GUI boundary."""
from pathlib import Path
import argparse
import hashlib
import json
import subprocess
import sys

here = Path(__file__).resolve().parent
standalone = here.parents[1]
sys.path.insert(0, str(standalone / 'tools'))
from TestEnvironment import resolve
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--dotnet', type=Path)
parser.add_argument('--sdk-root', type=Path)
args = parser.parse_args()
try:
    environment = resolve(standalone, dotnet=args.dotnet, sdk_root=args.sdk_root,
                          require_managed=False, require_references=False, require_cecil=False)
except (ValueError, OSError, subprocess.SubprocessError) as error:
    parser.error(str(error))
dotnet, compiler, refs = environment.dotnet, environment.compiler, environment.net_refs
output = here / 'build'
output.mkdir(exist_ok=True)
name = 'UiRenderTests'
response = output / (name + '.rsp')
sources = [here.parents[1] / 'src/RollWindow.cs', here / 'UnityStubs.cs', here / 'BoundaryStubs.cs', here / 'UiRenderTests.cs']
(here / 'source-manifest.json').write_text(json.dumps({str(path.relative_to(standalone)): hashlib.sha256(path.read_bytes()).hexdigest() for path in sources}, indent=2) + '\n')
lines = ['-nologo', '-nostdlib+', '-langversion:7.3', '-target:exe', '-out:"' + str(output / (name + '.dll')) + '"']
lines += ['-r:"' + str(path) + '"' for path in refs]
lines += ['"' + str(path) + '"' for path in sources]
response.write_text('\n'.join(lines))
result = subprocess.run([str(dotnet), str(compiler), '@' + str(response)], text=True, capture_output=True)
(output / 'compile.txt').write_text(result.stdout + result.stderr)
print(result.stdout + result.stderr, end='')
if result.returncode:
    raise SystemExit(result.returncode)
(output / (name + '.runtimeconfig.json')).write_text(json.dumps({'runtimeOptions': {'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': '8.0.0'}}}))
result = subprocess.run([str(dotnet), str(output / (name + '.dll'))], text=True, capture_output=True)
(here / 'results.txt').write_text(result.stdout + result.stderr)
print(result.stdout + result.stderr, end='')
raise SystemExit(result.returncode)
