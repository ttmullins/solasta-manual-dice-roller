using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ManualDiceRoller
{
    // The original wrapper selects the defending skill and calculates all three
    // modifiers. Its final contest is stopped before dice, events or condition
    // use. Random affinity bonuses are deliberately outside this static plan.
    internal static class ShovePatches
    {
        sealed class Side
        {
            internal RulesetCharacter Actor;
            internal string Ability, Skill, RollSkill;
            internal int BaseBonus, Modifier;
            internal bool Proficient, Incapacitated;
            internal RuleDefinitions.Side Faction;
            internal RuleDefinitions.AdvantageType Advantage;
            internal List<RuleDefinitions.TrendInfo> ModifierTrends, AdvantageTrends;
        }
        sealed class Preview
        {
            internal RulesetCharacter Actor, Opponent;
            internal Side First, Second;
            internal bool Captured;
        }
        [ThreadStatic] static Preview preview;
        static readonly MethodInfo native=AccessTools.Method(typeof(CharacterActionShove),"ResolveRolls");
        static readonly MethodInfo contest=AccessTools.Method(typeof(RulesetCharacter),"ResolveContestCheck");

        internal static bool TryPrepare(NativeInvocation call,NativeRollPlan plan)
        {
            if(!Equals(call.Method,native)) return false;
            var actor=call.Arg("actor") as GameLocationCharacter;
            var opponent=call.Arg("opponent") as GameLocationCharacter;
            if(actor==null||opponent==null||actor.RulesetCharacter==null||opponent.RulesetCharacter==null) return true;
            if(!RuntimeRolls.CanManual(actor.RulesetCharacter,RuleDefinitions.RollContext.AbilityCheck)&&
                !RuntimeRolls.CanManual(opponent.RulesetCharacter,RuleDefinitions.RollContext.AbilityCheck)) return true;
            Preview p;
            if(!Calculate(call,actor,opponent,out p)) return true;
            plan.AddActorDice(p.First.Actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1,p.First.Advantage,p.First.Proficient,p.First.Skill,
                title:"Shove contest",note:RollText.Check(p.First.Ability,p.First.Skill));
            int defendingStart=plan.Leaves.Count;
            plan.AddActorDice(p.Second.Actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1,p.Second.Advantage,p.Second.Proficient,p.Second.RollSkill,
                title:"Resist shove",note:RollText.Check(p.Second.Ability,p.Second.Skill));
            for(int index=defendingStart;index<plan.Leaves.Count;index++) plan.Leaves[index].DisplaySkill=p.Second.Skill;
            plan.RequireNativeState(delegate {Preview current;return Calculate(call,actor,opponent,out current)&&Same(p.First,current.First)&&Same(p.Second,current.Second);});
            return true;
        }
        static bool Calculate(NativeInvocation call,GameLocationCharacter actor,GameLocationCharacter opponent,out Preview result)
        {
            result=null;
            if(preview!=null) throw new InvalidOperationException("A shove is already being previewed.");
            if(!NativeAbilityPlanner.ForeignPatchGuard(call,actor)||!NativeAbilityPlanner.ForeignActorGuard(actor.RulesetCharacter)||
                !NativeAbilityPlanner.ForeignActorGuard(opponent.RulesetCharacter)||!NativeSavePlanner.NativeMethod(contest)) return false;
            var p=new Preview {Actor=actor.RulesetCharacter,Opponent=opponent.RulesetCharacter};
            using(var scratch=NativeScratchScope.ForActor(p.Actor))
            {
                scratch.AddActor(p.Opponent);
                if(p.Actor.IsSubstitute) scratch.AddActor(p.Actor.OriginalFormCharacter);
                if(p.Opponent.IsSubstitute) scratch.AddActor(p.Opponent.OriginalFormCharacter);
                if(!Providers(p.Actor)||!Providers(p.Opponent)) return false;
                foreach(var method in NativeSavePlanner.Named(typeof(ActionModifier),"get_AbilityCheckAdvantageTrend"))
                    if(!NativeSavePlanner.NativeMethod(method)) return false;
                foreach(var method in NativeSavePlanner.Named(typeof(RulesetInventory),"IsMatchingEquipementCondition"))
                    if(!NativeSavePlanner.NativeMethod(method)) return false;
                try {preview=p;native.Invoke(null,call.Args);}
                catch(TargetInvocationException error)
                {
                    // The preview raw-draw traps stop unsupported random bonuses
                    // before RNG is advanced. The real action remains automatic.
                    if(error.InnerException is InvalidOperationException) return false;
                    throw new InvalidOperationException("The native shove could not be previewed.",error.InnerException??error);
                }
                finally {preview=null;}
            }
            if(!p.Captured) return false;
            result=p;return true;
        }
        static bool Providers(RulesetCharacter actor)
        {
            if(!NativeSavePlanner.NativeRuntimeMethod(actor,"IsMatchingEquipementCondition")||!NativeSavePlanner.NativeRuntimeMethod(actor,"get_IsIncapacitated")) return false;
            var features=new List<FeatureDefinition>();
            actor.EnumerateFeaturesToBrowse<IAbilityCheckAffinityProvider>(features);
            if(!NativeSavePlanner.NativeInterfaceMethods(features,typeof(IAbilityCheckAffinityProvider))) return false;
            actor.EnumerateFeaturesToBrowse<IAbilityCheckPerformanceProvider>(features);
            if(!NativeSavePlanner.NativeInterfaceMethods(features,typeof(IAbilityCheckPerformanceProvider))||
                actor is RulesetCharacterMonster&&features.Cast<IAbilityCheckPerformanceProvider>().Any(f=>f.UseControllerAbilityChecks)) return false;
            actor.EnumerateFeaturesToBrowse<IActionPerformanceProvider>(features);
            if(!NativeSavePlanner.NativeInterfaceMethods(features,typeof(IActionPerformanceProvider))) return false;
            foreach(var receiver in new[]{actor,actor.OriginalFormCharacter})
            {
                if(receiver==null) continue;
                foreach(var method in NativeSavePlanner.Named(receiver.GetType(),"EnumerateFeaturesToBrowse"))
                    if(method.IsGenericMethodDefinition&&!NativeSavePlanner.NativeMethod(method.MakeGenericMethod(typeof(IActionPerformanceProvider)))) return false;
            }
            return true;
        }
        static Side Capture(RulesetCharacter actor,int bonus,int modifier,string ability,string skill,List<RuleDefinitions.TrendInfo> advantage,List<RuleDefinitions.TrendInfo> trends)
        {
            return new Side {Actor=actor,BaseBonus=bonus,Modifier=modifier,Ability=ability,Skill=skill,RollSkill=skill,
                Advantage=RuleDefinitions.ComputeAdvantage(advantage),Proficient=actor.IsProficient(skill),Faction=actor.Side,Incapacitated=actor.IsIncapacitated,
                AdvantageTrends=new List<RuleDefinitions.TrendInfo>(advantage),ModifierTrends=new List<RuleDefinitions.TrendInfo>(trends)};
        }
        static bool Same(Side expected,Side actual)
        {
            return ReferenceEquals(expected.Actor,actual.Actor)&&expected.BaseBonus==actual.BaseBonus&&expected.Modifier==actual.Modifier&&
                expected.Ability==actual.Ability&&expected.Skill==actual.Skill&&expected.RollSkill==actual.RollSkill&&expected.Advantage==actual.Advantage&&expected.Proficient==actual.Proficient&&expected.Faction==actual.Faction&&expected.Incapacitated==actual.Incapacitated&&
                expected.AdvantageTrends.SequenceEqual(actual.AdvantageTrends)&&expected.ModifierTrends.SequenceEqual(actual.ModifierTrends);
        }
        static bool ContestPrefix(RulesetCharacter __instance,int baseBonus,int rollModifier,string abilityScoreName,string proficiencyName,
            List<RuleDefinitions.TrendInfo> advantageTrends,List<RuleDefinitions.TrendInfo> modifierTrends,int opponentBaseBonus,int opponentRollModifier,
            string opponentAbilityScoreName,string opponentProficiencyName,List<RuleDefinitions.TrendInfo> opponentAdvantageTrends,
            List<RuleDefinitions.TrendInfo> opponentModifierTrends,RulesetCharacter opponent,ref RuleDefinitions.RollOutcome outcome,ref int __result)
        {
            var p=preview;if(p==null)return true;
            if(p.Captured||!ReferenceEquals(p.Actor,__instance)||!ReferenceEquals(p.Opponent,opponent)) throw new InvalidOperationException("Unexpected native contest during shove preparation.");
            p.First=Capture(__instance,baseBonus,rollModifier,abilityScoreName,proficiencyName,advantageTrends,modifierTrends);
            p.Second=Capture(opponent,opponentBaseBonus,opponentRollModifier,opponentAbilityScoreName,opponentProficiencyName,opponentAdvantageTrends,opponentModifierTrends);
            // Stock contest IL passes the acting skill to the opposing raw
            // dice call even when its selected defense is Acrobatics. Preserve
            // that native trait-filter argument independently of the display.
            p.Second.RollSkill=proficiencyName;
            p.Captured=true;outcome=RuleDefinitions.RollOutcome.Success;__result=0;return false;
        }
        static void RejectPreviewSum(int diceNumber)
        {if(preview!=null&&diceNumber>0)throw new InvalidOperationException("Random modifier dice require a different native shove preparation boundary.");}
        static void RejectPreviewDraw()
        {if(preview!=null)throw new InvalidOperationException("Random modifier dice require a different native shove preparation boundary.");}
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(contest,prefix:new HarmonyMethod(typeof(ShovePatches),"ContestPrefix") {priority=Priority.First});
            harmony.Patch(AccessTools.Method(typeof(RuleDefinitions),"RollStaticDiceAndSum"),prefix:new HarmonyMethod(typeof(ShovePatches),"RejectPreviewSum") {priority=Priority.First});
            harmony.Patch(AccessTools.Method(typeof(RuleDefinitions),"RollDie"),prefix:new HarmonyMethod(typeof(ShovePatches),"RejectPreviewDraw") {priority=Priority.First});
        }
    }
}
