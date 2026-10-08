using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ManualDiceRoller;

static class NativeAdapterTests
{
    static int checks, compositeCases;
    sealed class Flat : IEnumerator, IDisposable
    {
        readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();
        internal Flat(IEnumerator root) { stack.Push(root); }
        public object Current { get; private set; }
        public bool MoveNext()
        {
            while (stack.Count > 0) {
                var current = stack.Peek();
                if (!current.MoveNext()) { stack.Pop(); var d = current as IDisposable; if (d != null) d.Dispose(); continue; }
                var child = current.Current as IEnumerator; if (child != null) { stack.Push(child); continue; }
                Current = current.Current; return true;
            }
            return false;
        }
        public void Reset() { throw new NotSupportedException(); }
        public void Dispose() { while (stack.Count > 0) { var d = stack.Pop() as IDisposable; if (d != null) d.Dispose(); } }
    }
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Reset()
    {
        RuntimeRolls.Reset(); Plugin.Enabled = true; Plugin.Multiplayer = false; Plugin.Logs.Clear(); Plugin.DisabledContexts.Clear(); Plugin.Settings.SpellPools=true; Plugin.Settings.HitDice=true;
        RollWindow.Inputs.Clear(); RollWindow.Prompts.Clear(); RollWindow.BeforeAccept = null; RollWindow.CancelNext = false; RollWindow.MutateAfterAccept = false; RollWindow.DeferAccept=false;
        EffectPatches.Prepare = null; TA.DeterministicRandom.Calls = 0; TA.DeterministicRandom.Values.Clear();
        ServiceRepository.Settings = new TestSettings(); ServiceRepository.Rules = new TestRules();
    }
    static RulesetCharacterHero Actor(bool proficient, int force)
    {
        var actor = new RulesetCharacterHero(); if (proficient) actor.Proficiencies.Add("Stealth");
        actor.Features.Add(new FeatureDefinitionDieRollModifier { Name = "SkillClamp", ValidityContext = RuleDefinitions.RollContext.AbilityCheck, ValidSkills = new List<string> { "Stealth" }, RequireProficiency = true, MinRollValue = 2, MaxRollValue = 17 });
        actor.Features.Add(new FeatureDefinitionDieRollModifier { Name = "DieRollModifierHalfingLucky", ValidityContext = RuleDefinitions.RollContext.AbilityCheck, ValidSkills = new List<string> { "Arcana" }, RequireProficiency = true, MinRerollValue = 1, RerollCount = 1, RerollLocalizationKey = "Lucky" });
        actor.Features.Add(new FeatureDefinitionDieRollModifier { Name = "CompositeReroll", ValidityContext = RuleDefinitions.RollContext.AbilityCheck, MinRerollValue = 2, RerollCount = 2, RerollLocalizationKey = "Composite" });
        actor.Features.Add(new FeatureDefinitionDieRollModifier { Name = "Forced", ValidityContext = RuleDefinitions.RollContext.AbilityCheck, ForcedDamageOnDice = (RuleDefinitions.RollForcedDamage)force });
        return actor;
    }
    static List<RuleDefinitions.TrendInfo> Trends(RuleDefinitions.AdvantageType advantage)
    { return new List<RuleDefinitions.TrendInfo> { new RuleDefinitions.TrendInfo(advantage == RuleDefinitions.AdvantageType.Advantage ? 1 : advantage == RuleDefinitions.AdvantageType.Disadvantage ? -1 : 0) }; }
    static object[] AbilityArgs(RulesetCharacter actor, string skill, RuleDefinitions.AdvantageType advantage)
    { return new object[] { actor, "Wisdom", skill, Trends(advantage), false }; }
    static void Prepare(object owner, MethodBase method, object[] args, string site = "test-site")
    {
        var iterator = new Flat(RuntimeRolls.PrepareNativeRoll(owner, site, method, args)); int frames = 0;
        while (iterator.MoveNext()) if (++frames > 2000) throw new Exception("Preparation did not release its prompt.");
    }
    static int NativeDie(RulesetCharacter actor, string skill, RuleDefinitions.AdvantageType advantage, out int first, out int second)
    { return actor.RollDie(RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AbilityCheck, actor.IsProficient(skill), advantage, out first, out second, true, true, skill); }

    static void CompositeNativeComparison()
    {
        var method = typeof(RulesetCharacter).GetMethod("RollAbilityCheck");
        foreach (int firstInput in new[] { 1, 2, 3, 18, 20 })
        foreach (RuleDefinitions.AdvantageType advantage in Enum.GetValues(typeof(RuleDefinitions.AdvantageType)))
        foreach (bool proficient in new[] { false, true })
        foreach (string skill in new[] { "Stealth", "Other" }) {
            Reset(); int force = compositeCases % 3;
            var raw = new List<int> { firstInput };
            if (advantage != RuleDefinitions.AdvantageType.None) raw.Add(21 - firstInput);
            raw.AddRange(new[] { 1, 2, 14, 3, 19, 7, 9, 20 });
            var baseline = Actor(proficient, force); var baselineEvents = new List<string>();
            baseline.DieRerolled = (a, d, old, next, key) => baselineEvents.Add(old + ":" + next + ":" + key);
            Plugin.Enabled = false; TA.DeterministicRandom.Values = new Queue<int>(raw.Select(v => v - 1));
            int expectedFirst, expectedSecond; int expected = NativeDie(baseline, skill, advantage, out expectedFirst, out expectedSecond);
            int nativeDraws = TA.DeterministicRandom.Calls;
            Reset(); var actor = Actor(proficient, force); var events = new List<string>();
            actor.DieRerolled = (a, d, old, next, key) => events.Add(old + ":" + next + ":" + key);
            RollWindow.Inputs = new Queue<int>(raw); var owner = new object(); Prepare(owner, method, AbilityArgs(actor, skill, advantage));
            int actualFirst, actualSecond, actual;
            RuntimeRolls.EnterNative(owner, "test-site");
            try { actual = NativeDie(actor, skill, advantage, out actualFirst, out actualSecond); }
            finally { RuntimeRolls.ExitNative(owner, "test-site"); }
            Check(actual == expected && actualFirst == expectedFirst && actualSecond == expectedSecond, "Composite native outcome mismatch at case " + compositeCases);
            Check(events.SequenceEqual(baselineEvents), "Native reroll notification sequence changed at case " + compositeCases);
            Check(TA.DeterministicRandom.Calls == 0, "A planned native trait replacement used automatic RNG at case " + compositeCases);
            Check(RollWindow.Prompts.Sum(p => p.DieFaces.Length) == nativeDraws, "Planner/raw native draw count mismatch at case " + compositeCases);
            compositeCases++;
        }
        Console.WriteLine("PASS: " + compositeCases + " composite native comparisons across original faces, advantage/disadvantage, proficiency, skill eligibility, clamps, Lucky, repeated rerolls and forced faces.");
    }

