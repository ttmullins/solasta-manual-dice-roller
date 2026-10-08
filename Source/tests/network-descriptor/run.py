#!/usr/bin/env python3
"""Exercise production wire descriptors over actual pristine native actor records."""
from pathlib import Path
import argparse
import hashlib
import json
import shutil
import subprocess
import sys

here=Path(__file__).resolve().parent
project=here.parents[1]
sys.path.insert(0,str(project/'tools'))
from TestEnvironment import resolve
parser=argparse.ArgumentParser(description=__doc__)
for key in ('dotnet','sdk-root','managed','references','cecil'):
    parser.add_argument('--'+key,type=Path)
args=parser.parse_args()
env=resolve(project,dotnet=args.dotnet,sdk_root=args.sdk_root,managed=args.managed,
            references=args.references,cecil=args.cecil)
output=here/'build';output.mkdir(exist_ok=True)
game_refs=[]
for path in env.managed.glob('*.dll'):
    if path.name in {'mscorlib.dll','netstandard.dll','System.dll'} or path.name.startswith('System.'):
        continue
    game_refs.append(path);shutil.copyfile(path,output/path.name)
sources=[project/'src/NativeNetworkDescriptor.cs',project/'src/RollBroker.cs',
         here/'DescriptorHost.cs',here/'DescriptorTests.cs']
response=output/'DescriptorTests.rsp'
response.write_text('\n'.join(['-nologo','-nostdlib+','-optimize+','-langversion:7.3','-target:exe',
    '-out:"'+str(output/'DescriptorTests.dll')+'"']+
    ['-r:"'+str(p)+'"' for p in env.net_refs+game_refs]+
    ['"'+str(p)+'"' for p in sources]))
result=subprocess.run([str(env.dotnet),str(env.compiler),'@'+str(response)],text=True,capture_output=True)
(output/'compile.txt').write_text(result.stdout+result.stderr)
if result.returncode:print(result.stdout+result.stderr,end='')
result.check_returncode()
(output/'DescriptorTests.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{
    'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
result=subprocess.run([str(env.dotnet),str(output/'DescriptorTests.dll')],text=True,capture_output=True)
(here/'results.txt').write_text(result.stdout+result.stderr)
print(result.stdout+result.stderr,end='');result.check_returncode()
(here/'source-manifest.json').write_text(json.dumps({
    'production_sources':{str(p.relative_to(project)):hashlib.sha256(p.read_bytes()).hexdigest()
        for p in sources[:2]},'fixture_sources':{str(p.relative_to(project)):hashlib.sha256(p.read_bytes()).hexdigest()
        for p in sources[2:]},'pristine_game_sha256':hashlib.sha256((env.managed/'Assembly-CSharp.dll').read_bytes()).hexdigest(),
    'scope':'Actual pristine cached actor/attribute/condition getters on CoreCLR; containing data records are doubles; no native gameplay, RNG or Unity renderer.'
},sort_keys=True,indent=2)+'\n')
