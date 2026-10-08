// 0.7 additions to the single-player/deferred site suite: the deferred start
// of a battle turn, lingering effects, saves when damage ends a condition,
// class/spell feature dice, stealth with the Perception rolled against it,
// hidden-object Perception, dialogue profiling and travel crafting. Every
// resolver is compared with the original game method's own dice call where
// the original can run in this host; replicas of skipped native tails run
// against the pinned originals with their engine boundaries recorded.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ManualDiceRoller;

static partial class SyncTests
{
    // ---- boundaries shared by the 0.7 cases --------------------------------
    internal static readonly List<string> Events = new List<string>();
    internal static bool SkipNextTurn, SkipStartTurn;
    internal static object[] CapturedForms; internal static object CapturedFormsReceiver;
    internal static object[] CapturedSave; internal static object CapturedSaveReceiver;
    static void ExtraReset() { RollWindow.Reset(); try { MenuRolls.Reset(); } catch (Exception) { } Events.Clear(); SkipNextTurn = false; SkipStartTurn = true; CapturedForms = null; CapturedSave = null; }
    static bool RecordStateChange(Type type) { Events.Add("state:" + type.Name); return false; }
    static bool RecordStartTurn(GameLocationCharacter __instance, int roundNumber) { if (!SkipStartTurn) return true; Events.Add("start:" + roundNumber); return false; }
    static bool RecordNextTurn() { if (!SkipNextTurn) return true; Events.Add("next"); return false; }
    static bool RecordOtherContender() { Events.Add("other"); return false; }
    static bool CaptureForms(object __instance, object[] __args, ref bool damageAbsorbedByTemporaryHitPoints, ref bool terminateEffectOnTarget, ref int __result)
    {
        CapturedFormsReceiver = __instance; CapturedForms = (object[])__args.Clone();
        damageAbsorbedByTemporaryHitPoints = false; terminateEffectOnTarget = false; __result = 0; Events.Add("forms"); return false;
    }
    static bool CaptureRulesetSave(RulesetActor __instance, object[] __args, ref RuleDefinitions.RollOutcome outcome, ref int outcomeDelta)
    { CapturedSaveReceiver = __instance; CapturedSave = (object[])__args.Clone(); outcome = RuleDefinitions.RollOutcome.Failure; outcomeDelta = -1; Events.Add("save"); return false; }
    static bool SkipGrant() { Events.Add("grant"); return false; }
    static bool FixedSaveBonus(ref int __result) { __result = 3; return false; }
    static bool RecordApply(RulesetActor targetCharacter) { Events.Add("apply"); return true; }

