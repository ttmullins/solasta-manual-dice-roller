#!/usr/bin/env python3
"""Exercise actual Solasta RNG state and replicated rest IL behind managed Unity boundaries."""
from pathlib import Path
import argparse, hashlib, json, shutil, subprocess, sys
here=Path(__file__).resolve().parent
project=here.parents[1]
sys.path.insert(0,str(project/'tools'))
from TestEnvironment import resolve
parser=argparse.ArgumentParser(description=__doc__)
for key in ('dotnet','sdk-root','managed','references','cecil'): parser.add_argument('--'+key,type=Path)
args=parser.parse_args()
env=resolve(project,dotnet=args.dotnet,sdk_root=args.sdk_root,managed=args.managed,references=args.references,cecil=args.cecil)
out=here/'build';out.mkdir(exist_ok=True)
def compile(name,sources,extra=(),exe=False):
    rsp=out/(name+'.rsp')
    rows=['-nologo','-nostdlib+','-optimize+','-langversion:7.3','-target:'+('exe' if exe else 'library'),'-out:"'+str(out/name)+'"']
    rows+=['-r:"'+str(x)+'"' for x in env.net_refs+list(extra)]
    rows+=['"'+str(x)+'"' for x in sources]
    rsp.write_text('\n'.join(rows))
    proc=subprocess.run([str(env.dotnet),str(env.compiler),'@'+str(rsp)],text=True,capture_output=True)
    (out/(name+'.compile.txt')).write_text(proc.stdout+proc.stderr)
    if proc.returncode: print(proc.stdout+proc.stderr,end='')
    proc.check_returncode()
    if exe:(out/(Path(name).stem+'.runtimeconfig.json')).write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
shutil.copyfile(env.cecil,out/'Mono.Cecil.dll')
compile('Assembly-CSharp.dll',[here/'NetworkNativeTypes.cs'])
compile('ExtractNetworkNative.dll',[here/'ExtractNetworkNative.cs'],[out/'Mono.Cecil.dll'],True)
proc=subprocess.run([str(env.dotnet),str(out/'ExtractNetworkNative.dll'),str(env.managed/'Assembly-CSharp.dll'),str(out/'Assembly-CSharp.dll')],text=True,capture_output=True)
(here/'extracted-methods.txt').write_text(proc.stdout+proc.stderr)
if proc.returncode:print(proc.stdout+proc.stderr,end='')
proc.check_returncode();shutil.move(str(out/'Assembly-CSharp.dll.native'),str(out/'Assembly-CSharp.dll'))
compile('InventoryNetwork.dll',[here/'InventoryNetwork.cs'],[out/'Mono.Cecil.dll'],True)
proc=subprocess.run([str(env.dotnet),str(out/'InventoryNetwork.dll'),str(env.managed/'Assembly-CSharp.dll')],text=True,capture_output=True)
(here/'inventory.txt').write_text(proc.stdout+proc.stderr)
if proc.returncode:print(proc.stdout+proc.stderr,end='')
proc.check_returncode()
compile('NetworkNativeTests.dll',[here/'NetworkNativeTests.cs'],[out/'Assembly-CSharp.dll'],True)
proc=subprocess.run([str(env.dotnet),str(out/'NetworkNativeTests.dll')],text=True,capture_output=True)
(here/'results.txt').write_text(proc.stdout+proc.stderr);print(proc.stdout+proc.stderr,end='');proc.check_returncode()
(here/'source-manifest.json').write_text(json.dumps({'production_sources':{},'pristine_game_sha256':hashlib.sha256((env.managed/'Assembly-CSharp.dll').read_bytes()).hexdigest(),'runtime':'.NET8/Linux; native copied Solasta IL; Unity random API is a managed boundary double','photon_sessions_executed':False},indent=2)+'\n')

# A second process resolves the genuine game/Unity types, without ABI stubs.
scheduler=out/'scheduler';scheduler.mkdir(exist_ok=True)
shutil.copyfile(env.references/'net8-harmony/0Harmony.dll',scheduler/'0Harmony.dll')
old_out=out
out=scheduler
source=project/'src/NativeNetworkScheduler.cs';data=source.read_bytes();snapshot=scheduler/source.name;snapshot.write_bytes(data)
compile('SchedulerNativeTests.dll',[snapshot,here/'SchedulerNativeTests.cs',project/'tools/HarmonyFixtureHost.cs'],[env.managed/'Assembly-CSharp.dll',env.managed/'UnityEngine.CoreModule.dll',scheduler/'0Harmony.dll'],True)
proc=subprocess.run([str(env.dotnet),str(scheduler/'SchedulerNativeTests.dll'),str(env.managed)],text=True,capture_output=True)
(here/'scheduler-results.txt').write_text(proc.stdout+proc.stderr);print(proc.stdout+proc.stderr,end='');proc.check_returncode()
# Full plugin patch emission requires native Unity APIs on the real game host.
# Replace only those external dependency methods by throwing methods in this
# second qualification process. The original game main assembly is unchanged.
compile('ShimUnityInternalCalls.dll',[here/'ShimUnityInternalCalls.cs'],[old_out/'Mono.Cecil.dll'],True)
shutil.copyfile(old_out/'Mono.Cecil.dll',scheduler/'Mono.Cecil.dll')
shim=scheduler.parent/'scheduler-shim';shim.mkdir(exist_ok=True)
for name in ('SchedulerNativeTests.dll','SchedulerNativeTests.runtimeconfig.json','0Harmony.dll'):shutil.copyfile(scheduler/name,shim/name)
proc=subprocess.run([str(env.dotnet),str(scheduler/'ShimUnityInternalCalls.dll'),str(env.managed/'UnityEngine.CoreModule.dll'),str(shim/'UnityEngine.CoreModule.dll')],text=True,capture_output=True)
(here/'unity-qualification.txt').write_text(proc.stdout+proc.stderr);print(proc.stdout+proc.stderr,end='');proc.check_returncode()
proc=subprocess.run([str(env.dotnet),str(shim/'SchedulerNativeTests.dll'),str(env.managed),'shim'],text=True,capture_output=True)
(here/'scheduler-shim-results.txt').write_text(proc.stdout+proc.stderr);print(proc.stdout+proc.stderr,end='');proc.check_returncode()
if 'PASS complete production scheduler Install: 6 pristine game targets' not in proc.stdout:raise RuntimeError('Full Unity dependency qualification did not exercise production Install')
if data != source.read_bytes(): raise RuntimeError('Scheduler source changed during native integration tests')
manifest=json.loads((here/'source-manifest.json').read_text());manifest['production_sources']['src/NativeNetworkScheduler.cs']=hashlib.sha256(data).hexdigest();manifest['scheduler_fixture']='genuine pristine TA.Coroutine.Run, actual Unity state layout; no scheduler ABI double'
(here/'source-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
