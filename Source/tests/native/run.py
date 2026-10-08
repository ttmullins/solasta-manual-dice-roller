#!/usr/bin/env python3
"""Execute production runtime adapter with real Harmony and copied pristine dice IL."""
from pathlib import Path
import argparse
import hashlib
import json
import shutil
import subprocess

here = Path(__file__).resolve().parent
standalone = here.parents[1]
import sys
sys.path.insert(0, str(standalone / 'tools'))
from TestEnvironment import resolve
parser = argparse.ArgumentParser(description=__doc__)
for key in ('dotnet','sdk-root','managed','references','cecil'):
    parser.add_argument('--' + key, type=Path)
args = parser.parse_args()
environment = resolve(standalone, dotnet=args.dotnet, sdk_root=args.sdk_root, managed=args.managed,
                      references=args.references, cecil=args.cecil)
args.managed, args.cecil = environment.managed, environment.cecil
dotnet, compiler, refs = environment.dotnet, environment.compiler, environment.net_refs
runtime = '8.0.0'
output = here / 'build'; output.mkdir(exist_ok=True)

def compile(name, sources, extra=(), executable=False):
    response = output / (name + '.rsp')
    lines = ['-nologo', '-nostdlib+', '-optimize+', '-langversion:7.3', '-target:' + ('exe' if executable else 'library'), '-out:"' + str(output / name) + '"']
    lines += ['-r:"' + str(p) + '"' for p in refs + list(extra)]
    lines += ['"' + str(p) + '"' for p in sources]; response.write_text('\n'.join(lines))
    result = subprocess.run([str(dotnet), str(compiler), '@' + str(response)], text=True, capture_output=True)
    (output / (name + '.compile.txt')).write_text(result.stdout + result.stderr)
    if result.returncode: print(result.stdout + result.stderr, end='')
    result.check_returncode()
    if executable: (output / (Path(name).stem + '.runtimeconfig.json')).write_text(json.dumps({'runtimeOptions': {'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': runtime}}}))

shutil.copyfile(args.cecil, output / 'Mono.Cecil.dll')
shutil.copyfile(environment.references / 'net8-harmony/0Harmony.dll', output / '0Harmony.dll')
compile('Assembly-CSharp.dll', [here / 'NativeDiceTypes.cs'])
compile('ExtractNativeDice.dll', [here / 'ExtractNativeDice.cs'], [output / 'Mono.Cecil.dll'], True)
result = subprocess.run([str(dotnet), str(output / 'ExtractNativeDice.dll'), str(args.managed.resolve() / 'Assembly-CSharp.dll'), str(output / 'Assembly-CSharp.dll')], text=True, capture_output=True)
(here / 'extracted-methods.txt').write_text(result.stdout + result.stderr)
if result.returncode: print(result.stdout + result.stderr, end='')
result.check_returncode(); shutil.move(str(output / 'Assembly-CSharp.dll.native'), str(output / 'Assembly-CSharp.dll'))
snapshot = output / 'source-snapshot'; snapshot.mkdir(exist_ok=True)
production_sources = [standalone / 'src/RollBroker.cs', standalone / 'src/RuntimeRolls.cs', standalone / 'src/RollText.cs', standalone / 'src/NativeScratchScope.cs']
production_sources += [standalone / "tests/common/OfflineNetworkHost.cs", standalone / "tests/common/OfflineBardicHost.cs", standalone / "tests/common/OfflineShoveHost.cs"]
source_hashes = {}
for source in production_sources:
    data = source.read_bytes(); (snapshot / source.name).write_bytes(data)
    source_hashes[str(source.relative_to(standalone))] = hashlib.sha256(data).hexdigest()
compile('NativeAdapterTests.dll', [snapshot / p.name for p in production_sources] + [here / 'AdapterHost.cs', here / 'NativeAdapterTests.cs', standalone / 'tools/HarmonyFixtureHost.cs'], [output / 'Assembly-CSharp.dll', output / '0Harmony.dll'], True)
result = subprocess.run([str(dotnet), str(output / 'NativeAdapterTests.dll')], text=True, capture_output=True)
(here / 'results.txt').write_text(result.stdout + result.stderr); print(result.stdout + result.stderr, end=''); result.check_returncode()
(here / 'source-manifest.json').write_text(json.dumps({'production_sources': source_hashes, 'pristine_game_sha256': hashlib.sha256((args.managed.resolve() / 'Assembly-CSharp.dll').read_bytes()).hexdigest(), 'harmony_sha256': hashlib.sha256((output / '0Harmony.dll').read_bytes()).hexdigest(), 'fixture_runtime': '.NET8/Linux; real Harmony; fixture-only module enumeration shim', 'effect_rest_save_and_ui_are_host_doubles': True}, indent=2) + '\n')
