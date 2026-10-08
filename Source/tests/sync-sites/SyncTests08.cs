// 0.8 additions: monster recharge and random behaviour at the start of a
// turn, the creatures' ambush die, food found after a search, traps and
// hazards, the stealth breaker functor and a charged item's last charge.
// Resolvers are compared with the arguments the original game methods pass
// themselves wherever the original can run in this host.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ManualDiceRoller;

static partial class SyncTests
{
    internal static readonly List<object[]> EffectSaves = new List<object[]>();
    internal static readonly List<object[]> FormsCalls = new List<object[]>();
    static bool CaptureEffectSave(object[] __args, ref bool __result, ref RuleDefinitions.RollOutcome saveOutcome, ref int saveOutcomeDelta)
    {
        EffectSaves.Add((object[])__args.Clone());
        // Harmony writes __args back to by-reference parameters after the prefix.
        __args[6] = RuleDefinitions.RollOutcome.Failure; __args[7] = -2;
        saveOutcome = RuleDefinitions.RollOutcome.Failure; saveOutcomeDelta = -2; __result = true; Events.Add("effect-save"); return false;
    }
    static bool SkipTerminateEffect() { Events.Add("terminate"); return false; }
    static void CollectForms(object[] __args) { FormsCalls.Add((object[])__args.Clone()); }
    static bool SkipTerminate() { Events.Add("terminate"); return false; }
    static bool RecordStealthBreak(GameLocationCharacter __instance, bool roll, ref bool __result) { Events.Add("stealth-break:" + roll); __result = false; return false; }