    public static void PlannedSum(RulesetActor actor, object form) { }
    static void ConfigureSum(RulesetActor actor, object form, int count, RuleDefinitions.DieType die = RuleDefinitions.DieType.D6)
    {
        EffectPatches.Prepare = (call, plan) => { plan.AddActorDice(actor, die, RuleDefinitions.RollContext.MagicDamageValueRoll, count, form: form, title: "Damage"); return true; };
    }
    static void PrepareSum(object owner, RulesetActor actor, object form)
    { Prepare(owner, typeof(NativeAdapterTests).GetMethod("PlannedSum"), new object[] { actor, form }); }

    static void NestedEventDice()
    {
        Reset(); var actor = new RulesetCharacterHero(); var form = new object(); var owner = new object();
        actor.Features.Add(new FeatureDefinitionDieRollModifier { Name = "DieRollModifierHalfingLucky", ValidityContext = RuleDefinitions.RollContext.MagicDamageValueRoll, MinRerollValue = 1, RerollCount = 1, RerollLocalizationKey = "Lucky" });
        ConfigureSum(actor, form, 2); RollWindow.Inputs = new Queue<int>(new[] { 1, 4, 5 }); PrepareSum(owner, actor, form);
        int nested = 0; actor.DieRerolled = (a, d, old, next, key) => { int first, second; nested = a.RollDie(d, RuleDefinitions.RollContext.MagicDamageValueRoll, false, RuleDefinitions.AdvantageType.None, out first, out second, enumerateFeatures: false); };
        TA.DeterministicRandom.Values.Enqueue(2); int total;
        RuntimeRolls.EnterNative(owner, "test-site"); var previous = RuntimeRolls.EnterForm(form);
        var rolled = new List<int>();
        try { total = actor.RollDiceAndSum(RuleDefinitions.DieType.D6, RuleDefinitions.RollContext.MagicDamageValueRoll, 2, rolled); }
        finally { RuntimeRolls.ExitForm(previous); RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(total == 9 && rolled.SequenceEqual(new[] { 5, 4 }) && nested == 3 && TA.DeterministicRandom.Calls == 1, "Nested actor notification stole second prepared die: total=" + total + ", nested=" + nested);

        Reset(); actor = new RulesetCharacterHero(); form = new object(); owner = new object();
        actor.Features.Add(new FeatureDefinitionDieRollModifier { Name = "Repeated", ValidityContext = RuleDefinitions.RollContext.MagicDamageValueRoll, MinRerollValue = 1, RerollCount = 2, RerollLocalizationKey = "Repeated" });
        ConfigureSum(actor, form, 1, RuleDefinitions.DieType.D20); RollWindow.Inputs = new Queue<int>(new[] { 1, 1, 13 }); PrepareSum(owner, actor, form);
        var nestedValues = new List<int>();
        actor.DieRerolled = (a, d, old, next, key) => nestedValues.Add(RuleDefinitions.RollStaticDiceAndSum(1, d));
        TA.DeterministicRandom.Values = new Queue<int>(new[] { 2, 3 });
        RuntimeRolls.EnterNative(owner, "test-site"); previous = RuntimeRolls.EnterForm(form);
        try { total = actor.RollDiceAndSum(RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.MagicDamageValueRoll, 1); }
        finally { RuntimeRolls.ExitForm(previous); RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(total == 13 && nestedValues.SequenceEqual(new[] { 3, 4 }) && TA.DeterministicRandom.Calls == 2, "Nested static notification stole a future planned reroll: total=" + total + ", nested=" + String.Join(",", nestedValues));
        Console.WriteLine("PASS: nested actor and static-sum notifications keep independent native RNG.");
    }

    static void CancelMutationAndFormIdentity()
    {
        Reset(); var actor = new RulesetCharacterHero(); var owner = new object(); var method = typeof(RulesetCharacter).GetMethod("RollAbilityCheck");
        RollWindow.CancelNext = true; Prepare(owner, method, AbilityArgs(actor, "Other", RuleDefinitions.AdvantageType.None)); TA.DeterministicRandom.Values.Enqueue(7);
        RuntimeRolls.EnterNative(owner, "test-site"); int first, second, value;
        try { value = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.None, out first, out second); } finally { RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(value == 8 && TA.DeterministicRandom.Calls == 1 && !RollWindow.IsVisible, "Cancel does not fall back without a wait");

        Reset(); actor = new RulesetCharacterHero(); owner = new object();
        var feature = new FeatureDefinitionDieRollModifier { Name = "Clamp", ValidityContext = RuleDefinitions.RollContext.AbilityCheck, MinRollValue = 2 }; actor.Features.Add(feature);
        RollWindow.Inputs.Enqueue(18); RollWindow.BeforeAccept = p => { feature.MinRollValue = 7; RollWindow.BeforeAccept = null; };
        Prepare(owner, method, AbilityArgs(actor, "Other", RuleDefinitions.AdvantageType.None)); TA.DeterministicRandom.Values.Enqueue(2);
        RuntimeRolls.EnterNative(owner, "test-site");
        try { value = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.None, out first, out second); } finally { RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(value == 7 && TA.DeterministicRandom.Calls == 1 && Plugin.Logs.Any(l => l.Contains("features changed")), "Feature mutation did not invalidate accepted face");

        Reset(); actor = new RulesetCharacterHero(); owner = new object(); var skippedForm = new object(); var finalForm = new object();
        EffectPatches.Prepare = (call, plan) => { plan.AddActorDice(actor, RuleDefinitions.DieType.D6, RuleDefinitions.RollContext.MagicDamageValueRoll, 1, form: skippedForm); plan.AddActorDice(actor, RuleDefinitions.DieType.D6, RuleDefinitions.RollContext.MagicDamageValueRoll, 1, form: finalForm); return true; };
        RollWindow.Inputs = new Queue<int>(new[] { 2, 6 }); PrepareSum(owner, actor, skippedForm);
        RuntimeRolls.EnterNative(owner, "test-site"); var previous = RuntimeRolls.EnterForm(finalForm);
        try { value = actor.RollDiceAndSum(RuleDefinitions.DieType.D6, RuleDefinitions.RollContext.MagicDamageValueRoll, 1); } finally { RuntimeRolls.ExitForm(previous); RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(value == 6 && TA.DeterministicRandom.Calls == 0, "Skipped form shifted its entered face into a later form");
        Console.WriteLine("PASS: canceled requests, changed native features and skipped form identities.");
    }

    static void AtomicAutoStep()
    {
        Reset(); var actor = new RulesetCharacterHero(); var owner = new object();
        var firstForm = new object(); var secondForm = new object();
        EffectPatches.Prepare = (call, plan) => {
            plan.AddActorDice(actor, RuleDefinitions.DieType.D6, RuleDefinitions.RollContext.MagicDamageValueRoll, 1, form: firstForm);
            plan.AddActorDice(actor, RuleDefinitions.DieType.D6, RuleDefinitions.RollContext.MagicDamageValueRoll, 1, form: secondForm);
            return true;
        };
        RollWindow.Inputs.Enqueue(6);
        RollWindow.BeforeAccept = prompt => { if (RollWindow.Prompts.Count == 2) RollWindow.CancelNext = true; };
        PrepareSum(owner, actor, firstForm);
        Check(RollWindow.Prompts.Count == 2 && !RollWindow.IsVisible, "Auto on a later group did not finish preparation");
        Check(TA.DeterministicRandom.Calls == 0, "Group preparation applied an automatic native draw before the owned call");
        TA.DeterministicRandom.Values.Enqueue(2); TA.DeterministicRandom.Values.Enqueue(3);
        RuntimeRolls.EnterNative(owner, "test-site"); int firstValue, secondValue;
        try {
            var previous = RuntimeRolls.EnterForm(firstForm);
            try { firstValue = actor.RollDiceAndSum(RuleDefinitions.DieType.D6, RuleDefinitions.RollContext.MagicDamageValueRoll, 1); }
            finally { RuntimeRolls.ExitForm(previous); }
            previous = RuntimeRolls.EnterForm(secondForm);
            try { secondValue = actor.RollDiceAndSum(RuleDefinitions.DieType.D6, RuleDefinitions.RollContext.MagicDamageValueRoll, 1); }
            finally { RuntimeRolls.ExitForm(previous); }
        } finally { RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(firstValue == 3 && secondValue == 4, "Auto on group two partially retained group one's accepted face");
        Check(TA.DeterministicRandom.Calls == 2, "Auto did not return both groups to fresh native RNG");

        Reset(); actor = new RulesetCharacterHero(); owner = new object();
        actor.Features.Add(new FeatureDefinitionDieRollModifier { Name = "DieRollModifierHalfingLucky", ValidityContext = RuleDefinitions.RollContext.AbilityCheck, MinRerollValue = 1, RerollCount = 1, RerollLocalizationKey = "Lucky" });
        RollWindow.Inputs.Enqueue(1);
        RollWindow.BeforeAccept = prompt => { if (prompt.Title == "Lucky reroll") RollWindow.CancelNext = true; };
        Prepare(owner, typeof(RulesetCharacter).GetMethod("RollAbilityCheck"), AbilityArgs(actor, "Other", RuleDefinitions.AdvantageType.None));
        Check(RollWindow.Prompts.Count == 2 && RollWindow.Prompts[1].Title == "Lucky reroll" && !RollWindow.IsVisible, "Auto did not finish the accepted-one Lucky replacement request");
        Check(TA.DeterministicRandom.Calls == 0, "Lucky preparation drew native RNG before the owned call");
        TA.DeterministicRandom.Values.Enqueue(0); TA.DeterministicRandom.Values.Enqueue(16);
        var events = new List<string>(); actor.DieRerolled = (a, d, before, after, key) => events.Add(before + ">" + after + ":" + key);
        RuntimeRolls.EnterNative(owner, "test-site"); int first, second, result;
        try { result = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.None, out first, out second); }
        finally { RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(result == 17 && first == 17 && second == -1, "Lucky Auto did not use the fresh native replacement result");
        Check(TA.DeterministicRandom.Calls == 2, "Lucky Auto retained the accepted initial one instead of redrawing both stages");
        Check(events.SequenceEqual(new[] { "1>17:Lucky" }), "Lucky Auto changed the native reroll notification count or values");
        Check(RollWindow.Prompts.Count == 2 && !RollWindow.IsVisible, "Automatic native Lucky processing reopened the manual modal");
        Console.WriteLine("PASS: Auto on a later group or Lucky replacement discards the whole step and redraws every native stage.");
    }

    static void BatchGuardsAndPendingCleanup()
    {
        Reset(); var actor = new RulesetCharacterHero(); var owner = new object(); var form = new object();
        ConfigureSum(actor, form, 2); RollWindow.Inputs = new Queue<int>(new[] { 2, 6 }); PrepareSum(owner, actor, form);
        TA.DeterministicRandom.Values.Enqueue(2); int value;
        RuntimeRolls.EnterNative(owner, "test-site"); var previous = RuntimeRolls.EnterForm(form);
        try { value = actor.RollDiceAndSum(RuleDefinitions.DieType.D6, RuleDefinitions.RollContext.MagicDamageValueRoll, 1); }
        finally { RuntimeRolls.ExitForm(previous); RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(value == 3 && TA.DeterministicRandom.Calls == 1, "Changed actor batch count consumed a partial prepared vector");

        Reset(); actor = new RulesetCharacterHero(); owner = new object(); form = new object();
        EffectPatches.Prepare = (call, plan) => { plan.AddStaticDice(actor, RuleDefinitions.DieType.D6, 2, form); plan.AddStaticDice(actor, RuleDefinitions.DieType.D6, 2, form); return true; };
        RollWindow.Inputs = new Queue<int>(new[] { 2, 3, 4, 5 }); PrepareSum(owner, actor, form);
        TA.DeterministicRandom.Values = new Queue<int>(new[] { 2, 1, 2 });
        RuntimeRolls.EnterNative(owner, "test-site"); previous = RuntimeRolls.EnterForm(form);
        int next;
        try { value = RuleDefinitions.RollStaticDiceAndSum(1, RuleDefinitions.DieType.D6); next = RuleDefinitions.RollStaticDiceAndSum(2, RuleDefinitions.DieType.D6); }
        finally { RuntimeRolls.ExitForm(previous); RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(value == 3 && next == 5 && TA.DeterministicRandom.Calls == 3, "Changed static batch count consumed or shifted prepared vectors");

        Reset(); actor = new RulesetCharacterHero(); owner = new object(); var method = typeof(RulesetCharacter).GetMethod("RollAbilityCheck");
        RollWindow.Inputs.Enqueue(17); Prepare(owner, method, AbilityArgs(actor, "Other", RuleDefinitions.AdvantageType.None));
        TA.DeterministicRandom.Values = new Queue<int>(new[] { 2, 3 }); int first, second, extra, extraSum;
        RuntimeRolls.EnterNative(owner, "test-site");
        try { extra = RuleDefinitions.RollDie(RuleDefinitions.DieType.D20, RuleDefinitions.AdvantageType.None, out first, out second); extraSum = RuleDefinitions.RollStaticDiceAndSum(1, RuleDefinitions.DieType.D20); value = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.None, out first, out second); }
        finally { RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(extra == 3 && extraSum == 4 && value == 17 && TA.DeterministicRandom.Calls == 2, "Unprepared raw/modifier draws stole accepted primary input");

        Reset(); actor = new RulesetCharacterHero(); owner = new object(); RollWindow.Inputs.Enqueue(19); RollWindow.MutateAfterAccept = true;
        Prepare(owner, method, AbilityArgs(actor, "Other", RuleDefinitions.AdvantageType.None));
        RuntimeRolls.EnterNative(owner, "test-site");
        try { value = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.None, out first, out second); } finally { RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(value == 19 && TA.DeterministicRandom.Calls == 0, "UI-owned array mutation changed accepted native face");

        Reset(); actor = new RulesetCharacterHero(); owner = new object(); RollWindow.Inputs.Enqueue(0);
        using (var pending = new Flat(RuntimeRolls.PrepareNativeRoll(owner, "test-site", method, AbilityArgs(actor, "Other", RuleDefinitions.AdvantageType.None)))) {
            Check(pending.MoveNext() && RollWindow.IsVisible, "Invalid typed face must leave the request pending");
            RuntimeRolls.Reset(); Check(!pending.MoveNext() && !RollWindow.IsVisible, "Reset did not release pending coroutine and window");
        }
        Reset(); actor = new RulesetCharacterHero(); owner = new object(); RollWindow.Inputs.Enqueue(0);
        using (var pending = new Flat(RuntimeRolls.PrepareNativeRoll(owner, "test-site", method, AbilityArgs(actor, "Other", RuleDefinitions.AdvantageType.None))))
            Check(pending.MoveNext() && RollWindow.IsVisible, "Disposal case must be awaiting valid input");
        Check(!RollWindow.IsVisible, "Disposing the awaiting coroutine did not release its prompt");
        TA.DeterministicRandom.Values.Enqueue(6); RuntimeRolls.EnterNative(owner, "test-site");
        try { value = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.None, out first, out second); } finally { RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(value == 7 && TA.DeterministicRandom.Calls == 1, "Disposed pending coroutine left an accepted/native plan behind");
        Console.WriteLine("PASS: whole-batch count guards, automatic draw boundaries, accepted-array immutability and pending reset/disposal.");
    }

    static void StartAsk(object owner, RulesetCharacter actor, out IEnumerator collect, out IEnumerator group, out IEnumerator ask)
    {
        collect = RuntimeRolls.PrepareNativeRoll(owner, "test-site", typeof(RulesetCharacter).GetMethod("RollAbilityCheck"), AbilityArgs(actor, "Other", RuleDefinitions.AdvantageType.None));
        if (!collect.MoveNext() || (group = collect.Current as IEnumerator) == null) throw new Exception("Expected owned collection group");
        if (!group.MoveNext() || (ask = group.Current as IEnumerator) == null) throw new Exception("Expected owned Ask iterator");
        if (!ask.MoveNext()) throw new Exception("Expected pending Ask iterator");
    }
    static void DisposeIterator(IEnumerator iterator) { var disposable = iterator as IDisposable; if (disposable != null) disposable.Dispose(); }

    static void OrphanedModalCancellation()
    {
        Reset(); var actor = new RulesetCharacterHero(); var owner = new object(); RollWindow.Inputs.Enqueue(0);
        IEnumerator collect, group, ask; StartAsk(owner, actor, out collect, out group, out ask);
        var firstPrompt = RollWindow.CurrentPrompt; var staleAccept = RollWindow.Accept;
        Check(RollWindow.IsVisible && firstPrompt != null, "CancelOwner test did not open its owned pending modal");
        RuntimeRolls.CancelOwner(owner);
        Check(!RollWindow.IsVisible && RollWindow.Accept == null, "CancelOwner left an orphan modal when Ask was not disposed");
        staleAccept(new[] { 19 });
        Check(Plugin.Logs.Any(message => message.Contains("Stale or canceled")), "CancelOwner left its old UI callback able to accept input");
        var foreign = new RollPrompt("Other owner", "Robin", "", "", 20);
        RollWindow.Inputs.Enqueue(0); RollWindow.Display(foreign, values => { }, () => { });
        DisposeIterator(ask); DisposeIterator(group); DisposeIterator(collect);
        Check(RollWindow.IsVisible && ReferenceEquals(RollWindow.CurrentPrompt, foreign), "Late Ask cleanup closed another owner's modal");
        RollWindow.Close(foreign);
        TA.DeterministicRandom.Values.Enqueue(6); RuntimeRolls.EnterNative(owner, "test-site"); int first, second, value;
        try { value = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.None, out first, out second); }
        finally { RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(value == 7 && TA.DeterministicRandom.Calls == 1, "Canceled modal retained its stale face permit");

        Reset(); actor = new RulesetCharacterHero(); owner = new object(); RollWindow.Inputs.Enqueue(0);
        StartAsk(owner, actor, out collect, out group, out ask);
        Check(RollWindow.IsVisible, "Top-level wait disposal test did not open its modal");
        // TA.Reset drops nested enumerators without calling Ask.Dispose. Dispose
        // only the owning Collect waiter, leaving its child Ask untouched.
        DisposeIterator(collect);
        Check(!RollWindow.IsVisible && RollWindow.Accept == null, "Disposing Collect alone left the removed child Ask's modal visible");
        foreign = new RollPrompt("Other owner", "Robin", "", "", 20);
        RollWindow.Inputs.Enqueue(0); RollWindow.Display(foreign, values => { }, () => { });
        Check(!ask.MoveNext(), "Canceled orphan Ask did not finish when resumed");
        Check(RollWindow.IsVisible && ReferenceEquals(RollWindow.CurrentPrompt, foreign), "Resumed canceled Ask closed a newer owner's modal");
        DisposeIterator(ask); DisposeIterator(group); RollWindow.Close(foreign);

        Reset(); actor = new RulesetCharacterHero(); owner = new object();
        foreign = new RollPrompt("Visible second owner", "Robin", "", "", 20);
        RollWindow.Inputs.Enqueue(0); RollWindow.Display(foreign, values => { }, () => { });
        StartAsk(owner, actor, out collect, out group, out ask);
        Check(RollWindow.IsVisible && ReferenceEquals(RollWindow.CurrentPrompt, foreign) && RollWindow.Prompts.Count == 1, "Waiting first plan replaced a second owner's visible modal");
        RuntimeRolls.CancelOwner(owner); DisposeIterator(collect);
        Check(RollWindow.IsVisible && ReferenceEquals(RollWindow.CurrentPrompt, foreign), "Canceling a waiting plan closed a modal it never displayed");
        Check(!ask.MoveNext(), "Canceled plan waiting behind another modal did not finish");
        Check(RollWindow.IsVisible && ReferenceEquals(RollWindow.CurrentPrompt, foreign), "Waiting Ask cleanup closed another owner's modal");
        DisposeIterator(ask); DisposeIterator(group); RollWindow.Close(foreign);
        Console.WriteLine("PASS: owner cancellation and top-level waiter disposal retire orphan modals without closing another owner's prompt.");
    }

    static FeatureDefinitionDieRollModifier ChangingRerollFeature()
    { return new FeatureDefinitionDieRollModifier { Name = "ChangingReroll", ValidityContext = RuleDefinitions.RollContext.AbilityCheck, MinRerollValue = 1, RerollCount = 2, RerollLocalizationKey = "Changing" }; }
    static void MutatingRerollEvents()
    {
        Reset(); var actor = new RulesetCharacterHero(); var feature = ChangingRerollFeature(); actor.Features.Add(feature);
        var expectedEvents = new List<string>();
        actor.DieRerolled = (a, d, before, after, key) => { expectedEvents.Add(before + ">" + after); if (expectedEvents.Count == 1) feature.MinRerollValue = 2; };
        Plugin.Enabled = false;
        TA.DeterministicRandom.Values = new Queue<int>(new[] { 0, 0, 1, 5, 7 });
        int expectedFirst, expectedSecond; int expected = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.Advantage, out expectedFirst, out expectedSecond);
        Check(expected == 8 && expectedFirst == 6 && expectedSecond == 8 && TA.DeterministicRandom.Calls == 5, "Native mutating-feature baseline changed");

        Reset(); actor = new RulesetCharacterHero(); feature = ChangingRerollFeature(); actor.Features.Add(feature); var owner = new object();
        RollWindow.Inputs = new Queue<int>(new[] { 1, 1, 2, 19 });
        Prepare(owner, typeof(RulesetCharacter).GetMethod("RollAbilityCheck"), AbilityArgs(actor, "Other", RuleDefinitions.AdvantageType.Advantage));
        Check(RollWindow.Prompts.Count == 3 && TA.DeterministicRandom.Calls == 0, "Changing-feature plan did not collect two originals and their separate replacements");
        var actualEvents = new List<string>();
        actor.DieRerolled = (a, d, before, after, key) => { actualEvents.Add(before + ">" + after); if (actualEvents.Count == 1) feature.MinRerollValue = 2; };
        TA.DeterministicRandom.Values = new Queue<int>(new[] { 5, 7 });
        RuntimeRolls.EnterNative(owner, "test-site"); int first, second, result;
        try { result = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.Advantage, out first, out second); }
        finally { RuntimeRolls.ExitNative(owner, "test-site"); }
        Console.WriteLine("Mutating native notification: actual=" + result + " first=" + first + " second=" + second + " nativeRNG=" + TA.DeterministicRandom.Calls + " events=" + string.Join(",", actualEvents.ToArray()));
        Check(result == expected && first == expectedFirst && second == expectedSecond && TA.DeterministicRandom.Calls == 2 && actualEvents.SequenceEqual(expectedEvents), "A native notification changed reroll control and stole the other advantage face's accepted replacement");
        Console.WriteLine("PASS: live feature changes during native notifications revoke remaining prepared replacements.");
    }

    static void NullSkillsDuringNativeNotification()
    {
        Reset();var actor=new RulesetCharacterHero();var feature=ChangingRerollFeature();actor.Features.Add(feature);var owner=new object();RollWindow.Inputs=new Queue<int>(new[]{1,1,2,19});Prepare(owner,typeof(RulesetCharacter).GetMethod("RollAbilityCheck"),AbilityArgs(actor,"Other",RuleDefinitions.AdvantageType.Advantage));
        int notifications=0;actor.DieRerolled=(a,d,before,after,key)=>{if(++notifications==1)feature.ValidSkills=null;};TA.DeterministicRandom.Values.Enqueue(5);RuntimeRolls.EnterNative(owner,"test-site");int first,second,value;
        try{value=NativeDie(actor,"Other",RuleDefinitions.AdvantageType.Advantage,out first,out second);}finally{RuntimeRolls.ExitNative(owner,"test-site");}
        Check(value==6&&first==2&&second==6&&notifications==2&&TA.DeterministicRandom.Calls==1,"A feature's unused ValidSkills null mutation introduced a mod exception or stole prepared replacement");
        var next=new RulesetCharacterHero();var nextOwner=new object();RollWindow.Inputs.Enqueue(17);Prepare(nextOwner,typeof(RulesetCharacter).GetMethod("RollAbilityCheck"),AbilityArgs(next,"Other",RuleDefinitions.AdvantageType.None));RuntimeRolls.EnterNative(nextOwner,"test-site");try{value=NativeDie(next,"Other",RuleDefinitions.AdvantageType.None,out first,out second);}finally{RuntimeRolls.ExitNative(nextOwner,"test-site");}
        Check(value==17&&TA.DeterministicRandom.Calls==1,"Malformed live feature stamp poisoned the next owner's native lease");
        Console.WriteLine("PASS: malformed live feature stamps fall back without adding native exceptions or leaking leases.");
    }

    static void LateNativeAbort()
    {
        Reset(); var actor = new RulesetCharacterHero(); var owner = new object(); var feature = ChangingRerollFeature();
        feature.MinRerollValue = 2; actor.Features.Add(feature);
        RollWindow.Inputs = new Queue<int>(new[] { 1, 2, 13 });
        Prepare(owner, typeof(RulesetCharacter).GetMethod("RollAbilityCheck"), AbilityArgs(actor, "Other", RuleDefinitions.AdvantageType.None));
        var events = new List<string>(); actor.DieRerolled = (a, d, before, after, key) => { events.Add(before + ">" + after); if (events.Count == 1) RuntimeRolls.AbortNative(owner); };
        TA.DeterministicRandom.Values.Enqueue(5); RuntimeRolls.EnterNative(owner, "test-site"); int first, second, result;
        try { result = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.None, out first, out second); }
        finally { RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(result == 6 && first == 6 && TA.DeterministicRandom.Calls == 1 && events.SequenceEqual(new[] { "1>2", "2>6" }), "Native callback abort left its remaining accepted replacement active");
        actor.DieRerolled = null; TA.DeterministicRandom.Values.Enqueue(7);
        result = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.None, out first, out second);
        Check(result == 8 && TA.DeterministicRandom.Calls == 2, "An unprepared call consumed an aborted owner's replacement");
        var nextOwner = new object(); RollWindow.Inputs.Enqueue(17);
        Prepare(nextOwner, typeof(RulesetCharacter).GetMethod("RollAbilityCheck"), AbilityArgs(actor, "Other", RuleDefinitions.AdvantageType.None));
        RuntimeRolls.EnterNative(nextOwner, "test-site");
        try { result = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.None, out first, out second); }
        finally { RuntimeRolls.ExitNative(nextOwner, "test-site"); }
        Check(result == 17 && TA.DeterministicRandom.Calls == 2, "Aborting during a native notification poisoned a later owner's lease");
        Console.WriteLine("PASS: cancellation from a native notification revokes remaining faces and preserves later independent rolls.");
    }

    static void WildShapeAndPreviewPreservation()
    {
        Reset(); Plugin.Settings.EnemyRolls = false;
        var hero = new RulesetCharacterHero(); var firstSentinel = new FeatureDefinition { Name = "Scratch one" }; var secondSentinel = new FeatureDefinition { Name = "Scratch two" };
        hero.Features.Add(new FeatureDefinitionDieRollModifier { Name = "Inherited die feature", ValidityContext = RuleDefinitions.RollContext.AbilityCheck });
        hero.FeaturesToBrowse.Add(firstSentinel); hero.FeaturesToBrowse.Add(secondSentinel); hero.FeaturesOrigin.Add(firstSentinel, new RuleDefinitions.FeatureOrigin()); hero.FeaturesOrigin.Add(secondSentinel, new RuleDefinitions.FeatureOrigin());
        hero.AllConditionsForEnumeration.Add(new RulesetCondition()); hero.AllConditionsForEnumeration.Add(new RulesetCondition());
        var features = hero.FeaturesToBrowse; var origins = hero.FeaturesOrigin; var conditions = hero.AllConditionsForEnumeration;
        var featureIterator = features.GetEnumerator(); var originIterator = origins.GetEnumerator(); var conditionIterator = conditions.GetEnumerator();
        featureIterator.MoveNext(); originIterator.MoveNext(); conditionIterator.MoveNext();
        var shape = new RulesetCharacterMonster(hero); var owner = new object();
        Check(shape.IsSubstitute && ReferenceEquals(shape.OriginalFormCharacter, hero), "Extracted native WildShape metadata no longer binds the original hero");
        Check(RuntimeRolls.CanManual(shape, RuleDefinitions.RollContext.AbilityCheck), "Party hero's stock substitute was classified as an enemy-only roll");
        Check(!RuntimeRolls.CanManual(new RulesetCharacterMonster(), RuleDefinitions.RollContext.AbilityCheck) && !RuntimeRolls.CanManual(new RulesetCharacterMonster(new RulesetCharacter()), RuleDefinitions.RollContext.AbilityCheck), "Substitute eligibility incorrectly included ordinary NPC monsters");
        RollWindow.Inputs.Enqueue(17); Prepare(owner, typeof(RulesetCharacter).GetMethod("RollAbilityCheck"), AbilityArgs(shape, "Other", RuleDefinitions.AdvantageType.None));
        Check(ReferenceEquals(features, hero.FeaturesToBrowse) && ReferenceEquals(origins, hero.FeaturesOrigin) && ReferenceEquals(conditions, hero.AllConditionsForEnumeration), "WildShape preview changed original hero's scratch collection references");
        Check(features.SequenceEqual(new[] { firstSentinel, secondSentinel }) && origins.Count == 2 && conditions.Count == 2, "Native monster enumeration leaked its preview into original hero's scratch contents");
        Check(featureIterator.MoveNext() && originIterator.MoveNext() && conditionIterator.MoveNext(), "WildShape preview invalidated original hero's active scratch enumerators");
        RuntimeRolls.EnterNative(owner, "test-site"); int first, second, result;
        try { result = NativeDie(shape, "Other", RuleDefinitions.AdvantageType.None, out first, out second); }
        finally { RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(result == 17 && TA.DeterministicRandom.Calls == 0, "A stock party substitute did not consume its actor-owned accepted face");
        Console.WriteLine("PASS: native WildShape getters/enumeration preserve original-hero preview scratch and party actor ownership.");
    }

    static void PreparedResourceLimits()
    {
        Reset(); var actor = new RulesetCharacterHero(); var owner = new object();
        actor.Features.Add(new FeatureDefinitionDieRollModifier { Name = "Excessive rerolls", ValidityContext = RuleDefinitions.RollContext.AbilityCheck, MinRerollValue = 1, RerollCount = int.MaxValue });
        var wait = RuntimeRolls.PrepareNativeRoll(owner, "test-site", typeof(RulesetCharacter).GetMethod("RollAbilityCheck"), AbilityArgs(actor, "Other", RuleDefinitions.AdvantageType.None));
        Check(wait == null && !RollWindow.IsVisible && RollWindow.Prompts.Count == 0 && TA.DeterministicRandom.Calls == 0, "Unbounded feature reroll count was not rejected before displaying or rolling");
        Reset(); actor = new RulesetCharacterHero(); owner = new object();
        EffectPatches.Prepare = (call, plan) => { for (int i = 0; i < 17; i++) plan.AddActorDice(actor, RuleDefinitions.DieType.D6, RuleDefinitions.RollContext.MagicDamageValueRoll, 256, form: new object()); return true; };
        wait = RuntimeRolls.PrepareNativeRoll(owner, "test-site", typeof(NativeAdapterTests).GetMethod("PlannedSum"), new object[] { actor, null });
        Check(wait == null && !RollWindow.IsVisible && RollWindow.Prompts.Count == 0 && TA.DeterministicRandom.Calls == 0, "Aggregate multi-form budget was not rejected atomically before UI");
        TA.DeterministicRandom.Values.Enqueue(4); int first, second;
        Check(actor.RollDie(RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AbilityCheck, false, RuleDefinitions.AdvantageType.None, out first, out second) == 5 && TA.DeterministicRandom.Calls == 1, "Resource fallback altered a later ordinary native draw");
        Console.WriteLine("PASS: excessive native reroll and aggregate form plans use bounded automatic fallback before UI.");
    }

    static void ConstantsAndNativeFlags()
    {
        Reset(); var actor = new RulesetCharacterHero(); var owner = new object(); var method = typeof(RulesetCharacter).GetMethod("RollAbilityCheck");
        RollWindow.Inputs.Enqueue(17); Prepare(owner, method, AbilityArgs(actor, "Other", RuleDefinitions.AdvantageType.None));
        RuntimeRolls.EnterNative(owner, "test-site"); int first, second, value, zero, negative, fixedFaces;
        try {
            zero = actor.RollDiceAndSum(RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AbilityCheck, 0);
            negative = actor.RollDiceAndSum(RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AbilityCheck, -1);
            fixedFaces = actor.RollDiceAndSum(RuleDefinitions.DieType.D1, RuleDefinitions.RollContext.AbilityCheck, 3);
            fixedFaces += RuleDefinitions.RollStaticDiceAndSum(2, RuleDefinitions.DieType.D1);
            value = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.None, out first, out second);
        }
        finally { RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(zero == 0 && negative == 0 && fixedFaces == 5 && value == 17 && TA.DeterministicRandom.Calls == 0, "Empty/constant native boundaries invalidated a prepared primary face");

        Reset(); actor = new RulesetCharacterHero(); owner = new object(); var form = new object();
        actor.Features.Add(new FeatureDefinitionDieRollModifier { Name = "NoReroll", ValidityContext = RuleDefinitions.RollContext.MagicDamageValueRoll, MinRollValue = 5, MinRerollValue = 10, RerollCount = 2, ForcedDamageOnDice = RuleDefinitions.RollForcedDamage.Half, RerollLocalizationKey = "Must not reroll" });
        EffectPatches.Prepare = (call, plan) => { plan.AddActorDice(actor, RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.MagicDamageValueRoll, 2, canReroll: false, form: form); return true; };
        RollWindow.Inputs = new Queue<int>(new[] { 3, 4 }); PrepareSum(owner, actor, form); int rerolls = 0; actor.DieRerolled = (a, d, before, after, key) => rerolls++;
        RuntimeRolls.EnterNative(owner, "test-site"); var previous = RuntimeRolls.EnterForm(form); var actualFaces = new List<int>();
        try { value = actor.RollDiceAndSum(RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.MagicDamageValueRoll, 2, actualFaces, canRerollDice: false); }
        finally { RuntimeRolls.ExitForm(previous); RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(value == 20 && actualFaces.SequenceEqual(new[] { 10, 10 }) && rerolls == 0 && RollWindow.Prompts.Count == 1 && TA.DeterministicRandom.Calls == 0, "canReroll=false suppressed native clamp/forced faces or prompted replacements");

        Reset(); actor = new RulesetCharacterHero { Side = RuleDefinitions.Side.Ally }; owner = new object();
        ServiceRepository.Settings.ForceCritEveryFewRolls = true; ServiceRepository.Rules.LastCriticalCounter = 15;
        EffectPatches.Prepare = (call, plan) => { plan.AddActorDice(actor, RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AttackRoll, 1); return true; };
        RollWindow.Inputs.Enqueue(3); PrepareSum(owner, actor, null); RuntimeRolls.EnterNative(owner, "test-site");
        try { value = actor.RollDie(RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AttackRoll, false, RuleDefinitions.AdvantageType.None, out first, out second); }
        finally { RuntimeRolls.ExitNative(owner, "test-site"); }
        Check(value == 20 && first == 20 && ServiceRepository.Rules.LastCriticalCounter == 0 && TA.DeterministicRandom.Calls == 0, "Native forced-critical counter path changed");
        Console.WriteLine("PASS: empty/constant rolls, disabled rerolls with native forced faces, and native critical counters.");
    }

    static void InvalidNativeStateIsAtomic()
    {
        Reset();var actor=new RulesetCharacterHero();var owner=new object();var form=new object();bool valid=true;
        EffectPatches.Prepare=(call,plan)=>{plan.AddStaticDice(actor,RuleDefinitions.DieType.D4,2,category:RuleDefinitions.RollContext.AbilityCheck);plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1);plan.RequireNativeState(()=>valid);return true;};
        RollWindow.Inputs=new Queue<int>(new[]{4,4,17});PrepareSum(owner,actor,form);valid=false;TA.DeterministicRandom.Values=new Queue<int>(new[]{0,1,6});RuntimeRolls.EnterNative(owner,"test-site");int bonus,result,first,second;
        try{bonus=RuleDefinitions.RollStaticDiceAndSum(2,RuleDefinitions.DieType.D4);result=NativeDie(actor,"",RuleDefinitions.AdvantageType.None,out first,out second);}finally{RuntimeRolls.ExitNative(owner,"test-site");}
        Check(bonus==3&&result==7&&TA.DeterministicRandom.Calls==3,"False native-state validator must clear entire static+actor plan before any face consumption");
        Check(!RollWindow.Visible&&RollWindow.Prompts.Count==2,"Invalid native state left modal or opened replacement prompt");
        Console.WriteLine("PASS: rejected native state atomically retires static bonus and actor dice together.");
    }

    static void PendingCategoryChanges()
    {
        Reset();var actor=new RulesetCharacterHero();var owner=new object();var form=new object();EffectPatches.Prepare=(call,plan)=>{plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AttackRoll,1);return true;};RollWindow.DeferAccept=true;
        var wait=new Flat(RuntimeRolls.PrepareNativeRoll(owner,"test-site",typeof(NativeAdapterTests).GetMethod("PlannedSum"),new object[]{actor,form}));Check(wait.MoveNext()&&RollWindow.Visible,"Attack category test did not open genuine pending Ask");var stale=RollWindow.Accept;Plugin.DisabledContexts.Add(RuleDefinitions.RollContext.AttackRoll);int frames=0;while(wait.MoveNext())if(++frames>10)throw new Exception("Changed category did not retire pending Ask");wait.Dispose();
        Check(!RollWindow.Visible,"Disabling required roll category left pending modal");stale(new[]{17});TA.DeterministicRandom.Values.Enqueue(6);RuntimeRolls.EnterNative(owner,"test-site");int first,second,result;try{result=actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AttackRoll,false,RuleDefinitions.AdvantageType.None,out first,out second);}finally{RuntimeRolls.ExitNative(owner,"test-site");}
        Check(result==7&&TA.DeterministicRandom.Calls==1,"Disabled pending category consumed stale accepted face rather than automatic native roll");Plugin.DisabledContexts.Clear();RollWindow.DeferAccept=false;RollWindow.Inputs.Enqueue(17);var nextOwner=new object();PrepareSum(nextOwner,actor,form);RuntimeRolls.EnterNative(nextOwner,"test-site");try{result=actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AttackRoll,false,RuleDefinitions.AdvantageType.None,out first,out second);}finally{RuntimeRolls.ExitNative(nextOwner,"test-site");}Check(result==17&&TA.DeterministicRandom.Calls==1,"Reenabled category's independent request was poisoned");
        Reset();actor=new RulesetCharacterHero();owner=new object();var method=typeof(RulesetCharacter).GetMethod("RollMagicalHitPointsPool");var source=new BaseDefinition{Name="Sleep"};var args=new object[]{actor,source,2,RuleDefinitions.DieType.D8};RollWindow.DeferAccept=true;wait=new Flat(RuntimeRolls.PrepareNativeRoll(owner,"test-site",method,args));Check(wait.MoveNext()&&RollWindow.Visible,"Stock magical pool did not open pending prompt");Plugin.Settings.SpellPools=false;Plugin.Settings.HitDice=true;frames=0;while(wait.MoveNext())if(++frames>10)throw new Exception("Sleep pool settings did not retire pending Ask");wait.Dispose();Check(!RollWindow.Visible,"SpellPools=false retained Sleep prompt through unrelated HitDice=true");TA.DeterministicRandom.Values=new Queue<int>(new[]{0,5});int notified=0;actor.MagicalHitPointsPoolRolled=(a,b,total)=>{notified=total;};RuntimeRolls.EnterNative(owner,"test-site");try{result=actor.RollMagicalHitPointsPool(source,2,RuleDefinitions.DieType.D8);}finally{RuntimeRolls.ExitNative(owner,"test-site");}Check(result==7&&notified==7&&TA.DeterministicRandom.Calls==2,"Canceled native Sleep pool did not use fresh whole-pool RNG and original callback");
        Reset();actor=new RulesetCharacterHero();owner=new object();RollWindow.Inputs=new Queue<int>(new[]{4,8});Prepare(owner,method,new object[]{actor,source,2,RuleDefinitions.DieType.D8});Plugin.Settings.SpellPools=false;RuntimeRolls.EnterNative(owner,"test-site");TA.DeterministicRandom.Values=new Queue<int>(new[]{1,2});try{result=actor.RollMagicalHitPointsPool(source,2,RuleDefinitions.DieType.D8);}finally{RuntimeRolls.ExitNative(owner,"test-site");}Check(result==5&&TA.DeterministicRandom.Calls==2,"Postaccept Sleep availability change retained manual pool faces");
        Console.WriteLine("PASS: pending and accepted category changes retire whole steps; Sleep cannot borrow HitDice availability.");
    }

