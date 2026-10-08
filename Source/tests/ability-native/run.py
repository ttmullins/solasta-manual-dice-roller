#!/usr/bin/env python3
"""Run production ability planner against copied pristine wrappers and dice IL."""
from pathlib import Path
import sys, json, hashlib, shutil, subprocess, argparse
here=Path(__file__).resolve().parent
standalone=here.parents[1]
sys.path.insert(0,str(standalone/'tools'))
from TestEnvironment import resolve
parser=argparse.ArgumentParser(description=__doc__)
for key in ('dotnet','sdk-root','managed','references','cecil'): parser.add_argument('--'+key,type=Path)
args=parser.parse_args()
env=resolve(standalone,dotnet=args.dotnet,sdk_root=args.sdk_root,managed=args.managed,references=args.references,cecil=args.cecil)
out=here/'build';out.mkdir(exist_ok=True)
def compile(name,sources,extra=(),exe=False):
    rsp=out/(name+'.rsp')
    rsp.write_text('\n'.join(['-nologo','-nostdlib+','-optimize+','-langversion:7.3','-target:'+('exe' if exe else 'library'),'-out:"'+str(out/name)+'"']+['-r:"'+str(p)+'"' for p in env.net_refs+list(extra)]+['"'+str(p)+'"' for p in sources]))
    r=subprocess.run([str(env.dotnet),str(env.compiler),'@'+str(rsp)],text=True,capture_output=True)
    (out/(name+'.compile.txt')).write_text(r.stdout+r.stderr)
    if r.returncode: print(r.stdout+r.stderr,end='')
    r.check_returncode()
    if exe: (out/(Path(name).stem+'.runtimeconfig.json')).write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
def remove_block(text,start,end):
    a=text.index(start); b=text.index(end,a);return text[:a]+text[b:]
def method(text,signature):
    start=text.index(signature);brace=text.index('{',start);depth=1;i=brace+1
    while depth:
        depth+=(text[i]=='{')-(text[i]=='}');i+=1
    return text[start:i]
