#!/usr/bin/env python3
"""Run original TA/Jump control through production await/runtime/window ownership."""
from pathlib import Path
import hashlib
import argparse
import sys
import json
import shutil
import subprocess

here = Path(__file__).resolve().parent
standalone = here.parents[2]
sys.path.insert(0,str(standalone/'tools'))
from TestEnvironment import resolve
parser=argparse.ArgumentParser(description=__doc__)
for key in ('dotnet','sdk-root','managed','references','cecil'):
    parser.add_argument('--'+key,type=Path)
args=parser.parse_args()
environment=resolve(standalone,dotnet=args.dotnet,sdk_root=args.sdk_root,managed=args.managed,
                    references=args.references,cecil=args.cecil)
dotnet,compiler,refs=environment.dotnet,environment.compiler,environment.net_refs
runtime='8.0.0'
output = here / 'build'; output.mkdir(exist_ok=True)
managed = environment.managed / 'Assembly-CSharp.dll'

def compile(name, sources, extra=(), executable=False):
    response = output / (name + '.rsp')
    lines = ['-nologo', '-nostdlib+', '-optimize+', '-langversion:7.3', '-target:' + ('exe' if executable else 'library'), '-out:"' + str(output / name) + '"']
    lines += ['-r:"' + str(path) + '"' for path in refs + list(extra)]
    lines += ['"' + str(path) + '"' for path in sources]; response.write_text('\n'.join(lines))
    result = subprocess.run([str(dotnet), str(compiler), '@' + str(response)], text=True, capture_output=True)
    (output / (name + '.compile.txt')).write_text(result.stdout + result.stderr)
    if result.returncode: print(result.stdout + result.stderr, end='')
    result.check_returncode()
    if executable: (output / (Path(name).stem + '.runtimeconfig.json')).write_text(json.dumps({'runtimeOptions': {'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': runtime}}}))

shutil.copyfile(environment.cecil, output / 'Mono.Cecil.dll')
shutil.copyfile(environment.references / 'net8-harmony/0Harmony.dll', output / '0Harmony.dll')
abi = (standalone / 'tests/native/NativeDiceTypes.cs').read_text()
abi = abi[:abi.index('namespace UnityEngine')] + abi[abi.index('public static class FloatExtensions'):]
(output / 'NativeDiceTypes.cs').write_text(abi)
compile('Assembly-CSharp.dll', [output / 'NativeDiceTypes.cs', here / 'AwaitNativeTypes.cs', here / 'IntegrationGameBoundary.cs', standalone / 'tests/ui-render/UnityStubs.cs'])
for name, source in [('ExtractNativeDice', standalone / 'tests/native/ExtractNativeDice.cs'), ('ExtractAwaitNative', here / 'ExtractAwaitNative.cs')]:
    compile(name + '.dll', [source], [output / 'Mono.Cecil.dll'], True)
    result = subprocess.run([str(dotnet), str(output / (name + '.dll')), str(managed), str(output / 'Assembly-CSharp.dll')], text=True, capture_output=True)
    (output / (name + '.results.txt')).write_text(result.stdout + result.stderr)
    if result.returncode: print(result.stdout + result.stderr, end='')
    result.check_returncode()
    generated = output / 'Assembly-CSharp.dll.native'
    if generated.exists(): shutil.move(str(generated), str(output / 'Assembly-CSharp.dll'))
snapshot = output / 'source-snapshot'; snapshot.mkdir(exist_ok=True)
production_sources = [standalone / 'src' / name for name in ['RollBroker.cs', 'RuntimeRolls.cs', 'RollText.cs', 'NativeScratchScope.cs', 'NativePatches.cs', 'RollWindow.cs']]
production_sources += [standalone / "tests/common/OfflineNetworkHost.cs", standalone / "tests/common/OfflineBardicHost.cs", standalone / "tests/common/OfflineShoveHost.cs"]
hashes = {}
for source in production_sources:
    data = source.read_bytes(); (snapshot / source.name).write_bytes(data); hashes[str(source.relative_to(standalone))] = hashlib.sha256(data).hexdigest()
compile('IntegrationTests.dll', [snapshot / source.name for source in production_sources] + [here / 'IntegrationHost.cs', here / 'IntegrationTests.cs', standalone / 'tools/HarmonyFixtureHost.cs'], [output / 'Assembly-CSharp.dll', output / '0Harmony.dll'], True)
result = subprocess.run([str(dotnet), str(output / 'IntegrationTests.dll')], text=True, capture_output=True)
(here / 'results.txt').write_text(result.stdout + result.stderr); print(result.stdout + result.stderr, end=''); result.check_returncode()
(here / 'source-manifest.json').write_text(json.dumps({'production_sources': hashes, 'pristine_game_sha256': hashlib.sha256(managed.read_bytes()).hexdigest(), 'runtime': '.NET8/Linux real Harmony with fixture-only module enumeration shim', 'unity_and_ability_descriptor_are_boundaries': True}, indent=2) + '\n')
