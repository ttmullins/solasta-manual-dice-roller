using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Security;
using HarmonyLib;
using ManualDiceRoller;

class FullGeneration
{
    static MethodInfo Own(string name)
    { return typeof(NativePatches).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static); }

    static void Main(string[] args)
    {
        string managed=Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve+=(sender,request)=> {
            string path=Path.Combine(managed,new AssemblyName(request.Name).Name+".dll");
            return File.Exists(path)?Assembly.LoadFrom(path):null;
        };
        bool shim=args[1]=="shim";
        if(shim) Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,"UnityEngine.CoreModule.dll"));
        string error;
        if(!NativePatches.Preflight(out error))throw new Exception(error);
        HarmonyFixtureHost.Initialize(typeof(Harmony).Assembly);
        if(shim)
        {
            var harmony=new Harmony("await.full.install.regression");
            NativePatches.Install(harmony);
            Console.WriteLine("PASS actual NativePatches.Install: 30 native owners, 46 await sites, native disposal/TA reset hooks");
            harmony.UnpatchAll("await.full.install.regression");
            Console.WriteLine("PASS main rollback: all manual await and cleanup hooks removed");
            if(!NativePatches.Preflight(out error))throw new Exception("Reload preflight: "+error);
            Console.WriteLine("PASS native reload preflight: 5 neutral fault normalizers retained");
            return;
        }

        // First run with wholly unmodified game/Unity assemblies records the
        // precise CoreCLR limitation instead of concealing it behind a shim.
        var bound=(IDictionary)typeof(NativePatches).GetField("bound",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
        var compatibility=new Harmony("ManualDiceRoller.FaultCompatibility");
        var main=new Harmony("await.full.original.regression");
        int passed=0,blocked=0;
        foreach(DictionaryEntry entry in bound)
        {
            var method=(MethodBase)entry.Key;
            bool fault=false;
            foreach(var handler in method.GetMethodBody().ExceptionHandlingClauses)
                if(handler.Flags==ExceptionHandlingClauseOptions.Fault)fault=true;
            try
            {
                if(fault)compatibility.Patch(method,transpiler:new HarmonyMethod(Own("NormalizeFault")){priority=Priority.Last});
                main.Patch(method,transpiler:new HarmonyMethod(Own("Transpile")),finalizer:fault?null:new HarmonyMethod(Own("MoveFinalizer")));
                passed++;
                Console.WriteLine("PASS original owner: "+method.DeclaringType.FullName);
            }
            catch(SecurityException exception)
            {
                string owner=method.DeclaringType.FullName;
                if(!exception.Message.Contains("ECall")||
                    (owner!="CharacterActionAttack+<ExecuteImpl>d__7"&&owner!="CharacterActionMagicEffect+<MagicEffectExecuteOnTargets>d__74"))throw;
                blocked++;
                Console.WriteLine("BLOCKED by native UnityEngine.Component.get_transform InternalCall: "+owner);
            }
        }
        if(passed!=28||blocked!=2)throw new Exception("Unexpected original dependency qualification: "+passed+" generated, "+blocked+" blocked");
        Console.WriteLine("PASS original dependency qualification: 28 generated; 2 require unavailable native Unity InternalCall");
    }
}
