using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ManualDiceRoller;

static class Program
{
    // The resolver must exist before any type referring to the game loads.
    static void Main(string[] args)
    {
        string managed = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) => { string file = Path.Combine(managed, new AssemblyName(request.Name).Name + ".dll"); return File.Exists(file) ? Assembly.LoadFrom(file) : null; };
        if (args.Length > 1) Assembly.LoadFrom(Path.GetFullPath(args[1]));
        Start();
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Start() { SyncTests.Run(); }
}

static partial class SyncTests
{
    static int assertions, failures, cases;
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static void Case(string name, Action body)
    {
        cases++; RuntimeRolls.Reset(); Plugin.Enabled = true; Plugin.Online = false; Plugin.Logs.Clear(); Plugin.Settings = new TestSettings();
        Captured = null; CapturedReceiver = null; CapturedChecks.Clear(); ExtraReset();
        try { body(); Console.WriteLine("PASS " + name); }
        catch (Exception error)
        {
            failures++; Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + ": " + error.Message);
            if (error.GetType() != typeof(Exception)) Console.WriteLine("  at " + string.Join("\n  at ", (error.InnerException ?? error).ToString().Split('\n').Take(24)));
        }
        finally { Drop(); }
    }
    static T Make<T>() { return (T)RuntimeHelpers.GetUninitializedObject(typeof(T)); }
    static void Set(object target, string name, object value)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) { field.SetValue(target, value); return; }
        }
        throw new MissingFieldException(target.GetType().Name, name);
    }
    static object Call(Type type, object target, string name, params object[] args)
    {
        try { return type.GetMethod(name, Any).Invoke(target, args); }
        catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    // ---- test-only capture of the original game methods' own dice calls ----
    internal static object[] Captured; internal static object CapturedReceiver;
    // Harmony writes __args back into ref/out arguments after the prefix, so
    // the forced outcome is written to both.
    // The location wrapper is small enough for the CoreCLR JIT to inline into
    // its caller, so both paths are compared one level down, at the ruleset
    // check the wrapper produces. Bonus/armor/modifier preparation is a boundary.
    static bool CaptureCheck(RulesetCharacter __instance, object[] __args, ref RuleDefinitions.RollOutcome outcome, ref int __result)
    { CapturedReceiver = __instance; Captured = (object[])__args.Clone(); for (int i = 0; i < Captured.Length; i++) if (Captured[i] is List<RuleDefinitions.TrendInfo>) Captured[i] = new List<RuleDefinitions.TrendInfo>((List<RuleDefinitions.TrendInfo>)Captured[i]); CapturedChecks.Add(new KeyValuePair<object, object[]>(__instance, Captured)); outcome = ForcedOutcome; __args[12] = outcome; __result = 0; return false; }
    internal static readonly List<KeyValuePair<object, object[]>> CapturedChecks = new List<KeyValuePair<object, object[]>>();
    internal static RuleDefinitions.RollOutcome ForcedOutcome = RuleDefinitions.RollOutcome.Failure;
    internal static int Pauses;
    static IEnumerator OnePause() { yield return null; }
    // Unity's clock is a boundary: the two-second pause becomes one frame.
    static bool NoClockPause(ref IEnumerator __result) { Pauses++; __result = OnePause(); return false; }
    static bool FixedPassivePerception(out bool hasLightDisadvantage, ref int __result) { hasLightDisadvantage = false; __result = 13; return false; }
    static bool FixedBonus(ref int __result) { __result = 4; return false; }
    static bool NoHeavyArmor(ref bool __result) { __result = false; return false; }
    static bool SkipPreparation() { return false; }
    // Wall/feature preparation boundary: one advantage trend and a fixed +2.
    internal static int WallPreparations, ProbedWallPreparations;
    static bool WallPreparation(ActionModifier actionModifier, int contextField)
    {
        WallPreparations++; if (NativeProbe.Active) ProbedWallPreparations++;
        actionModifier.AbilityCheckAdvantageTrends.Add(new RuleDefinitions.TrendInfo(1, RuleDefinitions.FeatureSourceType.CharacterFeature, "Fixture", null));
        actionModifier.AbilityCheckModifier += 2; return false;
    }

    static bool CaptureSave(object __instance, object[] __args, ref RuleDefinitions.RollOutcome saveOutcome, ref bool __result)
    { CapturedReceiver = __instance; Captured = (object[])__args.Clone(); saveOutcome = RuleDefinitions.RollOutcome.Failure; __args[19] = saveOutcome; __result = true; return false; }

    static AbilityCheckDescription Check(string ability, string skill, int dc, RuleDefinitions.AdvantageType affinity, int minRoll, bool silent)
    {
        var check = new AbilityCheckDescription();
        Set(check, "abilityScoreName", ability); Set(check, "proficiencyName", skill); Set(check, "difficultyClass", dc);
        Set(check, "affinity", affinity); Set(check, "minRoll", minRoll); Set(check, "silent", silent);
        return check;
    }
    // Production characters always own their scratch collections.
    static T Actor<T>() where T : RulesetActor
    {
        var actor = Make<T>();
        Set(actor, "featuresToBrowse", new List<FeatureDefinition>());
        Set(actor, "featuresOrigin", new Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin>());
        Set(actor, "allConditionsForEnumeration", new List<RulesetCondition>());
        return actor;
    }
    // A hero whose own feature enumeration runs (no features, no conditions).
    static RulesetCharacterHero FeaturedHero()
    {
        var hero = Actor<RulesetCharacterHero>();
        Set(hero, "activeFeatures", new Dictionary<string, List<FeatureDefinition>>());
        Set(hero, "activeItemFeatures", new Dictionary<ItemDefinition, List<FeatureDefinition>>());
        Set(hero, "activeFightingStyles", new List<FightingStyleDefinition>());
        Set(hero, "trainedFeats", new List<FeatDefinition>());
        Set(hero, "conditionsByCategory", new Dictionary<string, List<RulesetCondition>>());
        Set(hero, "metamagicFeatures", new Dictionary<MetamagicOptionDefinition, FeatureDefinition>());
        Set(hero, "invocations", new List<RulesetInvocation>());
        return hero;
    }
    static GameLocationCharacter Character(RulesetCharacter ruleset)
    {
        var character = new GameLocationCharacter();
        if (ruleset != null) Set(character, "rulesetActor", ruleset);
        return character;
    }
    static FunctorParametersDescription Functor(AbilityCheckDescription check, params GameLocationCharacter[] acting)
    {
        var parameters = new FunctorParametersDescription();
        Set(parameters, "abilityCheck", check);
        parameters.ActingCharacters = new List<GameLocationCharacter>(acting);
        parameters.FailedCharacters = new List<ulong>(); parameters.FailedCharactersAlternate = new List<ulong>();
        Set(parameters, "hasAlternateTargetConditionState", false);
        return parameters;
    }
    static Functor.FunctorExecutionContext Context(bool async, bool replaying = false) { return new Functor.FunctorExecutionContext { Async = async, IsReplaying = replaying }; }
    static NativeInvocation Outer(string site, MethodBase method, params object[] args) { return new NativeInvocation(new object(), site, method, args, 1); }
    static void SameArguments(object[] expected, object[] actual, MethodBase method, string label)
    {
        var parameters = method.GetParameters();
        Check(expected.Length == actual.Length, label + ": argument count");
        for (int i = 0; i < expected.Length; i++)
        {
            if (parameters[i].ParameterType.IsByRef) continue;
            object a = expected[i], b = actual[i];
            if (a is ActionModifier || b is ActionModifier)
            {
                var x = (ActionModifier)a; var y = (ActionModifier)b;
                Check(x != null && y != null && x.AbilityCheckModifier == y.AbilityCheckModifier && x.AbilityCheckAdvantageTrends.Count == y.AbilityCheckAdvantageTrends.Count
                    && x.SavingThrowAdvantageTrends.Count == y.SavingThrowAdvantageTrends.Count, label + ": fresh action modifier " + parameters[i].Name);
            }
            else if (a is System.Collections.IList && b is System.Collections.IList) Check(((IList)a).Count == ((IList)b).Count, label + ": list " + parameters[i].Name);
            else if (a == null || b == null || a.GetType().IsValueType || a is string) Check(Equals(a, b), label + ": " + parameters[i].Name + " expected " + a + " actual " + b);
            else Check(ReferenceEquals(a, b), label + ": reference " + parameters[i].Name);
        }
    }

    internal static void Run()
    {
        HarmonyFixtureHost.Initialize(typeof(Harmony).Assembly);
        string error;
        Check(OuterSites.Preflight(out error), "Outer site preflight: " + error);
        Check(SyncPumpGuard.Preflight(out error), "Sync pump preflight: " + error);
        Check(DeferredRolls.Preflight(out error), "Deferred roll preflight: " + error);
        OuterSites.Register();
        var harmony = new Harmony("sync.sites.fixture");
        NativeProbe.Install(harmony);
        SyncPumpGuard.Install(harmony);
        var locationCheck = typeof(GameLocationCharacter).GetMethods().Single(m => m.Name == "RollAbilityCheck" && m.GetParameters().Length == 12);
        var tryRollSave = typeof(RulesetImplementationManager).GetMethod("TryRollSavingThrow");
        var rulesetCheck = typeof(RulesetCharacter).GetMethods(Any).Single(m => m.Name == "RollAbilityCheck" && m.DeclaringType == typeof(RulesetCharacter));
        harmony.Patch(rulesetCheck, prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("CaptureCheck", Any)) { priority = Priority.Low });
        harmony.Patch(typeof(RulesetCharacterHero).GetMethod("ComputeBaseAbilityCheckBonus"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("FixedBonus", Any)));
        harmony.Patch(typeof(RulesetCharacterHero).GetMethod("IsWearingHeavyArmor"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("NoHeavyArmor", Any)));
        harmony.Patch(typeof(GameLocationCharacter).GetMethod("ComputeAbilityCheckActionModifier"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("WallPreparation", Any)));
        foreach (var prepare in typeof(GameLocationCharacter).GetMethods(Any).Where(m => m.Name == "PrepareActionModifier"))
            harmony.Patch(prepare, prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("SkipPreparation", Any)));
        harmony.Patch(tryRollSave, prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("CaptureSave", Any)) { priority = Priority.First });
        Console.WriteLine("PASS production preflight and patch installation against the pristine game assembly");

        var gadgetCheck = RuntimeRolls.FindOuterSite(OuterSites.GadgetCheck);
        var gadgetSave = RuntimeRolls.FindOuterSite(OuterSites.GadgetSave);
        var narrative = RuntimeRolls.FindOuterSite(OuterSites.NarrativeCheck);
        var executeCheck = typeof(FunctorSetGadgetConditionByAbilityCheck).GetMethod("ExecuteCheckOnCharacter", Any);
        var executeSave = typeof(FunctorSetGadgetConditionBySavingThrow).GetMethod("ExecuteSaveOnCharacter", Any);
        var startState = typeof(NarrativePlayer).GetMethod("StartState", Any);

        Case("Gadget check resolver matches the original functor's own dice call", delegate {
            foreach (var affinity in new[] { RuleDefinitions.AdvantageType.None, RuleDefinitions.AdvantageType.Advantage, RuleDefinitions.AdvantageType.Disadvantage })
            foreach (bool silent in new[] { false, true })
            {
                var firstHero = Actor<RulesetCharacterHero>(); var secondHero = Actor<RulesetCharacterHero>();
                var first = Character(firstHero); var second = Character(secondHero);
                var parameters = Functor(Check("Wisdom", "Survival", 14, affinity, 3, silent), first, second);
                var functor = new FunctorSetGadgetConditionByAbilityCheck(); var context = Context(true);
                // The game rolls with ActingCharacters[0] even for the second actor.
                var inner = gadgetCheck.Resolve(Outer(OuterSites.GadgetCheck, executeCheck, functor, parameters, context, second));
                Check(inner != null && inner.Method == locationCheck && ReferenceEquals(inner.Receiver, first), "Resolver selects the first actor's 12-parameter location check");
                Captured = null; inner.Method.Invoke(inner.Receiver, inner.Args.Skip(1).ToArray());
                var fromResolver = Captured; var resolverActor = CapturedReceiver;
                Captured = null; parameters.FailedCharacters.Clear();
                Call(typeof(FunctorSetGadgetConditionByAbilityCheck), functor, "ExecuteCheckOnCharacter", parameters, context, second);
                Check(Captured != null && fromResolver != null && ReferenceEquals(CapturedReceiver, firstHero) && ReferenceEquals(resolverActor, firstHero), "Original and resolver roll with the first acting character");
                SameArguments(fromResolver, Captured, rulesetCheck, "gadget check " + affinity + "/" + silent);
                Check((int)Captured[0] == 4 && (string)Captured[1] == "Wisdom" && (string)Captured[2] == "Survival" && (int)Captured[6] == 14 && (int)Captured[8] == 3 && (bool)Captured[14] == !silent, "Ruleset check carries bonus, ability, skill, DC, minimum roll and notification");
            }
        });
        Case("Gadget check resolver refuses the paths the original rolls no dice for", delegate {
            var actor = Character(Actor<RulesetCharacterHero>()); var functor = new FunctorSetGadgetConditionByAbilityCheck();
            var parameters = Functor(Check("Wisdom", "Survival", 14, RuleDefinitions.AdvantageType.None, 0, false), actor);
            parameters.FailedCharacters.Add(actor.Guid);
            Check(gadgetCheck.Resolve(Outer(OuterSites.GadgetCheck, executeCheck, functor, parameters, Context(true), actor)) == null, "A failed character without retry permission is not prompted");
            Captured = null; Call(typeof(FunctorSetGadgetConditionByAbilityCheck), functor, "ExecuteCheckOnCharacter", parameters, Context(true), actor);
            Check(Captured == null, "The original also makes no roll for that character");
            parameters = Functor(Check("Wisdom", "Survival", 14, RuleDefinitions.AdvantageType.None, 0, false), actor);
            Check(gadgetCheck.Resolve(Outer(OuterSites.GadgetCheck, executeCheck, functor, parameters, Context(false), actor)) == null, "A synchronously pumped functor keeps native dice");
            Check(gadgetCheck.Resolve(Outer(OuterSites.GadgetCheck, executeCheck, functor, parameters, Context(true, true), actor)) == null, "A replayed functor keeps native dice");
            parameters = Functor(Check("Wisdom", "Perception", 14, RuleDefinitions.AdvantageType.None, 0, false), actor);
            Check(gadgetCheck.Resolve(Outer(OuterSites.GadgetCheck, executeCheck, functor, parameters, Context(true), actor)) == null, "Perception (passive/lighting path) is not this site's roll");
        });
        Case("Gadget saving throw resolver matches the original functor's own save call", delegate {
            var service = Make<RulesetImplementationManager>();
            ServiceRepository.AddService<IRulesetImplementationService>(service);
            try
            {
                var hero = Make<RulesetCharacterHero>(); var actor = Character(hero);
                var parameters = Functor(Check("Dexterity", "", 15, RuleDefinitions.AdvantageType.None, 0, false), actor);
                // A live gadget definition is resolved through Unity's
                // Application.isPlaying; the fixture uses none (a boundary).
                var functor = new FunctorSetGadgetConditionBySavingThrow(); var context = Context(true);
                var inner = gadgetSave.Resolve(Outer(OuterSites.GadgetSave, executeSave, functor, parameters, context, actor));
                Check(inner != null && inner.Method.Name == "TryRollSavingThrow" && ReferenceEquals(inner.Receiver, service), "Resolver targets the registered save service");
                Captured = null; Call(typeof(FunctorSetGadgetConditionBySavingThrow), functor, "ExecuteSaveOnCharacter", parameters, context, actor);
                Check(Captured != null && ReferenceEquals(CapturedReceiver, service), "Original save captured");
                SameArguments(inner.Args.Skip(1).ToArray(), Captured, inner.Method, "gadget save");
                Check(ReferenceEquals(inner.Arg("target"), hero) && inner.Arg<int>("saveDC") == 15 && inner.Arg<string>("savingThrowAbility") == "Dexterity" && inner.Arg("caster") == null, "Target, ability, DC and no caster");
                Check(gadgetSave.Resolve(Outer(OuterSites.GadgetSave, executeSave, functor, parameters, Context(false), actor)) == null, "A synchronously pumped save keeps native dice");
            }
            finally { ServiceRepository.RemoveService<IRulesetImplementationService>(); }
        });
        Case("Dialog check resolver reproduces the dialog state's own check arguments", delegate {
            foreach (bool gameplay in new[] { false, true })
            foreach (bool skipped in new[] { false, true })
            {
                NarrativeState state = gameplay ? (NarrativeState)Make<NarrativeStateAbilityCheckActiveGameplay>() : Make<NarrativeStateAbilityCheck>();
                var description = Make<NarrativeStateDescription>();
                Set(description, "role", "Speaker"); Set(description, "abilityCheckDescription", Check("Charisma", "Persuasion", 17, RuleDefinitions.AdvantageType.Disadvantage, 2, false));
                var world = Make<WorldLocationCharacter>(); Set(world, "m_CachedPtr", new IntPtr(1));
                var actor = Character(null); Set(world, "gameCharacter", actor);
                Set(state, "narrativeStateDescription", description);
                Set(state, "playerRolesMap", new Dictionary<string, WorldLocationCharacter> { { "Speaker", world } });
                state.IsSkipped = skipped;
                var inner = narrative.Resolve(Outer(OuterSites.NarrativeCheck, startState, Make<NarrativePlayer>(), state, null));
                Check(inner != null && inner.Method.Name == "RollAbilityCheck" && inner.Method.GetParameters().Length == 16 && ReferenceEquals(inner.Receiver, actor), "Dialog check targets the speaking character");
                Check(inner.Arg<string>("abilityScoreName") == "Charisma" && inner.Arg<string>("proficiencyName") == "Persuasion" && inner.Arg<int>("checkDC") == 17
                    && inner.Arg<RuleDefinitions.AdvantageType>("baseAffinity") == RuleDefinitions.AdvantageType.Disadvantage && inner.Arg<int>("minRoll") == 2 && !inner.Arg<bool>("passive"), "Ability, skill, DC, affinity and minimum roll");
                bool notify = gameplay && !skipped;
                Check(inner.Arg<bool>("rollDie") == notify && inner.Arg<bool>("notify") == notify, "Dialog flags follow the state type");
            }
        });
        Case("Dialog resolver ignores non-check states and automatic successes", delegate {
            var state = Make<NarrativeStateAbilityCheck>(); var description = Make<NarrativeStateDescription>();
            Set(description, "role", "Speaker"); Set(description, "abilityCheckDescription", Check("Charisma", "Persuasion", 0, RuleDefinitions.AdvantageType.None, 0, false));
            var world = Make<WorldLocationCharacter>(); Set(world, "m_CachedPtr", new IntPtr(1)); Set(world, "gameCharacter", Character(null));
            Set(state, "narrativeStateDescription", description); Set(state, "playerRolesMap", new Dictionary<string, WorldLocationCharacter> { { "Speaker", world } });
            Check(narrative.Resolve(Outer(OuterSites.NarrativeCheck, startState, Make<NarrativePlayer>(), state, null)) == null, "DC 0 is an automatic success with no roll");
            Check(narrative.Resolve(Outer(OuterSites.NarrativeCheck, startState, Make<NarrativePlayer>(), Make<NarrativeState>(), null)) == null, "Ordinary dialog states never prompt");
            Set(state, "playerRolesMap", new Dictionary<string, WorldLocationCharacter>());
            Check(narrative.Resolve(Outer(OuterSites.NarrativeCheck, startState, Make<NarrativePlayer>(), state, null)) == null, "A missing role never prompts");
            var gone = Make<WorldLocationCharacter>(); Set(gone, "gameCharacter", Character(null));
            Set(state, "playerRolesMap", new Dictionary<string, WorldLocationCharacter> { { "Speaker", gone } });
            Check(narrative.Resolve(Outer(OuterSites.NarrativeCheck, startState, Make<NarrativePlayer>(), state, null)) == null, "A destroyed Unity character never prompts");
        });
        Case("Resolver failures are contained and logged", delegate {
            Check(gadgetCheck.Resolve(Outer(OuterSites.GadgetCheck, executeCheck, null, null, null, null)) == null, "Missing arguments resolve to native");
            var broken = new OuterSite("broken", true, outer => { throw new InvalidOperationException("injected resolver"); });
            Check(broken.Resolve(Outer("broken", executeCheck)) == null && Plugin.Logs.Any(l => l.Contains("injected resolver")), "A throwing resolver keeps native dice");
        });

        // ---- NativeProbe ---------------------------------------------------
        Case("Probe captures the first actor die call without running it", delegate {
            var hero = Make<RulesetCharacterHero>();
            var result = NativeProbe.Run(delegate {
                int first, second;
                hero.RollDie(RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.ConcentrationCheck, false, RuleDefinitions.AdvantageType.Advantage, out first, out second, true, true, "");
                throw new Exception("The die call must not return during a probe");
            }, null, new RulesetActor[0]);
            Check(result != null && result.Roll != null && ReferenceEquals(result.Roll.Actor, hero), "Captured actor");
            Check(result.Roll.Die == RuleDefinitions.DieType.D20 && result.Roll.Context == RuleDefinitions.RollContext.ConcentrationCheck && result.Roll.Advantage == RuleDefinitions.AdvantageType.Advantage
                && !result.Roll.Proficient && result.Roll.CanReroll && result.Roll.EnumerateFeatures && result.Roll.Skill == "", "Captured die arguments");
            Check(!NativeProbe.Active, "Probe state is cleared");
        });
        Case("Probe records bonus dice at their minimum without random draws", delegate {
            var hero = Make<RulesetCharacterHero>(); int sum = -1;
            var result = NativeProbe.Run(delegate {
                sum = RuleDefinitions.RollStaticDiceAndSum(2, RuleDefinitions.DieType.D4, null);
                int first, second;
                hero.RollDie(RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.SavingThrow, false, RuleDefinitions.AdvantageType.None, out first, out second, true, true, "");
            }, null, new RulesetActor[0]);
            Check(sum == 2 && result.Bonuses.Count == 1 && result.Bonuses[0].Die == RuleDefinitions.DieType.D4 && result.Bonuses[0].Count == 2 && result.BonusMinimum == 2, "Bonus group recorded at minimum faces");
            Check(result.Roll != null && result.Roll.Context == RuleDefinitions.RollContext.SavingThrow, "Primary roll still captured");
        });
        Case("Probe can capture a specific method's receiver and arguments", delegate {
            var hero = Make<RulesetCharacterHero>();
            var target = typeof(RulesetCharacter).GetMethods(Any).Single(m => m.Name == "RollAbilityCheck" && m.DeclaringType == typeof(RulesetCharacter));
            var trends = new List<RuleDefinitions.TrendInfo>();
            var result = NativeProbe.Run(delegate {
                int a, b, c, d; RuleDefinitions.RollOutcome o;
                hero.RollAbilityCheck(5, "Wisdom", "Survival", trends, trends, 1, 12, false, 0, out a, out b, out c, out o, out d, false, false);
            }, target, new RulesetActor[0]);
            Check(result != null && ReferenceEquals(result.Receiver, hero) && result.Args.Length == 18 && (int)result.Args[0] == 5 && (string)result.Args[2] == "Survival" && (int)result.Args[6] == 12, "Receiver and arguments captured");
        });
        Case("Probe stops before a travel journal entry", delegate {
            var journal = Make<GameTravelJournal>(); bool continued = false;
            var result = NativeProbe.Run(delegate { journal.AddEntry(null); continued = true; }, typeof(GameTravelScheduler).GetMethod("RollSurprise", Any), new RulesetActor[0]);
            Check(result == null && !continued && !NativeProbe.Active, "A probe never writes the journal; it reports no roll");
        });
        Case("Probe reports non-capture outcomes and failures", delegate {
            Check(NativeProbe.Run(delegate { }, null, new RulesetActor[0]) == null, "A method without dice yields no capture");
            bool threw = false;
            try { NativeProbe.Run(delegate { throw new FormatException("injected"); }, null, new RulesetActor[0]); } catch (InvalidOperationException) { threw = true; }
            Check(threw && !NativeProbe.Active, "A failing method is reported and the probe is released");
            threw = false;
            try { NativeProbe.Run(delegate { NativeProbe.Run(delegate { }, null, null); }, null, null); } catch (InvalidOperationException) { threw = true; }
            Check(threw && !NativeProbe.Active, "Nested probes are refused");
        });
        Case("Concentration planner prompts the probed d20 with its bonus dice", delegate {
            var hero = Actor<RulesetCharacterHero>();
            var method = typeof(RulesetCharacter).GetMethod("RollConcentrationCheck");
            // The probe stops at the die; the native preparation itself is a boundary here.
            var plan = new NativeRollPlan(); var invocation = new NativeInvocation(new object(), "Concentration", method, new object[] { hero, 14, "DamageFire", RuleDefinitions.RollOutcome.Neutral }, 1);
            ProbeBoundary.Install();
            try { Check(NativeProbePlanner.TryPrepare(invocation, plan), "Concentration is handled"); }
            finally { ProbeBoundary.Remove(); }
            Check(plan.Groups.Count == 2 && plan.Groups[0].Static && plan.Groups[0].Die == RuleDefinitions.DieType.D4 && plan.Groups[0].Count == 1, "Bless-style bonus die precedes the d20");
            var d20 = plan.Groups[1];
            Check(!d20.Static && d20.Die == RuleDefinitions.DieType.D20 && d20.Context == RuleDefinitions.RollContext.ConcentrationCheck && d20.Advantage == RuleDefinitions.AdvantageType.Advantage && d20.DC == 14, "Concentration d20 with War Caster advantage and DC");
            Check(d20.Title == "Concentration" && d20.Note.StartsWith("Constitution save"), "Concentration prompt text");
            Check(!NativeProbePlanner.TryPrepare(new NativeInvocation(new object(), "x", typeof(RulesetActor).GetMethod("RollDie"), new object[10], 1), new NativeRollPlan()), "Other methods are not this planner's");
        });

        // ---- Deferred roll queue -------------------------------------------
        Case("A deferred roll prompts, then replays the original once", delegate {
            int replays = 0, steps = 0; bool replayingSeen = false;
            RuntimeRolls.Prepare = (owner, site, method, args) => Wait(3, () => steps++);
            Check(Defer("Test.Site", () => Call0(), () => { replays++; replayingSeen = DeferredRolls.Replaying; }), "Queued");
            Check(DeferredRolls.Holding && !DeferredRolls.Replaying && replays == 0, "Queued roll holds native coroutines");
            DeferredRolls.Update(); Check(replays == 0 && steps == 1, "Planning is lazy and the wait advances one frame at a time");
            DeferredRolls.Update(); DeferredRolls.Update(); Check(replays == 0 && steps == 3, "Still waiting for the player");
            DeferredRolls.Update(); Check(replays == 1 && replayingSeen && !DeferredRolls.Holding && !DeferredRolls.Replaying, "Replayed exactly once after the answer");
            Check(RuntimeRolls.Calls.SequenceEqual(new[] { "prepare:Test.Site", "enter:Test.Site", "observe:Test.Site", "exit:Test.Site" }), "Replay runs inside the native roll scope and records its void result: " + string.Join(",", RuntimeRolls.Calls));
            Check(RuntimeRolls.LastOuter == OuterMethod && RuntimeRolls.LastOuterArgs != null && RuntimeRolls.LastOuterArgs.Length == 1, "The skipped original call is the native invocation");
        });
        Case("Unplanned and failed deferrals replay with game dice", delegate {
            int replays = 0;
            Check(Defer("Null.Resolve", () => null, () => replays++), "Queued");
            DeferredRolls.Update(); Check(replays == 1 && !RuntimeRolls.Calls.Contains("prepare:Null.Resolve"), "No inner call: replay immediately");
            RuntimeRolls.Calls.Clear(); RuntimeRolls.Prepare = (o, s, m, a) => null;
            Defer("Null.Plan", () => Call0(), () => replays++); DeferredRolls.Update();
            Check(replays == 2 && RuntimeRolls.Calls.SequenceEqual(new[] { "prepare:Null.Plan", "enter:Null.Plan", "observe:Null.Plan", "exit:Null.Plan" }), "No plan: replay natively");
            RuntimeRolls.Calls.Clear(); RuntimeRolls.Prepare = (o, s, m, a) => Throwing();
            Defer("Throwing.Wait", () => Call0(), () => replays++); DeferredRolls.Update(); DeferredRolls.Update();
            Check(replays == 3 && Plugin.Logs.Any(l => l.Contains("injected wait")), "A failing prompt falls back to native");
            RuntimeRolls.Calls.Clear(); RuntimeRolls.Prepare = null;
            Defer("Throwing.Replay", () => null, () => { throw new InvalidOperationException("injected replay"); });
            Defer("After", () => null, () => replays++);
            DeferredRolls.Update();
            Check(replays == 4 && RuntimeRolls.Calls.Contains("abort") && Plugin.Logs.Any(l => l.Contains("injected replay")), "A failing replay aborts its scope and the queue continues");
        });
        Case("Entries are planned and answered strictly in order", delegate {
            var order = new List<string>();
            RuntimeRolls.Prepare = (owner, site, method, args) => { order.Add("plan " + site); return Wait(1, null); };
            Defer("First", () => Call0(), () => order.Add("replay First"));
            Defer("Second", () => Call0(), () => order.Add("replay Second"));
            for (int i = 0; i < 6; i++) DeferredRolls.Update();
            Check(order.SequenceEqual(new[] { "plan First", "replay First", "plan Second", "replay Second" }), "Second entry is planned only after the first replays: " + string.Join(",", order));
        });
        Case("Disable, flush and drop", delegate {
            int replays = 0;
            RuntimeRolls.Prepare = (o, s, m, a) => Wait(100, null);
            Defer("Waiting", () => Call0(), () => replays++); DeferredRolls.Update();
            Plugin.Enabled = false; DeferredRolls.Update();
            Check(replays == 1 && !DeferredRolls.Holding && RuntimeRolls.Calls.Contains("cancel"), "Disabling the mod finishes a waiting roll with game dice");
            Plugin.Enabled = true;
            Defer("A", () => Call0(), () => replays++); Defer("B", () => Call0(), () => replays++);
            DeferredRolls.Flush(); Check(replays == 3 && !DeferredRolls.Holding, "Flush replays every queued original");
            Defer("C", () => Call0(), () => replays++); DeferredRolls.Drop();
            Check(replays == 3 && !DeferredRolls.Holding, "Drop discards queued originals during teardown");
        });
        Case("Deferral is refused where it could not finish safely", delegate {
            Plugin.Online = true; RuntimeRolls.ExtendedOnline = () => false;
            Check(!Defer("Mp", () => null, () => { }), "Multiplayer keeps synchronous rolls native unless every peer enables extended rolls");
            RuntimeRolls.ExtendedOnline = null;
            Plugin.Online = false; Plugin.Enabled = false; Check(!Defer("Off", () => null, () => { }), "Disabled mod keeps native rolls");
            Plugin.Enabled = true; RuntimeRolls.SyncPumpDepth = 1; Check(!Defer("Pump", () => null, () => { }), "A synchronous pump keeps native rolls");
            RuntimeRolls.SyncPumpDepth = 0;
            bool nested = true;
            Defer("Outer", () => null, () => { nested = Defer("Nested", () => null, () => { }); });
            DeferredRolls.Update(); Check(!nested && !DeferredRolls.Holding, "A roll inside a replay stays native");
            for (int i = 0; i < 64; i++) Check(Defer("Fill" + i, () => null, () => { }), "Capacity");
            Check(!Defer("Overflow", () => null, () => { }), "The queue is bounded");
        });
        Case("Deferred prompts carry their site heading", delegate {
            int replays = 0;
            RuntimeRolls.Prepare = (o, site, m, a) => null;
            Defer("Labelled", () => { var call = Call0(); call.Title = "Monster identification"; call.Detail = "Unknown creature"; return call; }, () => replays++);
            DeferredRolls.Update();
            Check(replays == 1 && RuntimeRolls.Calls.Contains("label:Monster identification|Unknown creature"), "Heading and detail reach the planner: " + string.Join(",", RuntimeRolls.Calls));
        });

        // ---- Monster identification (bestiary) -----------------------------
        var identify = typeof(GameLoreManager).GetMethod("RollIMonsterdentificationCheck", Any);
        harmony.Patch(typeof(GameLocationCharacterAbilityCheckSorter).GetMethod("SortByDecreasingAbilityCheckScore"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("SkipPreparation", Any)));
        Case("Monster identification probes the lore manager's own Survival check", delegate {
            var fallen = Actor<RulesetCharacterHero>(); Set(fallen, "isDeadOrDyingOrUnconscious", true);
            var ellie = Actor<RulesetCharacterHero>(); var other = Actor<RulesetCharacterHero>();
            var party = new List<GameLocationCharacter> { Character(fallen), Character(ellie), Character(other) };
            var manager = Lore(party, out var entry, 2.5f);
            var call = (DeferredRolls.NativeCall)typeof(DeferredRolls).GetMethod("ProbeIdentification", Any).Invoke(null, new object[] { manager, entry, false });
            Check(call != null && call.Method == rulesetCheck && ReferenceEquals(call.Args[0], ellie), "The first conscious character (after the native sort) rolls");
            Check((int)call.Args[1] == 4 && (string)call.Args[2] == "Wisdom" && (string)call.Args[3] == "Survival" && (int)call.Args[7] == 12 && !(bool)call.Args[8], "Wisdom (Survival), DC 10 + CR, not passive");
            Check(call.Title == "Monster identification" && call.Detail == "Unknown creature", "An unidentified monster stays unnamed");
            Check(Captured == null && !NativeProbe.Active, "The probe stopped before the check ran");
            ServiceRepository.RemoveService<IGameLocationCharacterService>();
        });
        Case("Monster identification defers only when a manual roll can follow", delegate {
            var ellie = Actor<RulesetCharacterHero>();
            var manager = Lore(new List<GameLocationCharacter> { Character(ellie) }, out var entry, 1);
            Func<bool> prefix = () => (bool)typeof(DeferredRolls).GetMethod("IdentificationPrefix", Any).Invoke(null, new object[] { manager, entry, true });
            Plugin.Settings.Lore = false; Check(prefix() && !DeferredRolls.Holding, "Setting off: native");
            Plugin.Settings.Lore = true; Plugin.Online = true; Check(prefix() && !DeferredRolls.Holding, "Multiplayer: native");
            Plugin.Online = false; RuntimeRolls.AllowManual = false; Check(prefix() && !DeferredRolls.Holding, "No manual roller: native");
            RuntimeRolls.AllowManual = true; Set(ellie, "isDeadOrDyingOrUnconscious", true); Check(prefix() && !DeferredRolls.Holding, "Nobody conscious: native");
            Set(ellie, "isDeadOrDyingOrUnconscious", false);
            bool native = prefix();
            Check(!native && DeferredRolls.Pending == 1, "Otherwise the check waits for the player");
            int ran = 0; RuntimeRolls.Prepare = (o, site, m, a) => { ran++; return null; };
            DeferredRolls.Update();
            Check(ran == 1 && Captured != null && ReferenceEquals(CapturedReceiver, ellie) && !DeferredRolls.Holding, "Planned once, then the original check replays");
            Check(RuntimeRolls.Calls.Contains("label:Monster identification|Unknown creature"), "Identification heading");
            ServiceRepository.RemoveService<IGameLocationCharacterService>();
        });

        // ---- Multiplayer: the front deferred roll runs in its own coroutine --
        Case("Multiplayer deferred rolls run inside their own native coroutine", delegate {
            Plugin.Online = true; RuntimeRolls.ExtendedOnline = () => true;
            int replays = 0, steps = 0;
            RuntimeRolls.Prepare = (owner, site, m, a) => Wait(2, () => steps++);
            Check(Defer("Net.Site", () => Call0(), () => replays++), "Queued online when every peer enables extended rolls");
            var queue = (IList)typeof(DeferredRolls).GetField("queue", Any).GetValue(null);
            DeferredRolls.Update();
            var runner = Field(queue[0], "Runner");
            Check(runner is TA.Coroutine && DeferredRolls.IsRunner(runner) && !DeferredRolls.IsRunner(new TA.Coroutine()) && !DeferredRolls.IsRunner(null), "The front entry owns exactly one runner coroutine");
            Check(steps == 1 && replays == 0 && DeferredRolls.Holding, "The shared prompt advances inside the runner");
            DeferredRolls.Update(); DeferredRolls.Update();
            Check(replays == 1 && RuntimeRolls.Calls.Contains("observe:Net.Site") && RuntimeRolls.Calls.IndexOf("enter:Net.Site") < RuntimeRolls.Calls.IndexOf("exit:Net.Site"), "Replayed once inside the native scope with its void result observed");
            RuntimeRolls.NetworkOwner = RuntimeRolls.LastOwner; RuntimeRolls.NetworkHolding = true; DeferredRolls.Update();
            Check(DeferredRolls.Holding, "The entry waits for the shared completion barrier");
            RuntimeRolls.NetworkHolding = false; RuntimeRolls.NetworkOwner = null; DeferredRolls.Update();
            Check(!DeferredRolls.Holding && replays == 1, "Released after the completion barrier, without a second replay");
            // 0.8.1: manual dice paused after the players disagreed. Nothing
            // new is deferred, and a queued roll not yet shared runs at once
            // with the game's dice; nobody is held and nobody leaves the room.
            MultiplayerRolls.Failed = true;
            Check(!Defer("Net.Paused", () => Call0(), () => replays++) && !DeferredRolls.Holding, "Paused multiplayer dice: the game runs the call itself");
            MultiplayerRolls.Failed = false;
            Check(Defer("Net.Queued", () => Call0(), () => replays++) && DeferredRolls.Holding, "Queued before the pause");
            MultiplayerRolls.Failed = true; DeferredRolls.Update();
            Check(replays == 2 && !DeferredRolls.Holding && RuntimeRolls.Calls.Contains("enter:Net.Queued"), "A queued roll finishes with the game's dice once paused");
            MultiplayerRolls.Failed = false;
            // 0.8.3 lockstep: queued multiplayer rolls keep the game's ticks
            // stopped; a roll reached while another shared step waits for its
            // tick keeps the game's dice (queued, it would wait on that step).
            Check(!DeferredRolls.NetworkedPending, "Nothing queued, nothing holds the ticks");
            RuntimeRolls.NetworkStepOpen = false;
            Check(!Defer("Net.Behind", () => Call0(), () => replays++) && !DeferredRolls.Holding, "No deferral behind another pending shared step");
            RuntimeRolls.NetworkStepOpen = true;
            Check(Defer("Net.Ticks", () => Call0(), () => replays++) && DeferredRolls.NetworkedPending, "A queued multiplayer roll holds the ticks until it finishes");
            for (int frame = 0; frame < 8 && DeferredRolls.Holding; frame++) { Check(DeferredRolls.NetworkedPending, "The ticks stay stopped until the queued roll finishes"); DeferredRolls.Update(); }
            Check(replays == 3 && !DeferredRolls.NetworkedPending, "The finished roll releases the ticks");
            Defer("Net.Left", () => Call0(), () => replays++); DeferredRolls.Update();
            Plugin.Online = false; DeferredRolls.Update();
            Check(replays == 4 && !DeferredRolls.Holding, "Leaving the room finishes a pending shared roll locally with the game's dice");
        });

        // ---- Repeat saving throws ------------------------------------------
        var saveToCancel = typeof(RulesetActor).GetMethod("SaveToCancelCondition", Any);
        Case("Repeat save resolver matches the condition's own saving throw", delegate {
            var service = Make<RulesetImplementationManager>();
            ServiceRepository.AddService<IRulesetImplementationService>(service);
            Databases(true);
            try
            {
                var hero = Actor<RulesetCharacterHero>(); var condition = SavingCondition("Wisdom", 13);
                Hold(hero, condition);
                var call = (DeferredRolls.NativeCall)typeof(DeferredRolls).GetMethod("ResolveRepeatSave", Any).Invoke(null, new object[] { hero, condition });
                Check(call != null && call.Method.Name == "TryRollSavingThrow" && ReferenceEquals(call.Args[0], service) && call.Title == "Repeat saving throw", "Resolver targets the registered save service");
                Captured = null; Call(typeof(RulesetActor), hero, "SaveToCancelCondition", condition);
                Check(Captured != null && ReferenceEquals(CapturedReceiver, service), "Original repeat save captured");
                SameArguments(call.Args.Skip(1).ToArray(), Captured, call.Method, "repeat save");
                Check(ReferenceEquals(Captured[2], hero) && (string)Captured[6] == "Wisdom" && (int)Captured[7] == 13 && Captured[0] == null && (RuleDefinitions.Side)Captured[1] == RuleDefinitions.Side.Enemy, "Target, ability, DC and the missing source");
                Unhold(hero);
                Check(typeof(DeferredRolls).GetMethod("ResolveRepeatSave", Any).Invoke(null, new object[] { hero, condition }) == null, "A condition that has already ended is not prompted");
            }
            finally { ServiceRepository.RemoveService<IRulesetImplementationService>(); Databases(false); }
        });
        Case("Repeat saves wait only at the end of a turn", delegate {
            var service = Make<RulesetImplementationManager>();
            ServiceRepository.AddService<IRulesetImplementationService>(service);
            Databases(true);
            var push = typeof(DeferredRolls).GetMethod("OccurrencePrefix", Any); var pop = typeof(DeferredRolls).GetMethod("OccurrenceFinalizer", Any);
            try
            {
                var hero = Actor<RulesetCharacterHero>(); var condition = SavingCondition("Wisdom", 13); Hold(hero, condition);
                Func<bool> prefix = () => (bool)typeof(DeferredRolls).GetMethod("RepeatSavePrefix", Any).Invoke(null, new object[] { hero, condition });
                Check(prefix() && !DeferredRolls.Holding, "Outside turn processing the save stays native");
                push.Invoke(null, new object[] { RuleDefinitions.TurnOccurenceType.StartOfTurn });
                Check(prefix() && !DeferredRolls.Holding, "Start-of-turn saves stay native");
                pop.Invoke(null, new object[] { null });
                push.Invoke(null, new object[] { RuleDefinitions.TurnOccurenceType.EndOfTurn });
                Plugin.Settings.RepeatSaves = false; Check(prefix() && !DeferredRolls.Holding, "Setting off: native");
                Plugin.Settings.RepeatSaves = true; RuntimeRolls.AllowManual = false; Check(prefix() && !DeferredRolls.Holding, "No manual roller: native");
                RuntimeRolls.AllowManual = true; condition.HasSaveOverride = false; Check(prefix() && !DeferredRolls.Holding, "A condition without a save: native");
                condition.HasSaveOverride = true;
                Check(!prefix() && DeferredRolls.Pending == 1, "End-of-turn save waits for the player");
                Check(!prefix() && DeferredRolls.Pending == 1, "A repeated request for the same pending condition is covered by the first");
                pop.Invoke(null, new object[] { null });
                RuntimeRolls.Prepare = (o, site, m, a) => null; Captured = null; DeferredRolls.Update();
                Check(Captured != null && ReferenceEquals(Captured[2], hero) && !DeferredRolls.Holding, "The original save replays after the answer");
                Check(RuntimeRolls.Calls.Contains("label:Repeat saving throw|Unnamed"), "Repeat save heading names the condition");
                push.Invoke(null, new object[] { RuleDefinitions.TurnOccurenceType.EndOfSourceTurn });
                Check(!prefix() && DeferredRolls.Pending == 1, "End of the source's turn also waits");
                pop.Invoke(null, new object[] { null });
                Unhold(hero); Captured = null; DeferredRolls.Update();
                Check(Captured == null && !DeferredRolls.Holding, "A condition removed meanwhile is not saved against again");
            }
            finally { ServiceRepository.RemoveService<IRulesetImplementationService>(); Databases(false); }
        });

        // ---- Surprise attack ----------------------------------------------
        var surprise = RuntimeRolls.FindOuterSite(OuterSites.SurpriseAttack);
        var trigger = typeof(IGameLocationBattleService).GetMethod("TriggerBattleStart");
        Case("Surprise resolver matches the battle's own Stealth roll", delegate {
            var positioning = DispatchProxy.Create<IGameLocationPositioningService, PositioningService>();
            var battleService = DispatchProxy.Create<IGameLocationBattleService, BattleService>();
            ServiceRepository.AddService<IGameLocationPositioningService>(positioning);
            try
            {
                foreach (bool wall in new[] { false, true })
                {
                    ((PositioningService)(object)positioning).Wall = wall;
                    var hero = Actor<RulesetCharacterHero>(); Set(hero, "conditionsByCategory", new Dictionary<string, List<RulesetCondition>>());
                    var foe = Actor<RulesetCharacterHero>(); Set(foe, "side", RuleDefinitions.Side.Enemy);
                    var attacker = Character(hero); var defender = Character(foe);
                    var inner = surprise.Resolve(Outer(OuterSites.SurpriseAttack, trigger, battleService, attacker, defender, true, true));
                    Check(inner != null && inner.Method == locationCheck && ReferenceEquals(inner.Receiver, attacker), "Resolver plans the attacker's 12-parameter location check");
                    Captured = null; inner.Method.Invoke(inner.Receiver, inner.Args.Skip(1).ToArray());
                    var fromResolver = Captured;
                    var battle = Make<GameLocationBattle>();
                    Set(battle, "surprisedEnemyContenders", new List<GameLocationCharacter>()); Set(battle, "enemyContenders", new List<GameLocationCharacter>());
                    Captured = null; battle.ComputeSurprisedEnemies(false, true, attacker, true);
                    Check(Captured != null && fromResolver != null && ReferenceEquals(CapturedReceiver, hero), "The battle rolls the attacker's Stealth");
                    SameArguments(fromResolver, Captured, rulesetCheck, "surprise " + wall);
                    Check((string)Captured[1] == "Dexterity" && (string)Captured[2] == "Stealth" && (int)Captured[6] == 0 && !(bool)Captured[14] && (bool)Captured[15] && !(bool)Captured[16], "Dexterity (Stealth), no DC, notified without a die display");
                    Check(!NativeProbe.Active && ProbedWallPreparations == (wall ? 1 : 0) && WallPreparations == (wall ? 2 : 0), "Wall preparation runs once under the probe for planning and once natively");
                    WallPreparations = ProbedWallPreparations = 0;
                    if (wall) Check(((List<RuleDefinitions.TrendInfo>)Captured[4]).Count == 1 && ((List<RuleDefinitions.TrendInfo>)fromResolver[4]).Count == 1 && (int)fromResolver[5] == 2, "The wall advantage and modifier reach the planned check");
                }
                var ally = Actor<RulesetCharacterHero>(); var someone = Character(Actor<RulesetCharacterHero>());
                Check(surprise.Resolve(Outer(OuterSites.SurpriseAttack, trigger, battleService, someone, Character(ally), true, true)) == null, "Attacking an ally (the party is surprised) makes no Stealth roll");
                var enemy = Actor<RulesetCharacterHero>(); Set(enemy, "side", RuleDefinitions.Side.Enemy);
                Check(surprise.Resolve(Outer(OuterSites.SurpriseAttack, trigger, battleService, someone, Character(enemy), false, true)) == null, "Without surprise there is no roll");
                Check(surprise.Resolve(Outer(OuterSites.SurpriseAttack, trigger, battleService, someone, Character(enemy), true, false)) == null, "A battle not started by an action surprises without a roll");
                ((BattleService)(object)battleService).InProgress = true;
                Check(surprise.Resolve(Outer(OuterSites.SurpriseAttack, trigger, battleService, someone, Character(enemy), true, true)) == null, "A battle already in progress makes no roll");
            }
            finally { ServiceRepository.RemoveService<IGameLocationPositioningService>(); }
        });

        // ---- Staying hidden at the end of a battle turn -----------------------
        harmony.Patch(typeof(GameLocationCharacter).GetMethod("ComputePassivePerceptionOnTarget"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("FixedPassivePerception", Any)));
        OuterSites.Install(harmony);
        harmony.Patch(typeof(TA.Coroutine).GetMethod("WaitForSeconds"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("NoClockPause", Any)));
        Func<GameLocationCharacter> hidden = () =>
        {
            var hero = Actor<RulesetCharacterHero>(); Set(hero, "conditionsByCategory", new Dictionary<string, List<RulesetCondition>>());
            var foe = Actor<RulesetCharacterHero>(); Set(foe, "side", RuleDefinitions.Side.Enemy);
            var character = Character(hero); var detector = Character(foe);
            Set(character, "stealthy", true); character.CharactersInNoiseRange.Add(detector); detector.LineOfSightRatio[character] = 1f;
            return character;
        };
        Case("Turn-end stealth resolver matches the hidden character's own Stealth roll", delegate {
            var positioning = DispatchProxy.Create<IGameLocationPositioningService, PositioningService>();
            ServiceRepository.AddService<IGameLocationPositioningService>(positioning);
            try
            {
                foreach (bool wall in new[] { false, true })
                {
                    ((PositioningService)(object)positioning).Wall = wall;
                    var character = hidden();
                    var inner = OuterSites.ResolveTurnEndStealth(character);
                    Check(inner != null && inner.Method == locationCheck && ReferenceEquals(inner.Receiver, character), "Resolver plans the hidden character's location check");
                    Captured = null; inner.Method.Invoke(inner.Receiver, inner.Args.Skip(1).ToArray()); var fromResolver = Captured;
                    ForcedOutcome = RuleDefinitions.RollOutcome.Success; Captured = null;
                    try { character.ComputeStealthBreak(true, new ActionModifier(), null); } finally { ForcedOutcome = RuleDefinitions.RollOutcome.Failure; }
                    Check(Captured != null && fromResolver != null && ReferenceEquals(CapturedReceiver, character.RulesetCharacter), "The game rolls the hidden character's Stealth");
                    Check((int)Captured[6] == 13 && (int)fromResolver[6] == 0, "The DC is the detectors' best Perception, known only after their dice");
                    fromResolver[6] = Captured[6];
                    SameArguments(fromResolver, Captured, rulesetCheck, "turn-end stealth " + wall);
                    Check((string)Captured[1] == "Dexterity" && (string)Captured[2] == "Stealth" && (bool)Captured[14] && (bool)Captured[15] && (bool)Captured[16], "Dexterity (Stealth), rolled with its die shown");
                }
                var lone = hidden(); lone.CharactersInNoiseRange.Clear();
                Check(OuterSites.ResolveTurnEndStealth(lone) == null, "Nobody to notice: no roll");
                var visible = hidden(); Set(visible, "stealthy", false);
                Check(OuterSites.ResolveTurnEndStealth(visible) == null, "A character who is not hidden makes no roll");
            }
            finally { ServiceRepository.RemoveService<IGameLocationPositioningService>(); }
        });
        Case("Turn-end stealth waits for the roll inside its own coroutine", delegate {
            var positioning = DispatchProxy.Create<IGameLocationPositioningService, PositioningService>();
            ServiceRepository.AddService<IGameLocationPositioningService>(positioning);
            try
            {
                var character = hidden(); int steps = 0; MethodBase planned = null; object[] plannedArgs = null;
                RuntimeRolls.Prepare = (owner, site, m, a) => { planned = m; plannedArgs = a; return Wait(2, () => steps++); };
                var routine = character.RefreshStealthForBattleTurnEnd();
                Check(routine.GetType().DeclaringType == typeof(OuterSites), "The native turn-end routine is replaced");
                var coroutine = new TA.Coroutine(); coroutine.Start(routine); Captured = null; Pauses = 0;
                ForcedOutcome = RuleDefinitions.RollOutcome.Success;
                try
                {
                    coroutine.Run(); Check(steps == 1 && Captured == null, "The stealth check waits for the player");
                    coroutine.Run(); coroutine.Run();
                }
                finally { ForcedOutcome = RuleDefinitions.RollOutcome.Failure; }
                Check(Captured != null && (string)Captured[2] == "Stealth", "Then the original stealth break runs");
                var parts = plannedArgs == null ? null : plannedArgs[0] as DeferredRolls.RollPart[];
                Check(planned == DeferredRolls.CompositeMethod && parts != null && parts.Length == 1 && parts[0].Title == "Staying hidden", "One composite part: the hidden character's stealth check (not in battle: no detector rolls)");
                Check(RuntimeRolls.Calls.Contains("observe:" + OuterSites.TurnEndStealth + ":True")
                    && RuntimeRolls.Calls.IndexOf("enter:" + OuterSites.TurnEndStealth) < RuntimeRolls.Calls.IndexOf("exit:" + OuterSites.TurnEndStealth), "Inside the native scope, with the attempted-check result observed");
                coroutine.Run();
                Check(Pauses == 1 && coroutine.IsFinished && character.Stealthy, "An attempted check keeps the original two-second display pause, then ends");
                var unseen = hidden(); unseen.CharactersInNoiseRange.Clear(); RuntimeRolls.Calls.Clear();
                var quiet = new TA.Coroutine(); quiet.Start(unseen.RefreshStealthForBattleTurnEnd()); quiet.Run();
                Check(quiet.IsFinished && !RuntimeRolls.Calls.Any(c => c.StartsWith("prepare:")), "Without detectors nothing is prepared and the routine ends");
                Plugin.Settings.Ability = false;
                Check(hidden().RefreshStealthForBattleTurnEnd().GetType().DeclaringType != typeof(OuterSites), "Ability checks off: the original routine runs");
            }
            finally { ServiceRepository.RemoveService<IGameLocationPositioningService>(); }
        });

        // ---- Falling damage and travel ambush planners ----------------------
        var fall = typeof(RulesetCharacter).GetMethod("SustainFallDamage");
        Case("Falling damage asks for the game's own number of d6", delegate {
            var hero = FeaturedHero();
            Func<int, int, NativeRollPlan> plan = (cells, reduction) => { var result = new NativeRollPlan(); Check(FallDamage.TryPrepare(new NativeInvocation(new object(), "fall", fall, new object[] { hero, cells, reduction }, 1), result), "Falling damage is this planner's call"); return result; };
            Check(plan(2, 0).Groups.Count == 0, "A short fall rolls nothing");
            var groups = plan(7, 0).Groups;
            Check(groups.Count == 1 && groups[0].Static && groups[0].Die == RuleDefinitions.DieType.D6 && groups[0].Count == 3 && groups[0].Title == "Falling damage" && !groups[0].HasBonus, "Seven cells: 3d6");
            groups = plan(10, 4).Groups;
            Check(groups.Count == 1 && groups[0].Count == 5 && groups[0].HasBonus && groups[0].Bonus == -4, "Reduction is shown as a negative bonus");
            RuntimeRolls.AllowManual = false; Check(plan(10, 0).Groups.Count == 0, "No manual roller: native");
            Check(!FallDamage.TryPrepare(new NativeInvocation(new object(), "x", typeof(RulesetActor).GetMethod("RollDie"), new object[10], 1), new NativeRollPlan()), "Other calls are not this planner's");
        });
        Case("Travel ambush planner and raw-die hook", delegate {
            var hero = Actor<RulesetCharacterHero>();
            var ambush = typeof(DeferredRolls).GetMethod("TravelAmbushCheck", Any);
            var plan = new NativeRollPlan();
            Check(DeferredRolls.TryPlanAmbush(new NativeInvocation(new object(), "Travel.Encounter", ambush, new object[] { hero, 6 }, 1), plan), "The ambush identity is planned");
            Check(plan.Groups.Count == 1 && plan.Groups[0].Static && plan.Groups[0].Die == RuleDefinitions.DieType.D20 && plan.Groups[0].Count == 1 && plan.Groups[0].Context == RuleDefinitions.RollContext.AbilityCheck
                && plan.Groups[0].Bonus == 6 && plan.Groups[0].Title == "Ambush check", "One d20 for the lookout with the Perception bonus");
            Check(!DeferredRolls.TryPlanAmbush(new NativeInvocation(new object(), "x", fall, new object[] { hero, 1, 0 }, 1), new NativeRollPlan()), "Other calls are not the ambush planner's");
            var surpriseRoll = typeof(GameTravelScheduler).GetMethod("RollSurprise", Any);
            var patcher = new Harmony("sync.sites.surprise.transpiler");
            try
            {
                patcher.Patch(surpriseRoll, transpiler: new HarmonyMethod(typeof(DeferredRolls).GetMethod("SurpriseTranspiler", Any)));
                var calls = PatchProcessor.GetCurrentInstructions(surpriseRoll).Where(i => i.operand is MethodInfo).Select(i => ((MethodInfo)i.operand).Name).ToList();
                Check(calls.Count(n => n == "SurpriseFace") == 2 && !calls.Contains("RollDie"), "Both raw d20 draws of the pristine RollSurprise go through the hook");
            }
            finally { patcher.UnpatchAll(patcher.Id); }
            int first, second; RuntimeRolls.Calls.Clear();
            int face = DeferredRolls.SurpriseFace(RuleDefinitions.DieType.D20, RuleDefinitions.AdvantageType.None, out first, out second);
            Check(face == 11 && RuntimeRolls.Calls.SequenceEqual(new[] { "raw:ExecuteEncounter" }), "The hook defers to the replayed encounter's static dice");
        });

        Case("Synchronous pumps are detected", delegate {
            int depth = -1;
            IEnumerator Inner() { depth = RuntimeRolls.SyncPumpDepth; yield break; }
            var extensions = typeof(GameLocationCharacter).Assembly.GetType("IEnumeratorExtensions");
            extensions.GetMethod("ExecuteUntilDone").Invoke(null, new object[] { Inner() });
            Check(depth > 0 && RuntimeRolls.SyncPumpDepth == 0, "ExecuteUntilDone is counted and released");
            var coroutine = TA.Coroutine.StartCoroutine(Inner()); depth = -1; coroutine.RunUntilIsFinished();
            Check(depth > 0 && RuntimeRolls.SyncPumpDepth == 0, "RunUntilIsFinished is counted and released");
            depth = -1; TA.Coroutine.StartCoroutine(Inner()).Run(); Check(depth == 0, "Ordinary scheduling is not a synchronous pump");
        });
        Case("Prompt text helpers", delegate {
            Check(RollText.Check("Wisdom", "Survival") == "Wisdom (Survival)" && RollText.Save("Constitution") == "Constitution save", "Check and save text");
            Check(RollText.Words("SleightOfHand") == "Sleight Of Hand" && RollText.Check("Dexterity", "SleightOfHand") == "Dexterity (Sleight Of Hand)", "Unlocalized names become words");
            Check(RollText.Damage("DamageFire") == "Fire damage" && RollText.Damage(null) == "", "Damage text");
            Check(RollText.RerollHint(0, 1, 1, "DieRollModifierHalfingLucky").Contains("Lucky") && RollText.RerollHint(1, 2, 1, "X").StartsWith("Die 2 rolled 1"), "Reroll hints");
            RollText.Resolver = (kind, name) => kind == "ability" ? "Sagesse" : null; RollText.Reset();
            Check(RollText.Check("Wisdom", "Survival") == "Sagesse (Survival)", "Localized names are used when available");
            RollText.Resolver = (kind, name) => { throw new InvalidOperationException(); }; RollText.Reset();
            Check(RollText.Ability("Wisdom") == "Wisdom", "A failing localizer falls back");
            RollText.Resolver = null; RollText.Reset();
        });

        RunExtra(harmony, locationCheck, rulesetCheck);

        Console.WriteLine((failures == 0 ? "PASS " : "FAIL ") + cases + " single-player site cases; " + assertions + " assertions; " + failures + " failures.");
        Console.WriteLine("Uses the pristine game assembly under .NET 8; Unity rendering, the roll broker/window and actual Windows Mono are not exercised.");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
    static object Field(object target, string name)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        { var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); if (field != null) return field.GetValue(target); }
        throw new MissingFieldException(target.GetType().Name, name);
    }
    static RulesetCondition SavingCondition(string ability, int dc)
    {
        var condition = Make<RulesetCondition>();
        Set(condition, "conditionDefinition", Make<ConditionDefinition>());
        condition.HasSaveOverride = true; Set(condition, "saveOverrideAbilityScoreName", ability); Set(condition, "saveOverrideDC", dc);
        Set(condition, "saveOverrideSourceName", "Fixture save source");
        return condition;
    }
    static void Hold(RulesetActor actor, RulesetCondition condition)
    {
        Set(actor, "conditionsByCategory", new Dictionary<string, List<RulesetCondition>> { { "10Combat", new List<RulesetCondition> { condition } } });
        Set(actor, "actionModifier", new ActionModifier()); Set(actor, "dummyEffectForms", new List<EffectForm>());
    }
    static void Unhold(RulesetActor actor) { Set(actor, "conditionsByCategory", new Dictionary<string, List<RulesetCondition>>()); }
    static void Databases(bool add)
    {
        if (add) { DatabaseRepository.AddDatabase(new Database<FeatureDefinition>()); DatabaseRepository.AddDatabase(new Database<MonsterAttackDefinition>()); DatabaseRepository.AddDatabase(new Database<ItemDefinition>()); }
        else { DatabaseRepository.RemoveDatabase<FeatureDefinition>(); DatabaseRepository.RemoveDatabase<MonsterAttackDefinition>(); DatabaseRepository.RemoveDatabase<ItemDefinition>(); }
    }
    static GameLoreManager Lore(List<GameLocationCharacter> party, out GameBestiaryEntry entry, float challenge)
    {
        var characters = DispatchProxy.Create<IGameLocationCharacterService, PartyService>();
        ((PartyService)(object)characters).Party = party;
        ServiceRepository.RemoveService<IGameLocationCharacterService>();
        ServiceRepository.AddService<IGameLocationCharacterService>(characters);
        var manager = Make<GameLoreManager>();
        Set(manager, "characterService", characters); Set(manager, "sortedCharacters", new List<GameLocationCharacter>());
        GameLoreManager.GameLocationCharacterSorter = Make<GameLocationCharacterAbilityCheckSorter>();
        var monster = Make<MonsterDefinition>(); Set(monster, "challengeRating", challenge);
        entry = Make<GameBestiaryEntry>(); Set(entry, "monsterDefinition", monster);
        return manager;
    }
    static DeferredRolls.NativeCall Call0() { return new DeferredRolls.NativeCall(typeof(object).GetMethod("ToString"), new object[] { new object() }); }
    static readonly MethodBase OuterMethod = typeof(object).GetMethod("GetHashCode");
    static bool Defer(string site, Func<DeferredRolls.NativeCall> resolve, Action replay, object key = null)
    { return (bool)typeof(DeferredRolls).GetMethod("TryDefer", Any).Invoke(null, new object[] { site, OuterMethod, new object[] { new object() }, resolve, replay, key }); }
    static void Drop() { try { DeferredRolls.Drop(); } catch (Exception) { } }
    static IEnumerator Wait(int frames, Action step) { for (int i = 0; i < frames; i++) { if (step != null) step(); yield return null; } }
    static IEnumerator Throwing() { yield return null; throw new InvalidOperationException("injected wait"); }
}