    static void RunExtra(Harmony harmony, MethodInfo locationCheck, MethodInfo rulesetCheck)
    {
        string error;
        Check(TurnRolls.Preflight(out error), "Turn roll preflight: " + error);
        Check(FeatureRolls.Preflight(out error), "Feature roll preflight: " + error);
        Check(CompositeRolls.Preflight(out error), "Composite roll preflight: " + error);
        CompositeRolls.Install(harmony);
        Check(MenuRolls.Preflight(out error), "Character screen preflight: " + error);
        MenuRolls.Install(harmony);
        harmony.Patch(Declared(typeof(CharacterStageAbilityScoresPanel).GetMethod("Reset", Any)), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("RecordPanelReset", Any)));
        harmony.Patch(Declared(typeof(CharacterStageAbilityScoresPanel).GetMethod("RefreshNow", Any)), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("RecordPanelRefresh", Any)));
        harmony.Patch(typeof(RulesetCharacterHero).GetMethod("ComputeHitPoints", new[] { typeof(bool) }), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("RecordHitPoints", Any)));
        FeatureRolls.Register();
        TurnRolls.Install(harmony);
        FeatureRolls.Install(harmony);
        var nextTurn = typeof(GameLocationBattle).GetMethod("NextTurn");
        var firstTurn = typeof(GameLocationBattle).GetMethod("StartFirstTurnOfRound");
        var startTurn = typeof(GameLocationCharacter).GetMethod("StartBattleTurn");
        // Engine boundaries: the battle state machine and a turn's own start
        // are recorded instead of run (they need a live scene).
        harmony.Patch(typeof(FiniteStateMachine).GetMethod("RequestStateChange", new[] { typeof(Type), typeof(object[]) }), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("RecordStateChange", Any)) { priority = Priority.First });
        harmony.Patch(startTurn, prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("RecordStartTurn", Any)) { priority = Priority.Last });
        harmony.Patch(nextTurn, prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("RecordNextTurn", Any)) { priority = Priority.First });
        harmony.Patch(typeof(GameLocationCharacter).GetMethod("StartBattleTurnOtherContender"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("RecordOtherContender", Any)));
        harmony.Patch(typeof(RulesetImplementationManager).GetMethod("ApplyEffectForms"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("CaptureForms", Any)) { priority = Priority.First });
        harmony.Patch(typeof(RulesetActor).GetMethod("RollSavingThrow"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("CaptureRulesetSave", Any)) { priority = Priority.First });
        harmony.Patch(typeof(RulesetActor).GetMethod("GrantConditionOnSavingThrowOutcome"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("SkipGrant", Any)));
        harmony.Patch(typeof(RulesetCharacterHero).GetMethod("ComputeBaseSavingThrowBonus"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("FixedSaveBonus", Any)));
        var bestCrafting = typeof(RulesetCharacterHero).GetMethods(Any).Single(m => m.Name == "ComputeBestAbilityScoreAndProficiencyForCrafting" && m.GetParameters()[0].ParameterType == typeof(RecipeDefinition));
        harmony.Patch(bestCrafting, prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("CraftingProficiency", Any)));
        harmony.Patch(typeof(RulesetCharacterHero).GetMethod("ComputeCraftingDurationHours"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("CraftingHours", Any)));
        harmony.Patch(typeof(WorldGadget).GetMethod("ConditionChoices"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("NoChoices", Any)));
        harmony.Patch(typeof(GameGadget).GetMethod("SetCondition"), prefix: new HarmonyMethod(typeof(SyncTests).GetMethod("RecordGadgetCondition", Any)));
        Console.WriteLine("PASS 0.7 turn, effect and feature roll preflight and patch installation against the pristine game assembly");

        Case("The battle's turn start calls are rewritten exactly once", delegate {
            var next = PatchProcessor.GetCurrentInstructions(nextTurn);
            int at = next.FindIndex(i => i.operand is MethodInfo && ((MethodInfo)i.operand).Name == "BeginNextTurn");
            Check(at > 0 && next.Count(i => i.operand is MethodInfo && ((MethodInfo)i.operand).Name == "BeginNextTurn") == 1 && !next.Any(i => Equals(i.operand, startTurn)), "NextTurn calls BeginNextTurn instead of StartBattleTurn");
            Check(next[at - 1].opcode == OpCodes.Ldarg_0 && (next[at + 1].opcode == OpCodes.Brfalse || next[at + 1].opcode == OpCodes.Brfalse_S) && next[at + 2].opcode == OpCodes.Pop && next[at + 3].opcode == OpCodes.Ret,
                "A deferred NextTurn drops its saved character copy and returns before the death check and state change");
            var first = PatchProcessor.GetCurrentInstructions(firstTurn);
            at = first.FindIndex(i => i.operand is MethodInfo && ((MethodInfo)i.operand).Name == "BeginFirstTurn");
            Check(at > 0 && !first.Any(i => Equals(i.operand, startTurn)) && first[at - 1].opcode == OpCodes.Ldarg_0 &&
                (first[at + 1].opcode == OpCodes.Brfalse || first[at + 1].opcode == OpCodes.Brfalse_S) && first[at + 2].opcode == OpCodes.Ret, "StartFirstTurnOfRound returns before its other-contender loop when deferred");
            var deflect = PatchProcessor.GetCurrentInstructions(typeof(CharacterActionDeflectMissile).Assembly.GetType("CharacterActionDeflectMissile+<ExecuteImpl>d__1").GetMethod("MoveNext", Any));
            Check(deflect.Count(i => i.operand is MethodInfo && ((MethodInfo)i.operand).Name == "DeflectFace") == 1 && !deflect.Any(i => i.operand is MethodInfo && ((MethodInfo)i.operand).Name == "RollDie"), "Deflect Missiles' single raw die goes through its hook");
            var failure = PatchProcessor.GetCurrentInstructions(typeof(CharacterActionCastSpell).Assembly.GetType("CharacterActionCastSpell+<CheckExecutionFailure>d__19").GetMethod("MoveNext", Any));
            Check(failure.Count(i => i.operand is MethodInfo && ((MethodInfo)i.operand).Name == "FailureFace") == 1, "The spell failure d20 goes through its hook");
            var slot = PatchProcessor.GetCurrentInstructions(typeof(RulesetCharacter).GetMethod("SpendSpellSlot", Any));
            Check(slot.Count(i => i.operand is MethodInfo && ((MethodInfo)i.operand).Name == "SlotFace") == 1, "The slot preservation d20 goes through its hook");
            int a, b; RuntimeRolls.Calls.Clear();
            FeatureRolls.DeflectFace(RuleDefinitions.DieType.D10, RuleDefinitions.AdvantageType.None, out a, out b);
            FeatureRolls.FailureFace(RuleDefinitions.DieType.D20, RuleDefinitions.AdvantageType.None, out a, out b);
            FeatureRolls.SlotFace(RuleDefinitions.DieType.D20, RuleDefinitions.AdvantageType.None, out a, out b);
            Check(RuntimeRolls.Calls.SequenceEqual(new[] { "raw:RollDie", "raw:CheckExecutionFailure", "raw:SpendMagicEffectUses" }), "Each hook only consults its own prepared native step");
        });

        Case("A turn start with a save waits whole and replays NextTurn's pinned tail", delegate {
            var service = Make<RulesetImplementationManager>();
            ServiceRepository.AddService<IRulesetImplementationService>(service);
            var battleService = DispatchProxy.Create<IGameLocationBattleService, BattleService>();
            ServiceRepository.AddService<IGameLocationBattleService>(battleService);
            Databases(true);
            try
            {
                var hero = Actor<RulesetCharacterHero>(); var condition = SavingCondition("Wisdom", 13);
                Set(condition, "endOccurence", RuleDefinitions.TurnOccurenceType.StartOfTurn); condition.CanSaveToCancel = true;
                Set(condition, "durationType", RuleDefinitions.DurationType.Round); condition.RemainingRounds = 2;
                Hold(hero, condition);
                var character = Character(hero);
                var battle = Battle(character);
                ((BattleService)(object)battleService).InProgress = true; ((BattleService)(object)battleService).Current = battle;
                RuntimeRolls.Prepare = (o, site, m, a) => { Events.Add("prepare:" + site); return null; };
                battle.NextTurn();
                Check(DeferredRolls.Pending == 1 && !Events.Contains("start:3") && !Events.Any(e => e.StartsWith("state:")), "NextTurn stops before StartBattleTurn, the death check and the state change: pending=" + DeferredRolls.Pending + " events=" + string.Join(",", Events) + " logs=" + string.Join(" | ", Plugin.Logs));
                Check(ReferenceEquals(battle.ActiveContender, character), "The next contender is already chosen");
                DeferredRolls.Update();
                Check(Events.SequenceEqual(new[] { "prepare:" + TurnRolls.TurnStartSite, "start:3", "state:BattleState_TurnInitialize" }), "The replay starts the turn, then the TurnInitialize state, in the original order: " + string.Join(",", Events));
                Check(!DeferredRolls.Holding, "Nothing is left waiting");

                // The composite plan for the same start of turn: the condition's save.
                var parts = TurnRolls.TurnStartParts(character);
                Check(parts.Count == 1 && parts[0].Method.Name == "TryRollSavingThrow" && ReferenceEquals(parts[0].Anchor, condition) && ReferenceEquals(parts[0].Args[3], hero), "One part: the condition's save, bound to that condition");

                // A character that died meanwhile is passed over like NextTurn does.
                Events.Clear(); battle = Battle(character); ((BattleService)(object)battleService).Current = battle;
                battle.NextTurn(); Check(DeferredRolls.Pending == 1, "Deferred again");
                Set(hero, "isDead", true); SkipNextTurn = true;
                DeferredRolls.Update();
                Check(Events.SequenceEqual(new[] { "prepare:" + TurnRolls.TurnStartSite, "next" }) || Events.SequenceEqual(new[] { "next" }), "A dead character's turn is not started; the battle moves on: " + string.Join(",", Events));
                Set(hero, "isDead", false); SkipNextTurn = false;

                // Settings off, or nobody rolling manually: the native order.
                Events.Clear(); battle = Battle(character); ((BattleService)(object)battleService).Current = battle;
                Plugin.Settings.RepeatSaves = false; Plugin.Settings.LingeringEffects = false;
                battle.NextTurn();
                Check(!DeferredRolls.Holding && Events.SequenceEqual(new[] { "start:3", "state:BattleState_TurnInitialize" }), "Settings off: StartBattleTurn and the state change run at once");
                Plugin.Settings.RepeatSaves = true; Plugin.Settings.LingeringEffects = true; RuntimeRolls.AllowManual = false;
                Events.Clear(); battle = Battle(character); ((BattleService)(object)battleService).Current = battle;
                battle.NextTurn();
                Check(!DeferredRolls.Holding && Events.SequenceEqual(new[] { "start:3", "state:BattleState_TurnInitialize" }), "No manual roller: native order");
                RuntimeRolls.AllowManual = true;
                // The first turn of a round replays its own tail (other contenders, then the state).
                Events.Clear(); battle = Battle(character); ((BattleService)(object)battleService).Current = battle;
                var other = Character(Actor<RulesetCharacterHero>());
                ((List<GameLocationCharacter>)Field(battle, "initiativeSortedContenders")).Add(other);
                battle.StartFirstTurnOfRound();
                Check(DeferredRolls.Pending == 1 && Events.Count == 0, "StartFirstTurnOfRound waits before StartBattleTurn");
                DeferredRolls.Update();
                Check(Events.SequenceEqual(new[] { "prepare:" + TurnRolls.TurnStartSite, "start:3", "other", "state:BattleState_TurnInitialize" }), "Replay: the turn, the other contender's refresh, then TurnInitialize: " + string.Join(",", Events));
            }
            finally
            {
                ServiceRepository.RemoveService<IRulesetImplementationService>(); ServiceRepository.RemoveService<IGameLocationBattleService>(); Databases(false);
            }
        });

        var tryRollSave = typeof(RulesetImplementationManager).GetMethod("TryRollSavingThrow");
        Case("Lingering spell parts match the effect's own save and forms", delegate {
            var service = Make<RulesetImplementationManager>();
            ServiceRepository.AddService<IRulesetImplementationService>(service);
            try
            {
                foreach (bool save in new[] { true, false })
                {
                    var caster = SavingHero(); var target = SavingHero(); Set(target, "side", RuleDefinitions.Side.Enemy);
                    var form = DamageEffectForm(RuleDefinitions.DieType.D8, 3, RuleDefinitions.EffectSavingThrowType.HalfDamage);
                    var description = Lingering(save, form);
                    Set(description.EffectAdvancement, "effectIncrementMethod", RuleDefinitions.EffectIncrementMethod.PerAdditionalSlotLevel);
                    Set(description.EffectAdvancement, "additionalDicePerIncrement", 1);
                    var effect = CastSpell(caster, description, 3, 4);
                    var parts = new List<DeferredRolls.RollPart>();
                    TurnRolls.AddEffectParts(parts, effect, target, true, new TA.int3(1, 0, 1));
                    Check(parts.Count == (save ? 2 : 1) && parts.All(part => ReferenceEquals(part.Anchor, effect)), "Save (if any) then forms, both bound to this effect");
                    // The original application, with nobody rolling manually (no deferral).
                    RuntimeRolls.AllowManual = false; Captured = null; CapturedForms = null; Events.Clear();
                    effect.ApplyEffectOnCharacter(target, true, new TA.int3(1, 0, 1));
                    RuntimeRolls.AllowManual = true;
                    Check(CapturedForms != null && ReferenceEquals(CapturedFormsReceiver, service), "The original applies its forms through the service");
                    var original = CapturedForms; var originalSave = Captured;
                    var forms = parts[parts.Count - 1];
                    Check(forms.Method.Name == "ApplyEffectForms" && ReferenceEquals(forms.Args[0], service) && ReferenceEquals(forms.Args[1], original[0]), "Same service and the effect's own form list");
                    SameFormsParams(forms.Args[2], original[1], "lingering " + save);
                    Check(Equals(forms.Args[9], original[8]) && ReferenceEquals(forms.Args[10], original[9]), "Same application mode and filters");
                    var p = (RulesetImplementationDefinitions.ApplyFormsParams)forms.Args[2];
                    Check(p.addDice == 1 && p.effectLevel == 4 && p.rolledSaveThrow == save && (!save || p.saveOutcome == RuleDefinitions.RollOutcome.Failure), "Upcast dice, slot level and (planned as failed) save outcome");
                    if (save)
                    {
                        Check(originalSave != null && Events.Contains("grant"), "The original rolled the target's save first");
                        Captured = null; parts[0].Method.Invoke(parts[0].Args[0], parts[0].Args.Skip(1).ToArray());
                        Check(Captured != null, "The planned save reaches the same service call");
                        SameArguments(originalSave, Captured, tryRollSave, "lingering save");
                        Check(ReferenceEquals(Captured[2], target) && (int)Captured[7] == 10 && (string)Captured[6] == "Wisdom", "Target, ability and the effect's save DC");
                    }
                    Set(effect, "terminated", true);
                    parts.Clear(); TurnRolls.AddEffectParts(parts, effect, target, true, new TA.int3(1, 0, 1));
                    Check(parts.Count == 0, "A terminated effect rolls nothing");
                }
            }
            finally { ServiceRepository.RemoveService<IRulesetImplementationService>(); }
        });
        Case("A condition's recurring forms match ExecuteRecurrentForms", delegate {
            var service = Make<RulesetImplementationManager>();
            ServiceRepository.AddService<IRulesetImplementationService>(service);
            try
            {
                var hero = SavingHero(); var condition = SavingCondition("Constitution", 12);
                var definition = condition.ConditionDefinition; Set(definition, "cachedName", "FixtureBurning");
                Set(definition, "recurrentEffectForms", new List<EffectForm> { DamageEffectForm(RuleDefinitions.DieType.D4, 2, RuleDefinitions.EffectSavingThrowType.None) });
                Set(definition, "amountOrigin", ConditionDefinition.OriginOfAmount.AddDice);
                Set(condition, "amount", 2); Set(condition, "sourceAbilityBonus", 3);
                var parts = new List<DeferredRolls.RollPart>();
                TurnRolls.AddRecurrentFormParts(parts, hero, condition);
                Check(parts.Count == 1 && ReferenceEquals(parts[0].Anchor, condition) && parts[0].Title == "Lingering damage", "One part bound to the condition");
                CapturedForms = null; Call(typeof(RulesetActor), hero, "ExecuteRecurrentForms", condition);
                Check(CapturedForms != null && ReferenceEquals(CapturedForms[0], definition.RecurrentEffectForms), "The original applies the condition's recurring forms");
                SameFormsParams(parts[0].Args[2], CapturedForms[1], "recurring forms");
                var p = (RulesetImplementationDefinitions.ApplyFormsParams)parts[0].Args[2];
                Check(p.addDice == 2 && p.formAbilityBonus == 3 && ReferenceEquals(p.targetCharacter, hero), "Added dice and ability bonus come from the condition");
            }
            finally { ServiceRepository.RemoveService<IRulesetImplementationService>(); }
        });
        Case("Saves when damage ends a condition match the original loop", delegate {
            DatabaseRepository.AddDatabase(new Database<SpellDefinition>()); DatabaseRepository.AddDatabase(new Database<FeatureDefinition>());
            try
            {
                var hero = SavingHero();
                Func<string, int, RuleDefinitions.AdvantageType, RulesetCondition> laughing = (ability, dc, affinity) =>
                {
                    var condition = SavingCondition(ability, dc); var definition = condition.ConditionDefinition; Set(definition, "features", new List<FeatureDefinition>());
                    Set(definition, "cachedName", "FixtureLaughter"); Set(definition, "specialInterruptions", new List<RuleDefinitions.ConditionInterruption> { RuleDefinitions.ConditionInterruption.Damaged });
                    Set(definition, "interruptionRequiresSavingThrow", true); Set(definition, "interruptionSavingThrowAffinity", affinity);
                    return condition;
                };
                var first = laughing("Wisdom", 14, RuleDefinitions.AdvantageType.Advantage); var second = laughing("Constitution", 11, RuleDefinitions.AdvantageType.None);
                Set(hero, "conditionsByCategory", new Dictionary<string, List<RulesetCondition>> { { "10Combat", new List<RulesetCondition> { first, second } } });
                var parts = TurnRolls.InterruptionParts(hero, RuleDefinitions.ConditionInterruption.Damaged, 9);
                Check(parts.Count == 2 && parts.All(part => part.Method.Name == "RollSavingThrow" && ReferenceEquals(part.Args[0], hero)), "One save for each condition, in the loop's order");
                var originals = new List<object[]>();
                harmony.Patch(typeof(RulesetActor).GetMethod("RollSavingThrow"), postfix: new HarmonyMethod(typeof(SyncTests).GetMethod("CollectSave", Any)));
                try
                {
                    Collected.Clear();
                    hero.ProcessConditionsMatchingInterruption(RuleDefinitions.ConditionInterruption.Damaged, 9);
                    originals.AddRange(Collected);
                    Check(originals.Count == 2, "The original rolls both saves (and keeps both conditions on failures)");
                    for (int index = 0; index < 2; index++)
                    {
                        Collected.Clear(); parts[index].Method.Invoke(hero, parts[index].Args.Skip(1).ToArray());
                        SameArguments(originals[index], Collected[0], parts[index].Method, "interruption save " + index);
                        Check(((List<RuleDefinitions.TrendInfo>)Collected[0][4]).Count == ((List<RuleDefinitions.TrendInfo>)originals[index][4]).Count, "Same advantage trends");
                    }
                    Check((string)originals[0][1] == "Wisdom" && (int)originals[0][6] == 14 && ((List<RuleDefinitions.TrendInfo>)originals[0][4]).Count == 1, "The first save: Wisdom DC 14 with the condition's advantage");
                }
                finally { harmony.Unpatch(typeof(RulesetActor).GetMethod("RollSavingThrow"), typeof(SyncTests).GetMethod("CollectSave", Any)); }
                Check(TurnRolls.InterruptionParts(hero, RuleDefinitions.ConditionInterruption.Attacks, 9).Count == 0, "Other interruptions are not these saves");
                Set(first.ConditionDefinition, "interruptionDamageThreshold", 10); Set(second.ConditionDefinition, "interruptionDamageThreshold", 10);
                Check(TurnRolls.InterruptionParts(hero, RuleDefinitions.ConditionInterruption.Damaged, 9).Count == 0, "Damage below the threshold: no save");
            }
            finally { DatabaseRepository.RemoveDatabase<SpellDefinition>(); DatabaseRepository.RemoveDatabase<FeatureDefinition>(); }
        });
        Case("Lingering effects and damage saves wait only where it is safe", delegate {
            var service = Make<RulesetImplementationManager>();
            ServiceRepository.AddService<IRulesetImplementationService>(service);
            DatabaseRepository.AddDatabase(new Database<SpellDefinition>()); DatabaseRepository.AddDatabase(new Database<FeatureDefinition>());
            try
            {
                var caster = SavingHero(); var target = SavingHero();
                var effect = CastSpell(caster, Lingering(true, DamageEffectForm(RuleDefinitions.DieType.D8, 3, RuleDefinitions.EffectSavingThrowType.HalfDamage)), 3, 3);
                RuntimeRolls.Prepare = (o, site, m, a) => { Events.Add("prepare:" + site); return null; };
                Events.Clear(); CapturedForms = null;
                effect.ApplyEffectOnCharacter(target, true, new TA.int3(0, 0, 0));
                Check(DeferredRolls.Pending == 1 && CapturedForms == null && !Events.Contains("grant"), "Entering the area waits before the save and the damage");
                DeferredRolls.Update();
                Check(Events.SequenceEqual(new[] { "prepare:" + TurnRolls.LingeringSite, "grant", "forms" }) && !DeferredRolls.Holding, "Then the original application runs once: " + string.Join(",", Events));
                effect.ApplyEffectOnCharacter(target, true, new TA.int3(0, 0, 0));
                Set(effect, "terminated", true); Events.Clear();
                DeferredRolls.Update();
                Check(!Events.Contains("forms") && !DeferredRolls.Holding, "An effect that ended meanwhile is not applied");
                Set(effect, "terminated", false);
                Plugin.Settings.LingeringEffects = false; Events.Clear();
                effect.ApplyEffectOnCharacter(target, true, new TA.int3(0, 0, 0));
                Check(!DeferredRolls.Holding && Events.Contains("forms"), "Setting off: applied at once");
                Plugin.Settings.LingeringEffects = true;
                // Inside a turn start that is not itself deferred nothing waits.
                typeof(TurnRolls).GetMethod("StartTurnPrefix", Any).Invoke(null, null);
                try { Events.Clear(); effect.ApplyEffectOnCharacter(target, true, new TA.int3(0, 0, 0)); }
                finally { typeof(TurnRolls).GetMethod("StartTurnFinalizer", Any).Invoke(null, new object[] { null }); }
                Check(!DeferredRolls.Holding && Events.Contains("forms"), "Within StartBattleTurn the application stays native");

                // Damage saves: off by default, then the whole interruption waits.
                var condition = SavingCondition("Wisdom", 14); Set(condition.ConditionDefinition, "features", new List<FeatureDefinition>()); Set(condition.ConditionDefinition, "cachedName", "FixtureHideousLaughter");
                Set(condition.ConditionDefinition, "specialInterruptions", new List<RuleDefinitions.ConditionInterruption> { RuleDefinitions.ConditionInterruption.Damaged });
                Set(condition.ConditionDefinition, "interruptionRequiresSavingThrow", true);
                Set(target, "conditionsByCategory", new Dictionary<string, List<RulesetCondition>> { { "10Combat", new List<RulesetCondition> { condition } } });
                Events.Clear(); target.ProcessConditionsMatchingInterruption(RuleDefinitions.ConditionInterruption.Damaged, 5);
                Check(!DeferredRolls.Holding && Events.Contains("save"), "Damage saves are off by default");
                Plugin.Settings.DamageSaves = true; Events.Clear();
                target.ProcessConditionsMatchingInterruption(RuleDefinitions.ConditionInterruption.Damaged, 5);
                Check(DeferredRolls.Pending == 1 && !Events.Contains("save"), "With the option on the interruption waits");
                target.ProcessConditionsMatchingInterruption(RuleDefinitions.ConditionInterruption.Moved, 5);
                Check(DeferredRolls.Pending == 1, "Other interruptions never wait");
                DeferredRolls.Update();
                Check(Events.SequenceEqual(new[] { "prepare:" + TurnRolls.DamageSaveSite, "save" }) && !DeferredRolls.Holding, "Then the original saves: " + string.Join(",", Events));
            }
            finally { ServiceRepository.RemoveService<IRulesetImplementationService>(); DatabaseRepository.RemoveDatabase<SpellDefinition>(); DatabaseRepository.RemoveDatabase<FeatureDefinition>(); }
        });
        Case("A condition's recurring damage at the end of a turn waits in the queue's order", delegate {
            var service = Make<RulesetImplementationManager>();
            ServiceRepository.AddService<IRulesetImplementationService>(service);
            var push = typeof(DeferredRolls).GetMethod("OccurrencePrefix", Any); var pop = typeof(DeferredRolls).GetMethod("OccurrenceFinalizer", Any);
            try
            {
                var hero = SavingHero(); var condition = SavingCondition("Constitution", 12);
                Set(condition.ConditionDefinition, "recurrentEffectForms", new List<EffectForm> { DamageEffectForm(RuleDefinitions.DieType.D6, 1, RuleDefinitions.EffectSavingThrowType.None) });
                RuntimeRolls.Prepare = (o, site, m, a) => { Events.Add("prepare:" + site); return null; };
                // The recurring forms' source is the condition's caster; with none
                // nobody rolls them manually, so they apply at once.
                push.Invoke(null, new object[] { RuleDefinitions.TurnOccurenceType.EndOfTurn });
                try { Call(typeof(RulesetActor), hero, "ExecuteRecurrentForms", condition); }
                finally { pop.Invoke(null, new object[] { null }); }
                Check(!DeferredRolls.Holding && Events.Contains("forms"), "Without a source to roll for, the damage is native");
                Check(DeferredRolls.CurrentOccurrence == null, "Outside condition processing there is no occurrence");
            }
            finally { ServiceRepository.RemoveService<IRulesetImplementationService>(); }
        });

        // ---- class and spell feature dice ----------------------------------
        var deflectIterator = typeof(CharacterActionDeflectMissile).Assembly.GetType("CharacterActionDeflectMissile+<ExecuteImpl>d__1");
        var rollDie = typeof(RuleDefinitions).GetMethod("RollDie");
        Case("Deflect Missiles plans its reduction die with the bonus the action adds", delegate {
            foreach (bool level in new[] { false, true })
            {
                var hero = SavingHero();
                Set(hero, "attributes", new Dictionary<string, RulesetAttribute> { { "Dexterity", Attribute(16) }, { "CharacterLevel", Attribute(5) } });
                var definition = Make<ActionDefinition>(); Set(definition, "dieType", RuleDefinitions.DieType.D10); Set(definition, "abilityScore", "Dexterity"); Set(definition, "addLevel", level);
                var action = Make<CharacterActionDeflectMissile>(); Set(action, "actionParams", ActionParams(Character(hero), definition));
                var iterator = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(deflectIterator); Set(iterator, "<>4__this", action);
                var plan = new NativeRollPlan();
                Check(FeatureRolls.TryPrepare(new NativeInvocation(iterator, FeatureRolls.DeflectSite, rollDie, new object[] { RuleDefinitions.DieType.D10, RuleDefinitions.AdvantageType.None, null, null, 0f }, 1), plan), "The Deflect Missiles site is this planner's");
                Check(plan.Groups.Count == 1 && plan.Groups[0].Static && plan.Groups[0].Die == RuleDefinitions.DieType.D10 && plan.Groups[0].Count == 1 && plan.Groups[0].Context == Plugin.FeatureDice
                    && plan.Groups[0].HasBonus && plan.Groups[0].Bonus == 3 + (level ? 5 : 0) && plan.Groups[0].Title == "Deflect Missiles", "One d10 with the Dexterity modifier" + (level ? " and level" : ""));
                Plugin.Settings.Features = false; plan = new NativeRollPlan();
                FeatureRolls.TryPrepare(new NativeInvocation(iterator, FeatureRolls.DeflectSite, rollDie, new object[] { RuleDefinitions.DieType.D10, RuleDefinitions.AdvantageType.None, null, null, 0f }, 1), plan);
                Check(plan.Groups.Count == 0, "Setting off: the game rolls"); Plugin.Settings.Features = true;
            }
            Check(!FeatureRolls.TryPrepare(new NativeInvocation(new object(), "x", rollDie, new object[5], 1), new NativeRollPlan()), "Other raw dice are not this planner's");
        });
        Case("Spell failure waits before the original check and then fails on the entered die", delegate {
            var dice = DispatchProxy.Create<IDiceSettingsService, DiceSettings>();
            ServiceRepository.AddService<IDiceSettingsService>(dice);
            try
            {
                foreach (int dc in new[] { 12, 10 })
                {
                    var hero = HeroWith(MagicAffinity(RuleDefinitions.CastingAffinity.CanFail, dc, false, 0, 20));
                    var action = Make<CharacterActionCastSpell>(); Set(action, "actionParams", ActionParams(Character(hero), Make<ActionDefinition>()));
                    RulesetCharacter caster; int planned;
                    Check(FeatureRolls.ResolveSpellFailure(action, out caster, out planned) && ReferenceEquals(caster, hero) && planned == dc, "The casting affinity that can fail is found with its DC");
                    int steps = 0; MethodBase method = null; object[] args = null;
                    RuntimeRolls.Prepare = (o, site, m, a) => { method = m; args = a; Events.Add("prepare:" + site); return Wait(2, () => steps++); };
                    var routine = (IEnumerator)typeof(CharacterActionCastSpell).GetMethod("CheckExecutionFailure", Any).Invoke(action, null);
                    Check(routine.GetType().DeclaringType == typeof(FeatureRolls), "The check is replaced by the waiting routine");
                    var coroutine = new TA.Coroutine(); coroutine.Start(routine);
                    coroutine.Run(); Check(steps == 1 && !RuntimeRolls.Calls.Any(c => c.StartsWith("raw:")), "It waits before the original loop rolls");
                    coroutine.Run(); coroutine.Run(); coroutine.Run();
                    Check(RuntimeRolls.Calls.Contains("raw:CheckExecutionFailure") && RuntimeRolls.Calls.Contains("observe:" + FeatureRolls.FailureSite + ":" + (11 < dc)), "The original loop rolls through the hook (11) inside its native step: " + string.Join(",", RuntimeRolls.Calls));
                    Check(method != null && method.Name == "SpellFailureCheck" && ReferenceEquals(args[0], hero) && (int)args[1] == dc, "The planned identity carries the caster and DC");
                    var plan = new NativeRollPlan();
                    FeatureRolls.TryPrepare(new NativeInvocation(new object(), FeatureRolls.FailureSite, method, args, 1), plan);
                    Check(plan.Groups.Count == 1 && plan.Groups[0].Die == RuleDefinitions.DieType.D20 && plan.Groups[0].DC == dc && plan.Groups[0].Context == Plugin.FeatureDice, "A d20 against the DC");
                    for (int i = 0; i < 4 && !coroutine.IsFinished; i++) coroutine.Run();
                    Check(coroutine.IsFinished, "The original routine finishes");
                    RuntimeRolls.Calls.Clear();
                }
                var forbidden = HeroWith(MagicAffinity(RuleDefinitions.CastingAffinity.CannotCast, 12, false, 0, 20), MagicAffinity(RuleDefinitions.CastingAffinity.CanFail, 12, false, 0, 20));
                var blocked = Make<CharacterActionCastSpell>(); var parameters = ActionParams(Character(forbidden), Make<ActionDefinition>());
                var spell = CastSpell(forbidden, Lingering(false), 1, 1); Set(parameters, "activeEffect", spell); Set(blocked, "actionParams", parameters);
                RulesetCharacter ignored; int none;
                Check(!FeatureRolls.ResolveSpellFailure(blocked, out ignored, out none), "A spell that cannot be cast at all rolls nothing");
                Plugin.Settings.Features = false;
                var plain = Make<CharacterActionCastSpell>(); Set(plain, "actionParams", ActionParams(Character(HeroWith(MagicAffinity(RuleDefinitions.CastingAffinity.CanFail, 12, false, 0, 20))), Make<ActionDefinition>()));
                Check(((IEnumerator)typeof(CharacterActionCastSpell).GetMethod("CheckExecutionFailure", Any).Invoke(plain, null)).GetType().DeclaringType == typeof(CharacterActionCastSpell), "Setting off: the original routine");
            }
            finally { ServiceRepository.RemoveService<IDiceSettingsService>(); }
        });
        var spendUses = typeof(CharacterActionMagicEffect).GetMethod("SpendMagicEffectUses", Any);
        Case("Spell slot preservation finds the original's feature and threshold", delegate {
            var slotSite = RuntimeRolls.FindOuterSite(FeatureRolls.SlotSite);
            Check(slotSite != null && slotSite.SkipUnresolved, "Registered as an outer site that skips casts without a roll");
            foreach (int cap in new[] { 3, 1 })
            {
                var hero = HeroWith(MagicAffinity(RuleDefinitions.CastingAffinity.Normal, 10, true, cap, 15));
                var spell = CastSpell(hero, Lingering(false), 2, 2);
                var repertoire = Make<RulesetSpellRepertoire>(); Set(repertoire, "extraSpellsByTag", new Dictionary<string, List<SpellDefinition>>());
                Set(spell, "spellRepertoire", repertoire);
                var action = Make<CharacterActionCastSpell>(); Set(action, "actionParams", ActionParams(Character(hero), Make<ActionDefinition>())); Set(action, "activeSpell", spell);
                var inner = slotSite.Resolve(Outer(FeatureRolls.SlotSite, spendUses, action));
                if (cap < 2) { Check(inner == null, "A slot above the feature's cap is never preserved: no roll"); continue; }
                Check(inner != null && inner.Method.Name == "SlotPreservationCheck" && ReferenceEquals(inner.Args[0], hero) && (int)inner.Args[1] == 15, "The preserving feature's threshold");
                var plan = new NativeRollPlan(); FeatureRolls.TryPrepare(inner, plan);
                Check(plan.Groups.Count == 1 && plan.Groups[0].Die == RuleDefinitions.DieType.D20 && plan.Groups[0].DC == 15, "A d20 against the threshold");
            }
            var item = HeroWith(MagicAffinity(RuleDefinitions.CastingAffinity.Normal, 10, true, 5, 15));
            var fromItem = CastSpell(item, Lingering(false), 2, 2); Set(fromItem, "originItem", Make<RulesetItemDevice>());
            var device = Make<CharacterActionCastSpell>(); Set(device, "actionParams", ActionParams(Character(item), Make<ActionDefinition>())); Set(device, "activeSpell", fromItem);
            Check(slotSite.Resolve(Outer(FeatureRolls.SlotSite, spendUses, device)) == null, "A spell cast from an item spends no slot");
        });
        // ---- stealth checks with the Perception rolled against them ---------
        var stealthBehavior = typeof(CharacterAction).GetMethod("ApplyStealthBreakerBehavior", Any);
        Case("In battle the detectors' Perception and the hidden character's Stealth are planned in the original order", delegate {
            var positioning = DispatchProxy.Create<IGameLocationPositioningService, PositioningService>();
            var battleService = DispatchProxy.Create<IGameLocationBattleService, BattleService>();
            ServiceRepository.AddService<IGameLocationPositioningService>(positioning); ServiceRepository.AddService<IGameLocationBattleService>(battleService);
            try
            {
                var character = Hidden(); var battle = Make<GameLocationBattle>(); Set(battle, "activeContender", character);
                ((BattleService)(object)battleService).InProgress = true; ((BattleService)(object)battleService).Current = battle;
                var parts = OuterSites.StealthParts(character, new ActionModifier(), null);
                Check(parts != null && parts.Count == 2 && parts[0].Method == rulesetCheck && parts[1].Method == locationCheck && parts[0].Title == "Spotting a hidden creature" && parts[1].Title == "Staying hidden",
                    "The detector's Perception, then the hidden character's Stealth");
                CapturedChecks.Clear(); ForcedOutcome = RuleDefinitions.RollOutcome.Success;
                try { character.ComputeStealthBreak(true, new ActionModifier(), null); } finally { ForcedOutcome = RuleDefinitions.RollOutcome.Failure; }
                Check(CapturedChecks.Count == 2 && ReferenceEquals(CapturedChecks[0].Key, character.CharactersInNoiseRange[0].RulesetCharacter) && ReferenceEquals(CapturedChecks[1].Key, character.RulesetCharacter), "The original rolls the same two checks");
                var original = CapturedChecks.ToList(); CapturedChecks.Clear();
                parts[0].Method.Invoke(parts[0].Args[0], parts[0].Args.Skip(1).ToArray());
                parts[1].Method.Invoke(parts[1].Args[0], parts[1].Args.Skip(1).ToArray());
                SameArguments(original[0].Value, CapturedChecks[0].Value, rulesetCheck, "detector Perception");
                Check((string)CapturedChecks[0].Value[2] == "Perception" && (int)CapturedChecks[0].Value[17] == 14 && !(bool)CapturedChecks[0].Value[15], "Wisdom (Perception) against the passive minimum, without a notification");
                CapturedChecks[1].Value[6] = original[1].Value[6];
                SameArguments(original[1].Value, CapturedChecks[1].Value, rulesetCheck, "hidden Stealth");
                ((BattleService)(object)battleService).InProgress = false;
                parts = OuterSites.StealthParts(character, new ActionModifier(), null);
                Check(parts.Count == 1 && parts[0].Method == locationCheck, "Outside the hidden character's battle turn only the Stealth check is rolled");
            }
            finally { ServiceRepository.RemoveService<IGameLocationPositioningService>(); ServiceRepository.RemoveService<IGameLocationBattleService>(); }
        });
        Case("An action that can give a hidden character away waits for the same checks", delegate {
            var positioning = DispatchProxy.Create<IGameLocationPositioningService, PositioningService>();
            var battleService = DispatchProxy.Create<IGameLocationBattleService, BattleService>();
            ServiceRepository.AddService<IGameLocationPositioningService>(positioning); ServiceRepository.AddService<IGameLocationBattleService>(battleService);
            try
            {
                var site = RuntimeRolls.FindOuterSite(OuterSites.ActionStealth);
                Check(site != null && site.SkipUnresolved, "Every action passes this site; only a roll enters a step");
                foreach (var behavior in new[] { ActionDefinitions.StealthBreakerBehavior.Roll, ActionDefinitions.StealthBreakerBehavior.RollIfTargets })
                {
                    var character = Hidden(); var battle = Make<GameLocationBattle>(); Set(battle, "activeContender", character);
                    ((BattleService)(object)battleService).InProgress = true; ((BattleService)(object)battleService).Current = battle;
                    var extra = Character(Actor<RulesetCharacterHero>()); Set(extra.RulesetCharacter, "side", RuleDefinitions.Side.Enemy); extra.LineOfSightRatio[character] = 1f;
                    ((BattleService)(object)battleService).Targets = new List<GameLocationCharacter> { extra };
                    var definition = Make<ActionDefinition>(); Set(definition, "stealthBreakerBehavior", behavior);
                    var action = Make<CharacterActionDeflectMissile>(); Set(action, "actionParams", ActionParams(character, definition));
                    var inner = site.Resolve(Outer(OuterSites.ActionStealth, stealthBehavior, action));
                    Check(inner != null && inner.Method == DeferredRolls.CompositeMethod, "A composite step: " + behavior);
                    var parts = (DeferredRolls.RollPart[])inner.Args[0];
                    int detectors = behavior == ActionDefinitions.StealthBreakerBehavior.RollIfTargets ? 2 : 1;
                    Check(parts.Length == detectors + 1, "One Perception per valid detector, then Stealth");
                    CapturedChecks.Clear(); ForcedOutcome = RuleDefinitions.RollOutcome.Success;
                    try { Call(typeof(CharacterAction), action, "ApplyStealthBreakerBehavior"); } finally { ForcedOutcome = RuleDefinitions.RollOutcome.Failure; }
                    var original = CapturedChecks.ToList(); CapturedChecks.Clear();
                    Check(original.Count == parts.Length, "The original rolls exactly these checks");
                    foreach (var part in parts) part.Method.Invoke(part.Args[0], part.Args.Skip(1).ToArray());
                    original[original.Count - 1].Value[6] = CapturedChecks[CapturedChecks.Count - 1].Value[6] = 0;
                    for (int index = 0; index < parts.Length; index++) SameArguments(original[index].Value, CapturedChecks[index].Value, rulesetCheck, behavior + " check " + index);
                    if (detectors == 2) Check(((List<RuleDefinitions.TrendInfo>)CapturedChecks[1].Value[4]).Count == ((List<RuleDefinitions.TrendInfo>)CapturedChecks[0].Value[4]).Count + 1, "A target of the action detects with advantage");
                }
                var quiet = Hidden(); var none = Make<ActionDefinition>(); Set(none, "stealthBreakerBehavior", ActionDefinitions.StealthBreakerBehavior.None);
                var harmless = Make<CharacterActionDeflectMissile>(); Set(harmless, "actionParams", ActionParams(quiet, none));
                Check(site.Resolve(Outer(OuterSites.ActionStealth, stealthBehavior, harmless)) == null, "An action that never breaks stealth rolls nothing");
                var targeted = Make<ActionDefinition>(); Set(targeted, "stealthBreakerBehavior", ActionDefinitions.StealthBreakerBehavior.RollIfTargets);
                ((BattleService)(object)battleService).Targets = new List<GameLocationCharacter>();
                var untargeted = Make<CharacterActionDeflectMissile>(); Set(untargeted, "actionParams", ActionParams(quiet, targeted));
                Check(site.Resolve(Outer(OuterSites.ActionStealth, stealthBehavior, untargeted)) == null, "Without offensive targets there is no roll");
                Plugin.Settings.Stealth = false;
                var roll = Make<ActionDefinition>(); Set(roll, "stealthBreakerBehavior", ActionDefinitions.StealthBreakerBehavior.Roll);
                var off = Make<CharacterActionDeflectMissile>(); Set(off, "actionParams", ActionParams(Hidden(), roll));
                Check(site.Resolve(Outer(OuterSites.ActionStealth, stealthBehavior, off)) == null, "Stealth checks off: native");
            }
            finally { ServiceRepository.RemoveService<IGameLocationPositioningService>(); ServiceRepository.RemoveService<IGameLocationBattleService>(); }
        });

        // ---- optional hidden-object Perception -----------------------------
        Case("Hidden-object Perception plans only the active roll the functor makes", delegate {
            var functor = new FunctorSetGadgetConditionByAbilityCheck(); var context = Context(true);
            foreach (int dc in new[] { 20, 12 })
            foreach (bool alert in new[] { true, false })
            {
                var hero = SavingHero(); var acting = Character(hero); Set(acting, "alertPerception", alert);
                var parameters = Functor(Check("Wisdom", "Perception", dc, RuleDefinitions.AdvantageType.None, 0, false), acting);
                var gadget = Make<WorldGadget>(); Set(gadget, "m_CachedPtr", new IntPtr(1));
                var game = Make<GameGadget>(); Set(game, "lightingInformationPerFlow", new Dictionary<int, GameGadget.LightingInformation>()); Set(gadget, "gameGadget", game);
                parameters.SourceGadget = gadget; parameters.BoolParameter = true;
                var inner = OuterSites.ResolveGadgetPerception(parameters, acting);
                CapturedChecks.Clear(); Call(typeof(FunctorSetGadgetConditionByAbilityCheck), functor, "ExecuteCheckOnCharacter", parameters, context, acting);
                var original = CapturedChecks.ToList();
                if (!alert) { Check(inner == null && original.Count == 1 && (bool)original[0].Value[7], "A character who is not searching makes a passive check: no die"); continue; }
                if (dc <= 14) { Check(inner == null && original.Count == 0, "A passive score that already succeeds rolls nothing"); continue; }
                Check(inner != null && inner.Method == locationCheck && ReferenceEquals(inner.Receiver, acting), "The searching character's active check");
                CapturedChecks.Clear(); inner.Method.Invoke(inner.Receiver, inner.Args.Skip(1).ToArray());
                Check(original.Count == 1 && CapturedChecks.Count == 1, "One active roll in both");
                SameArguments(original[0].Value, CapturedChecks[0].Value, rulesetCheck, "hidden Perception");
            }
        });
        Case("The Perception loop waits only for the opt-in setting", delegate {
            var functor = new FunctorSetGadgetConditionByAbilityCheck(); var context = Context(true);
            var acting = Character(SavingHero()); Set(acting, "alertPerception", true);
            Func<FunctorParametersDescription> make = () =>
            {
                var parameters = Functor(Check("Wisdom", "Perception", 20, RuleDefinitions.AdvantageType.None, 0, false), acting);
                var gadget = Make<WorldGadget>(); Set(gadget, "m_CachedPtr", new IntPtr(1));
                var game = Make<GameGadget>(); Set(game, "lightingInformationPerFlow", new Dictionary<int, GameGadget.LightingInformation>()); Set(gadget, "gameGadget", game);
                parameters.SourceGadget = gadget; parameters.BoolParameter = true;
                return parameters;
            };
            var routine = functor.Execute(make(), context);
            Check(routine.GetType().DeclaringType != typeof(OuterSites), "Off by default: the original functor");
            Plugin.Settings.HiddenPerception = true;
            int steps = 0; RuntimeRolls.Prepare = (o, site, m, a) => Wait(2, () => steps++);
            var parameters2 = make();
            routine = functor.Execute(parameters2, context);
            Check(routine.GetType().DeclaringType == typeof(OuterSites), "With the option on the loop waits for each active roll");
            var coroutine = new TA.Coroutine(); coroutine.Start(routine); CapturedChecks.Clear();
            coroutine.Run(); Check(steps == 1 && CapturedChecks.Count == 0, "It waits before the original check");
            for (int i = 0; i < 4 && !coroutine.IsFinished; i++) coroutine.Run();
            Check(CapturedChecks.Count == 1 && RuntimeRolls.Calls.Contains("observe:" + OuterSites.GadgetPerception + ":False") && coroutine.IsFinished, "Then the original check runs in its own native step: " + string.Join(",", RuntimeRolls.Calls));
            Check(parameters2.FailedCharactersAlternate.Contains(acting.Guid), "A failed search is recorded exactly as the original does");
            Check(functor.Execute(make(), Context(false)).GetType().DeclaringType != typeof(OuterSites), "A synchronously pumped functor keeps the original");
        });

        // ---- dialogue: the Insight read when answers are offered -----------
        var narrative = RuntimeRolls.FindOuterSite(OuterSites.NarrativeCheck);
        var startState = typeof(NarrativePlayer).GetMethod("StartState", Any);
        Case("Answer choices with a check roll the party's best Insight against DC 15", delegate {
            var direction = DispatchProxy.Create<INarrativeDirectionService, NarrativeDirection>();
            ServiceRepository.AddService<INarrativeDirectionService>(direction);
            try
            {
                var first = SavingHero(); var second = SavingHero();
                var speaker = World(first); var other = World(second);
                Func<string, NarrativeStateDescription> child = type => { var d = Make<NarrativeStateDescription>(); Set(d, "type", type); Set(d, "childrenNarrativeStates", new NarrativeStateDescription[0]); return d; };
                var state = Answers(new[] { speaker, other }, child("AbilityCheck"), child("NpcSpeech"));
                var inner = narrative.Resolve(Outer(OuterSites.NarrativeCheck, startState, Make<NarrativePlayer>(), state, null));
                Check(inner != null && inner.Method == rulesetCheck && ReferenceEquals(inner.Receiver, first), "The first party member with the best Insight rolls");
                Check(inner.Arg<int>("baseBonus") == 4 && inner.Arg<string>("abilityScoreName") == "Wisdom" && inner.Arg<string>("proficiencyName") == "Insight" && inner.Arg<int>("checkDC") == 15
                    && !inner.Arg<bool>("passive") && inner.Arg<int>("minRoll") == -1 && !inner.Arg<bool>("rollDie") && !inner.Arg<bool>("notify"), "Bonus, Wisdom (Insight), DC 15, an active roll shown without a die");
                Check(narrative.Titler(inner) == "Reading the conversation", "Its own heading");
                state = Answers(new[] { speaker, other }, child("NpcSpeech"), child("NpcSpeech"));
                Check(narrative.Resolve(Outer(OuterSites.NarrativeCheck, startState, Make<NarrativePlayer>(), state, null)) == null, "Answers without a check: no Insight read");
                var nested = child("CharacterSpeech"); Set(nested, "childrenNarrativeStates", new[] { child("AbilityCheck") });
                state = Answers(new[] { speaker }, nested);
                Check(narrative.Resolve(Outer(OuterSites.NarrativeCheck, startState, Make<NarrativePlayer>(), state, null)) != null, "A check behind a speech line counts, as the state's own search finds it");
            }
            finally { ServiceRepository.RemoveService<INarrativeDirectionService>(); }
        });

        // ---- travel crafting ---------------------------------------------
        Case("Travel crafting plans each awake crafter's check like the activity's own", delegate {
            var resting = DispatchProxy.Create<IGameRestingService, RestingService>();
            var games = DispatchProxy.Create<IGameService, GameService>();
            ServiceRepository.AddService<IGameRestingService>(resting); ServiceRepository.AddService<IGameService>(games);
            try
            {
                var crafter = SavingHero(); Set(crafter, "name", "Crafter");
                var sleeper = SavingHero(); Set(sleeper, "name", "Sleeper"); Set(sleeper, "remainingSleepTime", 2);
                ((GameService)(object)games).Heroes = new List<RulesetCharacterHero> { crafter, sleeper };
                Func<string, GameCampActivityCrafting> activity = name =>
                {
                    var tool = Make<ToolTypeDefinition>(); Set(tool, "mainAbilityScore", "Intelligence");
                    var item = Make<ItemDefinition>(); Set(item, "cachedName", "FixtureBlade");
                    var recipe = Make<RecipeDefinition>(); Set(recipe, "toolTypeDefinition", tool); Set(recipe, "craftingDC", 13); Set(recipe, "craftedItem", item);
                    var crafting = Make<GameCampActivityCrafting>(); Set(crafting, "heroName", name); Set(crafting, "recipeDefinition", recipe);
                    return crafting;
                };
                var working = activity("Crafter");
                ((RestingService)(object)resting).Activities = new List<GameCampActivity> { activity("Sleeper"), working };
                var call = DeferredRolls.ResolveTravelCrafting();
                Check(call != null && call.Parts != null && call.Parts.Count == 1 && ReferenceEquals(call.Parts[0].Args[0], crafter) && call.Parts[0].Title == "Crafting while travelling", "Only the awake crafter's check is planned");
                CapturedChecks.Clear(); working.Execute();
                Check(CapturedChecks.Count == 1, "The activity's own crafting check");
                var original = CapturedChecks[0].Value; CapturedChecks.Clear();
                call.Parts[0].Method.Invoke(crafter, call.Parts[0].Args.Skip(1).ToArray());
                SameArguments(original, CapturedChecks[0].Value, rulesetCheck, "travel crafting");
                Check((string)original[1] == "Intelligence" && (string)original[2] == "FixtureTool" && (int)original[6] == 13 && (int)original[0] == 5, "Best proficiency, ability, DC and bonus");
                ((RestingService)(object)resting).Activities = new List<GameCampActivity> { activity("Sleeper") };
                Check(DeferredRolls.ResolveTravelCrafting() == null, "Nobody awake to craft: no roll");
            }
            finally { ServiceRepository.RemoveService<IGameRestingService>(); ServiceRepository.RemoveService<IGameService>(); }
        });
        Case("Composite parts are planned in order and bound to their anchors", delegate {
            var one = new object(); var two = new object(); var hero = SavingHero();
            var first = typeof(RulesetActor).GetMethod("RollSavingThrow"); var second = typeof(RulesetCharacter).GetMethods(Any).First(m => m.Name == "RollAbilityCheck");
            RuntimeRolls.Planner = (target, planned) => { planned.AddActorDice(hero, RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.SavingThrow, 1, title: "Saving throw"); return true; };
            var parts = new[] { new DeferredRolls.RollPart(first, new object[] { hero }) { Anchor = one, Title = "One", Detail = "first detail" }, new DeferredRolls.RollPart(second, new object[] { hero }) { Anchor = two, Title = "Two" } };
            var plan = new NativeRollPlan();
            Check(CompositeRolls.TryPrepare(new NativeInvocation(new object(), "site", DeferredRolls.CompositeMethod, new object[] { parts }, 1), plan), "The composite identity is this planner's");
            Check(RuntimeRolls.Calls.SequenceEqual(new[] { "plan:RollSavingThrow", "plan:RollAbilityCheck" }), "Each part through the ordinary planner chain, in order");
            Check(plan.Leaves.Count == 2 && ReferenceEquals(plan.Leaves[0].Anchor, one) && ReferenceEquals(plan.Leaves[1].Anchor, two) && plan.Leaves[0].PartTarget.Method == first && plan.Leaves[1].PartTarget.Method == second, "Dice bound to their part's anchor and planned call");
            Check(plan.Leaves[0].Title == "One" && plan.Leaves[0].Note == "first detail" && plan.Leaves[1].Title == "Two", "Each part's own heading");
            Check(!CompositeRolls.TryPrepare(new NativeInvocation(new object(), "site", first, new object[] { hero }, 1), new NativeRollPlan()), "Other calls are not composite");
            var nested = new[] { new DeferredRolls.RollPart(DeferredRolls.CompositeMethod, new object[] { parts }) };
            bool refused = false; try { CompositeRolls.TryPrepare(new NativeInvocation(new object(), "site", DeferredRolls.CompositeMethod, new object[] { nested }, 1), new NativeRollPlan()); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "A composite inside a composite is refused");
        });
        Case("The game's own condition and effect processing names the anchor", delegate {
            var service = Make<RulesetImplementationManager>();
            ServiceRepository.AddService<IRulesetImplementationService>(service);
            Databases(true);
            try
            {
                var hero = SavingHero(); var condition = SavingCondition("Wisdom", 13); Hold(hero, condition);
                Set(condition.ConditionDefinition, "cachedName", "FixtureHeld"); Set(condition.ConditionDefinition, "features", new List<FeatureDefinition>());
                Set(condition.ConditionDefinition, "recurrentEffectForms", new List<EffectForm> { DamageEffectForm(RuleDefinitions.DieType.D4, 1, RuleDefinitions.EffectSavingThrowType.None) });
                RuntimeRolls.AllowManual = false;
                Call(typeof(RulesetActor), hero, "SaveToCancelCondition", condition);
                Call(typeof(RulesetActor), hero, "ExecuteRecurrentForms", condition);
                var effect = CastSpell(SavingHero(), Lingering(true, DamageEffectForm(RuleDefinitions.DieType.D8, 1, RuleDefinitions.EffectSavingThrowType.HalfDamage)), 1, 1);
                effect.ApplyEffectOnCharacter(hero, true, new TA.int3(0, 0, 0));
                Check(RuntimeRolls.Calls.Count(c => c == "anchor:RulesetCondition") == 2 && RuntimeRolls.Calls.Contains("anchor:RulesetEffectSpell") && RuntimeRolls.CurrentAnchor == null,
                    "A condition's save and recurring forms, and an effect's application, each set and restore their anchor: " + string.Join(",", RuntimeRolls.Calls));
            }
            finally { ServiceRepository.RemoveService<IRulesetImplementationService>(); Databases(false); }
        });
        // ---- character screens -------------------------------------------
        Case("Character creation asks for six 4d6 rolls and the game keeps the best three of each", delegate {
            Check(MenuRolls.SumBestThree(new[] { 1, 6, 4, 3 }) == 13 && MenuRolls.SumBestThree(new[] { 2, 2, 2, 2 }) == 6, "The lowest die is dropped");
            var panel = Make<CharacterStageAbilityScoresPanel>(); Set(panel, "m_CachedPtr", new IntPtr(1));
            Set(panel, "rollValues", new List<int> { 9, 9, 9, 9, 9, 9 }); Set(panel, "intDecreasingComparer", Make<IntDecreasingComparer>());
            typeof(CharacterStageAbilityScoresPanel).GetMethod("OnReRollCb").Invoke(panel, null);
            Check(((List<int>)Field(panel, "rollValues")).All(v => v == 9), "The reroll button asks first; the game's own roll does not run");
            var flows = (Queue<object>)null;
            var queue = typeof(MenuRolls).GetField("flows", Any).GetValue(null) as System.Collections.IEnumerable;
            var flow = queue.Cast<object>().Single();
            var prompts = (List<RollPrompt>)flow.GetType().GetField("Prompts", Any).GetValue(flow);
            Check(prompts.Count == 6 && prompts.All(p => p.Faces.Length == 4 && p.Faces.All(f => f == 6)), "Six prompts of four d6");
            typeof(CharacterStageAbilityScoresPanel).GetMethod("OnReRollCb").Invoke(panel, null);
            Check(queue.Cast<object>().Count() == 1, "A second click while asking changes nothing");
            var done = (Action<List<int[]>>)flow.GetType().GetField("Done", Any).GetValue(flow);
            done(new List<int[]> { new[] { 6, 6, 6, 1 }, new[] { 1, 1, 1, 1 }, new[] { 5, 4, 3, 2 }, new[] { 6, 5, 1, 1 }, new[] { 4, 4, 4, 4 }, new[] { 2, 3, 4, 5 } });
            Check(((List<int>)Field(panel, "rollValues")).SequenceEqual(new[] { 18, 12, 12, 12, 12, 3 }), "The original reroll sorts the entered totals: " + string.Join(",", (List<int>)Field(panel, "rollValues")));
            Check(Events.Contains("panel-reset") && Events.Contains("panel-refresh"), "The panel refreshes exactly as after a game roll");
            var sumArgs = new object[] { 4, RuleDefinitions.DieType.D6, 3, 0 };
            Check((bool)typeof(MenuRolls).GetMethod("SumBestPrefix", Any).Invoke(null, sumArgs) && (int)sumArgs[3] == 0, "Outside the replay the game's own dice are untouched");
        });
        Case("Rolled level-up hit points replace the game's average only when chosen", delegate {
            var hero = SavingHero(); var fighter = Make<CharacterClassDefinition>(); Set(fighter, "cachedName", "FixtureFighter"); Set(fighter, "guid", "fixture-fighter"); Set(fighter, "hitDice", RuleDefinitions.DieType.D10);
            Set(hero, "classesAndLevels", new Dictionary<CharacterClassDefinition, int> { { fighter, 2 } });
            Set(hero, "hitPointsGainHistory", new List<int> { 10, 6 });
            var add = typeof(MenuRolls).GetMethod("AddLevelPostfix", Any); var finalize = typeof(MenuRolls).GetMethod("FinalizePostfix", Any);
            add.Invoke(null, new object[] { hero, fighter });
            finalize.Invoke(null, new object[] { hero, false });
            Check(typeof(MenuRolls).GetField("flows", Any).GetValue(null) is System.Collections.IEnumerable && ((System.Collections.IEnumerable)typeof(MenuRolls).GetField("flows", Any).GetValue(null)).Cast<object>().Count() == 0, "Off by default: no prompt");
            Plugin.Settings.LevelUpHitPoints = true;
            add.Invoke(null, new object[] { hero, fighter });
            finalize.Invoke(null, new object[] { hero, false });
            MenuRolls.Update();
            Check(RollWindow.Shown != null && RollWindow.Shown.Faces.SequenceEqual(new[] { 10 }) && RollWindow.Shown.Note.Contains("6"), "One d10 prompt that names the game's average");
            RollWindow.Answer(9);
            Check(((List<int>)Field(hero, "hitPointsGainHistory")).SequenceEqual(new[] { 10, 9 }) && Events.Contains("hit-points"), "The level's entry becomes the rolled value and hit points are recomputed");
            Set(hero, "hitPointsGainHistory", new List<int> { 10, 6 }); Events.Clear();
            add.Invoke(null, new object[] { hero, fighter }); finalize.Invoke(null, new object[] { hero, false }); MenuRolls.Update();
            RollWindow.LetGameRoll();
            Check(((List<int>)Field(hero, "hitPointsGainHistory")).SequenceEqual(new[] { 10, 6 }) && !Events.Contains("hit-points"), "Let the game roll keeps the average");
            add.Invoke(null, new object[] { hero, fighter }); finalize.Invoke(null, new object[] { hero, true }); MenuRolls.Update();
            Check(RollWindow.Shown == null, "A character created at a higher level is not asked level by level");
            Set(fighter, "hitDice", RuleDefinitions.DieType.D10); Set(hero, "classesAndLevels", new Dictionary<CharacterClassDefinition, int> { { fighter, 1 } });
            add.Invoke(null, new object[] { hero, fighter }); finalize.Invoke(null, new object[] { hero, false }); MenuRolls.Update();
            Check(RollWindow.Shown == null, "The first level always takes the maximum");
            Plugin.Online = true; Set(hero, "classesAndLevels", new Dictionary<CharacterClassDefinition, int> { { fighter, 3 } });
            add.Invoke(null, new object[] { hero, fighter }); finalize.Invoke(null, new object[] { hero, false }); MenuRolls.Update();
            Check(RollWindow.Shown == null, "Never in multiplayer");
        });
        // @@CASES@@
        RunExtra08(harmony);
    }
    static FeatureDefinitionMagicAffinity MagicAffinity(RuleDefinitions.CastingAffinity casting, int dc, bool preserve, int cap, int threshold)
    {
        var feature = Make<FeatureDefinitionMagicAffinity>(); Set(feature, "cachedName", "FixtureMagicAffinity");
        Set(feature, "castingAffinity", casting); Set(feature, "spellcastingSuccessDC", dc);
        Set(feature, "preserveSlotRoll", preserve); Set(feature, "preserveSlotLevelCap", cap); Set(feature, "preserveSlotThreshold", threshold);
        return feature;
    }
    static RulesetCharacterHero HeroWith(params FeatureDefinition[] features)
    {
        var hero = SavingHero();
        ((Dictionary<string, List<FeatureDefinition>>)Field(hero, "activeFeatures"))["Fixture"] = features.ToList();
        return hero;
    }
    static RulesetAttribute Attribute(int value) { var attribute = Make<RulesetAttribute>(); Set(attribute, "currentValue", value); Set(attribute, "upToDate", true); return attribute; }
    static CharacterActionParams ActionParams(GameLocationCharacter acting, ActionDefinition definition)
    {
        var parameters = Make<CharacterActionParams>();
        Set(parameters, "actingCharacter", acting); Set(parameters, "actionDefinition", definition);
        Set(parameters, "actionModifiers", new List<ActionModifier> { new ActionModifier() }); Set(parameters, "targetCharacters", new List<GameLocationCharacter>());
        return parameters;
    }
    static GameLocationCharacter Hidden()
    {
        var hero = Actor<RulesetCharacterHero>(); Set(hero, "conditionsByCategory", new Dictionary<string, List<RulesetCondition>>());
        var foe = Actor<RulesetCharacterHero>(); Set(foe, "side", RuleDefinitions.Side.Enemy);
        var character = Character(hero); var detector = Character(foe);
        Set(character, "stealthy", true); character.CharactersInNoiseRange.Add(detector); detector.LineOfSightRatio[character] = 1f;
        return character;
    }
    static WorldLocationCharacter World(RulesetCharacter character)
    {
        var world = Make<WorldLocationCharacter>(); Set(world, "m_CachedPtr", new IntPtr(1)); Set(world, "gameCharacter", Character(character));
        return world;
    }
    static NarrativeStateAnswerChoice Answers(WorldLocationCharacter[] actors, params NarrativeStateDescription[] children)
    {
        var description = Make<NarrativeStateDescription>();
        var lines = new List<DialogLine>();
        for (int index = 0; index < children.Length; index++) { var line = new DialogLine(); Set(line, "role", "Speaker"); Set(line, "textLine", "Line" + index); lines.Add(line); }
        Set(description, "dialogLines", lines); Set(description, "childrenNarrativeStates", children); Set(description, "useExitLine", false);
        var sequence = Make<NarrativeSequence>(); Set(sequence, "playerActors", actors.ToList());
        var state = Make<NarrativeStateAnswerChoice>();
        Set(state, "narrativeStateDescription", description); Set(state, "narrativeSequence", sequence);
        Set(state, "playerRolesMap", new Dictionary<string, WorldLocationCharacter> { { "Speaker", actors[0] } });
        return state;
    }
    static bool CraftingProficiency(List<RuleDefinitions.TrendInfo> modifierTrends, out string selectedProficiency, out int bestAbilityCheckBonus)
    {
        selectedProficiency = "FixtureTool"; bestAbilityCheckBonus = 5;
        if (modifierTrends != null) modifierTrends.Add(new RuleDefinitions.TrendInfo(5, RuleDefinitions.FeatureSourceType.CharacterFeature, "Fixture", null));
        return false;
    }
    static bool CraftingHours(ref int __result) { __result = 4; return false; }
    static bool NoChoices(ref string[] __result) { __result = new string[0]; return false; }
    static bool RecordGadgetCondition(int conditionIndex, bool state) { Events.Add("gadget:" + state); return false; }
    // Harmony patches a method where it is declared, not where it is inherited.
    static MethodInfo Declared(MethodInfo method) { return method.DeclaringType.GetMethod(method.Name, Any, null, method.GetParameters().Select(p => p.ParameterType).ToArray(), null); }
    static bool RecordPanelReset() { Events.Add("panel-reset"); return false; }
    static bool RecordPanelRefresh() { Events.Add("panel-refresh"); return false; }
    static bool RecordHitPoints() { Events.Add("hit-points"); return false; }
    // @@HELPERS@@
    internal static readonly List<object[]> Collected = new List<object[]>();
    static void CollectSave(object[] __args) { Collected.Add((object[])__args.Clone()); }

    static EffectForm DamageEffectForm(RuleDefinitions.DieType die, int count, RuleDefinitions.EffectSavingThrowType saveAffinity)
    {
        var form = new EffectForm(); Set(form, "formType", EffectForm.EffectFormType.Damage); Set(form, "savingThrowAffinity", saveAffinity);
        var damage = new DamageForm(); Set(damage, "dieType", die); Set(damage, "diceNumber", count); Set(damage, "damageType", "DamageRadiant");
        Set(form, "damageForm", damage); return form;
    }
    static EffectDescription Lingering(bool save, params EffectForm[] forms)
    {
        var effect = new EffectDescription();
        Set(effect, "hasSavingThrow", save); Set(effect, "savingThrowAbility", "Wisdom"); Set(effect, "effectForms", forms.ToList());
        Set(effect, "recurrentEffect", RuleDefinitions.RecurrentEffect.OnTurnStart | RuleDefinitions.RecurrentEffect.OnEnter);
        Set(effect, "targetType", RuleDefinitions.TargetType.Sphere); Set(effect, "rangeType", RuleDefinitions.RangeType.Self);
        Set(effect, "difficultyClassComputation", RuleDefinitions.EffectDifficultyClassComputation.FixedValue); Set(effect, "fixedSavingThrowDifficultyClass", 14);
        return effect;
    }
    static RulesetEffectSpell CastSpell(RulesetCharacter caster, EffectDescription description, int spellLevel, int slotLevel)
    {
        var spell = Make<SpellDefinition>(); Set(spell, "cachedName", "FixtureLingeringSpell"); Set(spell, "spellLevel", spellLevel); Set(spell, "effectDescription", description);
        Set(spell, "schoolOfMagic", "SchoolEvocation");
        var effect = Make<RulesetEffectSpell>();
        Set(effect, "spellDefinition", spell); Set(effect, "caster", caster); Set(effect, "slotLevel", slotLevel);
        Set(effect, "featuresToBrowse", new List<FeatureDefinition>());
        typeof(RulesetEffect).GetField("featuresToBrowse", Any).SetValue(effect, new List<FeatureDefinition>());
        Set(effect, "featuresOrigin", new Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin>());
        Set(effect, "accountedProviders", new List<ISavingThrowAffinityProvider>()); Set(effect, "trackedConditionGuids", new List<ulong>());
        return effect;
    }
    // Every field of the native forms parameter record agrees.
    static void SameFormsParams(object expected, object actual, string label)
    {
        foreach (var field in expected.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object a = field.GetValue(expected), b = field.GetValue(actual);
            if (a == null || b == null || a.GetType().IsValueType || a is string) Check(Equals(a, b), label + ": forms parameter " + field.Name + " expected " + a + " actual " + b);
            else Check(ReferenceEquals(a, b), label + ": forms parameter reference " + field.Name);
        }
    }
    static RulesetCharacterHero SavingHero()
    {
        var hero = FeaturedHero();
        Set(hero, "actionModifier", new ActionModifier()); Set(hero, "dummyEffectForms", new List<EffectForm>());
        Set(hero, "accountedProviders", new List<ISavingThrowAffinityProvider>()); Set(hero, "matchingInterruptionConditions", new List<RulesetCondition>());
        Set(hero, "matchingOccurenceConditions", new List<RulesetCondition>());
        Set(hero, "spellsCastByMe", new List<RulesetEffectSpell>()); Set(hero, "powersUsedByMe", new List<RulesetEffectPower>());
        return hero;
    }

    // A battle at the end of a turn: the given character acts next (round 3).
    static GameLocationBattle Battle(GameLocationCharacter next)
    {
        var battle = Make<GameLocationBattle>();
        Set(battle, "initiativeSortedContenders", new List<GameLocationCharacter> { next });
        Set(battle, "enemyContenders", new List<GameLocationCharacter>()); Set(battle, "legendaryCandidates", new List<GameLocationCharacter>());
        Set(battle, "currentRound", 3); Set(battle, "activeContenderIndex", 0);
        var machine = new FiniteStateMachine();
        machine.RegisterState(Make<BattleState_TurnInitialize>());
        Set(machine, "currentState", Make<BattleState_TurnEnd>());
        Set(battle, "finiteStateMachine", machine);
        return battle;
    }
}
// Test-only narrative direction: no line has been explored yet.
public class NarrativeDirection : DispatchProxy
{
    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == "IsTextLineAlreadyExplored") return false;
        throw new NotSupportedException("Unexpected narrative call: " + targetMethod.Name);
    }
}
// Test-only resting service: the scheduled camp activities.
public class RestingService : DispatchProxy
{
    internal List<GameCampActivity> Activities = new List<GameCampActivity>();
    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == "get_ScheduledCampActivities") return Activities;
        throw new NotSupportedException("Unexpected resting call: " + targetMethod.Name);
    }
}
// Test-only game service: the heroes a camp activity looks up by name.
public class GameService : DispatchProxy
{
    internal List<RulesetCharacterHero> Heroes = new List<RulesetCharacterHero>();
    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == "EnumerateHeroes") { var list = (List<RulesetCharacterHero>)args[0]; list.Clear(); list.AddRange(Heroes); return null; }
        throw new NotSupportedException("Unexpected game service call: " + targetMethod.Name);
    }
}
