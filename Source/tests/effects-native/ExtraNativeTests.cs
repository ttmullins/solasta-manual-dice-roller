// 0.8 additions run against the pristine dispatcher with the production
// RuntimeRolls, RollBroker and EffectPatches: trap damage (no source),
// Dispel Magic checks, a condition's bardic penalty die, attack bonus dice
// (Bless), effect proxies rolling for their controller, conditional dice
// groups and game master dice.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ManualDiceRoller;

static class ExtraNativeTests
{
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static int checks;
    static void Check(bool yes, string text) { if (!yes) throw new Exception(text); checks++; }
    static void Set(object obj, string name, object value)
    {
        for (var t = obj.GetType(); t != null; t = t.BaseType) { var f = t.GetField(name, Flags | BindingFlags.DeclaredOnly); if (f != null) { f.SetValue(obj, value); return; } }
        throw new Exception("Missing native field " + name);
    }
    static object Get(object obj, string name)
    {
        for (var t = obj.GetType(); t != null; t = t.BaseType) { var f = t.GetField(name, Flags | BindingFlags.DeclaredOnly); if (f != null) return f.GetValue(obj); }
        throw new Exception("Missing native field " + name);
    }
    static object Bare(Type type)
    {
        var obj = RuntimeHelpers.GetUninitializedObject(type);
        for (var t = type; t != null; t = t.BaseType) foreach (var f in t.GetFields(Flags | BindingFlags.DeclaredOnly))
            {
                if (f.IsStatic || f.GetValue(obj) != null) continue;
                if (f.FieldType.IsGenericType) { var g = f.FieldType.GetGenericTypeDefinition(); if (g == typeof(List<>) || g == typeof(Dictionary<,>) || g == typeof(HashSet<>)) f.SetValue(obj, Activator.CreateInstance(f.FieldType)); }
            }
        return obj;
    }
    static RulesetCharacterHero Hero(string name, int hp)
    {
        var hero = (RulesetCharacterHero)Bare(typeof(RulesetCharacterHero)); Set(hero, "currentHitPoints", hp); Set(hero, "name", name);
        Attribute(hero, "HitPoints", 100); Attribute(hero, "ProficiencyBonus", 3);
        return hero;
    }
    static void Attribute(RulesetActor actor, string name, int value)
    {
        var attribute = new RulesetAttribute(); Set(attribute, "currentValue", value); Set(attribute, "upToDate", true);
        ((Dictionary<string, RulesetAttribute>)AccessTools.Field(typeof(RulesetActor), "attributes").GetValue(actor))[name] = attribute;
    }
    static void Drive(IEnumerator iterator)
    {
        if (iterator == null) return;
        var stack = new Stack<IEnumerator>(); stack.Push(iterator); int steps = 0;
        while (stack.Count > 0)
        {
            if (++steps > 400) throw new Exception("Collection hung");
            var top = stack.Peek();
            if (!top.MoveNext()) { (top as IDisposable)?.Dispose(); stack.Pop(); continue; }
            var child = top.Current as IEnumerator; if (child != null) stack.Push(child);
        }
    }
    static void Reset()
    {
        RuntimeRolls.Reset(); Plugin.Enabled = true; Plugin.Online = false; Plugin.Settings.EnemyRolls = true; Plugin.Settings.Traps = true; Plugin.Settings.Features = true;
        RollWindow.Prompts.Clear(); RollWindow.Inputs.Clear(); RollWindow.CancelNext = false; Damage.Clear(); Dissipated = null;
    }
    // ---- boundaries ----------------------------------------------------------
    internal static readonly List<int> Damage = new List<int>();
    static bool CaptureDamage(int rolledDamage, ref bool damageAbsorbedByTemporaryHitPoints, ref int __result) { Damage.Add(rolledDamage); damageAbsorbedByTemporaryHitPoints = false; __result = 0; return false; }
    internal static int[] Dissipated;
    static bool CaptureDissipate(int autoSpellsLevel, int baseDC, int dieRoll, int rollBonus) { Dissipated = new[] { autoSpellsLevel, baseDC, dieRoll, rollBonus }; return false; }
    static bool SkipRefresh() { return false; }
    static bool BardicD8(ref RuleDefinitions.DieType __result) { __result = RuleDefinitions.DieType.D8; return false; }
    internal static ConditionDefinition Inflicted;
    static bool InflictFixture(RulesetActor __instance, RuleDefinitions.DieType bardicInspirationDie, ref RulesetCondition __result)
    {
        var condition = (RulesetCondition)Bare(typeof(RulesetCondition));
        Set(condition, "conditionDefinition", Inflicted); Set(condition, "bardicInspirationDie", bardicInspirationDie);
        typeof(RulesetCharacter).GetMethod("OnConditionAdded", Flags).Invoke(__instance, new object[] { condition });
        __result = condition; LastInflicted = condition; return false;
    }
    internal static RulesetCondition LastInflicted;
    sealed class AttackIterator { internal ActionModifier attackModifier = new ActionModifier(); }
    static void ConditionalMarker(RulesetCharacterHero hero) { }
    // Entity lookups (proxies -> controller).
    public class EntityService : DispatchProxy
    {
        internal static readonly Dictionary<ulong, RulesetEntity> Entities = new Dictionary<ulong, RulesetEntity>();
        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == "TryGetEntityByGuid") { RulesetEntity entity; bool found = Entities.TryGetValue((ulong)args[0], out entity); args[1] = entity; return found; }
            if (method.ReturnType == typeof(bool)) return false;
            return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    public static int Run(Harmony harmony, RulesetImplementationManager manager, MethodInfo applyForms)
    {
        harmony.Patch(AccessTools.Method(typeof(RulesetActor), "InflictDamage"), prefix: new HarmonyMethod(typeof(ExtraNativeTests), nameof(CaptureDamage)) { priority = Priority.First });
        harmony.Patch(AccessTools.Method(typeof(RulesetCharacter), "DissipateSpells"), prefix: new HarmonyMethod(typeof(ExtraNativeTests), nameof(CaptureDissipate)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(RulesetCharacterHero), "RefreshAll"), prefix: new HarmonyMethod(typeof(ExtraNativeTests), nameof(SkipRefresh)));
        harmony.Patch(AccessTools.Method(typeof(RulesetCharacterHero), "GetBardicInspirationDieValue"), prefix: new HarmonyMethod(typeof(ExtraNativeTests), nameof(BardicD8)));
        harmony.Patch(AccessTools.Method(typeof(RulesetActor), "InflictCondition"), prefix: new HarmonyMethod(typeof(ExtraNativeTests), nameof(InflictFixture)));

        // ---- traps: source-less static damage --------------------------------
        foreach (bool sphere in new[] { false, true })
        {
            Reset(); manager.ClearDamageFormsByIndex();
            var form = new EffectForm { FormType = EffectForm.EffectFormType.Damage, DamageForm = new DamageForm { DiceNumber = 2, DieType = RuleDefinitions.DieType.D6, BonusDamage = 1, DamageType = "DamageFire" } };
            var forms = new List<EffectForm> { form };
            var first = Hero("Trap A", 40); var second = Hero("Trap B", 40);
            int[] answers = { 3, 5 };
            RollWindow.Inputs.Enqueue(answers);
            for (int index = 0; index < 2; index++)
            {
                var p = new RulesetImplementationDefinitions.ApplyFormsParams { targetCharacter = index == 0 ? first : second, effectSourceType = RuleDefinitions.EffectSourceType.Environment,
                    trapRangeType = sphere ? RuleDefinitions.TrapRangeType.Sphere : RuleDefinitions.TrapRangeType.Triggerer, targetIndex = index, totalTargetsNumber = 2 };
                if (!sphere && index == 1) RollWindow.Inputs.Enqueue(new[] { 6, 6 });
                var owner = new object();
                var wait = RuntimeRolls.PrepareNativeRoll(owner, "Trap", applyForms, new object[] { manager, forms, p, null, null, null, false, false, false, RuleDefinitions.EffectApplication.All, null });
                Check((wait != null) == (!sphere || index == 0), "A sphere trap asks only for its first target's shared roll (sphere " + sphere + ", target " + index + ")");
                Drive(wait);
                RuntimeRolls.EnterNative(owner, "Trap");
                try { bool absorbed, terminated; manager.ApplyEffectForms(forms, p, null, out absorbed, out terminated); }
                finally { RuntimeRolls.ExitNative(owner, "Trap"); }
            }
            Check(RollWindow.Prompts.Count == (sphere ? 1 : 2) && RollWindow.Prompts.All(prompt => prompt.Faces.SequenceEqual(new[] { 6, 6 })), "The trap's 2d6 are asked " + (sphere ? "once" : "per target"));
            Check(Damage.SequenceEqual(sphere ? new[] { 9, 9 } : new[] { 9, 13 }), "The entered dice plus the bonus are inflicted: " + string.Join(",", Damage));
        }
        Reset(); manager.ClearDamageFormsByIndex(); Plugin.Settings.Traps = false;
        {
            var form = new EffectForm { FormType = EffectForm.EffectFormType.Damage, DamageForm = new DamageForm { DiceNumber = 1, DieType = RuleDefinitions.DieType.D6, DamageType = "DamageFire" } };
            var p = new RulesetImplementationDefinitions.ApplyFormsParams { targetCharacter = Hero("Off", 40), effectSourceType = RuleDefinitions.EffectSourceType.Environment };
            Check(RuntimeRolls.PrepareNativeRoll(new object(), "TrapOff", applyForms, new object[] { manager, new List<EffectForm> { form }, p, null, null, null, false, false, false, RuleDefinitions.EffectApplication.All, null }) == null,
                "Traps off: no prompt");
        }

        // ---- Dispel Magic: the caster's d20 against the target's spells ------
        Reset();
        {
            var location = (RulesetImplementationManagerLocation)Bare(typeof(RulesetImplementationManagerLocation));
            var spells = new Database<SpellDefinition>(); var haste = (SpellDefinition)Bare(typeof(SpellDefinition)); Set(haste, "cachedName", "FixtureHaste"); Set(haste, "guid", "fixture-haste");
            spells.Add(haste); DatabaseRepository.AddDatabase(spells);
            try
            {
                var caster = Hero("Dispeller", 30); var target = Hero("Hasted", 30);
                var condition = (RulesetCondition)Bare(typeof(RulesetCondition)); Set(condition, "effectDefinitionName", "FixtureHaste"); Set(condition, "effectLevel", 5);
                ((Dictionary<string, List<RulesetCondition>>)Get(target, "conditionsByCategory"))["11Effect"] = new List<RulesetCondition> { condition };
                var counter = new CounterForm(); Set(counter, "type", (CounterForm.CounterType)1); Set(counter, "automaticSpellLevel", 3); Set(counter, "checkBaseDC", 10); Set(counter, "addProficiencyBonus", true);
                var form = new EffectForm { FormType = EffectForm.EffectFormType.Counter }; Set(form, "counterForm", counter);
                var forms = new List<EffectForm> { form };
                var p = new RulesetImplementationDefinitions.ApplyFormsParams { sourceCharacter = caster, targetCharacter = target };
                RollWindow.Inputs.Enqueue(new[] { 14 });
                var owner = new object();
                Drive(RuntimeRolls.PrepareNativeRoll(owner, "Dispel", applyForms, new object[] { location, forms, p, null, null, null, false, false, false, RuleDefinitions.EffectApplication.All, null }));
                Check(RollWindow.Prompts.Count == 1 && RollWindow.Prompts[0].Faces.SequenceEqual(new[] { 20 }) && RollWindow.Prompts[0].Title == "Dispel check", "One d20 for the dispel check");
                RuntimeRolls.EnterNative(owner, "Dispel");
                try { bool absorbed, terminated; location.ApplyEffectForms(forms, p, null, out absorbed, out terminated); }
                finally { RuntimeRolls.ExitNative(owner, "Dispel"); }
                Check(Dissipated != null && Dissipated.SequenceEqual(new[] { 3, 10, 14, 3 }), "The game compares the entered 14 + proficiency with each spell: " + (Dissipated == null ? "none" : string.Join(",", Dissipated)));
                Set(condition, "effectLevel", 3); Reset();
                Check(RuntimeRolls.PrepareNativeRoll(new object(), "DispelAuto", applyForms, new object[] { location, forms, p, null, null, null, false, false, false, RuleDefinitions.EffectApplication.All, null }) == null,
                    "Spells at or below the automatic level need no roll");
            }
            finally { DatabaseRepository.RemoveDatabase<SpellDefinition>(); }
        }

        // ---- a condition's bardic penalty die (Cutting Words style) ---------
        Reset();
        {
            var bard = Hero("Bard", 30); var foe = Hero("Foe", 30);
            var affinity = (FeatureDefinitionCombatAffinity)Bare(typeof(FeatureDefinitionCombatAffinity));
            Set(affinity, "myAttackModifierValueDetermination", RuleDefinitions.CombatAffinityValueDetermination.BardicInspirationRoll);
            Inflicted = (ConditionDefinition)Bare(typeof(ConditionDefinition)); Set(Inflicted, "cachedName", "FixtureCuttingWords");
            Set(Inflicted, "conditionType", RuleDefinitions.ConditionType.Detrimental); Set(Inflicted, "features", new List<FeatureDefinition> { affinity });
            var conditionForm = new ConditionForm(); Set(conditionForm, "conditionDefinition", Inflicted); Set(conditionForm, "operation", ConditionForm.ConditionOperation.Add);
            var form = new EffectForm { FormType = EffectForm.EffectFormType.Condition }; Set(form, "conditionForm", conditionForm);
            var forms = new List<EffectForm> { form };
            var p = new RulesetImplementationDefinitions.ApplyFormsParams { sourceCharacter = bard, targetCharacter = foe };
            RollWindow.Inputs.Enqueue(new[] { 6 });
            var owner = new object();
            Drive(RuntimeRolls.PrepareNativeRoll(owner, "Cutting", applyForms, new object[] { manager, forms, p, null, null, null, false, false, false, RuleDefinitions.EffectApplication.All, null }));
            Check(RollWindow.Prompts.Count == 1 && RollWindow.Prompts[0].Faces.SequenceEqual(new[] { 8 }) && RollWindow.Prompts[0].Title == "Bardic die", "The bard's d8");
            int before = SleepNativeTestsFallbacks();
            RuntimeRolls.EnterNative(owner, "Cutting");
            try { bool absorbed, terminated; manager.ApplyEffectForms(forms, p, null, out absorbed, out terminated); }
            finally { RuntimeRolls.ExitNative(owner, "Cutting"); }
            Check(SleepNativeTestsFallbacks() == before && LastInflicted != null && LastInflicted.BardicInspirationRoll == 6, "The condition stores the entered 6; no native draw");
        }

        // ---- attack bonus dice (Bless) ----------------------------------------
        // "received": the trend as another player's game sends it (no die
        // flag, die type or feature source); "ambiguous": a second to-hit
        // trend from the same source keeps the game's roll.
        foreach (string mode in new[] { "bless", "bane", "native", "received", "ambiguous" })
        {
            Reset();
            RuntimeRolls.AttackBonusPlanner = AttackBonusDice.Plan;
            var attacker = Hero("Blessed", 30); var target = Hero("Target", 30);
            var affinity = (FeatureDefinitionCombatAffinity)Bare(typeof(FeatureDefinitionCombatAffinity)); Set(affinity, "guid", "fixture-bless-" + mode); Set(affinity, "cachedName", "Blessed");
            Set(affinity, "myAttackModifierValueDetermination", RuleDefinitions.CombatAffinityValueDetermination.Die);
            Set(affinity, "myAttackModifierSign", mode == "bane" ? RuleDefinitions.AttackModifierSign.Substract : RuleDefinitions.AttackModifierSign.Add);
            Set(affinity, "myAttackModifierDiceNumber", 1); Set(affinity, "myAttackModifierDieType", RuleDefinitions.DieType.D4);
            ((Dictionary<string, List<FeatureDefinition>>)Get(attacker, "activeFeatures"))["Fixture"] = new List<FeatureDefinition> { affinity };
            var iterator = new AttackIterator();
            int auto = mode == "bane" ? -3 : 3;
            // The game's trend carries the affinity's origin: an active
            // character feature named after the feature.
            var trend = new RuleDefinitions.TrendInfo(auto, RuleDefinitions.FeatureSourceType.CharacterFeature, "Blessed", null);
            if (mode != "received") { trend.dieFlag = mode == "bane" ? RuleDefinitions.TrendInfoDieFlag.SubstractDie : RuleDefinitions.TrendInfoDieFlag.AddDie; trend.dieType = RuleDefinitions.DieType.D4; }
            else trend.dieType = (RuleDefinitions.DieType)0;
            iterator.attackModifier.AttacktoHitTrends.Add(trend);
            if (mode == "ambiguous") iterator.attackModifier.AttacktoHitTrends.Add(new RuleDefinitions.TrendInfo(2, RuleDefinitions.FeatureSourceType.CharacterFeature, "Blessed", null));
            iterator.attackModifier.AttackRollModifier = auto + 2 + (mode == "ambiguous" ? 2 : 0);
            var attackMode = (RulesetAttackMode)Bare(typeof(RulesetAttackMode)); Set(attackMode, "toHitBonus", 4);
            var rollAttackMode = typeof(RulesetCharacter).GetMethod("RollAttackMode");
            var args = new object[] { attacker, attackMode, false, target, null, iterator.attackModifier.AttacktoHitTrends, false, iterator.attackModifier.AttackAdvantageTrends, false,
                iterator.attackModifier.AttackRollModifier, RuleDefinitions.RollOutcome.Neutral, 0, -1, true };
            if (mode == "native") RollWindow.CancelNext = true;
            RollWindow.Inputs.Enqueue(new[] { 12 }); RollWindow.Inputs.Enqueue(new[] { 2 });
            Drive(RuntimeRolls.PrepareNativeRoll(iterator, "Attack", rollAttackMode, args));
            if (mode == "native")
            {
                Check(iterator.attackModifier.AttackRollModifier == auto + 2 && iterator.attackModifier.AttacktoHitTrends[0].value == auto, "Let the game roll keeps the game's own bonus die");
                continue;
            }
            if (mode == "ambiguous")
            {
                Check(RollWindow.Prompts.Count == 1 && RollWindow.Prompts[0].Faces.SequenceEqual(new[] { 20 }) && iterator.attackModifier.AttackRollModifier == auto + 4,
                    "Two to-hit trends from one source: only the d20 is asked, the game's bonus stays");
                RuntimeRolls.CancelOwner(iterator);
                continue;
            }
            Check(RollWindow.Prompts.Count == 2 && RollWindow.Prompts[0].Faces.SequenceEqual(new[] { 20 }) && RollWindow.Prompts[1].Faces.SequenceEqual(new[] { 4 }), "The attack's d20, then its d4");
            int entered = mode == "bane" ? -2 : 2; // "received" plans exactly like "bless"
            Check(iterator.attackModifier.AttackRollModifier == entered + 2 && iterator.attackModifier.AttacktoHitTrends[0].value == entered,
                "The game's rolled bonus is replaced by the entered die (" + mode + "): " + iterator.attackModifier.AttackRollModifier);
            var table = typeof(RuntimeRolls).GetField("plans", Flags).GetValue(null);
            var found = new object[] { iterator, null };
            Check((bool)table.GetType().GetMethod("TryGetValue").Invoke(table, found), "The plan is held");
            var plan = (NativeRollPlan)found[1];
            Check(!(bool)typeof(NativeRollPlan).GetField("NativeFallback", Flags).GetValue(plan) && (int)plan.Invocation.Args[9] == entered + 2, "The planned arguments follow the replaced bonus");
            var actual = (object[])args.Clone(); actual[9] = iterator.attackModifier.AttackRollModifier;
            RuntimeRolls.ValidateNativeArguments(iterator, "Attack", actual);
            Check(!(bool)typeof(NativeRollPlan).GetField("NativeFallback", Flags).GetValue(plan), "The original's re-read arguments match: no fallback");
            RuntimeRolls.CancelOwner(iterator);
        }
        RuntimeRolls.AttackBonusPlanner = null;

        // ---- an effect proxy rolls for its controller --------------------------
        Reset();
        {
            var service = DispatchProxy.Create<IRulesetEntityService, EntityService>();
            ServiceRepository.AddService<IRulesetEntityService>(service);
            try
            {
                var hero = Hero("Caster", 30);
                EntityService.Entities[77] = hero;
                var proxy = (RulesetCharacterEffectProxy)Bare(typeof(RulesetCharacterEffectProxy)); Set(proxy, "controllerGuid", 77UL);
                var stray = (RulesetCharacterEffectProxy)Bare(typeof(RulesetCharacterEffectProxy)); Set(stray, "controllerGuid", 99UL);
                Plugin.Settings.EnemyRolls = false;
                Check(!RuntimeRolls.CanManual(proxy, RuleDefinitions.RollContext.AttackRoll), "Without the mapping a proxy is not a party member");
                RuntimeRolls.ControllerOf = AttackBonusDice.Controller;
                Check(ReferenceEquals(AttackBonusDice.Controller(proxy), hero) && ReferenceEquals(AttackBonusDice.Controller(stray), stray), "A proxy maps to its controller; an orphan to itself");
                Check(RuntimeRolls.CanManual(proxy, RuleDefinitions.RollContext.MagicDamageValueRoll) && !RuntimeRolls.CanManual(stray, RuleDefinitions.RollContext.MagicDamageValueRoll),
                    "The controller's player rolls the proxy's dice");
            }
            finally { RuntimeRolls.ControllerOf = null; EntityService.Entities.Clear(); ServiceRepository.RemoveService<IRulesetEntityService>(); }
        }

        // ---- conditional groups and game master dice ---------------------------
        var marker = typeof(ExtraNativeTests).GetMethod(nameof(ConditionalMarker), Flags);
        Func<NativeInvocation, NativeRollPlan, bool> planner = (call, plan) => {
            if (call.Method != marker) return false;
            var hero = (RulesetCharacterHero)call.Args[0];
            plan.AddActorDice(hero, RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AbilityCheck, 1, title: "Search");
            plan.Describe(10, true, 2);
            plan.AddStaticDice(hero, RuleDefinitions.DieType.D6, 2, title: "Food found", category: RuleDefinitions.RollContext.AbilityCheck);
            plan.AskWhen(() => plan.CheckSucceeds(0));
            plan.AddMasterDice(hero, "The creatures", RuleDefinitions.DieType.D20, 1, title: "Ambush: the creatures", category: RuleDefinitions.RollContext.AbilityCheck);
            return true;
        };
        RuntimeRolls.ExtraPlanners.Add(planner);
        try
        {
            foreach (int face in new[] { 9, 5 })
            {
                foreach (bool enemy in new[] { false, true })
                {
                    Reset(); Plugin.Settings.EnemyRolls = enemy;
                    var hero = Hero("Searcher", 30);
                    RollWindow.Inputs.Enqueue(new[] { face }); if (face == 9) RollWindow.Inputs.Enqueue(new[] { 3, 4 }); if (enemy) RollWindow.Inputs.Enqueue(new[] { 15 });
                    var owner = new object();
                    Drive(RuntimeRolls.PrepareNativeRoll(owner, "Conditional", marker, new object[] { hero }));
                    var titles = RollWindow.Prompts.Select(prompt => prompt.Title).ToArray();
                    var expected = new List<string> { "Search" }; if (face == 9) expected.Add("Food found"); if (enemy) expected.Add("Ambush: the creatures");
                    Check(titles.SequenceEqual(expected), "Face " + face + " (+2 vs DC 10), enemy dice " + enemy + ": " + string.Join(",", titles));
                    Check(RollWindow.Inputs.Count == 0, "Every queued answer was consumed");
                    RuntimeRolls.CancelOwner(owner);
                }
            }
        }
        finally { RuntimeRolls.ExtraPlanners.Remove(planner); }
        return checks;
    }
    // The Sleep fixture counts the automatic draws its RNG boundary makes.
    static int SleepNativeTestsFallbacks() { return (int)typeof(SleepNativeTests).GetField("fallbackDraws", Flags).GetValue(null); }
}
