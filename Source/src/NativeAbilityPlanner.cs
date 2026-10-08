using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;

namespace ManualDiceRoller
{
    // Run the stock location wrapper on private scratch collections and a cloned
    // modifier. Stop before the character's dice, notifications and interruptions.
    // This preserves lighting, armor, skill and difficulty rules without copying
    // the game's ability-check implementation into the plugin.
    internal static class NativeAbilityPlanner
    {
        sealed class Group { internal RuleDefinitions.DieType Die; internal int Count; }
        sealed class Preview
        {
            internal RulesetCharacter Actor;
            internal string Ability, Skill;
            internal int DC, SumDepth, BonusFaces, BaseBonus, RollModifier;
            internal bool Captured, Proficient;
            internal RuleDefinitions.AdvantageType Advantage;
            internal readonly List<Group> Bonuses = new List<Group>();
        }
        [ThreadStatic] static Preview preview;
        [ThreadStatic] static int modifierDepth;
        internal static bool Previewing { get { return preview != null; } }
        internal static bool AllowsStaticBonus { get { return modifierDepth > 0; } }

        internal static bool TryPrepare(NativeInvocation call, NativeRollPlan plan)
        {
            bool locationCheck=call.Method.DeclaringType == typeof(GameLocationCharacter) && call.Method.Name == "RollAbilityCheck";
            bool basicCheck=call.Receiver is RulesetCharacter && call.Method.Name == "RollBasicAbilityCheck";
            if(!locationCheck&&!basicCheck) return false;
            var location = call.Receiver as GameLocationCharacter;
            if (call.Arg<bool>("passive")) return true;
            if (!ForeignPatchGuard(call, location)) return true;
            var actor = locationCheck ? location.RulesetCharacter : (RulesetCharacter)call.Receiver;
            if (!RuntimeRolls.CanManual(actor, RuleDefinitions.RollContext.AbilityCheck)) return true;
            if (preview != null) throw new InvalidOperationException("An ability check is already being previewed.");
            if (!ForeignActorGuard(actor)) return true;
            string signature = Signature(call, actor);
            Preview p;
            if(!Calculate(call,actor,out p)) return true;
            if (!p.Captured) return true;
            for (int index = 0; index < p.Bonuses.Count; index++)
                plan.AddStaticDice(actor, p.Bonuses[index].Die, p.Bonuses[index].Count,
                    title: "Ability check bonus", note: RollText.Check(p.Ability, p.Skill), hint: "Enter the bonus die granted by a feature or spell.",
                    role: "AbilityBonus/" + index, category: RuleDefinitions.RollContext.AbilityCheck);
            plan.AddActorDice(actor, RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AbilityCheck, 1,
                p.Advantage, p.Proficient, p.Skill, title: "Ability check", note: RollText.Check(p.Ability, p.Skill));
            plan.Describe(p.DC, true, p.BaseBonus + p.RollModifier);
            plan.RequireNativeState(delegate {
                if(!ForeignPatchGuard(call,location)||!ForeignActorGuard(actor)||Signature(call,actor)!=signature) return false;
                Preview current;
                return Calculate(call,actor,out current)&&SameCalculation(p,current);
            });
            return true;
        }

