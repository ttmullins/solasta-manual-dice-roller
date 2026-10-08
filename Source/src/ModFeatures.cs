using System;
using System.Linq;
using HarmonyLib;

namespace ManualDiceRoller
{
    // Production wiring for the 0.5 additions: dialog/gadget outer await sites,
    // single-player deferred rolls (concentration, travel, camp), synchronous
    // pump guards, probe planning, game-font theming, localized prompt text and
    // the "rolled by the game" notices. Keeping it in one entry point lets the
    // core runtime compile unchanged inside its isolated test fixtures.
    internal static class ModFeatures
    {
        internal static bool Preflight(out string error)
        {
            if (!OuterSites.Preflight(out error) || !SyncPumpGuard.Preflight(out error) || !DeferredRolls.Preflight(out error) ||
                !CompositeRolls.Preflight(out error) || !TurnRolls.Preflight(out error) || !FeatureRolls.Preflight(out error) || !MenuRolls.Preflight(out error) || !TrapRolls.Preflight(out error)) return false;
            foreach (var id in OuterSites.Ids.Concat(FeatureRolls.OuterIds).Concat(new[] { FeatureRolls.DeflectSite }))
                if (!NativePatches.IsRegistered(id)) { error = "Outer roll site is not bound to a verified native await: " + id; return false; }
            return true;
        }
        internal static void Install(Harmony harmony)
        {
            OuterSites.Register();
            FeatureRolls.Register();
            OuterSites.Install(harmony);
            SyncPumpGuard.Install(harmony);
            NativeProbe.Install(harmony);
            DeferredRolls.Install(harmony);
            CompositeRolls.Install(harmony);
            TurnRolls.Install(harmony);
            FeatureRolls.Install(harmony);
            MenuRolls.Install(harmony);
            TrapRolls.Install(harmony);
            AutoRollNotices.Install(harmony);
            RuntimeRolls.ExtraPlanners.Clear();
            RuntimeRolls.ExtraPlanners.Add(NativeProbePlanner.TryPrepare);
            RuntimeRolls.ExtraPlanners.Add(DeferredRolls.TryPlanAmbush);
            RuntimeRolls.ExtraPlanners.Add(DeferredRolls.TryPlanFoodYield);
            RuntimeRolls.ExtraPlanners.Add(FallDamage.TryPrepare);
            RuntimeRolls.ExtraPlanners.Add(FeatureRolls.TryPrepare);
            RuntimeRolls.ExtraPlanners.Add(CompositeRolls.TryPrepare);
            RuntimeRolls.ExtraPlanners.Add(TurnRolls.TryPrepare);
            RuntimeRolls.ExtraPreviewing = () => NativeProbe.Active;
            RuntimeRolls.LocalHold = () => DeferredRolls.Holding && !DeferredRolls.Replaying;
            RuntimeRolls.HoldExempt = DeferredRolls.IsRunner;
            RuntimeRolls.DeferredNetworked = () => DeferredRolls.NetworkedPending;
            RuntimeRolls.ExtendedOnline = () => Plugin.Settings != null && Plugin.Settings.MultiplayerExtras;
            RuntimeRolls.PromptFactory = CreatePrompt;
            RuntimeRolls.AttackDescriber = DescribeAttack;
            RuntimeRolls.AttackBonusPlanner = AttackBonusDice.Plan;
            RuntimeRolls.ControllerOf = AttackBonusDice.Controller;
            RuntimeRolls.Explainer = RollReasons.Explain;
            RollReasons.Titler = GameNames.Title;
            MultiplayerRolls.OwnerActor = AttackBonusDice.Controller;
            RollText.Resolver = GameNames.Resolve;
            RollWindow.FrameUpdate = FrameUpdate;
            RollWindow.ThemeProvider = GameTheme.Resolve;
        }
        static void FrameUpdate()
        {
            try { DeferredRolls.Update(); }
            finally { MenuRolls.Update(); }
        }
        // Disable/unload: finish queued single-player rolls with game dice.
        internal static void Flush() { DeferredRolls.Flush(); MenuRolls.Reset(); }
        // Location/game teardown.
        internal static void Drop() { DeferredRolls.Drop(); }
        internal static void Detach()
        {
            RollWindow.FrameUpdate = null;
            RuntimeRolls.LocalHold = null;
            RuntimeRolls.HoldExempt = null;
            RuntimeRolls.DeferredNetworked = null;
            RuntimeRolls.ExtendedOnline = null;
            RuntimeRolls.AttackDescriber = null;
            RuntimeRolls.AttackBonusPlanner = null;
            RuntimeRolls.ControllerOf = null;
            RuntimeRolls.Explainer = null;
            RollReasons.Titler = null;
            MultiplayerRolls.OwnerActor = null;
        }
        // Read-only display context: the target's armor class and the attack's
        // fixed bonus. The game still computes the actual result.
        static void DescribeAttack(NativeInvocation call, NativeRollPlan plan, string name)
        {
            if (name == "RollInitiative")
            {
                // The game adds the character's Initiative attribute to the d20.
                var contender = call.Arg("character") as GameLocationCharacter;
                var roller = contender != null ? contender.RulesetCharacter : call.Receiver as RulesetCharacter;
                RulesetAttribute initiative;
                if (roller != null && roller.TryGetAttribute("Initiative", out initiative) && initiative != null) plan.Describe(0, true, initiative.CurrentValue);
                return;
            }
            int armor = 0, bonus = call.Arg<int>("rollModifier"); bool known = false;
            var target = call.Arg("target") as RulesetActor; RulesetAttribute attribute;
            if (target != null && target.TryGetAttribute("ArmorClass", out attribute) && attribute != null) armor = attribute.CurrentValue;
            if (name == "RollAttackMode") { var mode = call.Arg("attackMode") as RulesetAttackMode; if (mode != null) { bonus += mode.ToHitBonus; known = true; } }
            else { var effect = call.Arg("activeEffect") as RulesetEffect; if (effect != null) { bonus += effect.MagicAttackBonus; known = true; } }
            plan.Describe(armor, known, bonus);
        }
        static RollPrompt CreatePrompt(string title, string actor, string detail, string note, int[] faces, PromptInfo info)
        {
            var prompt = new RollPrompt(title, actor, detail, note, faces);
            if (info == null) return prompt;
            prompt.Advantage = info.Advantage; prompt.IsReroll = info.IsReroll; prompt.Kind = info.Kind;
            prompt.TargetValue = info.Target; prompt.TargetLabel = info.TargetLabel;
            if (info.HasBonus) prompt.SetBonus(info.Bonus);
            return prompt;
        }
    }

