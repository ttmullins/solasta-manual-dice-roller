#!/usr/bin/env python3
"""Exercise the 0.5 single-player sites against the actual pristine game assembly.

Outer dialog/gadget resolvers are compared with the original game methods'
own dice calls (captured by a test-only Harmony prefix). The probe and the
deferred-roll queue run their production code against real game types; the
roll broker, window and Unity rendering are boundaries.
"""
from pathlib import Path
import argparse, hashlib, json, shutil, subprocess, sys
here = Path(__file__).resolve().parent
project = here.parents[1]
sys.path.insert(0, str(project / 'tools'))
from TestEnvironment import resolve
parser = argparse.ArgumentParser(description=__doc__)
for key in ('dotnet', 'sdk-root', 'managed', 'references', 'cecil'): parser.add_argument('--' + key, type=Path)
args = parser.parse_args()
env = resolve(project, dotnet=args.dotnet, sdk_root=args.sdk_root, managed=args.managed, references=args.references, cecil=args.cecil)
out = here / 'build'; out.mkdir(exist_ok=True)
production = [project / 'src' / name for name in ('OuterSites.cs', 'NativeProbePlanner.cs', 'DeferredRolls.cs', 'SyncPumpGuard.cs', 'RollText.cs', 'NativeScratchScope.cs', 'FallDamage.cs',
    'NativeMethodPin.cs', 'TurnRolls.cs', 'FeatureRolls.cs', 'CompositeRolls.cs', 'MenuRolls.cs', 'TrapRolls.cs')]
runtime = project / 'src/RuntimeRolls.cs'
fixtures = [here / 'SyncHost.cs', here / 'SyncTests.cs', here / 'SyncTests07.cs', here / 'SyncTests08.cs', project / 'tools/HarmonyFixtureHost.cs']
tracked = production + [runtime] + fixtures + [here / 'run.py']
before = {str(p.relative_to(project)): hashlib.sha256(p.read_bytes()).hexdigest() for p in tracked}
snapshot = out / 'source-snapshot'; snapshot.mkdir(exist_ok=True)
for path in production: (snapshot / path.name).write_bytes(path.read_bytes())
# Generate the game-independent RuntimeRolls types used by these sources:
# NativeInvocation (file prelude) plus OuterSite and PromptInfo.
text = runtime.read_text(encoding='utf-8')
prelude = text[:text.index('    public sealed class NativeRollPlan')]
outer = text[text.index('    // An outer await site suspends'):text.index('    public static class RuntimeRolls')]
(out / 'RuntimePrelude.cs').write_text(prelude + outer + '}\n', encoding='utf-8')
harmony = env.references / 'net8-harmony/0Harmony.dll'
shutil.copyfile(harmony, out / '0Harmony.dll')
# Test-only: Unity InternalCalls become managed throwing methods so CoreCLR can
# JIT game getters that merely reference them (none may actually run).
shutil.copyfile(env.cecil, out / 'Mono.Cecil.dll')
shim_tool = project / 'tests/network-native/ShimUnityInternalCalls.cs'
tracked.append(shim_tool); before[str(shim_tool.relative_to(project))] = hashlib.sha256(shim_tool.read_bytes()).hexdigest()
srsp = out / 'Shim.rsp'
srsp.write_text('\n'.join(['-nologo', '-nostdlib+', '-target:exe', '-out:"' + str(out / 'Shim.dll') + '"'] + ['-r:"' + str(x) + '"' for x in env.net_refs + [out / 'Mono.Cecil.dll']] + ['"' + str(shim_tool) + '"']))
proc = subprocess.run([str(env.dotnet), str(env.compiler), '@' + str(srsp)], text=True, capture_output=True)
if proc.returncode: print(proc.stdout + proc.stderr, end='')
proc.check_returncode()
(out / 'Shim.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': '8.0.0'}}}))
(out / 'shim').mkdir(exist_ok=True)
proc = subprocess.run([str(env.dotnet), str(out / 'Shim.dll'), str(env.managed / 'UnityEngine.CoreModule.dll'), str(out / 'shim' / 'UnityEngine.CoreModule.dll')], text=True, capture_output=True)
(out / 'shim.txt').write_text(proc.stdout + proc.stderr)
if proc.returncode: print(proc.stdout + proc.stderr, end='')
proc.check_returncode()
game_refs = [p for p in sorted(env.managed.glob('*.dll')) if not p.name.startswith('System') and p.name not in {'mscorlib.dll', 'netstandard.dll'}]
rsp = out / 'SyncTests.rsp'
rows = ['-nologo', '-nostdlib+', '-optimize+', '-langversion:7.3', '-target:exe', '-out:"' + str(out / 'SyncTests.dll') + '"']
rows += ['-r:"' + str(x) + '"' for x in env.net_refs + game_refs + [out / '0Harmony.dll']]
rows += ['"' + str(x) + '"' for x in [snapshot / p.name for p in production] + [out / 'RuntimePrelude.cs'] + fixtures]
rsp.write_text('\n'.join(rows))
proc = subprocess.run([str(env.dotnet), str(env.compiler), '@' + str(rsp)], text=True, capture_output=True)
(out / 'compile.txt').write_text(proc.stdout + proc.stderr)
if proc.returncode: print(proc.stdout + proc.stderr, end='')
proc.check_returncode()
(out / 'SyncTests.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': '8.0.0'}}}))
proc = subprocess.run([str(env.dotnet), str(out / 'SyncTests.dll'), str(env.managed), str(out / 'shim' / 'UnityEngine.CoreModule.dll')], text=True, capture_output=True)
(here / 'results.txt').write_text(proc.stdout + proc.stderr); print(proc.stdout + proc.stderr, end='')
proc.check_returncode()
after = {str(p.relative_to(project)): hashlib.sha256(p.read_bytes()).hexdigest() for p in tracked}
if before != after: raise RuntimeError('Sources changed during the run; results were not refreshed')
(here / 'source-manifest.json').write_text(json.dumps({
    'pristine_game_sha256': hashlib.sha256((env.managed / 'Assembly-CSharp.dll').read_bytes()).hexdigest(),
    'net8_harmony_sha256': hashlib.sha256(harmony.read_bytes()).hexdigest(),
    'runtime': '.NET8/Linux; actual pristine Assembly-CSharp; real Harmony; Unity InternalCalls are throwing test shims; rendering, broker and window are boundaries',
    'results_sha256': hashlib.sha256((here / 'results.txt').read_bytes()).hexdigest(),
    'sources': after}, indent=2) + '\n')