// Supplies the native preparation a concentration probe would perform: a
// Bless-style bonus die, then War Caster advantage on the d20.
static class ProbeBoundary
{
    static Harmony harmony;
    internal static void Install()
    {
        harmony = new Harmony("sync.sites.probe.boundary");
        harmony.Patch(typeof(RulesetCharacter).GetMethod("RollConcentrationCheck"), prefix: new HarmonyMethod(typeof(ProbeBoundary).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)));
        harmony.Patch(typeof(RulesetCharacterHero).GetMethod("ComputeBaseSavingThrowBonus"), prefix: new HarmonyMethod(typeof(ProbeBoundary).GetMethod("Bonus", BindingFlags.Static | BindingFlags.NonPublic)));
    }
    internal static void Remove() { harmony.UnpatchAll(harmony.Id); }
    static bool Prefix(RulesetCharacter __instance)
    {
        RuleDefinitions.RollStaticDiceAndSum(1, RuleDefinitions.DieType.D4, null);
        int first, second;
        __instance.RollDie(RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.ConcentrationCheck, false, RuleDefinitions.AdvantageType.Advantage, out first, out second);
        return false;
    }
    static bool Bonus(ref int __result) { __result = 3; return false; }
}

// Test-only character service: only the party list is consulted.
public class PartyService : DispatchProxy
{
    internal List<GameLocationCharacter> Party = new List<GameLocationCharacter>();
    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == "get_PartyCharacters") return Party;
        throw new NotSupportedException("Unexpected character service call: " + targetMethod.Name);
    }
}

// Test-only dice settings: the saving-throw die is never displayed.
public class DiceSettings : DispatchProxy
{
    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == "get_RollSavingThrowDie") return "Never";
        throw new NotSupportedException("Unexpected dice settings call: " + targetMethod.Name);
    }
}
// Test-only positioning service: only the wall query is consulted.
public class PositioningService : DispatchProxy
{
    internal bool Wall;
    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == "IsNextToWall") return Wall;
        throw new NotSupportedException("Unexpected positioning call: " + targetMethod.Name);
    }
}
// Test-only battle service: only the in-progress flag is consulted by the resolver.
public class BattleService : DispatchProxy
{
    internal bool InProgress; internal GameLocationBattle Current; internal List<GameLocationCharacter> Targets = new List<GameLocationCharacter>();
    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == "get_IsBattleInProgress") return InProgress;
        if (targetMethod.Name == "get_Battle") return Current;
        if (targetMethod.Name == "GatherRelevantOffensiveActionTargets") { var list = (List<GameLocationCharacter>)args[1]; list.Clear(); list.AddRange(Targets); return null; }
        throw new NotSupportedException("Unexpected battle service call: " + targetMethod.Name);
    }
}
