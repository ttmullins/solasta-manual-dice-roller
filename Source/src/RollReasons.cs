using System;
using System.Collections.Generic;
using System.Linq;

namespace ManualDiceRoller
{
    // Display only: what a roll is for and what its result changes, read from
    // the planned native call (who attacks whom with what, what a successful
    // saving throw avoids). Nothing here changes a roll, a plan or what the
    // players agree on; a call this cannot read keeps the plainer text.
    internal static class RollReasons
    {
        // The game's localized title of a definition; null when unknown.
        internal static Func<BaseDefinition, string> Titler;

        internal static void Explain(NativeInvocation call, NativeRollPlan plan, int start)
        {
            if (call == null || plan == null || start < 0 || start >= plan.Leaves.Count) return;
            switch (call.Method.Name)
            {
                case "TryRollSavingThrow": ExplainSave(call, plan, start); break;
                case "RollAttackMode":
                case "RollMagicAttack": ExplainAttack(call, plan, start); break;
            }
        }

        // ---- saving throws -------------------------------------------------
        static void ExplainSave(NativeInvocation call, NativeRollPlan plan, int start)
        {
            var target = call.Arg("target") as RulesetActor;
            var forms = (call.Arg("actualEffectForms") ?? call.Arg("effectForms")) as List<EffectForm>;
            string against = SaveSource(call);
            string outcome = SaveOutcome(forms, target);
            for (int i = start; i < plan.Leaves.Count; i++)
            {
                var leaf = plan.Leaves[i];
                if (leaf.Static || leaf.Context != RuleDefinitions.RollContext.SavingThrow) continue;
                if (against.Length > 0) leaf.Note = (string.IsNullOrEmpty(leaf.Note) ? "Saving throw" : leaf.Note) + " against " + against;
                if (outcome.Length > 0) leaf.Hint = outcome;
            }
        }
        // "Giant Crow's Beak", "Fireball" (a trap or hazard has no caster).
        internal static string SaveSource(NativeInvocation call)
        {
            var receiver = call.Receiver;
            var caster = (call.Arg("caster") ?? call.Arg("attacker")) as RulesetActor;
            BaseDefinition definition = null; string name = null;
            var mode = receiver as RulesetAttackMode;
            var effect = receiver as RulesetEffect;
            if (mode != null) definition = mode.SourceDefinition;
            else if (effect != null) definition = effect.SourceDefinition;
            else { definition = call.Arg("sourceDefinition") as BaseDefinition; name = call.Arg("sourceName") as string; }
            string what = definition != null ? Title(definition) : SourceName(name);
            return Owned(Name(caster), what);
        }
        // What a successful save changes, from the forms the game applies.
        internal static string SaveOutcome(List<EffectForm> forms, RulesetActor target)
        {
            if (forms == null || forms.Count == 0) return "";
            var success = new List<string>(); var always = new List<string>();
            foreach (var form in forms)
            {
                if (form == null) continue;
                var affinity = form.SavingThrowAffinity;
                switch (form.FormType)
                {
                    case EffectForm.EffectFormType.Damage:
                    {
                        string type = form.DamageForm == null ? "" : RollText.Damage(form.DamageForm.DamageType);
                        if (type.Length == 0) type = "Damage";
                        if (affinity == RuleDefinitions.EffectSavingThrowType.HalfDamage) Add(success, "half damage");
                        else if (affinity == RuleDefinitions.EffectSavingThrowType.Negates) Add(success, "no " + Lower(type));
                        else Add(always, type);
                        break;
                    }
                    case EffectForm.EffectFormType.Condition:
                    {
                        var condition = form.ConditionForm;
                        if (condition == null || condition.Operation != ConditionForm.ConditionOperation.Add || condition.ConditionDefinition == null) break;
                        string title = Title(condition.ConditionDefinition);
                        if (title.Length == 0) break;
                        if (affinity == RuleDefinitions.EffectSavingThrowType.None) Add(always, title);
                        else Add(success, title + (Has(target, condition.ConditionDefinition) ? " ends" : " is avoided"));
                        break;
                    }
                    default:
                        if (affinity != RuleDefinitions.EffectSavingThrowType.None) Add(success, Avoided(form.FormType));
                        break;
                }
            }
            string text = success.Count > 0 ? "On a success: " + string.Join("; ", success.ToArray()) + "." : "";
            if (always.Count > 0)
                text += (text.Length > 0 ? " " : "") + Join(always) + (always.Count == 1 ? " applies" : " apply") + " either way.";
            return text;
        }
        static string Avoided(EffectForm.EffectFormType type)
        {
            switch (type)
            {
                case EffectForm.EffectFormType.Motion: return "no forced movement";
                case EffectForm.EffectFormType.Kill: return "no instant death";
                case EffectForm.EffectFormType.ShapeChange: return "no change of shape";
                case EffectForm.EffectFormType.Healing:
                case EffectForm.EffectFormType.TemporaryHitPoints: return "no hit points";
                default: return "the effect is avoided";
            }
        }
        static bool Has(RulesetActor target, ConditionDefinition definition)
        {
            try { return target != null && target.HasConditionOfType(definition.Name); }
            catch (Exception) { return false; }
        }

