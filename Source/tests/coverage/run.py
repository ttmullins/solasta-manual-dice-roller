#!/usr/bin/env python3
"""Verify registered native boundaries and execute native Bardic Inspiration IL."""
from pathlib import Path
import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys

here=Path(__file__).resolve().parent
project=here.parents[1]
sys.path.insert(0,str(project/'tools'))
from TestEnvironment import resolve
import Build

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for key in ('dotnet','sdk-root','managed','references','cecil'):
        parser.add_argument('--'+key,type=Path)
    args=parser.parse_args()
    env=resolve(project,**{key:getattr(args,key) for key in ('dotnet','sdk_root','managed','references','cecil')})
    output=here/'build';output.mkdir(exist_ok=True)
    tracked=[project/'src/RollBroker.cs',project/'src/RuntimeRolls.cs',project/'src/RollText.cs',project/'src/NativeScratchScope.cs',project/'src/BardicPatches.cs',project/'src/NativeSiteManifest.cs',project/'tools/HarmonyFixtureHost.cs',project/'tools/Build.py',project/'tools/TestEnvironment.py']+sorted(here.glob('*.cs'))+[here/'README.md',here/'run.py',project/'tests/common/OfflineNetworkHost.cs',project/'tests/common/OfflineShoveHost.cs']
    before={str(p.relative_to(project)):hashlib.sha256(p.read_bytes()).hexdigest() for p in tracked}
    snapshot=output/'snapshot';snapshot.mkdir(exist_ok=True)
    for p in tracked:
        if p.parent==project/'src':(snapshot/p.name).write_bytes(p.read_bytes())
    shutil.copyfile(env.cecil,output/'Mono.Cecil.dll')
    harmony=env.references/'net8-harmony/0Harmony.dll';shutil.copyfile(harmony,output/'0Harmony.dll')
    game=env.managed/'Assembly-CSharp.dll'
    if Build.digest(game)!=Build.BASELINE or Build.digest(harmony)!=Build.NET8_HARMONY:
        parser.error('Pinned pristine game or actual Harmony generator input differs')
    Build.compile_cs(env.dotnet,env.compiler,[here/'Inventory.cs'],env.net_refs+[env.cecil],output/'Inventory.dll',tool=True)
    def run(command,log):
        result=subprocess.run([str(x) for x in command],text=True,capture_output=True)
        log.write_text(result.stdout+result.stderr)
        if result.returncode:print(result.stdout+result.stderr,end='')
        result.check_returncode()
        return result.stdout+result.stderr
    inventory=run([env.dotnet,output/'Inventory.dll',game],output/'inventory.txt')
    owners={};owner=None
    for line in inventory.splitlines():
        if line.startswith('OWNER\t'):
            _,owner,fingerprint=line.split('\t');owners[owner]={'fingerprint':fingerprint,'calls':{}}
        elif line.startswith('CALL\t'):
            _,offset,target=line.split('\t');owners[owner]['calls'][int(offset[3:],16)]=target
    manifest=(snapshot/'NativeSiteManifest.cs').read_text()
    sites=re.findall(r'new AwaitSite\("([^"]+)","([a-f0-9]+)",0x([0-9a-f]+),"([^"]+)"(?:,true)?\)',manifest)
    if not sites:raise RuntimeError('No runtime coroutine sites found')
    for name,fingerprint,offset,identity in sites:
        actual=owners.get(name.replace('+','/'))
        if actual is None or actual['fingerprint']!=fingerprint or int(offset,16) not in actual['calls']:
            raise RuntimeError('Registered native site differs from actual PE metadata: '+identity)
    Build.compile_cs(env.dotnet,env.compiler,[here/'BardicTypes.cs'],env.net_refs,output/'Assembly-CSharp.dll')
    Build.compile_cs(env.dotnet,env.compiler,[here/'ExtractBardic.cs'],env.net_refs+[env.cecil],output/'ExtractBardic.dll',tool=True)
    extraction=run([env.dotnet,output/'ExtractBardic.dll',game,output/'Assembly-CSharp.dll'],output/'extraction.txt')
    shutil.move(str(output/'Assembly-CSharp.dll.native'),str(output/'Assembly-CSharp.dll'))
    production=['RollBroker.cs','RuntimeRolls.cs','RollText.cs','NativeScratchScope.cs','BardicPatches.cs']
    Build.compile_cs(env.dotnet,env.compiler,[snapshot/name for name in production]+[here/'BardicHost.cs',here/'BardicTests.cs',project/'tools/HarmonyFixtureHost.cs',project/'tests/common/OfflineNetworkHost.cs',project/'tests/common/OfflineShoveHost.cs'],env.net_refs+[output/'Assembly-CSharp.dll',output/'0Harmony.dll'],output/'BardicTests.dll',tool=True)
    log=run([env.dotnet,output/'BardicTests.dll'],output/'behavior.txt')
    if not re.search(r'PASS \d+ pristine native Bardic Inspiration / production adapter checks',log):raise RuntimeError('Native behavior success evidence missing')
    after={str(p.relative_to(project)):Build.digest(p) for p in tracked}
    if before!=after:raise RuntimeError('Coverage source changed during execution; result was not refreshed')
    if Build.digest(game)!=Build.BASELINE:raise RuntimeError('Pristine game input changed')
    result='PASS '+str(len(sites))+' exact registered native coroutine call offsets/body hashes independently verified.\n'+extraction+log
    report={'game_sha256':Build.BASELINE,'harmony_sha256':Build.NET8_HARMONY,'registered_sites':len(sites),'registered_owners':len({name for name,_,_,_ in sites}),'behavior_assertions':int(re.search(r'PASS (\d+) pristine',log).group(1)),'native_methods_executed':True,'condition_spending_and_UI_are_declared_boundaries':True,'live_unity_or_photon_qualified':False,'sources_before':before,'sources_after':after,'sources':after}
    (here/'results.txt').write_text(result)
    (here/'source-manifest.json').write_text(json.dumps(report,indent=2)+'\n')
    print(result,end='')

if __name__=='__main__':main()
