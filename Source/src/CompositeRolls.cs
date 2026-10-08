using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TA;

namespace ManualDiceRoller
{
    // A replay that makes several native rolls in one synchronous step (the
    // start of a turn, a travel hour with two crafters, a stealth check with
    // the Perception rolled against it) is planned as a list of parts. Each
    // part is an ordinary native call planned by the usual planners; its dice
    // are bound to the condition or effect the game is processing when it
    // rolls them (the anchor), so a die can never move to a different part.
    // A part the game does not reach is simply left unused.
    internal static class CompositeRolls
    {
        static MethodInfo saveToCancel, recurrentForms, spellApply, powerApply, environmentApply;

        internal static bool Preflight(out string error)
        {
            error = null;
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            saveToCancel = typeof(RulesetActor).GetMethod("SaveToCancelCondition", any, null, new[] { typeof(RulesetCondition) }, null);
            recurrentForms = typeof(RulesetActor).GetMethod("ExecuteRecurrentForms", any, null, new[] { typeof(RulesetCondition) }, null);
            spellApply = typeof(RulesetEffectSpell).GetMethod("ApplyEffectOnCharacter", any, null, new[] { typeof(RulesetActor), typeof(bool), typeof(int3) }, null);
            powerApply = typeof(RulesetEffectPower).GetMethod("ApplyEffectOnCharacter", any, null, new[] { typeof(RulesetActor), typeof(bool), typeof(int3) }, null);
            environmentApply = typeof(RulesetEffectEnvironment).GetMethod("ApplyEffectOnCharacter", any, null, new[] { typeof(RulesetActor), typeof(bool), typeof(int3) }, null);
            if (saveToCancel == null || recurrentForms == null || spellApply == null || powerApply == null || environmentApply == null || DeferredRolls.CompositeMethod == null)
            { error = "The native condition/effect processing methods changed."; return false; }
            return true;
        }
        internal static void Install(Harmony harmony)
        {
            string error;
            if (saveToCancel == null && !Preflight(out error)) throw new InvalidOperationException(error);
            var finalizer = new HarmonyMethod(typeof(CompositeRolls).GetMethod(nameof(AnchorFinalizer), BindingFlags.Static | BindingFlags.NonPublic));
            harmony.Patch(saveToCancel, prefix: Own(nameof(ConditionAnchor)), finalizer: finalizer);
            harmony.Patch(recurrentForms, prefix: Own(nameof(RecurrentAnchor)), finalizer: finalizer);
            harmony.Patch(spellApply, prefix: Own(nameof(EffectAnchor)), finalizer: finalizer);
            harmony.Patch(powerApply, prefix: Own(nameof(EffectAnchor)), finalizer: finalizer);
            harmony.Patch(environmentApply, prefix: Own(nameof(EffectAnchor)), finalizer: finalizer);
        }
        static HarmonyMethod Own(string name)
        { return new HarmonyMethod(typeof(CompositeRolls).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First }; }

        sealed class AnchorState { internal object Previous; }
        static void ConditionAnchor(RulesetCondition activeCondition, out AnchorState __state)
        { __state = new AnchorState { Previous = RuntimeRolls.EnterAnchor(activeCondition) }; }
        static void RecurrentAnchor(RulesetCondition rulesetCondition, out AnchorState __state)
        { __state = new AnchorState { Previous = RuntimeRolls.EnterAnchor(rulesetCondition) }; }
        static void EffectAnchor(RulesetEffect __instance, out AnchorState __state)
        { __state = new AnchorState { Previous = RuntimeRolls.EnterAnchor(__instance) }; }
        static Exception AnchorFinalizer(Exception __exception, AnchorState __state)
        {
            if (__state != null) RuntimeRolls.ExitAnchor(__state.Previous);
            return __exception;
        }

        // Plans every part with the ordinary planner chain.
        internal static bool TryPrepare(NativeInvocation call, NativeRollPlan plan)
        {
            if (call == null || !Equals(call.Method, DeferredRolls.CompositeMethod)) return false;
            var parts = call.Arg("parts") as DeferredRolls.RollPart[];
            if (parts == null) return true;
            foreach (var part in parts)
            {
                if (part == null || part.Method == null || part.Args == null) continue;
                if (Equals(part.Method, DeferredRolls.CompositeMethod)) throw new InvalidOperationException("A composite roll cannot contain another composite roll.");
                int start = plan.Leaves.Count;
                var target = new NativeInvocation(call.Owner, call.Site, part.Method, part.Args, call.Generation);
                RuntimeRolls.PlanTarget(target, plan);
                for (int index = start; index < plan.Leaves.Count; index++)
                {
                    plan.Leaves[index].Anchor = part.Anchor;
                    plan.Leaves[index].PartTarget = target;
                }
                plan.LabelRange(start, part.Title, part.Detail);
            }
            return true;
        }
    }
}