    public static void PlannedParts() { }
    // Composite replays (a turn start, a lingering effect) bind each part's dice
    // to the condition/effect the game is processing; a part's group the game
    // rolls differently is retired alone.
    static void AnchoredCompositeParts()
    {
        Reset(); var actor = Actor(false, 0); var first = new object(); var second = new object(); var owner = new object();
        var identity = typeof(NativeAdapterTests).GetMethod("PlannedParts");
        EffectPatches.Prepare = (call, plan) => {
            plan.AddActorDice(actor, RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AbilityCheck, 1, skill: "Other", title: "First part"); plan.Leaves[plan.Leaves.Count - 1].Anchor = first;
            plan.AddActorDice(actor, RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AbilityCheck, 1, skill: "Other", title: "Second part"); plan.Leaves[plan.Leaves.Count - 1].Anchor = second;
            plan.AddActorDice(actor, RuleDefinitions.DieType.D6, RuleDefinitions.RollContext.MagicDamageValueRoll, 2, form: second, title: "Damage"); plan.Leaves[plan.Leaves.Count - 1].Anchor = second; plan.Leaves[plan.Leaves.Count - 2].Anchor = second;
            return true; };
        RollWindow.Inputs = new Queue<int>(new[] { 5, 17, 3, 4 });
        var wait = new Flat(RuntimeRolls.PrepareDeferredRoll(owner, "composite", identity, new object[0], identity, new object[0], null, null)); int frames = 0;
        while (wait.MoveNext()) if (++frames > 2000) throw new Exception("Composite preparation did not finish.");
        Check(RollWindow.Prompts.Select(p => p.Title).SequenceEqual(new[] { "First part", "Second part", "Damage" }), "Each part is asked for in order: " + string.Join(",", RollWindow.Prompts.Select(p => p.Title)));
        int a, b, die; TA.DeterministicRandom.Values = new Queue<int>(new[] { 8, 1, 1, 0 });
        RuntimeRolls.EnterNative(owner, "composite");
        try
        {
            var previous = RuntimeRolls.EnterAnchor(second);
            try { die = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.None, out a, out b); }
            finally { RuntimeRolls.ExitAnchor(previous); }
            Check(die == 17 && TA.DeterministicRandom.Calls == 0, "The second part's die is used while the game processes the second anchor, out of order");
            die = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.None, out a, out b);
            Check(die == 9 && TA.DeterministicRandom.Calls == 1, "Outside every anchor the game rolls its own die");
            previous = RuntimeRolls.EnterAnchor(first);
            try { die = NativeDie(actor, "Other", RuleDefinitions.AdvantageType.None, out a, out b); }
            finally { RuntimeRolls.ExitAnchor(previous); }
            Check(die == 5 && TA.DeterministicRandom.Calls == 1, "The first part's die under its own anchor");
            previous = RuntimeRolls.EnterAnchor(second); var form = RuntimeRolls.EnterForm(second);
            try { die = actor.RollDiceAndSum(RuleDefinitions.DieType.D6, RuleDefinitions.RollContext.MagicDamageValueRoll, 3); }
            finally { RuntimeRolls.ExitForm(form); RuntimeRolls.ExitAnchor(previous); }
            Check(TA.DeterministicRandom.Calls == 4 && Plugin.Logs.Any(l => l.Contains("did not match")), "A batch of a different size retires only that group; the game rolls it: calls=" + TA.DeterministicRandom.Calls);
        }
        finally { RuntimeRolls.ExitNative(owner, "composite"); }
        Console.WriteLine("PASS: anchored composite parts and group retirement");
    }
    static void Main()
    {
        HarmonyFixtureHost.Initialize(typeof(Harmony).Assembly);
        var harmony = new Harmony("ManualDiceRoller.NativeDiceFixture"); RuntimeRolls.Install(harmony);
        try { CompositeNativeComparison(); NestedEventDice(); CancelMutationAndFormIdentity(); AtomicAutoStep(); BatchGuardsAndPendingCleanup(); OrphanedModalCancellation(); MutatingRerollEvents(); NullSkillsDuringNativeNotification(); LateNativeAbort(); WildShapeAndPreviewPreservation(); PreparedResourceLimits(); ConstantsAndNativeFlags(); InvalidNativeStateIsAtomic(); PendingCategoryChanges(); AnchoredCompositeParts(); }
        finally { RuntimeRolls.Reset(); harmony.UnpatchAll(harmony.Id); }
        Console.WriteLine("PASS: " + checks + " production adapter assertions over pristine native dice IL with real Harmony.");
    }
}