    // Localized names from the game's own definitions.
    internal static class GameNames
    {
        internal static string Resolve(string kind, string name)
        {
            BaseDefinition definition = null;
            switch (kind)
            {
                case "ability":
                    SmartAttributeDefinition attribute;
                    if (DatabaseRepository.GetDatabase<SmartAttributeDefinition>().TryGetElement(name, out attribute)) definition = attribute;
                    break;
                case "proficiency":
                    SkillDefinition skill; ToolTypeDefinition tool;
                    if (DatabaseRepository.GetDatabase<SkillDefinition>().TryGetElement(name, out skill)) definition = skill;
                    else if (DatabaseRepository.GetDatabase<ToolTypeDefinition>().TryGetElement(name, out tool)) definition = tool;
                    break;
                case "damage":
                    DamageDefinition damage;
                    if (DatabaseRepository.GetDatabase<DamageDefinition>().TryGetElement(name, out damage)) definition = damage;
                    break;
                case "source":
                    ConditionDefinition condition; SpellDefinition spell; FeatureDefinition any;
                    if (DatabaseRepository.GetDatabase<ConditionDefinition>().TryGetElement(name, out condition)) definition = condition;
                    else if (DatabaseRepository.GetDatabase<SpellDefinition>().TryGetElement(name, out spell)) definition = spell;
                    else if (DatabaseRepository.GetDatabase<FeatureDefinition>().TryGetElement(name, out any)) definition = any;
                    break;
                case "feature":
                    FeatureDefinitionDieRollModifier feature;
                    if (DatabaseRepository.GetDatabase<FeatureDefinitionDieRollModifier>().TryGetElement(name, out feature)) definition = feature;
                    break;
            }
            return Title(definition);
        }
        internal static string Title(BaseDefinition definition)
        {
            try
            {
                if (definition == null || definition.GuiPresentation == null) return null;
                string key = definition.GuiPresentation.Title;
                if (string.IsNullOrEmpty(key)) return null;
                string text = Gui.Localize(key, true);
                // An unresolved key comes back as its raw "Category/&Key" token.
                return string.IsNullOrEmpty(text) || text.IndexOf("/&", StringComparison.Ordinal) >= 0 ? null : text;
            }
            catch (Exception) { return null; }
        }
        internal static string SpellName(BaseDefinition definition)
        {
            if (definition == null) return "";
            return Title(definition) ?? RollText.Words(definition.Name);
        }
        internal static string ActorName(RulesetActor actor)
        {
            try { return actor == null ? "" : actor.Name ?? ""; }
            catch (Exception) { return ""; }
        }
    }
}
