using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ManualDiceRoller
{
    // The supplied native save preparer is a synchronous calculation. Preview
    // stops at its final RollSavingThrow call, before outcomes, notifications or
    // condition interruptions. Scratch collections and the one earlier event
    // delegate are restored even when a calculation fails. No effect is applied.
    internal static class NativeSavePlanner
    {
        sealed class Group { internal RuleDefinitions.DieType Die; internal int Count; internal RuleDefinitions.AdvantageType Advantage; }
        sealed class Preview
        {
            internal RulesetActor Target;
            internal string Ability;
            internal int DC, SumDepth;
            internal int SaveBonus, RollModifier, PotentialFaces;
            internal bool Captured;
            internal RuleDefinitions.AdvantageType Advantage;
            internal readonly List<Group> Bonus=new List<Group>();
            internal readonly List<object> State=new List<object>();
        }
        [ThreadStatic] static Preview preview;
        [ThreadStatic] static int modifierDepth;
        internal static bool Previewing { get { return preview!=null; } }
        internal static bool AllowsStaticBonus { get { return modifierDepth>0; } }
        static readonly FieldInfo accounted=AccessTools.Field(typeof(RulesetImplementationManager),"accountedProviders");
        static readonly FieldInfo effectFeatures=AccessTools.Field(typeof(RulesetEffect),"featuresToBrowse");
        static readonly FieldInfo effectOrigins=AccessTools.Field(typeof(RulesetEffect),"featuresOrigin");

        internal static bool TryPrepare(NativeInvocation call,NativeRollPlan plan)
        {
            if(call.Method.Name!="TryRollSavingThrow") return false;
            var target=call.Arg("target") as RulesetActor;
            if(target==null) return true;
            if(preview!=null) throw new InvalidOperationException("A save calculation is already being previewed.");
            var caster=call.Arg("caster") as RulesetCharacter;
            // Version pinning protects the file, while this gate protects the
            // live methods. A foreign prefix/getter must never run a second
            // time merely to discover which dice the native save will need.
            if(!ForeignPatchGuard(call,target,caster)) return true;
            if(!RuntimeRolls.CanManual(target,RuleDefinitions.RollContext.SavingThrow)) return true;
            Preview p;
            if(!Calculate(call,target,caster,out p)||!p.Captured) return true;
            plan.RequireNativeState(delegate {
                if(!ReferenceEquals(call.Arg("target"),target)||!ReferenceEquals(call.Arg("caster"),caster)||!ForeignPatchGuard(call,target,caster)||!RuntimeRolls.CanManual(target,RuleDefinitions.RollContext.SavingThrow)) return false;
                Preview current;
                return Calculate(call,target,caster,out current)&&SameCalculation(p,current);
            });
            for(int i=0;i<p.Bonus.Count;i++)
            {
                var group=p.Bonus[i];
                plan.AddStaticDice(target,group.Die,group.Count,title:"Saving throw bonus",note:RollText.Save(p.Ability),hint:"Enter the bonus die granted by a feature or spell.",
                    advantage:group.Advantage,role:"SaveBonus/"+i,category:RuleDefinitions.RollContext.SavingThrow);
            }
            plan.AddActorDice(target,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.SavingThrow,1,p.Advantage,
                title:"Saving throw",note:RollText.Save(p.Ability));
            plan.Describe(p.DC,true,p.SaveBonus+p.RollModifier);
            return true;
        }
        static bool Calculate(NativeInvocation call,RulesetActor target,RulesetCharacter caster,out Preview result)
        {
            result=null;
            if(preview!=null) throw new InvalidOperationException("A save calculation is already being previewed.");
            var manager=call.Receiver as RulesetImplementationManager ?? ServiceRepository.GetService<IRulesetImplementationService>() as RulesetImplementationManager;
            if(manager==null||accounted==null) return false;
            if(!NativeRuntimeMethod(manager,"ComputeCharacterSavingThrowContext")) return false;
            var replaced=target.ReplacedAbilityScoreForSave;
            var effect=call.Receiver as RulesetEffect;
            var p=new Preview{Target=target};
            var state=new StateWriter(p.State);
            state.Add(call.Receiver,false);
            foreach(var argument in call.Args.Skip(call.Method.IsStatic?0:1)) state.Add(argument,true);
            var args=call.Args.Skip(call.Method.IsStatic?0:1).ToArray();
            var parameters=call.Method.GetParameters();
            for(int i=0;i<parameters.Length;i++)
                if(args[i] is ActionModifier) args[i]=((ActionModifier)args[i]).Clone();
            using(var scratch=NativeScratchScope.ForActor(target))
            {
                scratch.AddActor(caster); scratch.AddClone(manager,accounted);
                var targetCharacter=target as RulesetCharacter;
                if(targetCharacter!=null&&targetCharacter.IsSubstitute) scratch.AddActor(targetCharacter.OriginalFormCharacter);
                if(caster!=null&&caster.IsSubstitute) scratch.AddActor(caster.OriginalFormCharacter);
                if(effect!=null) { scratch.AddClone(effect,effectFeatures); scratch.AddClone(effect,effectOrigins); }
                try
                {
                    // Opaque providers contributed by another assembly are outside
                    // the verified calculation contract and use native rolls.
                    if(!NativeProviders(target,state)||caster!=null&&!NativeProviders(caster,state)) return false;
                    target.ReplacedAbilityScoreForSave=null;
                    preview=p;
                    call.Method.Invoke(call.Receiver,args);
                }
                catch(TargetInvocationException error)
                { throw new InvalidOperationException("The native save calculation could not be previewed.",error.InnerException??error); }
                finally
                {
                    preview=null;
                    target.ReplacedAbilityScoreForSave=replaced;
                }
            }
            result=p; return true;
        }
        static bool NativeProviders(RulesetActor actor,StateWriter state)
        {
            var features=new List<FeatureDefinition>();
            state.Add(actor,false);
            actor.EnumerateFeaturesToBrowse<ISavingThrowAffinityProvider>(features); if(!RecordProviders(features,typeof(ISavingThrowAffinityProvider),state)) return false;
            actor.EnumerateFeaturesToBrowse<IEffectAffinityProvider>(features); if(!RecordProviders(features,typeof(IEffectAffinityProvider),state)) return false;
            actor.EnumerateFeaturesToBrowse<ISavingThrowPerformanceProvider>(features); if(!RecordProviders(features,typeof(ISavingThrowPerformanceProvider),state)) return false;
            // A conjured monster can consult a separately resolved controller's
            // saving throws. That actor is outside this isolated preview scope.
            if(actor is RulesetCharacterMonster&&features.OfType<ISavingThrowPerformanceProvider>().Any(feature=>feature.UseControllerSavingThrows)) return false;
            actor.EnumerateFeaturesToBrowse<ISpellCastingAffinityProvider>(features); if(!RecordProviders(features,typeof(ISpellCastingAffinityProvider),state)) return false;
            return true;
        }
        static bool RecordProviders(List<FeatureDefinition> features,Type provider,StateWriter state)
        {
            if(!NativeInterfaceMethods(features,provider)) return false;
            state.Add(provider,false); state.Add(features.Count,false);
            foreach(var feature in features) state.Add(feature,true);
            return true;
        }
        static bool SameCalculation(Preview original,Preview current)
        {
            if(current==null||!current.Captured||original.Ability!=current.Ability||original.DC!=current.DC||original.Advantage!=current.Advantage||original.SaveBonus!=current.SaveBonus||original.RollModifier!=current.RollModifier||original.Bonus.Count!=current.Bonus.Count||original.State.Count!=current.State.Count) return false;
            for(int index=0;index<original.Bonus.Count;index++)
                if(original.Bonus[index].Die!=current.Bonus[index].Die||original.Bonus[index].Count!=current.Bonus[index].Count||original.Bonus[index].Advantage!=current.Bonus[index].Advantage) return false;
            for(int index=0;index<original.State.Count;index++) {
                object left=original.State[index],right=current.State[index];
                if(left==null||right==null) { if(!ReferenceEquals(left,right)) return false; }
                else if(left is string||left.GetType().IsValueType) { if(!left.Equals(right)) return false; }
                else if(!ReferenceEquals(left,right)) return false;
            }
            return true;
        }
        sealed class ReferenceComparer:IEqualityComparer<object>
        {
            public new bool Equals(object left,object right) { return ReferenceEquals(left,right); }
            public int GetHashCode(object value) { return RuntimeHelpers.GetHashCode(value); }
        }
        sealed class StateWriter
        {
            readonly List<object> values;
            readonly HashSet<object> visited=new HashSet<object>(new ReferenceComparer());
            internal StateWriter(List<object> values) { this.values=values; }
            internal void Add(object value,bool fields) { Add(value,fields,0); }
            void Add(object value,bool fields,int depth)
            {
                if(values.Count>=32768) throw new InvalidOperationException("The native save state exceeds the supported safe limit.");
                values.Add(value);
                if(value==null||value is string||value is Type||value.GetType().IsValueType||value is Delegate||value is RulesetActor||!fields||depth>=8||!visited.Add(value)) return;
                var list=value as IList;
                if(list!=null) {
                    if(list.Count>4096) throw new InvalidOperationException("The native save state exceeds the supported safe limit.");
                    values.Add(list.Count); foreach(var item in list) Add(item,true,depth+1); return;
                }
                var dictionary=value as IDictionary;
                if(dictionary!=null) {
                    if(dictionary.Count>4096) throw new InvalidOperationException("The native save state exceeds the supported safe limit.");
                    values.Add(dictionary.Count); foreach(DictionaryEntry pair in dictionary) { Add(pair.Key,false,depth+1); Add(pair.Value,true,depth+1); } return;
                }
                for(Type type=value.GetType();type!=null&&type.Assembly==typeof(RulesetActor).Assembly;type=type.BaseType)
                    foreach(var field in type.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)) {
                        values.Add(field.DeclaringType.FullName+"/"+field.Name); object item=field.GetValue(value);
                        Add(item,!(item is BaseDefinition),depth+1);
                    }
            }
        }
        internal static bool NativeInterfaceMethods(IEnumerable<FeatureDefinition> features,Type provider)
        {
            foreach(var feature in features)
            {
                if(feature.GetType().Assembly!=typeof(RulesetActor).Assembly) return false;
                foreach(var method in feature.GetType().GetInterfaceMap(provider).TargetMethods)
                    if(!NativeMethod(method)) return false;
            }
            return true;
        }
        internal static bool NativeMethod(MethodBase method)
        {
            if(method==null||method.DeclaringType==null||method.DeclaringType.Assembly!=typeof(RulesetActor).Assembly) return false;
            var patches=Harmony.GetPatchInfo(method); if(patches==null) return true;
            foreach(var patch in patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Transpilers).Concat(patches.Finalizers))
            {
                var owner=patch.PatchMethod==null?null:patch.PatchMethod.DeclaringType;
                if(owner==null||owner.Assembly!=typeof(NativeSavePlanner).Assembly||owner.Namespace!="ManualDiceRoller") return false;
            }
            return true;
        }
        internal static IEnumerable<MethodInfo> Named(Type type,params string[] names)
        {
            if(type==null) yield break;
            var matches=type.GetMethods(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
            foreach(var method in matches) if(names.Contains(method.Name)) yield return method;
        }
        internal static bool NativeRuntimeMethod(object receiver,string name)
        {
            if(receiver==null) return true;
            return Named(receiver.GetType(),name).All(NativeMethod);
        }
        internal static bool ForeignPatchGuard(NativeInvocation call,RulesetActor target,RulesetCharacter caster)
        {
            var methods=new List<MethodBase>{call.Method};
            methods.AddRange(Named(typeof(RulesetImplementationManager),"TryRollSavingThrow","ComputeCharacterSavingThrowContext"));
            methods.AddRange(Named(typeof(RulesetActor),"RollSavingThrow","IsAutomaticallyFailingSavingThrow","ComputeBaseSavingThrowBonus","ComputeSavingThrowModifier","FindFirstConditionHoldingFeature","HasConditionOfType","get_IsDeadOrDyingOrUnconscious","GetAllConditions","get_AllConditionsForEnumeration","get_FeaturesToBrowse","get_FeaturesOrigin","get_ReplacedAbilityScoreForSave","set_ReplacedAbilityScoreForSave","get_Side"));
            methods.AddRange(Named(typeof(RulesetCharacter),"ComputeSavingThrowModifierImposedOnTarget","get_CharacterFamily","HasSenseType","IsWearingHeavyArmor","get_IsSubstitute","get_OriginalFormCharacter","get_MetamagicFeatures","get_Invocations"));
            methods.AddRange(Named(typeof(RulesetCharacterHero),"ComputeBaseSavingThrowBonus","FindFirstFeatHoldingFeature"));
            methods.AddRange(Named(typeof(FeatureDefinitionSavingThrowAffinity),"ComputeSavingThrowModifier","CanPerformSaveForAbilityScore","get_PriorityAbilityScore","get_AffinityGroups"));
            methods.AddRange(Named(typeof(RulesetEntity),"GetAttribute","TryGetAttributeValue","get_Name","get_EntityImplementation"));
            methods.AddRange(Named(typeof(FeatureDefinition),"get_AllowsDuplicate","ParseSpecialFeatureTags"));
            methods.AddRange(Named(typeof(RulesetAttribute),"get_CurrentValue"));
            methods.AddRange(Named(typeof(RuleDefinitions),"RollDie","RollStaticDiceAndSum","ComputeAdvantage"));
            methods.AddRange(Named(typeof(AttributeDefinitions),"ComputeAbilityScoreModifier"));
            methods.AddRange(Named(typeof(ActionModifier),"Clone","GetRawSavingThrowModifier","GetSavingThrowModifier","get_SavingThrowModifierTrends","get_SavingThrowAdvantageTrends","get_SavingThrowModifier","set_SavingThrowModifier"));
            var constructor=typeof(ActionModifier).GetConstructor(Type.EmptyTypes); if(constructor!=null) methods.Add(constructor);
            methods.AddRange(typeof(RuleDefinitions.FeatureOrigin).GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic));
            methods.AddRange(typeof(RuleDefinitions.TrendInfo).GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic));
            methods.AddRange(Named(typeof(BaseDefinition),"get_Name","op_Equality","op_Inequality"));
            methods.AddRange(Named(typeof(EffectForm),"get_FormType","get_OverrideSavingThrowInfo","get_DamageForm","get_ConditionForm","get_SavingThrowAffinity"));
            foreach(var typeName in new[]{"OverrideSavingThrowInfo","DamageForm","ConditionForm","EffectDescription","SaveAffinityBySenseDescription","SaveAffinityByFamilyDescription","MonsterDefinition","MetamagicOptionDefinition","FeatureDefinitionProficiency","FeatureDefinitionMagicAffinity","KnowledgeLevelDefinition","GameBestiaryEntry","FeatureDefinitionFeatureSet","RulesetCondition","FightingStyleDefinition","FeatDefinition","RulesetInvocation","InvocationDefinition","RulesetCharacterMonster","GameSettingsManager","RulesetItem","ItemDefinition","ArmorDescription","ArmorTypeDefinition","ArmorCategoryDefinition","RulesetInventory","RulesetInventorySlot","RulesetSpellRepertoire","RulesetUsablePower","UsableDeviceDescription"})
            {
                var type=typeof(RulesetActor).Assembly.GetType(typeName,false);
                if(type!=null) methods.AddRange(type.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Where(m=>m.DeclaringType.Assembly==typeof(RulesetActor).Assembly&&m.Name.StartsWith("get_",StringComparison.Ordinal)));
            }
            methods.AddRange(Named(typeof(RulesetEffect),"TryRollSavingThrow","get_EffectDescription","get_SourceDefinition","GetEffectSource","get_SaveDC","get_SourceType","get_Name","get_SchoolOfMagic","get_MetamagicOption"));
            var proficiency=typeof(RulesetActor).Assembly.GetType("FeatureDefinitionProficiency",false);
            var providerTypes=new List<Type>{typeof(ISavingThrowAffinityProvider),typeof(IEffectAffinityProvider),typeof(ISavingThrowPerformanceProvider),typeof(ISpellCastingAffinityProvider),typeof(FeatureDefinitionSavingThrowAffinity)};
            if(proficiency!=null) providerTypes.Add(proficiency);
            foreach(var typeName in new[]{"FeatureDefinitionFeatureSet","ConditionDefinition"})
            {
                var type=typeof(RulesetActor).Assembly.GetType(typeName,false);
                foreach(var method in Named(type,"EnumerateFeaturesToBrowseHierarchicaly","EnumerateFeaturesToBrowse"))
                {
                    methods.Add(method);
                    if(method.IsGenericMethodDefinition&&method.GetGenericArguments().Length==1)
                        foreach(var provider in providerTypes) methods.Add(method.MakeGenericMethod(provider));
                }
            }
            var bestiary=typeof(RulesetActor).Assembly.GetType("GameBestiary",false);
            methods.AddRange(Named(bestiary,"TryGetBestiaryEntry"));
            methods.AddRange(Named(typeof(ConditionDefinition),"HasFeature","IsSubtypeOf","get_Features"));
            var locationType=typeof(RulesetActor).Assembly.GetType("GameLocationCharacter",false);
            methods.AddRange(Named(locationType,"GetFromActor","get_LightingState","get_RulesetActor","get_RulesetCharacter"));
            foreach(var typeName in new[]{"ArmorTypeDefinition","ArmorCategoryDefinition"}) {
                var definition=typeof(RulesetActor).Assembly.GetType(typeName,false); if(definition==null) continue;
                foreach(var method in Named(typeof(DatabaseRepository),"GetDatabase")) {
                    methods.Add(method); if(method.IsGenericMethodDefinition) methods.Add(method.MakeGenericMethod(definition));
                }
                var database=typeof(RulesetActor).Assembly.GetType("Database`1",false);
                if(database!=null) methods.AddRange(Named(database.MakeGenericType(definition),"GetElement","get_Name"));
            }
            foreach(var receiver in new object[]{target,caster,call.Receiver})
            {
                if(receiver==null) continue;
                foreach(var name in new[]{"IsAutomaticallyFailingSavingThrow","ComputeBaseSavingThrowBonus","ComputeSavingThrowModifierImposedOnTarget","ComputeCharacterSavingThrowContext","get_EffectDescription","get_SourceDefinition","GetEffectSource","get_SaveDC","get_SourceType","get_Name","get_SchoolOfMagic","get_MetamagicOption","get_CharacterFamily","HasSenseType","IsWearingHeavyArmor","get_IsSubstitute","get_OriginalFormCharacter","GetAllConditions",call.Method.Name})
                    if(!NativeRuntimeMethod(receiver,name)) return false;
                foreach(var method in Named(receiver.GetType(),"EnumerateFeaturesToBrowse"))
                {
                    if(!NativeMethod(method)) return false;
                    if(method.IsGenericMethodDefinition&&method.GetGenericArguments().Length==1)
                        foreach(var provider in providerTypes)
                            if(!NativeMethod(method.MakeGenericMethod(provider))) return false;
                }
            }
            foreach(var method in Named(typeof(ServiceRepository),"GetService"))
            {
                if(!NativeMethod(method)) return false;
                if(method.IsGenericMethodDefinition&&method.GetGenericArguments().Length==1)
                {
                    if(!NativeMethod(method.MakeGenericMethod(typeof(IRulesetImplementationService)))||!NativeMethod(method.MakeGenericMethod(typeof(IGameSettingsService)))||!NativeMethod(method.MakeGenericMethod(typeof(IGameLoreService)))) return false;
                    var locationService=typeof(RulesetActor).Assembly.GetType("IGameLocationCharacterService",false);
                    if(locationService!=null&&!NativeMethod(method.MakeGenericMethod(locationService))) return false;
                }
            }
            if(!methods.Distinct().All(NativeMethod)) return false;
            var settings=ServiceRepository.GetService<IGameSettingsService>();
            if(settings!=null&&(settings.GetType().Assembly!=typeof(RulesetActor).Assembly||!NativeRuntimeMethod(settings,"get_SavingThrowAllyModifier")||!NativeRuntimeMethod(settings,"get_SavingThrowEnemyModifier"))) return false;
            foreach(var character in new[]{target as RulesetCharacter,caster})
            {
                if(character==null||!character.IsSubstitute) continue;
                var original=character.OriginalFormCharacter;
                if(original==null) continue;
                foreach(var name in new[]{"IsAutomaticallyFailingSavingThrow","ComputeBaseSavingThrowBonus","ComputeSavingThrowModifierImposedOnTarget","GetAllConditions"})
                    if(!NativeRuntimeMethod(original,name)) return false;
                foreach(var method in Named(original.GetType(),"EnumerateFeaturesToBrowse")) {
                    if(!NativeMethod(method)) return false;
                    if(method.IsGenericMethodDefinition&&method.GetGenericArguments().Length==1)
                        foreach(var provider in providerTypes) if(!NativeMethod(method.MakeGenericMethod(provider))) return false;
                }
            }
            // The effect wrapper may read the bestiary for a native spell DC
            // feature. Validate its concrete service getter before invoking it.
            if(call.Receiver is RulesetEffect&&caster!=null&&target is RulesetCharacter)
            {
                var lore=ServiceRepository.GetService<IGameLoreService>();
                if(lore!=null&&(lore.GetType().Assembly!=typeof(RulesetActor).Assembly||!NativeRuntimeMethod(lore,"get_Bestiary"))) return false;
            }
            return true;
        }
        static bool SavePrefix(RulesetActor __instance,string abilityScoreName,List<RuleDefinitions.TrendInfo> advantageTrends,int saveDC,int saveBonus,int rollModifier,
            ref RuleDefinitions.RollOutcome outcome,ref int outcomeDelta)
        {
            var p=preview; if(p==null) return true;
            if(!ReferenceEquals(__instance,p.Target)||p.Captured) throw new InvalidOperationException("Unexpected native save calculation.");
            p.Captured=true; p.Ability=abilityScoreName; p.DC=saveDC; p.Advantage=RuleDefinitions.ComputeAdvantage(advantageTrends);
            p.SaveBonus=saveBonus; p.RollModifier=rollModifier;
            outcome=RuleDefinitions.RollOutcome.Neutral; outcomeDelta=0;
            return false;
        }
        static bool RawPreviewPrefix(RuleDefinitions.DieType diceType,RuleDefinitions.AdvantageType advantageType,
            ref int firstRoll,ref int secondRoll,ref int __result)
        {
            var p=preview; if(p==null) return true;
            int min=RuleDefinitions.DiceMinValue[(int)diceType];
            firstRoll=min; secondRoll=advantageType==RuleDefinitions.AdvantageType.None?-1:min; __result=min;
            if(p.SumDepth==0&&RuntimeRolls.Max(diceType)>1)
                throw new InvalidOperationException("An unsupported direct bonus draw occurred in save preparation.");
            return false;
        }
        static void SumPrefix(int diceNumber,RuleDefinitions.DieType diceType,out Preview __state)
        {
            __state=null; var p=preview; if(p==null) return;
            if(p.SumDepth!=0) throw new InvalidOperationException("A nested static sum occurred in save preparation.");
            if(diceNumber<0||diceNumber>256) throw new InvalidOperationException("Unsupported saving throw bonus dice count.");
            if(diceNumber>0&&RuntimeRolls.Max(diceType)>1) {
                p.PotentialFaces+=diceNumber;
                if(p.PotentialFaces>4094) throw new InvalidOperationException("The native saving throw requires too many dice.");
                p.Bonus.Add(new Group{Die=diceType,Count=diceNumber,Advantage=RuleDefinitions.AdvantageType.None});
            }
            p.SumDepth++; __state=p;
        }
        static Exception SumFinalizer(Exception __exception,Preview __state)
        { if(__state!=null&&__state.SumDepth>0) __state.SumDepth--; return __exception; }
        static void ModifierPrefix() { modifierDepth++; }
        static Exception ModifierFinalizer(Exception __exception) { if(modifierDepth>0) modifierDepth--; return __exception; }
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(RulesetActor),"RollSavingThrow"),prefix:new HarmonyMethod(typeof(NativeSavePlanner),"SavePrefix"));
            harmony.Patch(AccessTools.Method(typeof(RuleDefinitions),"RollDie"),prefix:new HarmonyMethod(typeof(NativeSavePlanner),"RawPreviewPrefix"));
            harmony.Patch(AccessTools.Method(typeof(RuleDefinitions),"RollStaticDiceAndSum"),
                prefix:new HarmonyMethod(typeof(NativeSavePlanner),"SumPrefix"),finalizer:new HarmonyMethod(typeof(NativeSavePlanner),"SumFinalizer"));
            harmony.Patch(AccessTools.Method(typeof(RulesetActor),"ComputeSavingThrowModifier"),
                prefix:new HarmonyMethod(typeof(NativeSavePlanner),"ModifierPrefix"),finalizer:new HarmonyMethod(typeof(NativeSavePlanner),"ModifierFinalizer"));
            harmony.Patch(AccessTools.Method(typeof(RulesetCharacter),"ComputeSavingThrowModifierImposedOnTarget"),
                prefix:new HarmonyMethod(typeof(NativeSavePlanner),"ModifierPrefix"),finalizer:new HarmonyMethod(typeof(NativeSavePlanner),"ModifierFinalizer"));
        }
    }
}