        static bool Calculate(NativeInvocation call, RulesetCharacter actor, out Preview result)
        {
            result=null;
            if(preview!=null) throw new InvalidOperationException("An ability check is already being previewed.");
            var p = new Preview { Actor = actor };
            var args = call.Args.Skip(1).ToArray();
            using (var scratch = NativeScratchScope.ForActor(actor))
            {
                // Stock transformed monsters also enumerate the original hero.
                if (actor.IsSubstitute) scratch.AddActor(actor.OriginalFormCharacter);
                var features = new List<FeatureDefinition>();
                actor.EnumerateFeaturesToBrowse<IAbilityCheckAffinityProvider>(features);
                if (!NativeSavePlanner.NativeInterfaceMethods(features, typeof(IAbilityCheckAffinityProvider))) return false;
                var performance = new List<FeatureDefinition>();
                actor.EnumerateFeaturesToBrowse<IAbilityCheckPerformanceProvider>(performance);
                if (!NativeSavePlanner.NativeInterfaceMethods(performance, typeof(IAbilityCheckPerformanceProvider))) return false;
                // A conjured monster may consult a separate controller by GUID.
                // That actor is outside this isolated target/original-form scope.
                if(actor is RulesetCharacterMonster && performance.Cast<IAbilityCheckPerformanceProvider>().Any(f=>f.UseControllerAbilityChecks)) return false;
                var parameters = call.Method.GetParameters();
                for (int index = 0; index < parameters.Length; index++)
                    if (args[index] is ActionModifier) args[index] = ((ActionModifier)args[index]).Clone();
                try { preview = p; call.Method.Invoke(call.Receiver, args); }
                catch (TargetInvocationException error)
                { throw new InvalidOperationException("The native ability check could not be previewed.", error.InnerException ?? error); }
                finally { preview = null; }
            }
            result=p; return true;
        }
        static bool SameCalculation(Preview expected, Preview actual)
        {
            if(actual==null||!actual.Captured||expected.Ability!=actual.Ability||expected.Skill!=actual.Skill||expected.DC!=actual.DC||expected.Advantage!=actual.Advantage||expected.Proficient!=actual.Proficient||expected.BaseBonus!=actual.BaseBonus||expected.RollModifier!=actual.RollModifier||expected.Bonuses.Count!=actual.Bonuses.Count) return false;
            for(int index=0;index<expected.Bonuses.Count;index++)
                if(expected.Bonuses[index].Die!=actual.Bonuses[index].Die||expected.Bonuses[index].Count!=actual.Bonuses[index].Count) return false;
            return true;
        }

        static string Signature(NativeInvocation call, RulesetCharacter actor)
        {
            var text = new StringBuilder();
            var modifier = call.Arg("checkModifier") as ActionModifier;
            if (modifier == null && call.Method.Name != "RollBasicAbilityCheck") throw new InvalidOperationException("The native check modifier is unavailable.");
            if(modifier!=null) text.Append(modifier.AbilityCheckModifier).Append('/');
            var trends=modifier==null ? (call.Arg("modifierTrends") as List<RuleDefinitions.TrendInfo> ?? new List<RuleDefinitions.TrendInfo>()) : modifier.AbilityCheckModifierTrends.Concat(modifier.AbilityCheckAdvantageTrends);
            foreach (var trend in trends)
                text.Append(trend.value).Append(':').Append((int)trend.sourceType).Append(':').Append(trend.sourceName).Append(':').Append(Id(trend.source)).Append(':').Append((int)trend.dieType).Append(':').Append((int)trend.dieFlag).Append(';');
            if(call.Receiver is GameLocationCharacter) text.Append('|').Append(AccessTools.Field(typeof(GameLocationCharacter), "lightingState").GetValue(call.Receiver));
            using (var scratch = NativeScratchScope.ForActor(actor))
            {
                if (actor.IsSubstitute) scratch.AddActor(actor.OriginalFormCharacter);
                var features = new List<FeatureDefinition>();
                actor.EnumerateFeaturesToBrowse<IAbilityCheckAffinityProvider>(features);
                foreach (var feature in features) AppendFeature(text, feature);
                features.Clear(); actor.EnumerateFeaturesToBrowse<IAbilityCheckPerformanceProvider>(features);
                foreach (var feature in features) AppendFeature(text, feature);
                var conditions = new List<RulesetCondition>(); actor.GetAllConditions(conditions);
                foreach (var condition in conditions)
                {
                    text.Append('|').Append(Id(condition)).Append(':').Append((int)condition.BardicInspirationDie).Append(':').Append(condition.BardicInspirationRoll);
                    if (condition.BardicInspirationRoll > 0)
                        foreach (var feature in condition.ConditionDefinition.Features)
                            if (feature is IAbilityCheckAffinityProvider) AppendFeature(text, feature);
                }
            }
            return text.ToString();
        }
        static int Id(object value) { return value == null ? 0 : RuntimeHelpers.GetHashCode(value); }
        static void AppendFeature(StringBuilder text, FeatureDefinition feature)
        {
            var affinity = feature as FeatureDefinitionAbilityCheckAffinity;
            if (affinity == null) throw new InvalidOperationException("An unsupported native ability provider was encountered.");
            text.Append('|').Append(Id(feature)).Append(':').Append(affinity.SubstractBardicDieRoll).Append(':').Append(affinity.UseControllerAbilityChecks);
            foreach (var group in affinity.AffinityGroups)
                text.Append(';').Append(group.abilityScoreName).Append(':').Append(group.proficiencyName).Append(':').Append((int)group.affinity).Append(':').Append((int)group.abilityCheckGroupOperation).Append(':').Append(group.abilityCheckModifierDiceNumber).Append(':').Append((int)group.abilityCheckModifierDieType).Append(':').Append((int)group.abilityCheckContext).Append(':').Append((int)group.lightingContext);
        }

