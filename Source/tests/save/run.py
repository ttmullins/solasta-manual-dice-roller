#!/usr/bin/env python3
"""Run save previews over pristine IL; compile only RuntimeRolls' NativeInvocation prelude."""
from pathlib import Path
import json
import argparse
import sys
import shutil
import subprocess
import hashlib
import types

here=Path(__file__).resolve().parent
standalone=here.parents[1]
resolver_path=standalone/'tools/TestEnvironment.py'
resolver_bytes=resolver_path.read_bytes()
# Execute exactly the tracked helper, without relying on a cached Python module.
resolver=types.ModuleType('save_test_environment')
resolver.__file__=str(resolver_path)
sys.modules[resolver.__name__]=resolver
exec(compile(resolver_bytes,str(resolver_path),'exec'),resolver.__dict__)
resolve=resolver.resolve
parser=argparse.ArgumentParser(description=__doc__)
for key in ('dotnet','sdk-root','managed','references','cecil'):
    parser.add_argument('--'+key,type=Path)
args=parser.parse_args()
environment=resolve(standalone,dotnet=args.dotnet,sdk_root=args.sdk_root,managed=args.managed,
                    references=args.references,cecil=args.cecil)
dotnet,compiler,refs=environment.dotnet,environment.compiler,environment.net_refs
runtime='8.0.0'
output=here/'build';output.mkdir(exist_ok=True)

def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def label(path):
    return str(path.relative_to(standalone))

tracked_sources=[standalone/'src'/name for name in ('RuntimeRolls.cs','RollText.cs','NativeSavePlanner.cs','NativeScratchScope.cs')]
tracked_sources += [here/name for name in ('NativeDiceTypes.cs','SaveTypes.cs','ExtractNativeSave.cs','SaveHost.cs','SaveTests.cs','ActualGuardSmoke.cs','run.py')]
tracked_sources += [standalone/'tools/HarmonyFixtureHost.cs',resolver_path]
source_bytes={label(path):path.read_bytes() for path in tracked_sources}
if source_bytes[label(resolver_path)]!=resolver_bytes:
    raise RuntimeError('The save fixture resolver changed while resolving inputs.')
sources_before={name:hashlib.sha256(data).hexdigest() for name,data in source_bytes.items()}
snapshot=output/'source-snapshot'
snapshots={}
for path in tracked_sources:
    copy=snapshot/path.relative_to(standalone);copy.parent.mkdir(parents=True,exist_ok=True)
    copy.write_bytes(source_bytes[label(path)]);snapshots[path]=copy

# Include every native metadata/dependency and compiler reference used by the
# extracted fixture or complete actual-game metadata smoke, plus their tools.
managed_inputs=sorted(environment.managed.glob('*.dll'))
game_refs=[path for path in managed_inputs if path.name not in {'mscorlib.dll','netstandard.dll','System.dll'} and not path.name.startswith('System.')]
input_paths={'managed/'+path.name:path for path in managed_inputs}
input_paths.update({'net8-reference/'+path.name:path for path in refs})
input_paths.update({'roslyn/'+path.name:path for path in compiler.parent.iterdir() if path.is_file()})
input_paths.update({'host/dotnet':dotnet,'host/python':Path(sys.executable).resolve(),'dependency/Mono.Cecil.dll':environment.cecil})
for name in ('UnityModManager.dll','0Harmony.dll','net8-harmony/0Harmony.dll'):
    input_paths['dependency/'+name]=environment.references/name
runtime_inventory=subprocess.run([str(dotnet),'--list-runtimes'],text=True,capture_output=True,check=True).stdout
for line in runtime_inventory.splitlines():
    if ' [' not in line or not line.endswith(']'):
        continue
    heading,directory=line.rsplit(' [',1);parts=heading.split()
    if len(parts)!=2 or parts[0]!='Microsoft.NETCore.App' or not parts[1].startswith('8.'):
        continue
    framework=Path(directory[:-1])/parts[1]
    for path in framework.iterdir():
        if path.is_file():input_paths['net8-runtime/'+parts[1]+'/'+path.name]=path
    for version in (framework.parents[2]/'host/fxr').glob('8.*'):
        for path in version.iterdir():
            if path.is_file():input_paths['hostfxr/'+version.name+'/'+path.name]=path