    static void RunExtra08(Harmony harmony)
    {
        string error;
        Check(TrapRolls.Preflight(out error), "Trap roll preflight: " + error);
        TrapRolls.Install(harmony);
        harmony.Patch(typeof(RulesetEffect).GetMethod("TryRollSavingThrow"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("CaptureEffectSave", Any)) { priority = Priority.First });
        harmony.Patch(typeof(RulesetImplementationManager).GetMethod("ApplyEffectForms"), postfix: new HarmonyMethod(typeof(SyncTests).GetMethod("CollectForms", Any)));
        harmony.Patch(typeof(RulesetEffect).GetMethod("Terminate"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("SkipTerminate", Any)));
        // Terminate may be inlined into the trigger; its engine call is the boundary.
        harmony.Patch(typeof(RulesetImplementationManager).GetMethod("TerminateEffect"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("SkipTerminateEffect", Any)));
        harmony.Patch(typeof(GameLocationCharacter).GetMethod("ComputeStealthBreak"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("RecordStealthBreak", Any)) { priority = Priority.First });
        Console.WriteLine("PASS 0.8 trap, recharge and turn roll preflight and patch installation against the pristine game assembly");

        // ---- the start of a turn --------------------------------------------
        Case("A used power that recharges on a d6 joins the turn start, bound to its character", delegate {
            var hero = SavingHero(); var power = Power(RuleDefinitions.RechargeRate.D6_56, 0, 1);
            Set(hero, "usablePowers", new List<RulesetUsablePower> { power, Power(RuleDefinitions.RechargeRate.D6_6, 1, 1), Power(RuleDefinitions.RechargeRate.TurnStart, 0, 1) });
            var character = Character(hero); var battle = Battle(character);
            var parts = TurnRolls.TurnStartParts(character, battle).Where(part => part.Method.Name == "RechargeRoll").ToList();
            Check(parts.Count == 1 && ReferenceEquals(parts[0].Args[1], power) && (int)parts[0].Args[2] == 5 && ReferenceEquals(parts[0].Anchor, TurnRolls.RechargeKey(hero)),
                "Only the used 5-6 power is rolled, bound to its character");
            var plan = new NativeRollPlan();
            Check(TurnRolls.TryPrepare(parts[0].ToInvocation("x"), plan) && plan.Groups.Count == 1 && plan.Groups[0].Static && plan.Groups[0].Die == RuleDefinitions.DieType.D6 &&
                plan.Groups[0].DC == 5 && plan.Groups[0].Context == Plugin.FeatureDice, "A d6 that recharges on 5 or more");
            RuntimeRolls.Calls.Clear();
            Set(hero, "usablePowers", new List<RulesetUsablePower> { power });
            hero.RechargePowersForTurnStart();
            Check(RuntimeRolls.Calls.Contains("raw:StartBattleTurn") && RuntimeRolls.Calls.Contains("anchor:Object") && power.RemainingUses == 0,
                "The original recharge rolls through the hook inside its anchor (11 does not recharge): " + string.Join(",", RuntimeRolls.Calls));
            Plugin.Settings.Features = false;
            Check(!TurnRolls.TurnStartParts(character, battle).Any(part => part.Method.Name == "RechargeRoll"), "Setting off: the game rolls");
        });
        Case("A random behaviour feature joins the turn start with its weighted table", delegate {
            var hero = SavingHero();
            var affinity = Make<FeatureDefinitionActionAffinity>(); Set(affinity, "cachedName", "FixtureConfusion"); Set(affinity, "guid", "fixture-confusion");
            Set(affinity, "specialBehaviour", RuleDefinitions.SpecialBehaviour.RandomBehaviour); Set(affinity, "randomBehaviorDie", RuleDefinitions.DieType.D10);
            Set(affinity, "randomBehaviourOptions", new List<BehaviorModeDescription> { Behaviour(1, RuleDefinitions.RandomBehaviour.None), Behaviour(6, RuleDefinitions.RandomBehaviour.ConditionDuringTurn), Behaviour(3, RuleDefinitions.RandomBehaviour.None) });
            ((Dictionary<string, List<FeatureDefinition>>)Field(hero, "activeFeatures"))["Fixture"] = new List<FeatureDefinition> { affinity };
            var definition = Make<ConditionDefinition>(); Set(definition, "cachedName", "FixtureConfused"); Set(definition, "features", new List<FeatureDefinition> { affinity });
            var condition = Make<RulesetCondition>(); Set(condition, "conditionDefinition", definition);
            ((Dictionary<string, List<RulesetCondition>>)Field(hero, "conditionsByCategory"))["11Effect"] = new List<RulesetCondition> { condition };
            var character = Character(hero); var battle = Battle(character);
            var state = battle.FiniteStateMachine.FiniteStates[typeof(BattleState_TurnInitialize)];
            var parts = TurnRolls.TurnStartParts(character, battle).Where(part => part.Method.Name == "BehaviorRoll").ToList();
            Check(parts.Count == 1 && (RuleDefinitions.DieType)parts[0].Args[1] == RuleDefinitions.DieType.D10 && ReferenceEquals(parts[0].Anchor, state),
                "One d10 bound to the turn initialisation state");
            string ranges = (string)parts[0].Args[2];
            Check(ranges.StartsWith("1: act normally") && ranges.Contains("2–7: ") && ranges.Contains("8–10: act normally"), "The weighted table: " + ranges);
            var plan = new NativeRollPlan();
            Check(TurnRolls.TryPrepare(parts[0].ToInvocation("x"), plan) && plan.Groups.Count == 1 && plan.Groups[0].Static && plan.Groups[0].Die == RuleDefinitions.DieType.D10 && plan.Groups[0].Note == ranges,
                "A static d10 whose note lists the table");
            var begin = typeof(BattleState_TurnInitialize).GetMethod("Begin");
            var patched = PatchProcessor.GetCurrentInstructions(begin);
            Check(patched.Count(code => Equals(code.operand, typeof(TurnRolls).GetMethod("BehaviorFace"))) == 1 &&
                !patched.Any(code => Equals(code.operand, typeof(RuleDefinitions).GetMethod("RollDie"))), "The turn initialisation's raw die goes through the hook");
            Plugin.Settings.RepeatSaves = false;
            Check(!TurnRolls.TurnStartParts(character, battle).Any(part => part.Method.Name == "BehaviorRoll"), "Setting off: the game rolls");
        });

        // ---- travel ---------------------------------------------------------
        Case("The creatures' ambush die is a game master die after the party's", delegate {
            var hero = SavingHero(); var ambush = typeof(DeferredRolls).GetMethod("TravelAmbushCheck", Any);
            var plan = new NativeRollPlan();
            DeferredRolls.TryPlanAmbush(new NativeInvocation(new object(), "Travel.Encounter", ambush, new object[] { hero, 6, 4 }, 1), plan);
            Check(plan.Groups.Count == 1, "Enemy dice off: only the party's lookout");
            Plugin.Settings.EnemyRolls = true; plan = new NativeRollPlan();
            DeferredRolls.TryPlanAmbush(new NativeInvocation(new object(), "Travel.Encounter", ambush, new object[] { hero, 6, 4 }, 1), plan);
            Check(plan.Groups.Count == 2 && plan.Leaves[1].MasterOwned && plan.Leaves[1].ActorLabel == "The creatures" && plan.Groups[1].Die == RuleDefinitions.DieType.D20 &&
                plan.Groups[1].HasBonus && plan.Groups[1].Bonus == 4, "Enemy dice on: the creatures' d20 with their best Perception");
            RuntimeRolls.AllowManual = false; plan = new NativeRollPlan();
            DeferredRolls.TryPlanAmbush(new NativeInvocation(new object(), "Travel.Encounter", ambush, new object[] { hero, 6, 4 }, 1), plan);
            Check(plan.Groups.Count == 0, "Without the party's die the creatures' die stays with the game (the raw draws are consumed in order)");
        });
        Case("Food found is asked only after a successful search", delegate {
            var hero = SavingHero(); var food = typeof(DeferredRolls).GetMethod("FoodYield", Any);
            var plan = new NativeRollPlan();
            plan.AddActorDice(hero, RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AbilityCheck, 1);
            Check(DeferredRolls.TryPlanFoodYield(new NativeInvocation(new object(), "Travel.FoodSearch", food, new object[] { hero, 2, RuleDefinitions.DieType.D6, 1 }, 1), plan),
                "The food identity is planned");
            Check(plan.Groups.Count == 2 && plan.Groups[1].Static && plan.Groups[1].Die == RuleDefinitions.DieType.D6 && plan.Groups[1].Count == 2 && plan.Leaves[1].When != null,
                "Two d6 asked conditionally");
            plan.Succeeds = index => index == 0;
            Check(plan.Leaves[1].When(), "Asked when the search's d20 succeeds");
            plan.Succeeds = null;
            Check(!plan.Leaves[1].When(), "Not asked after a failed search");
            Plugin.Settings.Downtime = false; plan = new NativeRollPlan();
            plan.AddActorDice(hero, RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AbilityCheck, 1);
            DeferredRolls.TryPlanFoodYield(new NativeInvocation(new object(), "Travel.FoodSearch", food, new object[] { hero, 2, RuleDefinitions.DieType.D6, 1 }, 1), plan);
            Check(plan.Groups.Count == 1, "Setting off: the game rolls the food");
        });

        // ---- traps and hazards ----------------------------------------------
        Case("A trap's saves and damage are planned per affected character like the original trigger", delegate {
            var service = Make<RulesetImplementationManager>();
            ServiceRepository.AddService<IRulesetImplementationService>(service);
            try
            {
                var first = SavingHero(); var second = SavingHero();
                var targets = new List<GameLocationCharacter> { Character(first), Character(second) };
                var effect = EnvironmentEffect(true, 2, DamageEffectForm(RuleDefinitions.DieType.D10, 2, RuleDefinitions.EffectSavingThrowType.HalfDamage));
                var manager = Make<GameLocationEnvironmentManager>(); Set(manager, "m_CachedPtr", new IntPtr(1)); Set(manager, "affectedCharacters", new List<GameLocationCharacter>(targets));
                var trigger = typeof(GameLocationEnvironmentManager).GetMethod("TriggerEnvironmentEffect", Any, null, new[] { typeof(RulesetEffectEnvironment) }, null);
                EffectSaves.Clear(); FormsCalls.Clear();
                trigger.Invoke(manager, new object[] { effect });
                Check(EffectSaves.Count == 2 && FormsCalls.Count == 2 && Events.Contains("terminate"), "The original rolls a save and applies the forms for each character");
                var parts = TrapRolls.Parts(effect, targets);
                Check(parts.Count == 4, "Per character: the save, then the forms");
                for (int index = 0; index < 2; index++)
                {
                    var save = parts[index * 2]; var forms = parts[index * 2 + 1];
                    var expected = EffectSaves[index];
                    Check(ReferenceEquals(save.Args[0], effect) && save.Args[1] == null && Equals(save.Args[2], expected[1]) && ReferenceEquals(save.Args[3], expected[2]) &&
                        ReferenceEquals(save.Args[5], expected[4]) && Equals(save.Args[6], expected[5]) && ReferenceEquals(save.Anchor, targets[index].RulesetActor),
                        "Save " + index + " matches the original's arguments and is bound to its character");
                    var planned = (RulesetImplementationDefinitions.ApplyFormsParams)forms.Args[2];
                    var actual = (RulesetImplementationDefinitions.ApplyFormsParams)FormsCalls[index][1];
                    foreach (var field in typeof(RulesetImplementationDefinitions.ApplyFormsParams).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (field.Name == "actionModifier" || field.Name == "saveOutcomeDelta") continue;
                        object a = field.GetValue(actual), b = field.GetValue(planned);
                        if (a == null || b == null || a.GetType().IsValueType || a is string) Check(Equals(a, b), "Forms " + index + ": " + field.Name + " expected " + a + " planned " + b);
                        else Check(ReferenceEquals(a, b), "Forms " + index + ": reference " + field.Name);
                    }
                    Check(ReferenceEquals(forms.Args[1], FormsCalls[index][0]) && ReferenceEquals(forms.Anchor, targets[index].RulesetActor), "Forms " + index + " are the effect's own and bound to the character");
                }
                Check(TrapRolls.AnyManual(parts), "A party member's save is the player's");
                Plugin.Settings.Traps = false;
                Check(TrapRolls.Parts(effect, targets).Count == 0, "Setting off: the trap stays the game's");
            }
            finally { ServiceRepository.RemoveService<IRulesetImplementationService>(); }
        });
        Case("A trap's private trigger is captured, waits for the dice and then runs once in its own step", delegate {
            var service = Make<RulesetImplementationManager>();
            ServiceRepository.AddService<IRulesetImplementationService>(service);
            try
            {
                var hero = SavingHero(); var targets = new List<GameLocationCharacter> { Character(hero) };
                var effect = EnvironmentEffect(true, 0, DamageEffectForm(RuleDefinitions.DieType.D6, 3, RuleDefinitions.EffectSavingThrowType.HalfDamage));
                var manager = Make<GameLocationEnvironmentManager>(); Set(manager, "m_CachedPtr", new IntPtr(1)); Set(manager, "affectedCharacters", new List<GameLocationCharacter>(targets));
                var trigger = typeof(GameLocationEnvironmentManager).GetMethod("TriggerEnvironmentEffect", Any, null, new[] { typeof(RulesetEffectEnvironment) }, null);
                var capture = typeof(TrapRolls).GetMethod("Trigger", Any).Invoke(null, new object[] { (Action)delegate { trigger.Invoke(manager, new object[] { effect }); } });
                EffectSaves.Clear(); FormsCalls.Clear();
                Check(capture != null && EffectSaves.Count == 0 && FormsCalls.Count == 0, "The private trigger is captured instead of run");
                ((List<GameLocationCharacter>)Field(manager, "affectedCharacters")).Clear();
                int steps = 0; MethodBase method = null; object[] args = null;
                RuntimeRolls.Prepare = (o, site, m, a) => { method = m; args = a; return Wait(2, () => steps++); };
                var routine = (IEnumerator)typeof(TrapRolls).GetMethod("Resolve", Any).Invoke(null, new object[] { manager, capture });
                var coroutine = new TA.Coroutine(); coroutine.Start(routine);
                coroutine.Run();
                Check(steps == 1 && EffectSaves.Count == 0 && RuntimeRolls.Calls.Contains("prepare:" + TrapRolls.TrapSite) && RuntimeRolls.Calls.Contains("label:Trap|FixtureTrap"),
                    "It waits before the trap's dice: " + string.Join(",", RuntimeRolls.Calls));
                Check(method == DeferredRolls.CompositeMethod && ((DeferredRolls.RollPart[])args[0]).Length == 2, "One composite plan: the save and the damage");
                for (int i = 0; i < 4 && !coroutine.IsFinished; i++) coroutine.Run();
                Check(coroutine.IsFinished && EffectSaves.Count == 1 && FormsCalls.Count == 1 && ((List<GameLocationCharacter>)Field(manager, "affectedCharacters")).SequenceEqual(targets),
                    "The original trigger then runs once with its own affected characters");
                Check(RuntimeRolls.Calls.IndexOf("enter:" + TrapRolls.TrapSite) < RuntimeRolls.Calls.IndexOf("observe:" + TrapRolls.TrapSite) &&
                    RuntimeRolls.Calls.Contains("exit:" + TrapRolls.TrapSite) && RuntimeRolls.Calls.Count(c => c == "anchor:RulesetCharacterHero") == 1,
                    "Inside its own native step, with the forms bound to the character (the effect's save is a recorded boundary here): " + string.Join(",", RuntimeRolls.Calls));
                // The save's anchor: RulesetImplementationManager.TryRollSavingThrow, during the replay only.
                var saveAnchor = typeof(TrapRolls).GetMethod("SaveAnchor", Any); var finalizer = typeof(TrapRolls).GetMethod("AnchorFinalizer", Any);
                RuntimeRolls.Calls.Clear();
                var state = new object[] { null, hero, null };
                saveAnchor.Invoke(null, state);
                Check(!RuntimeRolls.Calls.Any(c => c.StartsWith("anchor:")), "Outside a trap replay no anchor is entered");
                // Without a die to enter the trigger runs at once, where the game ran it.
                RuntimeRolls.AllowManual = false; EffectSaves.Clear(); FormsCalls.Clear(); RuntimeRolls.Calls.Clear();
                capture = typeof(TrapRolls).GetMethod("Trigger", Any).Invoke(null, new object[] { (Action)delegate { trigger.Invoke(manager, new object[] { effect }); } });
                Check(typeof(TrapRolls).GetMethod("Resolve", Any).Invoke(null, new object[] { manager, capture }) == null && EffectSaves.Count == 1 && FormsCalls.Count == 1 &&
                    !RuntimeRolls.Calls.Any(c => c.StartsWith("prepare:")), "No manual die: the trigger runs immediately without a wait");
                RuntimeRolls.AllowManual = true;
                var functor = Make<FunctorEnvironmentEffect>();
                var parameters = new FunctorParametersDescription(); parameters.ActingCharacters = new List<GameLocationCharacter>();
                var execute = typeof(FunctorEnvironmentEffect).GetMethod("Execute");
                Check(((IEnumerator)execute.Invoke(functor, new object[] { parameters, Context(true) })).GetType().DeclaringType == typeof(TrapRolls), "The functor's coroutine is replaced");
                Plugin.Settings.Traps = false;
                Check(((IEnumerator)execute.Invoke(functor, new object[] { parameters, Context(true) })).GetType().DeclaringType == typeof(FunctorEnvironmentEffect), "Setting off: the original functor");
            }
            finally { ServiceRepository.RemoveService<IRulesetImplementationService>(); }
        });
        Case("A hazard's lingering parts match RulesetEffectEnvironment.ApplyEffectOnCharacter", delegate {
            var service = Make<RulesetImplementationManager>();
            ServiceRepository.AddService<IRulesetImplementationService>(service);
            try
            {
                var hero = SavingHero();
                var effect = EnvironmentEffect(true, 1, DamageEffectForm(RuleDefinitions.DieType.D4, 2, RuleDefinitions.EffectSavingThrowType.Negates));
                Plugin.Settings.Traps = false; EffectSaves.Clear(); FormsCalls.Clear();
                effect.ApplyEffectOnCharacter(hero, false, new TA.int3());
                Check(EffectSaves.Count == 1 && FormsCalls.Count == 1, "The original applies a save and the forms (setting off: native)");
                Plugin.Settings.Traps = true;
                var parts = new List<DeferredRolls.RollPart>();
                TurnRolls.AddEffectParts(parts, effect, hero, false, new TA.int3());
                Check(parts.Count == 2 && ReferenceEquals(parts[0].Anchor, effect) && ReferenceEquals(parts[1].Anchor, effect), "A save and the forms, bound to the hazard");
                var save = parts[0].Args; var expected = EffectSaves[0];
                Check(ReferenceEquals(save[0], effect) && save[1] == null && Equals(save[2], expected[1]) && ReferenceEquals(save[3], hero) && ReferenceEquals(save[5], expected[4]) && Equals(save[6], expected[5]),
                    "The save's arguments match");
                var planned = (RulesetImplementationDefinitions.ApplyFormsParams)parts[1].Args[2];
                var actual = (RulesetImplementationDefinitions.ApplyFormsParams)FormsCalls[0][1];
                foreach (var field in typeof(RulesetImplementationDefinitions.ApplyFormsParams).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.Name == "actionModifier" || field.Name == "saveOutcomeDelta") continue;
                    object a = field.GetValue(actual), b = field.GetValue(planned);
                    if (a == null || b == null || a.GetType().IsValueType || a is string) Check(Equals(a, b), "Hazard forms: " + field.Name + " expected " + a + " planned " + b);
                    else Check(ReferenceEquals(a, b), "Hazard forms: reference " + field.Name);
                }
            }
            finally { ServiceRepository.RemoveService<IRulesetImplementationService>(); }
        });