types=(here.parent/'native/NativeDiceTypes.cs').read_text()
types=remove_block(types,'public class FeatureDefinitionAbilityCheckAffinity','public interface IDieRollModificationProvider')
types=remove_block(types,'public class RulesetCondition','public class FeatureDefinitionFeatureSet')
types=types.replace('public class FeatureDefinition { public string Name { get; set; } = ""; }','public class FeatureDefinition : BaseDefinition { }')
types=types.replace('public class RulesetActor\n','public partial class RulesetActor : RulesetEntity\n').replace('public class RulesetCharacter :','public partial class RulesetCharacter :').replace('public class RulesetCharacterHero :','public partial class RulesetCharacterHero :')
types=types.replace('public bool IsProficient(string skill)','public virtual bool IsProficient(string skill)')
old=method(types,'    public int RollAbilityCheck(string abilityScoreName');types=types.replace(old,'')
types=types.replace('public RulesetCharacter RulesetCharacter;','public RulesetCharacter RulesetCharacter { get; set; }')
types=types.replace('public enum AbilityCheckContext { None }','public enum AbilityCheckContext { None, NotWearingHeavyArmor }')
types=types.replace('public enum FeatureSourceType { None, MonsterFeature }','public enum FeatureSourceType { None, MonsterFeature, CharacterFeature, AbilityScore, Proficiency, Difficulty, Condition }')
types=types.replace('AutomaticSuccess }','AutomaticSuccess, HalfProficiencyWhenNotProficient }')
types=types.replace('public struct FeatureOrigin { }','public struct FeatureOrigin { public FeatureSourceType sourceType; public string sourceName; public object source; public string tags; }')
types=types.replace('public struct TrendInfo { public int value; public TrendInfo(int amount) { value = amount; } }','public struct TrendInfo { public int value; public FeatureSourceType sourceType; public string sourceName; public object source; public DieType dieType; public TrendInfoDieFlag dieFlag; public TrendInfo(int amount) : this(amount, FeatureSourceType.None, "", null) {} public TrendInfo(int amount,FeatureSourceType type,string name,object obj) { value=amount;sourceType=type;sourceName=name;source=obj;dieType=DieType.D1;dieFlag=TrendInfoDieFlag.None; } }')
types=types.replace('bool GroupedMonsterInitiatives { get; }','bool GroupedMonsterInitiatives { get; } int AbilityCheckAllyModifier { get; } int AbilityCheckEnemyModifier { get; }')
types=types.replace('bool GroupedMonsterInitiatives { get; set; }','bool GroupedMonsterInitiatives { get; set; } public int AbilityCheckAllyModifier { get; set; } public int AbilityCheckEnemyModifier { get; set; }')
types=types.replace('conditions.Clear(); }','conditions.Clear(); conditions.AddRange(Conditions); }')
types=types.replace('origins[f] = new RuleDefinitions.FeatureOrigin();','origins[f] = new RuleDefinitions.FeatureOrigin { sourceType=RuleDefinitions.FeatureSourceType.CharacterFeature,sourceName=f.Name,source=f };')
(out/'NativeAbilityTypes.cs').write_text(types)
shutil.copyfile(env.cecil,out/'Mono.Cecil.dll');shutil.copyfile(env.references/'net8-harmony/0Harmony.dll',out/'0Harmony.dll')
compile('Assembly-CSharp.dll',[out/'NativeAbilityTypes.cs',here/'AbilityTypes.cs'])
compile('ExtractNativeAbility.dll',[here/'ExtractNativeAbility.cs'],[out/'Mono.Cecil.dll'],True)
r=subprocess.run([str(env.dotnet),str(out/'ExtractNativeAbility.dll'),str(env.managed/'Assembly-CSharp.dll'),str(out/'Assembly-CSharp.dll')],text=True,capture_output=True)
(here/'extracted-methods.txt').write_text(r.stdout+r.stderr)
if r.returncode: print(r.stdout+r.stderr,end='')
r.check_returncode();shutil.move(str(out/'Assembly-CSharp.dll.native'),str(out/'Assembly-CSharp.dll'))
sources=[standalone/'src'/p for p in ['RollBroker.cs','RuntimeRolls.cs','RollText.cs','NativeScratchScope.cs','NativeAbilityPlanner.cs','NativeSavePlanner.cs']]
sources += [standalone/"tests/common/OfflineNetworkHost.cs",standalone/"tests/common/OfflineBardicHost.cs",standalone/"tests/common/OfflineShoveHost.cs"]
snapshot=out/'source-snapshot';snapshot.mkdir(exist_ok=True)
for p in sources:shutil.copyfile(p,snapshot/p.name)
save=(snapshot/'NativeSavePlanner.cs').read_text()
helpers='\n'.join(method(save,'        internal static '+s) for s in ['bool NativeInterfaceMethods','bool NativeMethod','IEnumerable<MethodInfo> Named','bool NativeRuntimeMethod'])
(out/'NativeSaveHelper.cs').write_text('using System;using System.Collections.Generic;using System.Linq;using System.Reflection;using HarmonyLib;namespace ManualDiceRoller { internal static class NativeSavePlanner { internal static bool Previewing=>false;internal static bool AllowsStaticBonus=>true;internal static bool TryPrepare(NativeInvocation c,NativeRollPlan p)=>false;internal static void Install(Harmony h) {}\n'+helpers+'\n}}')
compile('AbilityNativeTests.dll',[snapshot/p.name for p in sources if p.name!='NativeSavePlanner.cs']+[out/'NativeSaveHelper.cs',here/'AbilityHost.cs',here/'AbilityTests.cs',standalone/'tools/HarmonyFixtureHost.cs'],[out/'Assembly-CSharp.dll',out/'0Harmony.dll'],True)
r=subprocess.run([str(env.dotnet),str(out/'AbilityNativeTests.dll')],text=True,capture_output=True)
(here/'results.txt').write_text(r.stdout+r.stderr);print(r.stdout+r.stderr,end='');r.check_returncode()
(here/'source-manifest.json').write_text(json.dumps({'production_sources':{str(p.relative_to(standalone)):hashlib.sha256((snapshot/p.name).read_bytes()).hexdigest() for p in sources},'pristine_game_sha256':hashlib.sha256((env.managed/'Assembly-CSharp.dll').read_bytes()).hexdigest(),'boundary':'net8 Linux real Harmony; stock native wrappers/providers/dice; terminal VerifyAbilityCheck, armor/inventory/conditions storage and UI explicit doubles; Save producer stub with exact guard helpers'},indent=2)+'\n')
# Full pristine metadata smoke: fixture ABI must not hide inherited Unity guards.
actual=out/'actual';actual.mkdir(exist_ok=True)
game_refs=[]
for path in env.managed.glob('*.dll'):
    if path.name in {'mscorlib.dll','netstandard.dll','System.dll'} or path.name.startswith('System.'): continue
    game_refs.append(path);shutil.copyfile(path,actual/path.name)
shutil.copyfile(out/'0Harmony.dll',actual/'0Harmony.dll')
rsp=actual/'ActualGuardSmoke.rsp'
rsp.write_text('\n'.join(['-nologo','-nostdlib+','-optimize+','-langversion:7.3','-target:exe','-out:"'+str(actual/'ActualGuardSmoke.dll')+'"']+['-r:"'+str(p)+'"' for p in env.net_refs+game_refs+[out/'0Harmony.dll']]+['"'+str(p)+'"' for p in [snapshot/p.name for p in sources if p.name!='NativeSavePlanner.cs']]+['"'+str(p)+'"' for p in [out/'NativeSaveHelper.cs',here/'AbilityHost.cs',here/'ActualGuardSmoke.cs']]))
r=subprocess.run([str(env.dotnet),str(env.compiler),'@'+str(rsp)],text=True,capture_output=True)
(actual/'compile.txt').write_text(r.stdout+r.stderr)
if r.returncode:print(r.stdout+r.stderr,end='')
r.check_returncode()
(actual/'ActualGuardSmoke.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
r=subprocess.run([str(env.dotnet),str(actual/'ActualGuardSmoke.dll')],text=True,capture_output=True)
with (here/'results.txt').open('a') as log:log.write(r.stdout+r.stderr)
print(r.stdout+r.stderr,end='');r.check_returncode()