inputs_before={name:sha256(path) for name,path in input_paths.items()}
copied_inputs={}
generated_invocation_sha256=None

def copy_input(source,destination):
    shutil.copyfile(source,destination);copied_inputs[destination]=sha256(source)

def verify_evidence():
    sources_after={label(path):sha256(path) for path in tracked_sources}
    inputs_after={name:sha256(path) for name,path in input_paths.items()}
    if sources_after!=sources_before:
        raise RuntimeError('Save fixture sources changed after the compiler snapshot; rerun one unchanged source version.')
    if inputs_after!=inputs_before:
        raise RuntimeError('A save fixture native/reference/compiler dependency changed during the run.')
    if any(sha256(copy)!=sources_before[label(path)] for path,copy in snapshots.items()):
        raise RuntimeError('A save fixture compiler source snapshot changed during the run.')
    if generated_invocation_sha256 is not None and sha256(output/'NativeInvocation.cs')!=generated_invocation_sha256:
        raise RuntimeError('The generated NativeInvocation prelude changed during the run.')
    if any(sha256(copy)!=expected for copy,expected in copied_inputs.items()):
        raise RuntimeError('A copied save fixture dependency changed during the run.')
    return sources_after,inputs_after

evidence={'status':'running','compiled_from_source_snapshot':True,'sources':sources_before,'sources_before':sources_before,
          'inputs_before':inputs_before,'native_game_sha256':inputs_before['managed/Assembly-CSharp.dll'],
          'test_harmony_sha256':inputs_before['dependency/net8-harmony/0Harmony.dll'],'cecil_sha256':inputs_before['dependency/Mono.Cecil.dll'],
          'runtime_source_scope':'Only NativeInvocation before NativeRollPlan is generated and compiled from RuntimeRolls.cs; RuntimeRolls, broker, UI and multiplayer execution are not exercised.',
          'boundary':'Real .NET8 Harmony over copied pristine save/preparation IL and unchanged production save/scratch planners; scene/services/dice storage are explicit fixture boundaries. Separate actual-game metadata guard smoke executes no Unity engine calls.'}

def write_manifest():
    pending=here/'source-manifest.json.pending';pending.write_text(json.dumps(evidence,indent=2)+'\n');pending.replace(here/'source-manifest.json')

write_manifest()

def compile(name,sources,extra=(),executable=False):
    response=output/(name+'.rsp')
    lines=['-nologo','-nostdlib+','-optimize+','-langversion:7.3','-target:'+('exe' if executable else 'library'),'-out:"'+str(output/name)+'"']
    lines+=['-r:"'+str(p)+'"' for p in refs+list(extra)]
    lines+=['"'+str(snapshots.get(p,p))+'"' for p in sources];response.write_text('\n'.join(lines))
    result=subprocess.run([str(dotnet),str(compiler),'@'+str(response)],text=True,capture_output=True)
    (output/(name+'.compile.txt')).write_text(result.stdout+result.stderr)
    if result.returncode:print(result.stdout+result.stderr,end='')
    result.check_returncode()
    if executable:(output/(Path(name).stem+'.runtimeconfig.json')).write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':runtime}}}))