        internal static bool ForeignPatchGuard(NativeInvocation call, GameLocationCharacter location)
        {
            var methods = new List<MethodBase> { call.Method };
            methods.AddRange(NativeSavePlanner.Named(typeof(GameLocationCharacter), "get_RulesetCharacter", "get_Side", "RollAbilityCheck", "PrepareActionModifier", "ComputeAbilityCheckActionModifier"));
            methods.AddRange(NativeSavePlanner.Named(typeof(RulesetActor), "get_FeaturesToBrowse", "get_FeaturesOrigin", "get_AllConditionsForEnumeration", "get_Side", "GetAllConditions"));
            methods.AddRange(NativeSavePlanner.Named(typeof(RulesetCharacter), "RollAbilityCheck", "RollBasicAbilityCheck", "ComputeBaseAbilityCheckBonus", "IsProficient", "IsWearingHeavyArmor", "get_IsSubstitute", "get_OriginalFormCharacter"));
            methods.AddRange(NativeSavePlanner.Named(typeof(RulesetCharacterHero), "ComputeBaseAbilityCheckBonus", "IsProficient", "IsWearingHeavyArmor", "get_SkillProficiencies", "get_ToolTypeProficiencies"));
            methods.AddRange(NativeSavePlanner.Named(typeof(RulesetEntity), "GetAttribute", "TryGetAttributeValue"));
            methods.AddRange(NativeSavePlanner.Named(typeof(FeatureDefinition), "get_AllowsDuplicate", "ParseSpecialFeatureTags"));
            methods.AddRange(NativeSavePlanner.Named(typeof(BaseDefinition), "get_Name", "op_Equality", "op_Inequality"));
            methods.AddRange(NativeSavePlanner.Named(typeof(RulesetAttribute), "get_CurrentValue"));
            methods.AddRange(NativeSavePlanner.Named(typeof(AttributeDefinitions), "ComputeAbilityScoreModifier"));
            methods.AddRange(NativeSavePlanner.Named(typeof(FeatureDefinitionAbilityCheckAffinity), "ComputeAbilityCheckModifier", "ComputeAbilityCheckAdvantageTrendAndModifier", "get_AffinityGroups", "get_SubstractBardicDieRoll", "get_UseControllerAbilityChecks"));
            methods.AddRange(NativeSavePlanner.Named(typeof(ActionModifier), "Clone", "get_AbilityCheckModifierTrends", "get_AbilityCheckAdvantageTrends", "get_AbilityCheckModifier", "set_AbilityCheckModifier"));
            var constructor = typeof(ActionModifier).GetConstructor(Type.EmptyTypes); if (constructor != null) methods.Add(constructor);
            methods.AddRange(typeof(RuleDefinitions.TrendInfo).GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic));
            methods.AddRange(typeof(RuleDefinitions.FeatureOrigin).GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic));
            methods.AddRange(NativeSavePlanner.Named(typeof(RuleDefinitions), "ComputeAdvantage", "RollStaticDiceAndSum", "RollDie"));
            foreach (var typeName in new[] { "RulesetCondition", "ConditionDefinition", "GameSettingsManager", "RulesetItem", "ItemDefinition", "MonsterDefinition", "ArmorDescription", "ArmorTypeDefinition", "ArmorCategoryDefinition", "RulesetInventory", "RulesetInventorySlot", "FeatureDefinitionProficiency", "ToolTypeDefinition", "FeatureDefinitionFeatureSet", "FightingStyleDefinition", "FeatDefinition", "RulesetInvocation", "InvocationDefinition", "RulesetCharacterMonster" })
            {
                var type = typeof(RulesetActor).Assembly.GetType(typeName, false);
                if (type != null) methods.AddRange(type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.DeclaringType.Assembly == typeof(RulesetActor).Assembly && (m.Name.StartsWith("get_", StringComparison.Ordinal) || m.Name == "CountToolsOfType" || m.Name == "GetElement")));
            }
            foreach (var type in new[] { typeof(FeatureDefinitionFeatureSet), typeof(ConditionDefinition) })
                foreach (var method in NativeSavePlanner.Named(type, "EnumerateFeaturesToBrowseHierarchicaly", "EnumerateFeaturesToBrowse"))
                {
                    methods.Add(method);
                    if (method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1)
                        foreach (var provider in new[] { typeof(IAbilityCheckAffinityProvider), typeof(IAbilityCheckPerformanceProvider), typeof(FeatureDefinitionAbilityCheckAffinity) })
                            methods.Add(method.MakeGenericMethod(provider));
                }
            foreach (var method in NativeSavePlanner.Named(typeof(ServiceRepository), "GetService"))
            {
                if (!NativeSavePlanner.NativeMethod(method)) return false;
                if (method.IsGenericMethodDefinition && !NativeSavePlanner.NativeMethod(method.MakeGenericMethod(typeof(IGameSettingsService)))) return false;
            }
            foreach (var method in NativeSavePlanner.Named(typeof(DatabaseRepository), "GetDatabase"))
            {
                if (!NativeSavePlanner.NativeMethod(method)) return false;
                if (method.IsGenericMethodDefinition)
                    foreach (var definition in new[] { typeof(ToolTypeDefinition), typeof(ItemDefinition), typeof(ArmorTypeDefinition), typeof(ArmorCategoryDefinition) })
                    {
                        if (!NativeSavePlanner.NativeMethod(method.MakeGenericMethod(definition))) return false;
                        foreach (var lookup in NativeSavePlanner.Named(method.MakeGenericMethod(definition).ReturnType, "GetElement", "TryGetElement"))
                            if (!NativeSavePlanner.NativeMethod(lookup)) return false;
                    }
            }
            if (!methods.Distinct().All(NativeSavePlanner.NativeMethod)) return false;
            var settings = ServiceRepository.GetService<IGameSettingsService>();
            if (settings != null && (!NativeSavePlanner.NativeRuntimeMethod(settings, "get_AbilityCheckAllyModifier") || !NativeSavePlanner.NativeRuntimeMethod(settings, "get_AbilityCheckEnemyModifier"))) return false;
            return true;
        }
        internal static bool ForeignActorGuard(RulesetCharacter actor)
        {
            if (!NativeSavePlanner.NativeRuntimeMethod(actor, "get_OriginalFormCharacter") || !NativeSavePlanner.NativeRuntimeMethod(actor, "get_IsSubstitute")) return false;
            foreach (var receiver in new[] { actor, actor.OriginalFormCharacter })
            {
                if (receiver == null) continue;
                foreach (var name in new[] { "ComputeBaseAbilityCheckBonus", "IsProficient", "IsWearingHeavyArmor", "GetAllConditions", "get_IsSubstitute", "get_OriginalFormCharacter" })
                    if (!NativeSavePlanner.NativeRuntimeMethod(receiver, name)) return false;
                foreach (var method in NativeSavePlanner.Named(receiver.GetType(), "EnumerateFeaturesToBrowse"))
                {
                    if (!NativeSavePlanner.NativeMethod(method)) return false;
                    if (method.IsGenericMethodDefinition)
                        foreach (var provider in new[] { typeof(IAbilityCheckAffinityProvider), typeof(IAbilityCheckPerformanceProvider), typeof(FeatureDefinitionAbilityCheckAffinity) })
                            if (!NativeSavePlanner.NativeMethod(method.MakeGenericMethod(provider))) return false;
                }
            }
            return true;
        }
        static bool AbilityPrefix(RulesetCharacter __instance, string abilityScoreName, string proficiencyName,
            List<RuleDefinitions.TrendInfo> advantageTrends, int baseBonus, int rollModifier, int checkDC, bool passive,
            ref int diceRoll, ref int firstRoll, ref int secondRoll, ref RuleDefinitions.RollOutcome outcome,
            ref int successDelta, ref int __result)
        {
            var p = preview; if (p == null) return true;
            if (!ReferenceEquals(__instance, p.Actor) || p.Captured || passive) throw new InvalidOperationException("Unexpected native ability check calculation.");
            p.Captured = true; p.Ability = abilityScoreName; p.Skill = proficiencyName; p.DC = checkDC; p.BaseBonus=baseBonus; p.RollModifier=rollModifier;
            p.Advantage = RuleDefinitions.ComputeAdvantage(advantageTrends); p.Proficient = __instance.IsProficient(proficiencyName);
            diceRoll = firstRoll = successDelta = __result = 0; secondRoll = -1; outcome = RuleDefinitions.RollOutcome.Neutral;
            return false;
        }
        static bool RawPreviewPrefix(RuleDefinitions.DieType diceType, RuleDefinitions.AdvantageType advantageType,
            ref int firstRoll, ref int secondRoll, ref int __result)
        {
            var p = preview; if (p == null) return true;
            if (p.SumDepth == 0 && RuntimeRolls.Max(diceType) > 1) throw new InvalidOperationException("Unsupported direct draw in ability preparation.");
            firstRoll = RuleDefinitions.DiceMinValue[(int)diceType]; secondRoll = advantageType == RuleDefinitions.AdvantageType.None ? -1 : firstRoll; __result = firstRoll;
            return false;
        }
        static void SumPrefix(int diceNumber, RuleDefinitions.DieType diceType, out Preview __state)
        {
            __state = null; var p = preview; if (p == null) return;
            if (p.SumDepth != 0 || diceNumber < 0 || diceNumber > 256) throw new InvalidOperationException("Unsupported ability bonus dice batch.");
            if (diceNumber > 0 && RuntimeRolls.Max(diceType) > 1)
            {
                if (diceNumber > RollBroker.MaxFacesPerSession - p.BonusFaces) throw new InvalidOperationException("The native ability bonuses exceed the supported face budget.");
                p.BonusFaces += diceNumber;
                p.Bonuses.Add(new Group { Die = diceType, Count = diceNumber });
            }
            p.SumDepth++; __state = p;
        }
        static Exception SumFinalizer(Exception __exception, Preview __state)
        { if (__state != null && __state.SumDepth > 0) __state.SumDepth--; return __exception; }
        static void ModifierPrefix() { modifierDepth++; }
        static Exception ModifierFinalizer(Exception __exception) { if (modifierDepth > 0) modifierDepth--; return __exception; }
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(RulesetCharacter), "RollAbilityCheck"), prefix: new HarmonyMethod(typeof(NativeAbilityPlanner), "AbilityPrefix") { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(RuleDefinitions), "RollDie"), prefix: new HarmonyMethod(typeof(NativeAbilityPlanner), "RawPreviewPrefix"));
            harmony.Patch(AccessTools.Method(typeof(RuleDefinitions), "RollStaticDiceAndSum"), prefix: new HarmonyMethod(typeof(NativeAbilityPlanner), "SumPrefix"), finalizer: new HarmonyMethod(typeof(NativeAbilityPlanner), "SumFinalizer"));
            harmony.Patch(AccessTools.Method(typeof(GameLocationCharacter), "ComputeAbilityCheckActionModifier"), prefix: new HarmonyMethod(typeof(NativeAbilityPlanner), "ModifierPrefix"), finalizer: new HarmonyMethod(typeof(NativeAbilityPlanner), "ModifierFinalizer"));
        }
    }
}
