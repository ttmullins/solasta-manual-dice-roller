using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace ManualDiceRoller
{
    // Rest has a synchronous UI command, so collect before reentering that
    // preserved command. The game still owns healing, spent dice, and callbacks.
    internal static class RestPatches
    {
        internal const string Site = "RestHitDice";
        internal const string NetworkSite = "RestHitDice.Network";
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static FieldInfo characterField, bonusesField, canRollField, spentBonusField, heroSpentField, heroBonusesField;
        static MethodInfo onRoll, bind, unbind, rollHitDie, compute, localRoll, hitDieRolled;
        static readonly Dictionary<HitDiceRestItem, Pending> pending = new Dictionary<HitDiceRestItem, Pending>();
        [ThreadStatic] static Pending executing;
        // Native healing notifications may close or rebind the rest item before
        // LocalCommandManager invokes its captured UI callback. Keep callback
        // ownership until that synchronous command returns, including resets.
        [ThreadStatic] static Pending callbackOwner;
        [ThreadStatic] static int nativeDrawDepth;
        [ThreadStatic] static bool drawEligible;

        sealed class Pending
        {
            internal HitDiceRestItem Item;
            internal Snapshot State;
            internal IEnumerator Driver;
            internal Coroutine Coroutine;
            internal bool Cancelled, NativeStarted;
            internal readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
        }
        sealed class BonusState
        {
            internal RestHealingBonus Bonus;
            internal RuleDefinitions.DieType Die;
            internal int Amount;
            internal RulesetCharacterHero Source;
            internal IRestHealingModificationProvider Provider;
            internal BonusState(RestHealingBonus bonus)
            { Bonus=bonus; Die=bonus.Die; Amount=bonus.HealAmount; Source=bonus.SourceCharacter; Provider=bonus.Provider; }
            internal bool Matches(RestHealingBonus bonus)
            { return ReferenceEquals(Bonus,bonus) && Die==bonus.Die && Amount==bonus.HealAmount && ReferenceEquals(Source,bonus.SourceCharacter) && ReferenceEquals(Provider,bonus.Provider); }
        }
        sealed class HealingState
        {
            internal FeatureDefinition Feature;
            internal bool Advantage, Kindred;
            internal HealingState(FeatureDefinition feature)
            { Feature=feature; var provider=(IHealingModificationProvider)feature; Advantage=provider.AdvantageOnHitDieSpending; Kindred=provider.HitDiceHealsKindred; }
            internal bool Matches(FeatureDefinition feature)
            { var provider=feature as IHealingModificationProvider; return ReferenceEquals(Feature,feature) && provider!=null && Advantage==provider.AdvantageOnHitDieSpending && Kindred==provider.HitDiceHealsKindred; }
        }
        sealed class Snapshot
        {
            internal RulesetCharacterHero Hero;
            internal ulong Guid;
            internal int HP, MaxHP, Constitution;
            internal bool CanRoll, BonusSpent, Dead, Maximum;
            internal ICommandService Command;
            internal IGameSettingsService GameSettings;
            internal List<CharacterClassDefinition> Classes;
            internal CharacterClassDefinition[] ClassOrder;
            internal RuleDefinitions.DieType[] ClassDice;
            internal Dictionary<RuleDefinitions.DieType,int> Spent;
            internal KeyValuePair<RuleDefinitions.DieType,int>[] SpentValues;
            internal List<RestHealingBonus> Bonuses, ItemBonuses;
            internal BonusState[] BonusValues, ItemBonusValues;
            internal HealingState[] Features;
            internal RuleDefinitions.DieType SelectedDie;
            internal RuleDefinitions.AdvantageType Advantage;
            internal bool HealKindred;
            internal bool IsCurrent(HitDiceRestItem item)
            {
                if(item==null || !ReferenceEquals(characterField.GetValue(item),Hero) ||
                    CanRoll!=(bool)canRollField.GetValue(item) || BonusSpent!=(bool)spentBonusField.GetValue(item) ||
                    !MatchesBonuses(ItemBonuses,bonusesField.GetValue(item),ItemBonusValues)) return false;
                return IsActorCurrent();
            }
            internal bool IsActorCurrent()
            {
                if(Hero.Guid!=Guid || HP!=Hero.CurrentHitPoints || MaxHP!=Hero.GetAttribute("HitPoints").CurrentValue ||
                    Constitution!=Hero.GetAttribute("Constitution").CurrentValue || Dead!=Hero.IsDeadOrDying ||
                    !ReferenceEquals(Command,ServiceRepository.GetService<ICommandService>()) ||
                    !ReferenceEquals(GameSettings,ServiceRepository.GetService<IGameSettingsService>()) ||
                    Maximum!=(GameSettings!=null && GameSettings.MaxHpOnHitDice)) return false;
                if(!ReferenceEquals(Classes,Hero.ClassesHistory) || Classes.Count!=ClassOrder.Length) return false;
                for(int i=0;i<ClassOrder.Length;i++) if(!ReferenceEquals(Classes[i],ClassOrder[i]) || Classes[i].HitDice!=ClassDice[i]) return false;
                var spent=heroSpentField.GetValue(Hero) as Dictionary<RuleDefinitions.DieType,int>;
                if(!ReferenceEquals(Spent,spent) || spent.Count!=SpentValues.Length) return false;
                foreach(var value in SpentValues) { int observed; if(!spent.TryGetValue(value.Key,out observed) || observed!=value.Value) return false; }
                if(!MatchesBonuses(Bonuses,heroBonusesField.GetValue(Hero),BonusValues)) return false;
                var features=HealingFeatures(Hero);
                if(features.Count!=Features.Length) return false;
                for(int i=0;i<features.Count;i++) if(!Features[i].Matches(features[i])) return false;
                return true;
            }
        }
        static List<FeatureDefinition> HealingFeatures(RulesetCharacterHero hero)
        {
            var features=new List<FeatureDefinition>();
            using(var scratch=NativeScratchScope.ForActor(hero)) hero.EnumerateFeaturesToBrowse<IHealingModificationProvider>(features);
            return features;
        }
        static bool MatchesBonuses(List<RestHealingBonus> expected,object observed,BonusState[] values)
        {
            var list=observed as List<RestHealingBonus>;
            if(!ReferenceEquals(expected,list) || list==null || list.Count!=values.Length) return false;
            for(int i=0;i<values.Length;i++) if(!values[i].Matches(list[i])) return false;
            return true;
        }
        static Snapshot Capture(HitDiceRestItem item)
        {
            var hero=characterField.GetValue(item) as RulesetCharacterHero;
            var state=CaptureActor(hero);
            var itemBonuses=bonusesField.GetValue(item) as List<RestHealingBonus>;
            if(state==null || itemBonuses==null) return null;
            state.CanRoll=(bool)canRollField.GetValue(item); state.BonusSpent=(bool)spentBonusField.GetValue(item);
            state.ItemBonuses=itemBonuses; state.ItemBonusValues=itemBonuses.Select(b=>new BonusState(b)).ToArray();
            return state;
        }
        static Snapshot CaptureActor(RulesetCharacterHero hero)
        {
            if(hero==null) return null;
            var classes=hero.ClassesHistory;
            var spent=heroSpentField.GetValue(hero) as Dictionary<RuleDefinitions.DieType,int>;
            var bonuses=heroBonusesField.GetValue(hero) as List<RestHealingBonus>;
            if(classes==null || spent==null || bonuses==null) return null;
            var features=HealingFeatures(hero);
            var settings=ServiceRepository.GetService<IGameSettingsService>();
            var state=new Snapshot { Hero=hero, Guid=hero.Guid, HP=hero.CurrentHitPoints,
                MaxHP=hero.GetAttribute("HitPoints").CurrentValue, Constitution=hero.GetAttribute("Constitution").CurrentValue,
                Dead=hero.IsDeadOrDying,
                Command=ServiceRepository.GetService<ICommandService>(), GameSettings=settings, Maximum=settings!=null&&settings.MaxHpOnHitDice,
                Classes=classes, ClassOrder=classes.ToArray(), ClassDice=classes.Select(c=>c.HitDice).ToArray(),
                Spent=spent, SpentValues=spent.ToArray(), Bonuses=bonuses, BonusValues=bonuses.Select(b=>new BonusState(b)).ToArray(),
                Features=features.Select(f=>new HealingState(f)).ToArray() };
            var available=new Dictionary<RuleDefinitions.DieType,int>();
            foreach(var die in state.ClassDice) { int count; available.TryGetValue(die,out count); available[die]=count+1; }
            int max=0;
            foreach(var pair in available) { int count; spent.TryGetValue(pair.Key,out count); int faces=RuntimeRolls.Max(pair.Key); if(count<pair.Value && faces>max) { state.SelectedDie=pair.Key; max=faces; } }
            state.Advantage=features.Cast<IHealingModificationProvider>().Any(f=>f.AdvantageOnHitDieSpending)?RuleDefinitions.AdvantageType.Advantage:RuleDefinitions.AdvantageType.None;
            state.HealKindred=features.Cast<IHealingModificationProvider>().Any(f=>f.HitDiceHealsKindred);
            return state;
        }
        internal static bool TryPrepare(NativeInvocation call,NativeRollPlan plan)
        {
            // The native network RPC already orders this coroutine on every
            // peer and recovers its original local callback before the await.
            // Prepare here, rather than sending a second UI click or RPC.
            if(call.Site==NetworkSite && call.Method.DeclaringType==typeof(LocalCommandManager) && call.Method.Name=="RollHitDie")
            {
                if(!Plugin.Settings.HitDice) return true;
                var actor=call.Arg("hero") as RulesetCharacterHero;
                var snapshot=CaptureActor(actor);
                if(snapshot==null || snapshot.Dead || snapshot.HP>=snapshot.MaxHP) return true;
                plan.RequireManualState(()=>Plugin.Settings.HitDice);
                plan.RequireNativeState(()=>ReferenceEquals(call.Arg("hero"),actor)&&snapshot.IsActorCurrent());
                AddPreparedDice(plan,snapshot);
                return true;
            }
            if(call.Site!=Site || call.Method!=onRoll) return false;
            if(!Plugin.Settings.HitDice) return true;
            plan.RequireManualState(()=>Plugin.Settings.HitDice);
            var item=call.Owner as HitDiceRestItem; Pending request;
            if(item==null || !pending.TryGetValue(item,out request) || request.Cancelled || !request.State.IsCurrent(item)) return true;
            AddPreparedDice(plan,request.State);
            return true;
        }
        static void AddPreparedDice(NativeRollPlan plan,Snapshot state)
        {
            if(state.Maximum || RuntimeRolls.Max(state.SelectedDie)<=1) return;
            plan.AddStaticDice(state.Hero,state.SelectedDie,1,title:"Hit die",note:"Short rest healing",hint:"Enter the raw face. The game adds Constitution and healing bonuses.",advantage:state.Advantage,role:"HitDie");
            int ordinal=0;
            foreach(var bonus in state.BonusValues) if((int)bonus.Die!=0)
                plan.AddStaticDice(state.Hero,bonus.Die,1,title:"Rest healing bonus",hint:"Enter the raw bonus die.",advantage:state.Advantage,role:"RestBonus"+(ordinal++));
        }
        static bool OnRollPrefix(HitDiceRestItem __instance)
        {
            if(executing!=null && ReferenceEquals(executing.Item,__instance)) return true;
            if(!Plugin.Enabled || Plugin.Networked() || !Plugin.Settings.HitDice) return true;
            if(pending.ContainsKey(__instance)) return false;
            Snapshot state;
            try { state=Capture(__instance); }
            catch(Exception error) { Plugin.Log("Rest preparation used the native roll: "+error.Message); return true; }
            if(state==null || !(state.Command is LocalCommandManager) || !state.CanRoll || state.Dead || state.HP>=state.MaxHP || state.Maximum || RuntimeRolls.Max(state.SelectedDie)<=1) return true;
            var request=new Pending { Item=__instance, State=state };
            pending.Add(__instance,request);
            request.Driver=Run(request);
            try { request.Coroutine=__instance.StartCoroutine(request.Driver); }
            catch(Exception error) {
                Cancel(__instance);
                Plugin.Log("Rest coroutine startup failed: "+error.Message);
                // Unity may advance the driver before returning its handle. If
                // the preserved command already began, retrying the original
                // click would spend a second die after a late startup failure.
                return !request.NativeStarted;
            }
            return false;
        }
        static IEnumerator Run(Pending request)
        {
            try
            {
                var wait=RuntimeRolls.PrepareNativeRoll(request.Item,Site,onRoll,new object[]{request.Item});
                if(wait!=null) request.Stack.Push(wait);
                while(request.Stack.Count>0 && !request.Cancelled)
                {
                    var next=request.Stack.Peek();
                    if(!next.MoveNext()) { request.Stack.Pop(); var finished=next as IDisposable; if(finished!=null) finished.Dispose(); continue; }
                    var child=next.Current as IEnumerator;
                    if(child!=null) { request.Stack.Push(child); continue; }
                    yield return next.Current;
                }
                if(request.Cancelled || !Plugin.Enabled || Plugin.Networked() || !request.State.IsCurrent(request.Item)) yield break;
                pending.Remove(request.Item);
                var previousCallback=callbackOwner;
                callbackOwner=request;
                executing=request;
                RuntimeRolls.EnterNative(request.Item,Site);
                try { request.NativeStarted=true; onRoll.Invoke(request.Item,null); }
                finally { RuntimeRolls.ExitNative(request.Item,Site); executing=null; callbackOwner=previousCallback; }
            }
            finally
            {
                DisposeStack(request); RuntimeRolls.CancelOwner(request.Item);
                Pending current; if(pending.TryGetValue(request.Item,out current) && ReferenceEquals(current,request)) pending.Remove(request.Item);
            }
        }
        static void DisposeStack(Pending request)
        { while(request.Stack.Count>0) { var iterator=request.Stack.Pop() as IDisposable; if(iterator!=null) try { iterator.Dispose(); } catch(Exception error) { Plugin.Log("Rest cancellation cleanup: "+error.Message); } } }
        static void Cancel(HitDiceRestItem item)
        {
            Pending request; if(!pending.TryGetValue(item,out request)) return;
            request.Cancelled=true; pending.Remove(item);
            DisposeStack(request); RuntimeRolls.CancelOwner(item);
            if(request.Coroutine!=null && item!=null) try { item.StopCoroutine(request.Coroutine); } catch(Exception error) { Plugin.Log("Rest coroutine cleanup: "+error.Message); }
            var driver=request.Driver as IDisposable; if(driver!=null) try { driver.Dispose(); } catch(Exception error) { Plugin.Log("Rest driver cleanup: "+error.Message); }
        }
        static void BindingPrefix(HitDiceRestItem __instance)
        {
            if(callbackOwner!=null && ReferenceEquals(callbackOwner.Item,__instance)) callbackOwner.Cancelled=true;
            Cancel(__instance);
        }
        static bool CallbackPrefix(HitDiceRestItem __instance)
        {
            if(callbackOwner==null || !ReferenceEquals(callbackOwner.Item,__instance)) return true;
            return !callbackOwner.Cancelled && __instance!=null &&
                ReferenceEquals(characterField.GetValue(__instance),callbackOwner.State.Hero) &&
                callbackOwner.State.Hero.Guid==callbackOwner.State.Guid;
        }
        internal static void Reset()
        { foreach(var item in pending.Keys.ToArray()) Cancel(item); if(callbackOwner!=null) callbackOwner.Cancelled=true; executing=null; nativeDrawDepth=0; drawEligible=false; }
        // A notification inside healing can make another native hit-die roll.
        // Only the outer draw belonging to the preserved rest command may use
        // this command’s next prepared static face.
        struct DrawState { internal bool Eligible; }
        static void ComputePrefix(RulesetCharacterHero __instance,RuleDefinitions.DieType die,RuleDefinitions.AdvantageType advantageType,out DrawState __state)
        {
            __state=new DrawState { Eligible=drawEligible };
            drawEligible=nativeDrawDepth==0 &&
                (executing!=null && ReferenceEquals(executing.State.Hero,__instance) || RuntimeRolls.IsReplicatedRestOwner(__instance));
            nativeDrawDepth++;
        }
        static Exception ComputeFinalizer(Exception __exception,DrawState __state)
        { nativeDrawDepth=Math.Max(0,nativeDrawDepth-1); drawEligible=__state.Eligible; return __exception; }
        internal static bool CanConsumeNativeHitDie { get { return drawEligible && nativeDrawDepth==1; } }
        static IEnumerable<CodeInstruction> ComputeTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var native=AccessTools.Method(typeof(RuleDefinitions),"RollDie");
            var replacement=AccessTools.Method(typeof(RuntimeRolls),"HitDieFace");
            if(replacement==null) throw new InvalidOperationException("The rest dice shim is missing.");
            var codes=instructions.ToList(); int count=0;
            foreach(var code in codes) if(code.opcode==OpCodes.Call && Equals(code.operand,native)) { code.operand=replacement; count++; }
            if(count!=1) throw new InvalidOperationException("The native rest raw draw changed.");
            return codes;
        }
        static string Hash(MethodInfo method)
        { using(var hash=SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(method.GetMethodBody().GetILAsByteArray())).Replace("-","").ToLowerInvariant(); }
        static MethodInfo Pin(Type type,string name,string expected)
        {
            var method=type.GetMethods(Instance).Single(m=>m.Name==name);
            if(Hash(method)!=expected) throw new InvalidOperationException("Unsupported native rest method: "+type.Name+"."+name);
            var patches=Harmony.GetPatchInfo(method);
            if(patches!=null && patches.Transpilers.Count>0) throw new InvalidOperationException("Another mod rewrites the rest method: "+type.Name+"."+name);
            return method;
        }
        static FieldInfo Field(Type type,string name,Type expected)
        { var field=type.GetField(name,Instance); if(field==null || field.FieldType!=expected) throw new InvalidOperationException("Unsupported native rest field: "+name); return field; }
        internal static bool Preflight(out string error)
        {
            error=null;
            try
            {
                characterField=Field(typeof(HitDiceRestItem),"character",typeof(RulesetCharacterHero));
                bonusesField=Field(typeof(HitDiceRestItem),"restHealingBonuses",typeof(List<RestHealingBonus>));
                canRollField=Field(typeof(HitDiceRestItem),"canRollAtThisStage",typeof(bool));
                spentBonusField=Field(typeof(HitDiceRestItem),"restHealingBonusSpent",typeof(bool));
                heroSpentField=Field(typeof(RulesetCharacterHero),"spentHitDice",typeof(Dictionary<RuleDefinitions.DieType,int>));
                heroBonusesField=Field(typeof(RulesetCharacterHero),"restHealingBonus",typeof(List<RestHealingBonus>));
                onRoll=Pin(typeof(HitDiceRestItem),"OnRollCb","c0e098b4041ff1545178e4d6a2825df73de0d8dbf3c07e04ee16c88116e70a99");
                bind=Pin(typeof(HitDiceRestItem),"Bind","c33b5d454f6b93f0dc7fbf60c3f78261f2690924f48af45d8a4142c2daa10474");
                unbind=Pin(typeof(HitDiceRestItem),"Unbind","e580e7518686bac280aa06f4cf42da21b3f45f3d34372ee49ba4b6db74f95a6a");
                hitDieRolled=Pin(typeof(HitDiceRestItem),"HitDieRolled","6a46ad43b20f521374cf35edefb8eed248ca85062eaafb3750294f3d865fb1fc");
                rollHitDie=Pin(typeof(RulesetCharacterHero),"RollHitDie","c1970709a8baf395f3c81c9ae981c6e959b86118374717b2a70f52ff5779d258");
                compute=Pin(typeof(RulesetCharacterHero),"ComputeAndApplyHitDieRoll","a45a271885b6f60ecf920494c9b5bdf82712c437de814098914b75ddb3a94403");
                localRoll=Pin(typeof(LocalCommandManager),"RollHitDie","1e99db990eb2f649363d4e05798b0fd05fca5ee0166c76b639e6e81652aeb197");
                return true;
            }
            catch(Exception failure) { error=failure.Message; return false; }
        }
        static HarmonyMethod Own(string name) { return new HarmonyMethod(typeof(RestPatches).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static)); }
        internal static void Install(Harmony harmony)
        {
            if(onRoll==null || compute==null) throw new InvalidOperationException("Rest preflight has not succeeded.");
            harmony.Patch(onRoll,prefix:Own(nameof(OnRollPrefix)));
            harmony.Patch(bind,prefix:Own(nameof(BindingPrefix)));
            harmony.Patch(unbind,prefix:Own(nameof(BindingPrefix)));
            harmony.Patch(hitDieRolled,prefix:Own(nameof(CallbackPrefix)));
            harmony.Patch(compute,prefix:Own(nameof(ComputePrefix)),transpiler:Own(nameof(ComputeTranspiler)),finalizer:Own(nameof(ComputeFinalizer)));
        }
    }
}
