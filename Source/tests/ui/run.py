#!/usr/bin/env python3
"""Run deterministic UI and actual-game-metadata input guards without a Unity renderer."""
from pathlib import Path
import argparse
import json
import shutil
import subprocess

here = Path(__file__).resolve().parent
standalone = here.parents[1]
import sys
sys.path.insert(0, str(standalone / 'tools'))
from TestEnvironment import resolve
parser = argparse.ArgumentParser(description=__doc__)
for key in ('dotnet','sdk-root','managed','references'):
    parser.add_argument('--' + key, type=Path)
args = parser.parse_args()
environment = resolve(standalone, dotnet=args.dotnet, sdk_root=args.sdk_root, managed=args.managed,
                      references=args.references, require_cecil=False)
dotnet, compiler, refs = environment.dotnet, environment.compiler, environment.net_ref_root
runtime = '8.0.0'
output = here / 'build'
output.mkdir(exist_ok=True)
managed = environment.managed
dependency_refs = []
for path in managed.glob('*.dll'):
    if path.name == 'mscorlib.dll' or path.name == 'netstandard.dll' or path.name.startswith('System.') or path.name == 'System.dll':
        continue
    dependency_refs.append(path)
    shutil.copyfile(path, output / path.name)
standalone = here.parents[1]
umm = environment.references / 'UnityModManager.dll'
harmony = environment.references / 'net8-harmony/0Harmony.dll'
shutil.copyfile(umm, output / umm.name)
shutil.copyfile(harmony, output / harmony.name)
log = []
for name, source in [('UiTests', 'RollWindow.cs'), ('InputTests', 'Plugin.cs')]:
    response = output / (name + '.rsp')
    lines = ['-nologo', '-nostdlib+', '-langversion:7.3', '-target:exe', '-out:"' + str(output / (name + '.dll')) + '"']
    lines += ['-r:"' + str(path) + '"' for path in sorted(refs.glob('*.dll')) + dependency_refs + [umm, harmony]]
    lines += ['"' + str(standalone / 'src' / source) + '"', '"' + str(here / (name + '.cs')) + '"']
    response.write_text('\n'.join(lines))
    result = subprocess.run([str(dotnet), str(compiler), '@' + str(response)], text=True, capture_output=True)
    (output / (name + '.compile.txt')).write_text(result.stdout + result.stderr)
    print(result.stdout + result.stderr, end='')
    result.check_returncode()
    (output / (name + '.runtimeconfig.json')).write_text(json.dumps({'runtimeOptions': {'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': runtime}}}))
    result = subprocess.run([str(dotnet), str(output / (name + '.dll'))], text=True, capture_output=True)
    log.append(result.stdout + result.stderr)
    (output / (name + '.results.txt')).write_text(result.stdout + result.stderr)
    print(result.stdout + result.stderr, end='')
    result.check_returncode()
(here / 'results.txt').write_text(''.join(log))
