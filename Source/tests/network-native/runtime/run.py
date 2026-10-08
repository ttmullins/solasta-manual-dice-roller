#!/usr/bin/env python3
"""Use production MP raw adapters against pristine Solasta dice/state IL."""
from pathlib import Path
import argparse, hashlib, json, shutil, subprocess, sys
here=Path(__file__).resolve().parent
project=here.parents[2]
sys.path.insert(0,str(project/'tools'))
from TestEnvironment import resolve
parser=argparse.ArgumentParser(description=__doc__)
for key in ('dotnet','sdk-root','managed','references','cecil'):parser.add_argument('--'+key,type=Path)
args=parser.parse_args()
env=resolve(project,dotnet=args.dotnet,sdk_root=args.sdk_root,managed=args.managed,references=args.references,cecil=args.cecil)
out=here/'build';out.mkdir(exist_ok=True)
def compile(name,sources,extra=(),exe=False):
    rsp=out/(name+'.rsp');rows=['-nologo','-nostdlib+','-optimize+','-langversion:7.3','-target:'+('exe' if exe else 'library'),'-out:"'+str(out/name)+'"']
    rows+=['-r:"'+str(x)+'"' for x in env.net_refs+list(extra)];rows+=['"'+str(x)+'"' for x in sources];rsp.write_text('\n'.join(rows))
    proc=subprocess.run([str(env.dotnet),str(env.compiler),'@'+str(rsp)],text=True,capture_output=True);(out/(name+'.compile.txt')).write_text(proc.stdout+proc.stderr)
    if proc.returncode:print(proc.stdout+proc.stderr,end='')
    proc.check_returncode()
    if exe:(out/(Path(name).stem+'.runtimeconfig.json')).write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
abi=(project/'tests/native/NativeDiceTypes.cs').read_text()
prefix='using System.Collections;\n'+abi[:abi.index('public static class ServiceRepository')]
tail=abi[abi.index('public static class FloatExtensions'):]
tail=tail[tail.index('public static partial class RuleDefinitions'):]
network=(here.parent/'NetworkNativeTypes.cs').read_text();supplement=network[:network.index('public static class RuleDefinitions')]
supplement=supplement[supplement.index('namespace UnityEngine'):]
supplement=supplement.replace('return (T)(object)Current;', 'return typeof(T)==typeof(IGameService)?(T)(object)Current:typeof(T)==typeof(IGameSettingsService)?(T)(object)Settings:(T)(object)Rules;')
supplement=supplement.replace('public static TestGameService Current;', 'public static TestGameService Current;public static TestSettings Settings=new TestSettings();public static TestRules Rules=new TestRules();')
supplement=supplement.replace('public static int Max(int a,int b)', 'public static int Clamp(int a,int min,int max){return a<min?min:a>max?max:a;}public static float Clamp(float a,float min,float max){return a<min?min:a>max?max:a;}public static int Max(int a,int b)')
supplement=supplement.replace('public float MaxCurveOffset{', 'public float MaxLuckModificationPerRoll{get;set;}=.1f;public float NegativePlayerAttackRollMultiplier{get;set;}=1;public float MaxCurveOffset{')
extra='''\npublic static class FloatExtensions { public static float Remap(float value,float a,float b,float c,float d){return c+(value-a)/(b-a)*(d-c);} }\npublic static class Trace {public static void LogException(System.Exception exception){}}\npublic class GameLocationBanterManager {public void UpdateImpl(){}}\n'''
(out/'NativeNetworkRuntimeTypes.cs').write_text(prefix+supplement+tail+extra)
shutil.copyfile(env.cecil,out/'Mono.Cecil.dll');shutil.copyfile(env.references/'net8-harmony/0Harmony.dll',out/'0Harmony.dll')
compile('Assembly-CSharp.dll',[out/'NativeNetworkRuntimeTypes.cs'])
for name,source in [('ExtractNativeDice',project/'tests/native/ExtractNativeDice.cs'),('ExtractNetworkNative',here.parent/'ExtractNetworkNative.cs')]:
    compile(name+'.dll',[source],[out/'Mono.Cecil.dll'],True)
    proc=subprocess.run([str(env.dotnet),str(out/(name+'.dll')),str(env.managed/'Assembly-CSharp.dll'),str(out/'Assembly-CSharp.dll')],text=True,capture_output=True)
    (out/(name+'.txt')).write_text(proc.stdout+proc.stderr)
    if proc.returncode:print(proc.stdout+proc.stderr,end='')
    proc.check_returncode();shutil.move(str(out/'Assembly-CSharp.dll.native'),str(out/'Assembly-CSharp.dll'))
snapshot=out/'source-snapshot';snapshot.mkdir(exist_ok=True)
sources=[project/'src'/name for name in ['RollBroker.cs','RuntimeRolls.cs','RollText.cs','NativeScratchScope.cs','NativeNetworkScheduler.cs']]
hashes={}
for source in sources:
    data=source.read_bytes();(snapshot/source.name).write_bytes(data);hashes[str(source.relative_to(project))]=hashlib.sha256(data).hexdigest()
compile('NetworkRuntimeTests.dll',[snapshot/source.name for source in sources]+[project/'tests/native/AdapterHost.cs',project/'tests/common/OfflineBardicHost.cs',project/'tests/common/OfflineShoveHost.cs',here/'NetworkRuntimeHost.cs',here/'NetworkRuntimeTests.cs',project/'tools/HarmonyFixtureHost.cs'],[out/'Assembly-CSharp.dll',out/'0Harmony.dll'],True)
proc=subprocess.run([str(env.dotnet),str(out/'NetworkRuntimeTests.dll')],text=True,capture_output=True)
(here/'results.txt').write_text(proc.stdout+proc.stderr);print(proc.stdout+proc.stderr,end='');proc.check_returncode()
for source in sources:
    if hashes[str(source.relative_to(project))]!=hashlib.sha256(source.read_bytes()).hexdigest():raise RuntimeError('Runtime source changed during integration suite')
(here/'source-manifest.json').write_text(json.dumps({'production_sources':hashes,'pristine_game_sha256':hashlib.sha256((env.managed/'Assembly-CSharp.dll').read_bytes()).hexdigest(),'runtime':'.NET8/Linux; real Harmony; native copied dice/RNG state/TA.Run IL','transport_and_descriptor_are_boundary_doubles':True,'scheduler_preflight_uses_genuine_separate_native_fixture':True,'photon_sessions_executed':False},indent=2)+'\n')