copy_input(environment.cecil,output/'Mono.Cecil.dll')
copy_input(environment.references/'net8-harmony/0Harmony.dll',output/'0Harmony.dll')
compile('Assembly-CSharp.dll',[here/'NativeDiceTypes.cs',here/'SaveTypes.cs'])
compile('ExtractNativeSave.dll',[here/'ExtractNativeSave.cs'],[output/'Mono.Cecil.dll'],True)
result=subprocess.run([str(dotnet),str(output/'ExtractNativeSave.dll'),str(environment.managed/'Assembly-CSharp.dll'),str(output/'Assembly-CSharp.dll')],text=True,capture_output=True)
extraction=result.stdout+result.stderr
(output/'extracted-methods.txt').write_text(extraction)
if result.returncode:print(result.stdout+result.stderr,end='')
result.check_returncode();shutil.move(str(output/'Assembly-CSharp.dll.native'),str(output/'Assembly-CSharp.dll'))
source=source_bytes['src/RuntimeRolls.cs'].decode('utf-8')
(output/'NativeInvocation.cs').write_text(source[:source.index('    public sealed class NativeRollPlan')]+ '}\n')
generated_invocation_sha256=sha256(output/'NativeInvocation.cs')
compile('SaveTests.dll',[output/'NativeInvocation.cs',standalone/'src/RollText.cs',standalone/'src/NativeSavePlanner.cs',standalone/'src/NativeScratchScope.cs',here/'SaveHost.cs',here/'SaveTests.cs',standalone/'tools/HarmonyFixtureHost.cs'],[output/'Assembly-CSharp.dll',output/'0Harmony.dll'],True)
result=subprocess.run([str(dotnet),str(output/'SaveTests.dll')],text=True,capture_output=True)
fixture_results=result.stdout+result.stderr
(output/'fixture-results.txt').write_text(fixture_results);print(fixture_results,end='');result.check_returncode()
verify_evidence()

# This separate host uses the actual full game DLL, rather than the extracted
# ABI fixture, so the safety gate cannot quietly reject genuine stock types.
actual=output/'actual'
if actual.exists():shutil.rmtree(actual)
actual.mkdir()
for path in game_refs:copy_input(path,actual/path.name)
copy_input(output/'0Harmony.dll',actual/'0Harmony.dll')
response=actual/'ActualGuardSmoke.rsp'
lines=['-nologo','-nostdlib+','-optimize+','-target:exe','-out:"'+str(actual/'ActualGuardSmoke.dll')+'"']
lines+=['-r:"'+str(p)+'"' for p in refs+game_refs+[output/'0Harmony.dll']]
lines+=['"'+str(snapshots.get(p,p))+'"' for p in [output/'NativeInvocation.cs',standalone/'src/RollText.cs',standalone/'src/NativeSavePlanner.cs',standalone/'src/NativeScratchScope.cs',here/'SaveHost.cs',here/'ActualGuardSmoke.cs']]
response.write_text('\n'.join(lines))
result=subprocess.run([str(dotnet),str(compiler),'@'+str(response)],text=True,capture_output=True)
(actual/'compile.txt').write_text(result.stdout+result.stderr)
if result.returncode:print(result.stdout+result.stderr,end='')
result.check_returncode()
(actual/'ActualGuardSmoke.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':runtime}}}))
result=subprocess.run([str(dotnet),str(actual/'ActualGuardSmoke.dll')],text=True,capture_output=True)
guard_results=result.stdout+result.stderr
(actual/'results.txt').write_text(guard_results)
print(guard_results,end='');result.check_returncode()
if 'PASS 103 production save preview assertions' not in fixture_results or 'PASS foreign-patch guard accepts the actual pristine game/Unity method metadata without executing native engine calls.' not in guard_results:
    raise RuntimeError('Expected save fixture or actual metadata guard success evidence is missing.')
sources_after,inputs_after=verify_evidence()
# Publish success only after both hosts and all exact pre/post inputs qualify.
results_pending=here/'results.txt.pending';results_pending.write_text(fixture_results+guard_results);results_pending.replace(here/'results.txt')
extracted_pending=here/'extracted-methods.txt.pending';extracted_pending.write_text(extraction);extracted_pending.replace(here/'extracted-methods.txt')
evidence.update({'status':'passed','sources_after':sources_after,'inputs_after':inputs_after,
                 'source_consistency':'identical_before_and_after_successful_run',
                 'generated_native_invocation_sha256':generated_invocation_sha256,
                 'fixture_assertions':103,'actual_metadata_guard_smoke':'passed',
                 'results_sha256':sha256(here/'results.txt'),'extracted_methods_sha256':sha256(here/'extracted-methods.txt')})
write_manifest()
print('Refreshed save evidence only after both hosts passed with unchanged source snapshots and dependency inputs.')
