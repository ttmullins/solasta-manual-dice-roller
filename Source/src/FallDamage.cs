using System;
using System.Collections.Generic;

namespace ManualDiceRoller
{
    // Falling damage: CharacterActionFreeFall awaits before
    // RulesetCharacter.SustainFallDamage(cellsNumber, reducedDamage), which
    // rolls (cellsNumber / 2) d6 once the fall exceeds 2 cells plus any
    // feature threshold, subtracts the reduction and inflicts bludgeoning
    // damage. The raw d6 are entered; the game applies everything else.
    internal static class FallDamage
    {
        internal static bool TryPrepare(NativeInvocation call, NativeRollPlan plan)
        {
            if (call == null || call.Method.Name != "SustainFallDamage" || call.Method.DeclaringType != typeof(RulesetCharacter)) return false;
            var actor = call.Receiver as RulesetCharacter;
            if (actor == null || !RuntimeRolls.CanManual(actor, RuleDefinitions.RollContext.AttackDamageValueRoll)) return true;
            int cells = call.Arg<int>("cellsNumber"), reduction = call.Arg<int>("reducedDamage");
            int threshold = 0;
            var features = new List<FeatureDefinition>();
            using (var scope = NativeScratchScope.ForActor(actor))
            {
                if (actor.IsSubstitute) scope.AddActor(actor.OriginalFormCharacter);
                actor.EnumerateFeaturesToBrowse<FeatureDefinitionMovementAffinity>(features);
            }
            foreach (var feature in features)
            {
                var affinity = feature as FeatureDefinitionMovementAffinity;
                if (affinity != null) threshold += affinity.AdditionalFallThreshold;
            }
            if (cells <= 2 + threshold) return true;
            int dice = cells / 2;
            if (dice <= 0 || dice > 64) return true;
            plan.RequireManualState(() => Plugin.Settings.Damage);
            plan.AddStaticDice(actor, RuleDefinitions.DieType.D6, dice, title: "Falling damage", note: RollText.Damage("DamageBludgeoning"),
                hint: "Enter each d6. The game subtracts any reduction and applies resistances.", category: RuleDefinitions.RollContext.AttackDamageValueRoll);
            if (reduction > 0) plan.Describe(0, true, -reduction);
            return true;
        }
    }
}
