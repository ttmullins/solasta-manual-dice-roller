#!/usr/bin/env python3
"""Run the unchanged production Plugin class at mocked loader/game/UI boundaries."""
from pathlib import Path
import argparse
import hashlib
import json
import shutil
import subprocess
import sys

here = Path(__file__).resolve().parent
standalone = here.parents[1]
sys.path.insert(0, str(standalone / 'tools'))
from TestEnvironment import resolve
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--dotnet', type=Path)
parser.add_argument('--sdk-root', type=Path)
parser.add_argument('--managed', type=Path)
args = parser.parse_args()
try:
    environment = resolve(standalone, dotnet=args.dotnet, sdk_root=args.sdk_root, managed=args.managed,
                          require_references=False, require_cecil=False)
except (ValueError, OSError, subprocess.SubprocessError) as error:
    parser.error(str(error))
dotnet, compiler, refs = environment.dotnet, environment.compiler, environment.net_refs
output = here / 'build'
output.mkdir(exist_ok=True)
production = standalone / 'src/Plugin.cs'
source = production.read_text()
boundary = source.index('    // These guards leave Unity')
captured = output / 'PluginClass.cs'
captured.write_text(source[:boundary] + '}\n')
managed_refs = []
for path in sorted(environment.managed.glob('*.dll')):
    if path.name in ('mscorlib.dll', 'netstandard.dll', 'System.dll') or path.name.startswith('System.'):
        continue
    managed_refs.append(path)
    shutil.copyfile(path, output / path.name)
harmony = output / '0Harmony.dll'
base_refs = refs
harmony_response = output / 'HarmonyBoundary.rsp'
harmony_response.write_text('\n'.join(['-nologo', '-nostdlib+', '-langversion:7.3', '-target:library', '-out:"' + str(harmony) + '"'] + ['-r:"' + str(p) + '"' for p in base_refs] + ['"' + str(here / 'HarmonyBoundary.cs') + '"']))
result = subprocess.run([str(dotnet), str(compiler), '@' + str(harmony_response)], text=True, capture_output=True)
(output / 'harmony-compile.txt').write_text(result.stdout + result.stderr)
if result.returncode:
    print(result.stdout + result.stderr, end='')
    raise SystemExit(result.returncode)
sources = [captured, here / 'BoundaryStubs.cs', here / 'PluginTests.cs']
# Read (not compiled) by the fingerprint case: UI-only settings must stay out
# of the production multiplayer profile.
fingerprint = standalone / 'src/MultiplayerRolls.cs'
(here / 'source-manifest.json').write_text(json.dumps({'production_plugin_sha256': hashlib.sha256(production.read_bytes()).hexdigest(), 'captured_plugin_class_sha256': hashlib.sha256(captured.read_bytes()).hexdigest(), 'src/MultiplayerRolls.cs': hashlib.sha256(fingerprint.read_bytes()).hexdigest(), 'boundaries': {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in sources[1:] + [here / 'HarmonyBoundary.cs']}}, indent=2) + '\n')
response = output / 'PluginTests.rsp'
lines = ['-nologo', '-nostdlib+', '-langversion:7.3', '-nowarn:0436', '-target:exe', '-out:"' + str(output / 'PluginTests.dll') + '"']
lines += ['-r:"' + str(path) + '"' for path in refs + managed_refs + [harmony]]
lines += ['"' + str(path) + '"' for path in sources]
response.write_text('\n'.join(lines))
result = subprocess.run([str(dotnet), str(compiler), '@' + str(response)], text=True, capture_output=True)
(output / 'compile.txt').write_text(result.stdout + result.stderr)
print(result.stdout + result.stderr, end='')
if result.returncode: raise SystemExit(result.returncode)
(output / 'PluginTests.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': '8.0.0'}}}))
result = subprocess.run([str(dotnet), str(output / 'PluginTests.dll'), str(fingerprint)], text=True, capture_output=True)
(here / 'results.txt').write_text(result.stdout + result.stderr)
print(result.stdout + result.stderr, end='')
raise SystemExit(result.returncode)
