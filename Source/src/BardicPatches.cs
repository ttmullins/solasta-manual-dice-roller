using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ManualDiceRoller
{
    // Bardic reaction awaits are inserted after the game's reaction decision.
    // The original native routine keeps its notifications and condition use.
    internal static class BardicPatches
    {
        static readonly MethodInfo native=AccessTools.Method(typeof(RulesetCharacter),"RollBardicInspirationDie");
        static readonly MethodInfo raw=AccessTools.Method(typeof(RuleDefinitions),"RollDie");

        internal static bool TryPrepare(NativeInvocation call,NativeRollPlan plan)
        {
            if(!Equals(call.Method,native)) return false;
            var actor=call.Receiver as RulesetCharacter;
            var condition=call.Arg("sourceCondition") as RulesetCondition;
            if(actor==null||condition==null||call.Arg<bool>("forceMaxValue")) return true;
            var category=call.Site.Contains("HandleBardicInspirationForAttack")?RuleDefinitions.RollContext.AttackRoll:
                call.Site.Contains("HandleFailedSavingThrow")?RuleDefinitions.RollContext.SavingThrow:
                call.Site.Contains("HandleFailedAbilityCheck")?RuleDefinitions.RollContext.AbilityCheck:RuleDefinitions.RollContext.None;
            if(category==RuleDefinitions.RollContext.None||!RuntimeRolls.CanManual(actor,category)||!Guard(actor)) return true;
            var die=condition.BardicInspirationDie;
            Func<bool> valid=()=>Guard(actor)&&condition.BardicInspirationDie==die&&OwnsCondition(actor,condition);
            if(!valid()) return true;
            plan.RequireNativeState(valid);
            plan.AddStaticDice(actor,die,1,title:"Bardic Inspiration",hint:"Enter the raw inspiration die. The game applies it and consumes the condition.",
                advantage:call.Arg<bool>("advantage")?RuleDefinitions.AdvantageType.Advantage:RuleDefinitions.AdvantageType.None,
                role:"BardicInspiration",category:category);
            return true;
        }
        static bool OwnsCondition(RulesetCharacter actor,RulesetCondition condition)
        {
            foreach(var conditions in actor.ConditionsByCategory.Values)
                if(conditions.Any(item=>ReferenceEquals(item,condition))) return true;
            return false;
        }
        static bool Guard(RulesetCharacter actor)
        {
            return actor.GetType().Assembly==typeof(RulesetCharacter).Assembly && NativeSavePlanner.NativeMethod(native) &&
                NativeSavePlanner.NativeMethod(raw) &&
                NativeSavePlanner.NativeMethod(AccessTools.PropertyGetter(typeof(RulesetCondition),"BardicInspirationDie")) &&
                NativeSavePlanner.NativeMethod(AccessTools.PropertyGetter(typeof(RulesetActor),"ConditionsByCategory"));
        }
        public static int Face(RuleDefinitions.DieType diceType,RuleDefinitions.AdvantageType advantageType,out int firstRoll,out int secondRoll,float rollAlterationScore=0f)
        { return RuntimeRolls.InvocationStaticFace(native,diceType,advantageType,out firstRoll,out secondRoll,rollAlterationScore); }
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> source)
        {
            var codes=source.ToList(); int count=0;
            foreach(var code in codes)
                if(code.opcode==OpCodes.Call&&Equals(code.operand,raw)) {code.operand=AccessTools.Method(typeof(BardicPatches),"Face");count++;}
            if(count!=1) throw new InvalidOperationException("The native Bardic Inspiration raw call changed.");
            return codes;
        }
        internal static void Install(Harmony harmony)
        { harmony.Patch(native,transpiler:new HarmonyMethod(typeof(BardicPatches),"Transpile")); }
    }
}
