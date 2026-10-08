// Read-only audit of production patch parameters against pristine game metadata.
// This deliberately does not use fixture target parameter names.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class AuditHarmonyBindings
{
    sealed class Registration
    {
        internal string TargetType,TargetMethod,Owner,Prefix,Postfix,Transpiler,Finalizer;
        internal string[] TargetParameters;
    }
    static int errors,bindings,parameters;
    static readonly List<Registration> all=new List<Registration>();
    static void Add(string target,string method,string owner,string prefix=null,string postfix=null,string transpiler=null,string finalizer=null,string[] arguments=null)
    { all.Add(new Registration{TargetType=target.Replace('+','/'),TargetMethod=method,Owner="ManualDiceRoller."+owner,Prefix=prefix,Postfix=postfix,Transpiler=transpiler,Finalizer=finalizer,TargetParameters=arguments}); }
    static IEnumerable<TypeDefinition> Types(ModuleDefinition module)
    { return module.Types.SelectMany(Flatten); }
    static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    { yield return type; foreach(var nested in type.NestedTypes) foreach(var child in Flatten(nested)) yield return child; }
    static TypeReference Value(TypeReference type) {return type is ByReferenceType ? ((ByReferenceType)type).ElementType : type;}
    static bool Accepts(TypeReference patch,TypeReference target)
    {
        patch=Value(patch);target=Value(target);
        if(patch.FullName==target.FullName||patch.FullName=="System.Object"&&!target.IsValueType) return true;
        try {for(var t=target.Resolve();t!=null;t=t.BaseType==null?null:t.BaseType.Resolve())
            if(t.FullName==patch.FullName||t.Interfaces.Any(i=>i.InterfaceType.FullName==patch.FullName)) return true;}catch{}
        return false;
    }
    static void Error(string message) {errors++;Console.WriteLine("ERROR "+message);}
    static void Check(MethodDefinition target,MethodDefinition patch,string role,Registration registration,TypeDefinition owner)
    {
        bindings++;
        if(!patch.IsStatic) Error(patch.FullName+" is not static");
        if(role=="transpiler")
        {
            var allowed=new[]{"System.Collections.Generic.IEnumerable`1<HarmonyLib.CodeInstruction>","System.Reflection.Emit.ILGenerator","System.Reflection.MethodBase"};
            foreach(var p in patch.Parameters) if(!allowed.Contains(p.ParameterType.FullName)) Error(patch.FullName+": unknown transpiler injection "+p.ParameterType.FullName);
            if(patch.ReturnType.FullName!=allowed[0]) Error(patch.FullName+": invalid transpiler result");
            return;
        }
        foreach(var p in patch.Parameters)
        {
            parameters++;
            TypeReference expected=null;string source=p.Name;
            if(p.Name=="__instance") {if(target.IsStatic) Error(patch.FullName+": __instance on static target");else expected=target.DeclaringType;}
            else if(p.Name=="__result") {expected=target.ReturnType;if(expected.FullName=="System.Void")Error(patch.FullName+": __result on void target");}
            else if(p.Name=="__exception") {expected=patch.Module.ImportReference(typeof(Exception));if(role!="finalizer")Error(patch.FullName+": __exception outside finalizer");}
            else if(p.Name=="__state")
            {
                if(role=="prefix") {if(!(p.ParameterType is ByReferenceType)) Error(patch.FullName+": prefix __state is not byref");expected=Value(p.ParameterType);}
                else {
                    var prefix=owner.Methods.SingleOrDefault(m=>m.Name==registration.Prefix);
                    var state=prefix==null?null:prefix.Parameters.SingleOrDefault(v=>v.Name=="__state");
                    if(state==null) Error(patch.FullName+": no same-class prefix __state for "+target.FullName);
                    else expected=Value(state.ParameterType);
                }
            }
            else if(p.Name=="__args") expected=patch.Module.ImportReference(typeof(object[]));
            else if(p.Name=="__originalMethod") expected=patch.Module.ImportReference(typeof(System.Reflection.MethodBase));
            else if(p.Name=="__runOriginal") expected=patch.Module.ImportReference(typeof(bool));
            else if(p.Name.StartsWith("___",StringComparison.Ordinal))
            {
                var name=p.Name.Substring(3);FieldDefinition field=null;
                for(var t=target.DeclaringType;t!=null&&field==null;t=t.BaseType==null?null:t.BaseType.Resolve()) field=t.Fields.SingleOrDefault(f=>f.Name==name);
                if(field==null) Error(patch.FullName+": missing injected field "+name);else expected=field.FieldType;
            }
            else
            {
                var argument=p.CustomAttributes.FirstOrDefault(a=>a.AttributeType.FullName=="HarmonyLib.HarmonyArgument");
                ParameterDefinition original=null;int index;
                if(argument!=null&&argument.ConstructorArguments.Count>0) {
                    object mapping=argument.ConstructorArguments[0].Value;
                    if(mapping is string) source=(string)mapping; else if(mapping is int) source="__"+mapping;
                }
                if(source.StartsWith("__",StringComparison.Ordinal)&&int.TryParse(source.Substring(2),out index)) {
                    if(index>=0&&index<target.Parameters.Count) original=target.Parameters[index];
                } else original=target.Parameters.SingleOrDefault(v=>v.Name==source);
                if(original==null) Error(patch.FullName+": injected argument '"+p.Name+"' does not exist on "+target.FullName+"; real names: "+string.Join(",",target.Parameters.Select(v=>v.Name)));
                else {expected=original.ParameterType;source=original.Name;}
            }
            if(expected!=null&&!Accepts(p.ParameterType,expected)) Error(patch.FullName+": '"+p.Name+"' type "+p.ParameterType.FullName+" cannot receive "+expected.FullName);
            if(expected!=null&&p.ParameterType is ByReferenceType&&Value(p.ParameterType).FullName!=Value(expected).FullName)
                Error(patch.FullName+": byref type must match "+expected.FullName);
            Console.WriteLine("BIND "+target.DeclaringType.FullName+"."+target.Name+" -> "+owner.Name+"."+patch.Name+"("+p.Name+" <= "+source+" : "+(expected==null?"MISSING":expected.FullName)+")");
        }
        var result=patch.ReturnType.FullName;
        if(role=="prefix"&&result!="System.Void"&&result!="System.Boolean") Error(patch.FullName+": invalid prefix result "+result);
        if(role=="finalizer"&&result!="System.Void"&&result!="System.Exception") Error(patch.FullName+": invalid finalizer result "+result);
    }
    static void Registrations(string source)
    {
        Add("RulesetActor","RollDie","RuntimeRolls","ActorPrefix",transpiler:"FaceTranspiler",finalizer:"ActorFinalizer");
        Add("RulesetActor","RerollDieAsNeeded","RuntimeRolls","RerollPrefix",transpiler:"FaceTranspiler",finalizer:"RerollFinalizer");
        Add("RuleDefinitions","RollStaticDiceAndSum","RuntimeRolls","StaticSumPrefix",transpiler:"FaceTranspiler",finalizer:"StaticSumFinalizer");
        Add("RulesetActor","RollDiceAndSum","RuntimeRolls","ActorSumPrefix");
        Add("RulesetActor","RollSavingThrow","NativeSavePlanner","SavePrefix");
        Add("RuleDefinitions","RollDie","NativeSavePlanner","RawPreviewPrefix");
        Add("RuleDefinitions","RollStaticDiceAndSum","NativeSavePlanner","SumPrefix",finalizer:"SumFinalizer");
        Add("RulesetActor","ComputeSavingThrowModifier","NativeSavePlanner","ModifierPrefix",finalizer:"ModifierFinalizer");
        Add("RulesetCharacter","ComputeSavingThrowModifierImposedOnTarget","NativeSavePlanner","ModifierPrefix",finalizer:"ModifierFinalizer");
        Add("RulesetCharacter","RollAbilityCheck","NativeAbilityPlanner","AbilityPrefix");
        Add("RuleDefinitions","RollDie","NativeAbilityPlanner","RawPreviewPrefix");
        Add("RuleDefinitions","RollStaticDiceAndSum","NativeAbilityPlanner","SumPrefix",finalizer:"SumFinalizer");
        Add("GameLocationCharacter","ComputeAbilityCheckActionModifier","NativeAbilityPlanner","ModifierPrefix",finalizer:"ModifierFinalizer");
        Add("RulesetCharacter","RollBardicInspirationDie","BardicPatches",transpiler:"Transpile");
        Add("RulesetCharacter","ResolveContestCheck","ShovePatches","ContestPrefix");
        Add("RuleDefinitions","RollStaticDiceAndSum","ShovePatches","RejectPreviewSum");
        Add("RuleDefinitions","RollDie","ShovePatches","RejectPreviewDraw");
        Add("RulesetImplementationManager","ApplyEffectForm","EffectPatches","FormPrefix",finalizer:"FormFinalizer");
        Add("RulesetActor","RollDamage","EffectPatches","DamagePrefix",finalizer:"DrawFinalizer");
        Add("RulesetCharacter","RollAndHeal","EffectPatches","HealPrefix",finalizer:"DrawFinalizer");
        Add("RulesetCharacter","RollForTemporaryHitPoints","EffectPatches","TemporaryPrefix",finalizer:"DrawFinalizer");
        Add("RuleDefinitions","RollStaticDiceAndSum","EffectPatches","StaticSumPrefix",finalizer:"DrawFinalizer");
        Add("RuleDefinitions","RollStaticDamage","EffectPatches","StaticDamagePrefix",finalizer:"StaticDamageFinalizer");
        Add("RulesetImplementationManagerLocation","ApplyCounterForm","EffectPatches",transpiler:"CounterTranspiler");
        Add("RulesetCharacter","OnConditionAdded","EffectPatches",transpiler:"BardicConditionTranspiler");
        Add("HitDiceRestItem","OnRollCb","RestPatches","OnRollPrefix");
        Add("HitDiceRestItem","Bind","RestPatches","BindingPrefix");
        Add("HitDiceRestItem","Unbind","RestPatches","BindingPrefix");
        Add("HitDiceRestItem","HitDieRolled","RestPatches","CallbackPrefix");
        Add("RulesetCharacterHero","ComputeAndApplyHitDieRoll","RestPatches","ComputePrefix",transpiler:"ComputeTranspiler",finalizer:"ComputeFinalizer");
        Add("View","HandleInputs","InputGuards","ViewPrefix");
        Add("DebugOverlayManager","IsMouseCovered","InputGuards","CoveredPrefix");
        Add("GuiManager","OnCommandJustActivated","InputGuards","CommandsPrefix");
        Add("GuiManager","get_GrabbedCameraControls","InputGuards",postfix:"CameraPostfix");
        Add("InputPointerModule","Process","InputGuards","PointerPrefix");
        Add("InputPointerModule","RaycastPointer","InputGuards","RaycastPrefix");
        Add("GameLocationManager","EnterLocation","InputGuards","LifecyclePrefix");
        Add("GameLocationManager","ExitLocation","InputGuards","LifecyclePrefix");
        Add("GameManager","DestroyGame","InputGuards","LifecyclePrefix");
        Add("TA.Coroutine","Reset","NativePatches","CoroutineResetPrefix");
        Add("TA.Coroutine","Run","NativeNetworkScheduler","BeforeRun",postfix:"AfterRun",finalizer:"RunFinalizer");
        Add("GameLocationBanterManager","UpdateImpl","NativeNetworkScheduler","BeforeBanterUpdate");
        Add("TA.DeterministicRandom","Range","NativeNetworkScheduler","BeforeRandomDraw",arguments:new[]{"System.Int32","System.Int32"});
        Add("TA.DeterministicRandom","Range","NativeNetworkScheduler","BeforeRandomDraw",arguments:new[]{"System.Single","System.Single"});
        Add("TA.DeterministicRandom","get_value","NativeNetworkScheduler","BeforeRandomDraw",arguments:new string[0]);
        Add("NetworkTick","UpdateTimeManipulation","NativeNetworkScheduler",postfix:"HoldTicksPostfix",arguments:new string[0]);
        // Single-player deferred rolls, probes, synchronous-pump guards and notices.
        Add("GameLocationCharacter","CheckConcentration","DeferredRolls","ConcentrationPrefix");
        Add("GameTravelScheduler","ExecuteFoodSearch","DeferredRolls","FoodSearchPrefix");
        Add("GameTravelScheduler","ExecuteIngredientGathering","DeferredRolls","IngredientPrefix");
        Add("GameCampActivity","Update","DeferredRolls","CampUpdatePrefix");
        Add("GameCampaign","UpdateTime","DeferredRolls","HeldPrefix");
        Add("GameLocationBattle","Update","DeferredRolls","HeldPrefix");
        Add("RulesetActor","ProcessConditionsMatchingOccurenceType","DeferredRolls","OccurrencePrefix",finalizer:"OccurrenceFinalizer");
        Add("RulesetActor","SaveToCancelCondition","DeferredRolls","RepeatSavePrefix");
        Add("GameTravelScheduler","ExecuteEncounter","DeferredRolls","EncounterPrefix");
        Add("GameLocationCharacter","RefreshStealthForBattleTurnEnd","OuterSites","StealthTurnEndPrefix");
        Add("FunctorStealthBreaker","Execute","OuterSites","StealthFunctorPrefix");
        Add("GameTravelScheduler","RollSurprise","DeferredRolls",transpiler:"SurpriseTranspiler");
        Add("GameTravelScheduler","RollSurprise","NativeProbe","MethodPrefix");
        Add("GameTravelJournal","AddEntry","NativeProbe","StopPrefix");
        Add("GameLoreManager","RollIMonsterdentificationCheck","DeferredRolls","IdentificationPrefix");
        Add("RulesetActor","RollDie","NativeProbe","ActorDiePrefix");
        Add("RulesetCharacter","RollAbilityCheck","NativeProbe","MethodPrefix");
        Add("RuleDefinitions","RollDie","NativeProbe","RawPrefix");
        Add("RuleDefinitions","RollStaticDiceAndSum","NativeProbe","SumPrefix",finalizer:"SumFinalizer");
        Add("RulesetActor","ComputeSavingThrowModifier","NativeProbe",postfix:"SaveModifierPostfix");
        Add("IEnumeratorExtensions","ExecuteUntilDone","SyncPumpGuard","Enter",finalizer:"Exit");
        Add("TA.Coroutine","RunUntilIsFinished","SyncPumpGuard","Enter",finalizer:"Exit");
        Add("RulesetActor","RollDie","AutoRollNotices",postfix:"RollPostfix");
        // 0.7: composite anchors, turn starts, lingering effects, damage saves,
        // feature dice, stealth/Perception, travel crafting and character screens.
        Add("GameTravelScheduler","ExecuteCrafting","DeferredRolls","TravelCraftingPrefix");
        Add("RulesetActor","SaveToCancelCondition","CompositeRolls","ConditionAnchor",finalizer:"AnchorFinalizer");
        Add("RulesetActor","ExecuteRecurrentForms","CompositeRolls","RecurrentAnchor",finalizer:"AnchorFinalizer");
        Add("RulesetEffectSpell","ApplyEffectOnCharacter","CompositeRolls","EffectAnchor",finalizer:"AnchorFinalizer");
        Add("RulesetEffectPower","ApplyEffectOnCharacter","CompositeRolls","EffectAnchor",finalizer:"AnchorFinalizer");
        Add("RulesetEffectEnvironment","ApplyEffectOnCharacter","CompositeRolls","EffectAnchor",finalizer:"AnchorFinalizer");
        Add("RulesetEffectEnvironment","ApplyEffectOnCharacter","TurnRolls","EffectPrefix");
        // 0.8: traps and hazards.
        Add("FunctorEnvironmentEffect","Execute","TrapRolls","ExecutePrefix");
        Add("GameLocationEnvironmentManager","TriggerEnvironmentEffect","TrapRolls","TriggerPrefix",arguments:new[]{"RulesetEffectEnvironment"});
        Add("RulesetImplementationManager","ApplyEffectForms","TrapRolls","FormsAnchor",finalizer:"AnchorFinalizer");
        Add("RulesetImplementationManager","TryRollSavingThrow","TrapRolls","SaveAnchor",finalizer:"AnchorFinalizer");
        Add("GameLocationBattle","NextTurn","TurnRolls",transpiler:"NextTurnTranspiler");
        Add("GameLocationBattle","StartFirstTurnOfRound","TurnRolls",transpiler:"FirstTurnTranspiler");
        Add("GameLocationCharacter","StartBattleTurn","TurnRolls","StartTurnPrefix",finalizer:"StartTurnFinalizer");
        Add("RulesetEffectSpell","ApplyEffectOnCharacter","TurnRolls","EffectPrefix");
        Add("RulesetEffectPower","ApplyEffectOnCharacter","TurnRolls","EffectPrefix");
        Add("RulesetActor","ExecuteRecurrentForms","TurnRolls","RecurrentPrefix");
        Add("RulesetActor","ProcessConditionsMatchingInterruption","TurnRolls","InterruptionPrefix");
        // 0.8: turn-start raw dice (recharge, random behaviour).
        Add("RulesetCharacter","RechargePowersForTurnStart","TurnRolls","RechargeAnchor",transpiler:"RechargeTranspiler",finalizer:"AnchorFinalizer");
        Add("BattleState_TurnInitialize","Begin","TurnRolls","TurnInitializeAnchor",transpiler:"TurnInitializeTranspiler",finalizer:"AnchorFinalizer");
        Add("CharacterActionDeflectMissile+<ExecuteImpl>d__1","MoveNext","FeatureRolls",transpiler:"DeflectTranspiler");
        Add("CharacterActionCastSpell+<CheckExecutionFailure>d__19","MoveNext","FeatureRolls",transpiler:"FailureTranspiler");
        Add("RulesetCharacter","SpendSpellSlot","FeatureRolls",transpiler:"SlotTranspiler");
        Add("RulesetCharacterHero","UseDeviceFunction","FeatureRolls",transpiler:"DeviceTranspiler");
        Add("CharacterActionCastSpell","CheckExecutionFailure","FeatureRolls","FailurePrefix");
        Add("FunctorSetGadgetConditionByAbilityCheck","Execute","OuterSites","GadgetExecutePrefix");
        Add("CharacterStageAbilityScoresPanel","OnReRollCb","MenuRolls","RerollPrefix");
        Add("CharacterStageAbilityScoresPanel","EnterStage","MenuRolls","EnterPrefix",postfix:"EnterPostfix",finalizer:"EnterFinalizer");
        Add("RuleDefinitions","RollDiceAndSumBest","MenuRolls","SumBestPrefix");
        Add("RulesetCharacterHero","AddClassLevel","MenuRolls",postfix:"AddLevelPostfix");
        Add("RulesetCharacterHero","RemoveClassLevel","MenuRolls",postfix:"RemoveLevelPostfix");
        Add("CharacterBuildingManager","FinalizeCharacter","MenuRolls",postfix:"FinalizePostfix");
        var manifest=File.ReadAllText(Path.Combine(source,"NativeSiteManifest.cs"));
        foreach(var iterator in Regex.Matches(manifest,"new AwaitSite\\(\"([^\"]+)\"").Cast<Match>().Select(m=>m.Groups[1].Value).Distinct()) {
            Add(iterator,"MoveNext","NativePatches",transpiler:"Transpile",finalizer:"MoveFinalizer");
            Add(iterator,"System.IDisposable.Dispose","NativePatches","DisposePrefix");
        }
    }
    static int Main(string[] args)
    {
        if(args.Length!=4&&args.Length!=5) {Console.Error.WriteLine("AuditHarmonyBindings plugin.dll managed-dir reference-dir source-dir [--negative-name|--negative-type]");return 2;}
        bool negative=args.Length==5;
        if(negative&&args[4]!="--negative-name"&&args[4]!="--negative-type")return 2;
        Registrations(args[3]);
        var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(args[1]);resolver.AddSearchDirectory(args[2]);
        var settings=new ReaderParameters{AssemblyResolver=resolver};
        using(var plugin=AssemblyDefinition.ReadAssembly(args[0],settings))
        using(var game=AssemblyDefinition.ReadAssembly(Path.Combine(args[1],"Assembly-CSharp.dll"),settings))
        {
            var nativeTypes=Types(game.MainModule).ToDictionary(t=>t.FullName);
            foreach(var path in Directory.GetFiles(args[1],"*.dll").Where(p=>Path.GetFileName(p)!="Assembly-CSharp.dll")) {
                try {var assembly=AssemblyDefinition.ReadAssembly(path,settings);foreach(var type in Types(assembly.MainModule)) if(!nativeTypes.ContainsKey(type.FullName)) nativeTypes.Add(type.FullName,type);}catch(BadImageFormatException){}
            }
            var patchTypes=Types(plugin.MainModule).ToDictionary(t=>t.FullName);
            // Production installs an independent fault-normalization transpiler
            // for these precise native bodies and omits their MoveNext finalizer.
            foreach(var registration in all.Where(r=>r.Owner=="ManualDiceRoller.NativePatches"&&r.TargetMethod=="MoveNext").ToArray()) {
                TypeDefinition type;
                if(nativeTypes.TryGetValue(registration.TargetType,out type)&&type.Methods.Single(m=>m.Name=="MoveNext").Body.ExceptionHandlers.Any(h=>h.HandlerType==ExceptionHandlerType.Fault)) {
                    registration.Finalizer=null;
                    Add(registration.TargetType,"MoveNext","NativePatches",transpiler:"NormalizeFault");
                }
            }
            // In-memory mutations prove the audit catches fixture-name drift
            // and a mistyped injection. The plugin file is never changed.
            if(negative) {
                var parameter=patchTypes["ManualDiceRoller.EffectPatches"].Methods.Single(m=>m.Name=="StaticSumPrefix").Parameters.Single(p=>p.Name=="diceType");
                if(args[4]=="--negative-name")parameter.Name="dieType";
                else parameter.ParameterType=plugin.MainModule.TypeSystem.Int32;
            }
            // Fail closed when installation changes without updating this
            // transparent registration inventory. Loops are expanded above.
            var expectedCalls=new Dictionary<string,int>{{"NativePatches",4},{"RuntimeRolls",4},{"NativeSavePlanner",5},{"NativeAbilityPlanner",4},{"BardicPatches",1},{"ShovePatches",3},{"NativeNetworkScheduler",4},{"EffectPatches",8},{"RestPatches",5},{"InputGuards",7},
                {"DeferredRolls",12},{"NativeProbe",7},{"SyncPumpGuard",2},{"AutoRollNotices",1},{"OuterSites",3},{"CompositeRolls",5},{"TurnRolls",10},{"FeatureRolls",5},{"MenuRolls",6},{"TrapRolls",4}};
            foreach(var owner in patchTypes.Values) {
                var install=owner.Methods.SingleOrDefault(m=>m.Name=="Install");
                if(install==null||!install.HasBody)continue;
                int count=install.Body.Instructions.Count(i=>i.Operand is MethodReference&&((MethodReference)i.Operand).DeclaringType.FullName=="HarmonyLib.Harmony"&&((MethodReference)i.Operand).Name=="Patch");
                if(count==0)continue;
                int expected;
                if(!expectedCalls.TryGetValue(owner.Name,out expected)||count!=expected) Error("Install registration inventory changed: "+owner.FullName+" has "+count+" Harmony.Patch callsites");
                var known=all.Where(r=>r.Owner==owner.FullName).SelectMany(r=>new[]{r.Prefix,r.Postfix,r.Transpiler,r.Finalizer}).Where(n=>n!=null).Distinct().ToArray();
                var targetNames=all.Where(r=>r.Owner==owner.FullName).Select(r=>r.TargetMethod).ToArray();
                foreach(var name in install.Body.Instructions.Where(i=>i.OpCode==OpCodes.Ldstr).Select(i=>(string)i.Operand).Where(n=>owner.Methods.Any(m=>m.Name==n)))
                    if(!known.Contains(name)&&!targetNames.Contains(name)) Error("Unlisted patch helper in Install: "+owner.FullName+"."+name);
            }
            var load=patchTypes["ManualDiceRoller.Plugin"].Methods.Single(m=>m.Name=="Load");
            var installation=load.Body.Instructions.Select(i=>i.Operand as MethodReference).Where(m=>m!=null&&m.Name=="Install").Select(m=>m.DeclaringType.FullName).ToArray();
            var installed=new[]{"NativePatches","RuntimeRolls","EffectPatches","RestPatches","InputGuards","NativeNetworkScheduler","ModFeatures"};
            foreach(var name in installed)
                if(installation.Count(n=>n=="ManualDiceRoller."+name)!=1) Error("Plugin.Load must install "+name+" exactly once");
            if(installation.Length!=installed.Length) Error("Plugin.Load installation inventory changed");
            var featureInstall=patchTypes["ManualDiceRoller.ModFeatures"].Methods.Single(m=>m.Name=="Install");
            foreach(var helper in new[]{"SyncPumpGuard","NativeProbe","DeferredRolls","AutoRollNotices","OuterSites","CompositeRolls","TurnRolls","FeatureRolls","MenuRolls","TrapRolls"})
                if(featureInstall.Body.Instructions.Count(i=>i.Operand is MethodReference&&((MethodReference)i.Operand).DeclaringType.FullName=="ManualDiceRoller."+helper&&((MethodReference)i.Operand).Name=="Install")!=1)
                    Error("ModFeatures.Install must install "+helper+" exactly once");
            var runtimeInstall=patchTypes["ManualDiceRoller.RuntimeRolls"].Methods.Single(m=>m.Name=="Install");
            foreach(var helper in new[]{"NativeSavePlanner","NativeAbilityPlanner","BardicPatches","ShovePatches"})
                if(runtimeInstall.Body.Instructions.Count(i=>i.Operand is MethodReference&&((MethodReference)i.Operand).DeclaringType.FullName=="ManualDiceRoller."+helper&&((MethodReference)i.Operand).Name=="Install")!=1)
                    Error("RuntimeRolls.Install must install "+helper+" exactly once");
            foreach(var registration in all) {
                TypeDefinition native,owner;
                if(!nativeTypes.TryGetValue(registration.TargetType,out native)) {Error("Missing target type "+registration.TargetType);continue;}
                var targets=native.Methods.Where(m=>m.Name==registration.TargetMethod && (registration.TargetParameters==null ||
                    m.Parameters.Select(p=>p.ParameterType.FullName).SequenceEqual(registration.TargetParameters))).ToArray();
                if(targets.Length!=1) {Error("Target lookup is ambiguous/missing: "+registration.TargetType+"."+registration.TargetMethod+" count="+targets.Length);continue;}
                if(!patchTypes.TryGetValue(registration.Owner,out owner)) {Error("Missing patch type "+registration.Owner);continue;}
                Console.WriteLine("TARGET "+targets[0].FullName+" names="+string.Join(",",targets[0].Parameters.Select(p=>p.Name)));
                Console.WriteLine("TARGET_ABI "+targets[0].DeclaringType.FullName+"|"+targets[0].Name+"|"+string.Join(";",targets[0].Parameters.Select(p=>p.ParameterType.FullName)));
                foreach(var pair in new[]{new[]{"prefix",registration.Prefix},new[]{"postfix",registration.Postfix},new[]{"transpiler",registration.Transpiler},new[]{"finalizer",registration.Finalizer}}) {
                    if(pair[1]==null)continue;
                    var patches=owner.Methods.Where(m=>m.Name==pair[1]).ToArray();
                    if(patches.Length!=1) {Error("Missing/ambiguous patch "+registration.Owner+"."+pair[1]);continue;}
                    Check(targets[0],patches[0],pair[0],registration,owner);
                }
            }
        }
        Console.WriteLine("Real-metadata Harmony binding audit: "+all.Count+" registrations; "+bindings+" patch bindings; "+parameters+" injected parameters; "+errors+" errors.");
        if(negative) {Console.WriteLine("Negative audit self-test "+args[4]+": "+(errors==1?"PASS":"FAIL")+" (expected exactly one deliberately introduced error).");return errors==1?0:1;}
        return errors==0?0:1;
    }
}