        // ---- stealth and items ----------------------------------------------
        Case("The stealth breaker functor waits for each acting character's check", delegate {
            var functor = Make<FunctorStealthBreaker>();
            var parameters = new FunctorParametersDescription(); Set(parameters, "boolParameter", true);
            var hidden = Hidden(); parameters.ActingCharacters = new List<GameLocationCharacter> { hidden };
            var execute = typeof(FunctorStealthBreaker).GetMethod("Execute");
            int steps = 0;
            RuntimeRolls.Prepare = (o, site, m, a) => Wait(2, () => steps++);
            var routine = (IEnumerator)execute.Invoke(functor, new object[] { parameters, Context(true) });
            Check(routine.GetType().DeclaringType == typeof(OuterSites), "The functor's coroutine is replaced");
            var coroutine = new TA.Coroutine(); coroutine.Start(routine);
            coroutine.Run();
            Check(steps == 1 && !Events.Contains("stealth-break:True"), "It waits before the stealth check");
            for (int i = 0; i < 4 && !coroutine.IsFinished; i++) coroutine.Run();
            Check(coroutine.IsFinished && Events.Count(e => e == "stealth-break:True") == 1 && RuntimeRolls.Calls.Contains("observe:" + OuterSites.GadgetStealth + ":False"),
                "Then the original check runs once in its own step: " + string.Join(",", RuntimeRolls.Calls));
            Set(parameters, "boolParameter", false);
            Check(((IEnumerator)execute.Invoke(functor, new object[] { parameters, Context(true) })).GetType().DeclaringType == typeof(FunctorStealthBreaker), "No roll: the original functor");
        });
        Case("A charged item's last charge asks for the d20 that can destroy it", delegate {
            var slotSite = RuntimeRolls.FindOuterSite(FeatureRolls.SlotSite);
            var spendUses = typeof(CharacterActionMagicEffect).GetMethod("SpendMagicEffectUses", Any);
            foreach (var setup in new[] { "last", "spare", "persist" })
            {
                var hero = SavingHero();
                var definition = Make<FeatureDefinitionPower>(); Set(definition, "cachedName", "FixtureWandPower");
                var item = Make<ItemDefinition>(); Set(item, "cachedName", "FixtureWand");
                var description = new UsableDeviceDescription();
                Set(description, "usage", EquipmentDefinitions.ItemUsage.Charges);
                Set(description, "outOfChargesConsequence", setup == "persist" ? EquipmentDefinitions.ItemOutOfCharges.Persist : EquipmentDefinitions.ItemOutOfCharges.DestroyOnRoll1);
                var function = Make<DeviceFunctionDescription>(); Set(function, "type", DeviceFunctionDescription.FunctionType.Power); Set(function, "featureDefinitionPower", definition); Set(function, "useAmount", 1);
                Set(description, "deviceFunctions", new List<DeviceFunctionDescription> { function });
                Set(item, "usableDeviceDescription", description);
                var device = Make<RulesetItemDevice>(); Set(device, "itemDefinition", item);
                var usable = Make<RulesetDeviceFunction>(); Set(usable, "itemDefinition", item); Set(usable, "functionIndex", 0);
                Set(device, "usableFunctions", new List<RulesetDeviceFunction> { usable });
                Set(device, "attributes", new Dictionary<string, RulesetAttribute> { { "ItemCharges", Attribute(setup == "spare" ? 2 : 1) } });
                var usablePower = Make<RulesetUsablePower>(); Set(usablePower, "powerDefinition", definition);
                var power = Make<RulesetEffectPower>(); Set(power, "usablePower", usablePower); Set(power, "originItem", device);
                var action = Make<CharacterActionUsePower>(); Set(action, "actionParams", ActionParams(Character(hero), Make<ActionDefinition>())); Set(action, "activePower", power);
                var inner = slotSite.Resolve(Outer(FeatureRolls.SlotSite, spendUses, action));
                if (setup != "last") { Check(inner == null, "No die: " + setup); continue; }
                Check(inner != null && inner.Method.Name == "LastChargeCheck" && ReferenceEquals(inner.Args[0], hero) && (string)inner.Args[1] == "FixtureWand", "The last charge of an item that can crumble");
                var plan = new NativeRollPlan(); FeatureRolls.TryPrepare(inner, plan);
                Check(plan.Groups.Count == 1 && plan.Groups[0].Static && plan.Groups[0].Die == RuleDefinitions.DieType.D20 && plan.Groups[0].DC == 2 && plan.Groups[0].Context == Plugin.FeatureDice,
                    "A d20: the item is destroyed on a 1");
            }
            var deviceFunction = typeof(RulesetCharacterHero).GetMethod("UseDeviceFunction", Any);
            Check(PatchProcessor.GetCurrentInstructions(deviceFunction).Count(code => Equals(code.operand, typeof(FeatureRolls).GetMethod("DeviceFace"))) == 1, "The item's raw d20 goes through the hook");
        });
    }
    static RulesetUsablePower Power(RuleDefinitions.RechargeRate rate, int remaining, int maximum)
    {
        var definition = Make<FeatureDefinitionPower>(); Set(definition, "cachedName", "FixtureBreath"); Set(definition, "rechargeRate", rate);
        var power = Make<RulesetUsablePower>(); Set(power, "powerDefinition", definition); Set(power, "remainingUses", remaining); Set(power, "maxUses", maximum);
        return power;
    }
    static BehaviorModeDescription Behaviour(int weight, RuleDefinitions.RandomBehaviour behaviour)
    {
        var option = new BehaviorModeDescription(); Set(option, "weight", weight); Set(option, "behaviour", behaviour);
        if (behaviour == RuleDefinitions.RandomBehaviour.ConditionDuringTurn) { var condition = Make<ConditionDefinition>(); Set(condition, "cachedName", "FixtureLoseTurn"); Set(option, "condition", condition); }
        return option;
    }
    static RulesetEffectEnvironment EnvironmentEffect(bool save, int addDice, params EffectForm[] forms)
    {
        var description = Lingering(save, forms);
        Set(description, "recurrentEffect", RuleDefinitions.RecurrentEffect.No);
        Set(description, "durationType", RuleDefinitions.DurationType.Instantaneous);
        var definition = Make<EnvironmentEffectDefinition>(); Set(definition, "cachedName", "FixtureTrap"); Set(definition, "effectDescription", description);
        var effect = Make<RulesetEffectEnvironment>(); Set(effect, "environmentEffectDefinition", definition); Set(effect, "addDice", addDice);
        typeof(RulesetEffect).GetField("featuresToBrowse", Any).SetValue(effect, new List<FeatureDefinition>());
        Set(effect, "featuresOrigin", new Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin>());
        Set(effect, "accountedProviders", new List<ISavingThrowAffinityProvider>()); Set(effect, "trackedConditionGuids", new List<ulong>());
        return effect;
    }
}
