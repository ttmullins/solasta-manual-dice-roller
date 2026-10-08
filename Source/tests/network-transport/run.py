#!/usr/bin/env python3
"""Production transport/protocol/facade with genuine Photon ABI and a queued room double."""
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
out=here/'build'; out.mkdir(exist_ok=True)
sources=[project/'src'/name for name in ('MultiplayerProtocol.cs','MultiplayerTransport.cs','MultiplayerRolls.cs')]
snapshot=out/'source-snapshot'; snapshot.mkdir(exist_ok=True)
hashes={}
for source in sources:
 data=source.read_bytes(); (snapshot/source.name).write_bytes(data); hashes[str(source.relative_to(project))]=hashlib.sha256(data).hexdigest()
sources=[snapshot/source.name for source in sources]
def compile(name,sources,refs,executable=False):
 rsp=out/(name+'.rsp'); lines=['-nologo','-nostdlib+','-optimize+','-langversion:7.3','-target:'+('exe' if executable else 'library'),'-out:"'+str(out/name)+'"']
 lines+=['-r:"'+str(path)+'"' for path in refs]; lines+=['"'+str(path)+'"' for path in sources]; rsp.write_text('\n'.join(lines))
 result=subprocess.run([str(env.dotnet),str(env.compiler),'@'+str(rsp)],text=True,capture_output=True)
 (out/(name+'.compile.txt')).write_text(result.stdout+result.stderr)
 if result.returncode: print(result.stdout+result.stderr,end='')
 result.check_returncode()
compile('PhotonAbiProbe.dll',sources+[here/'AbiHost.cs'],sorted(env.managed.glob('*.dll'))+[env.references/'UnityModManager.dll',env.references/'0Harmony.dll'])
photon=[env.managed/name for name in ('Photon3Unity3D.dll','PhotonRealtime.dll','PhotonUnityNetworking.dll','UnityEngine.CoreModule.dll')]
for file in photon: shutil.copyfile(file,out/file.name)
compile('TransportTests.dll',sources+[here/'FixtureHost.cs',here/'TransportTests.cs'],env.net_refs+photon,True)
(out/'TransportTests.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
result=subprocess.run([str(env.dotnet),str(out/'TransportTests.dll')],text=True,capture_output=True)
(here/'results.txt').write_text(result.stdout+result.stderr); print(result.stdout+result.stderr,end=''); result.check_returncode()
(here/'source-manifest.json').write_text(json.dumps({'production_sources':hashes,'pristine_game_sha256':hashlib.sha256((env.managed/'Assembly-CSharp.dll').read_bytes()).hexdigest(),'photon_assembly_hashes':{file.name:hashlib.sha256(file.read_bytes()).hexdigest() for file in photon},'abi_probe':'All genuine Solasta/Unity/Photon/UMM/Harmony references','executed_fixture':'Actual Photon EventData and IOnEventCallback; injected queued room gateway; game, UI, Plugin are host doubles; no live Photon server'},indent=2)+'\n')
