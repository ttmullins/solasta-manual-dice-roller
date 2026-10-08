// Read pristine native IL through the actual pinned Harmony implementation.
// Does not claim execution in Unity or install patches in the user's game.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HarmonyLib;

static class AuditHarmonyRuntime
{
    static readonly List<Assembly> native=new List<Assembly>();
    static string managed,references;
    static Assembly Resolve(object sender,ResolveEventArgs args)
    {
        var name=new AssemblyName(args.Name).Name+".dll";
        foreach(var directory in new[]{managed,references}) {
            var path=Path.Combine(directory,name);
            if(File.Exists(path)) try{return Assembly.LoadFrom(path);}catch{}
        }
        return null;
    }
    static Type Find(string name)
    {
        name=name.Replace('/','+');
        foreach(var assembly in native) {var type=assembly.GetType(name,false);if(type!=null)return type;}
        return null;
    }
    static string MetadataType(Type type)
    {
        if(type.IsByRef)return MetadataType(type.GetElementType())+"&";
        if(type.IsPointer)return MetadataType(type.GetElementType())+"*";
        if(type.IsArray)return MetadataType(type.GetElementType())+"["+new string(',',type.GetArrayRank()-1)+"]";
        if(type.IsGenericType)return type.GetGenericTypeDefinition().FullName.Replace('+','/')+"<"+
            string.Join(",",type.GetGenericArguments().Select(MetadataType))+">";
        return type.FullName.Replace('+','/');
    }
    static int Main(string[] args)
    {
        if(args.Length<3||args.Length>5){Console.Error.WriteLine("AuditHarmonyRuntime managed-dir reference-dir bindings.txt [NativeSavePlanner.cs [compiled-plugin.dll]]");return 2;}
        managed=Path.GetFullPath(args[0]);references=Path.GetFullPath(args[1]);
        AppDomain.CurrentDomain.AssemblyResolve+=Resolve;
        foreach(var path in Directory.GetFiles(managed,"*.dll")) {
            var name=Path.GetFileName(path);
            if(name=="mscorlib.dll"||name.StartsWith("System",StringComparison.Ordinal)||name=="netstandard.dll"||name=="Mono.Security.dll")continue;
            try{native.Add(Assembly.LoadFrom(path));}catch(BadImageFormatException){}
        }
        int checkedCount=0,errors=0,total=0;
        var names=File.ReadLines(args[2]).Where(l=>l.StartsWith("TARGET_ABI ",StringComparison.Ordinal)).Select(l=>l.Substring(11).Split('|'))
            .Select(p=>new{Type=p[0],Method=p[1],Parameters=p[2].Length==0?new string[0]:p[2].Split(';')})
            .GroupBy(t=>t.Type+"::"+t.Method+"/"+string.Join(";",t.Parameters)).Select(g=>g.First()).ToArray();
        if(names.Length==0){Console.Error.WriteLine("No exact native TARGET_ABI signatures found in binding report");return 1;}
        foreach(var name in names) {
            try {
                var type=Find(name.Type);if(type==null)throw new Exception("Missing target "+name.Type);
                var methods=type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly)
                    .Where(m=>m.Name==name.Method && m.GetParameters().Select(p=>MetadataType(p.ParameterType)).SequenceEqual(name.Parameters)).ToArray();
                if(methods.Length!=1)throw new Exception("Ambiguous/missing target "+name.Method);
                var target=methods[0];
                var original=PatchProcessor.GetOriginalInstructions(target);
                var current=PatchProcessor.GetCurrentInstructions(target);
                if(original.Count!=current.Count||!original.Select(i=>i.opcode).SequenceEqual(current.Select(i=>i.opcode)))throw new Exception("Unpatched instruction shape differs");
                checkedCount++;total+=original.Count;
                Console.WriteLine("READ "+name.Type+"::"+name.Method+" "+original.Count+" actual Harmony instructions; "+original.Sum(i=>i.blocks.Count)+" exception markers");
            }catch(Exception error){errors++;Console.WriteLine("ERROR "+name.Type+"::"+name.Method+" "+error);}
        }
        if(args.Length>=4) {
            var sources=new[]{args[3],Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[3])),"NativeAbilityPlanner.cs")};
            foreach(var sourcePath in sources) {
            if(!File.Exists(sourcePath)){errors++;Console.WriteLine("ERROR Foreign-patch guard source missing: "+sourcePath);continue;}
            var source=File.ReadAllText(sourcePath);int guarded=0;
            foreach(Match group in Regex.Matches(source,@"Named\(typeof\(([^\)]+)\),([^\)]+)\)")) {
                string typeName=group.Groups[1].Value;var type=Find(typeName);
                foreach(Match name in Regex.Matches(group.Groups[2].Value,"\"([^\"]+)\"")) {
                    var found=type==null?new MethodInfo[0]:type.GetMethods(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Where(m=>m.Name==name.Groups[1].Value).ToArray();
                    if(found.Length==0){errors++;Console.WriteLine("ERROR Foreign-patch guard silently matches no method: "+typeName+"."+name.Groups[1].Value);}
                    else {guarded++;Console.WriteLine("GUARD "+typeName+"."+name.Groups[1].Value+" "+found.Length+" real native method(s)");}
                }
            }
            Console.WriteLine(Path.GetFileNameWithoutExtension(sourcePath)+" guard literal names validated: "+guarded+" nonempty selectors.");
            }
        }
        if(args.Length==5) {
            try {
                var plugin=Assembly.LoadFrom(Path.GetFullPath(args[4]));
                var target=RuntimeHelpers.GetUninitializedObject(Find("RulesetCharacterHero"));
                var managerType=Find("RulesetImplementationManager");
                var manager=RuntimeHelpers.GetUninitializedObject(managerType);
                var method=managerType.GetMethod("TryRollSavingThrow",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                var parameters=method.GetParameters();var invocationArgs=new object[parameters.Length+1];invocationArgs[0]=manager;
                for(int i=0;i<parameters.Length;i++)if(parameters[i].Name=="target")invocationArgs[i+1]=target;
                var invocationType=plugin.GetType("ManualDiceRoller.NativeInvocation",true);
                var invocation=Activator.CreateInstance(invocationType,new object[]{new object(),"CompiledPluginActualMetadata",method,invocationArgs,1L});
                var guard=plugin.GetType("ManualDiceRoller.NativeSavePlanner",true).GetMethod("ForeignPatchGuard",BindingFlags.NonPublic|BindingFlags.Static);
                if(!(bool)guard.Invoke(null,new object[]{invocation,target,null}))throw new Exception("Compiled guard rejected pristine full-game metadata");
                Console.WriteLine("PASS compiled runtime plugin foreign-patch guard accepts actual pristine game/Unity metadata; no engine calls or save preview executed.");
                var locationType=Find("GameLocationCharacter");
                var location=RuntimeHelpers.GetUninitializedObject(locationType);
                var abilityMethod=locationType.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
                    .Single(m=>m.Name=="RollAbilityCheck"&&m.GetParameters().Length==12);
                var abilityArgs=new object[abilityMethod.GetParameters().Length+1];abilityArgs[0]=location;
                var abilityInvocation=Activator.CreateInstance(invocationType,new object[]{new object(),"CompiledPluginActualAbilityMetadata",abilityMethod,abilityArgs,1L});
                var abilityGuard=plugin.GetType("ManualDiceRoller.NativeAbilityPlanner",true).GetMethod("ForeignPatchGuard",BindingFlags.NonPublic|BindingFlags.Static);
                if(!(bool)abilityGuard.Invoke(null,new object[]{abilityInvocation,location}))throw new Exception("Compiled ability guard rejected pristine full-game metadata");
                Console.WriteLine("PASS compiled runtime ability guard accepts actual pristine GameLocationCharacter metadata; no engine calls or ability preview executed.");
                foreach(var owner in new[]{"NativeNetworkScheduler","MultiplayerRolls"}) {
                    var preflight=plugin.GetType("ManualDiceRoller."+owner,true).GetMethod("Preflight",BindingFlags.NonPublic|BindingFlags.Static);
                    var values=new object[]{null};
                    if(preflight==null || !(bool)preflight.Invoke(null,values))throw new Exception("Compiled "+owner+" preflight rejected pristine full-game metadata: "+values[0]);
                    Console.WriteLine("PASS compiled runtime "+owner+" preflight accepts actual pristine game/Photon method hashes and ABI; no engine or network operation executed.");
                }
            }catch(Exception error){errors++;Console.WriteLine("ERROR compiled save guard smoke "+error);}
        }
        Console.WriteLine("Actual Harmony pristine IL read audit: "+checkedCount+" native methods; "+total+" instructions; "+errors+" errors. This validates metadata/IL reads, not Unity runtime patch execution.");
        return errors==0?0:1;
    }
}