        // ---- attacks ------------------------------------------------------
        static void ExplainAttack(NativeInvocation call, NativeRollPlan plan, int start)
        {
            var target = call.Arg("target") as RulesetActor;
            BaseDefinition source = null;
            var mode = call.Arg("attackMode") as RulesetAttackMode;
            var effect = call.Arg("activeEffect") as RulesetEffect;
            if (mode != null) source = mode.SourceDefinition;
            else if (effect != null) source = effect.SourceDefinition;
            string with = source == null ? "" : Title(source), whom = Name(target);
            string what = with.Length > 0 ? with + (whom.Length > 0 ? " against " + whom : "") : whom.Length > 0 ? "Against " + whom : "";
            for (int i = start; i < plan.Leaves.Count; i++)
            {
                var leaf = plan.Leaves[i];
                if (leaf.Static || leaf.Context != RuleDefinitions.RollContext.AttackRoll) continue;
                if (what.Length > 0) leaf.Note = what + (string.IsNullOrEmpty(leaf.Note) ? "" : " · " + leaf.Note);
                if (string.IsNullOrEmpty(leaf.Hint)) leaf.Hint = "Hits if the total reaches the target's armor class. A natural 20 is a critical hit; a natural 1 always misses.";
            }
        }

        // ---- names ------------------------------------------------------------
        internal static string Title(BaseDefinition definition)
        {
            if (definition == null) return "";
            var titler = Titler;
            if (titler != null)
            {
                try { var text = titler(definition); if (!string.IsNullOrEmpty(text)) return text; }
                catch (Exception) { }
            }
            string name;
            try { name = definition.Name ?? ""; } catch (Exception) { return ""; }
            // A monster attack's internal name: Attack_GiantCrow_Beak -> Beak.
            if (name.StartsWith("Attack_", StringComparison.Ordinal) && name.LastIndexOf('_') > 6) name = name.Substring(name.LastIndexOf('_') + 1);
            foreach (var prefix in new[] { "Condition", "Power", "Spell" })
                if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal) && char.IsUpper(name[prefix.Length])) { name = name.Substring(prefix.Length); break; }
            return RollText.Words(name).Trim();
        }
        // A save's source given only by name (a localized title in the game).
        static string SourceName(string name)
        {
            string text = RollText.Source(name);
            foreach (var prefix in new[] { "Power ", "Attack " })
                if (text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length) return text.Substring(prefix.Length);
            return text;
        }
        internal static string Name(RulesetActor actor)
        {
            try { return actor == null ? "" : (actor.Name ?? "").Trim(); }
            catch (Exception) { return ""; }
        }
        internal static string Owned(string owner, string what)
        {
            if (string.IsNullOrEmpty(what)) return owner ?? "";
            if (string.IsNullOrEmpty(owner)) return what;
            return owner + (owner.EndsWith("s", StringComparison.Ordinal) ? "' " : "'s ") + what;
        }
        static string Lower(string text) { return text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLowerInvariant(text[0]) + text.Substring(1) : text; }
        static void Add(List<string> list, string item) { if (!string.IsNullOrEmpty(item) && !list.Contains(item)) list.Add(item); }
        static string Join(List<string> items)
        {
            if (items.Count <= 1) return items.Count == 1 ? items[0] : "";
            return string.Join(", ", items.Take(items.Count - 1).ToArray()) + " and " + items[items.Count - 1];
        }
    }
}
