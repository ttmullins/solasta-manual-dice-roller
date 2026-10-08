using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TA;

namespace ManualDiceRoller
{
    // Rolls the game makes while a battle turn starts, while a lingering effect
    // is applied, and when damage ends a condition. All of them happen inside
    // one synchronous game call with no coroutine to suspend, so they use the
    // deferred queue (see DeferredRolls): the original call is skipped, the
    // game is held, the player enters every die the call will need, and the
    // original call is replayed once with those faces.
    //
    // The start of a turn is special: the game decides at once whether the
    // character can act, then starts the turn's state machine. Only deferring
    // a save or damage inside it would let a held character act or a dead one
    // start a turn. Instead the whole remainder of GameLocationBattle.NextTurn
    // (or StartFirstTurnOfRound) after the turn's character is chosen is
    // deferred: StartBattleTurn, the death check and the state change are
    // replayed together, exactly as the pinned original orders them.
    internal static class TurnRolls
    {
        const string NextTurnHash = "df1ef0a1366e492a2ebaed368145b66fe78a5c37b6d512491120c168fdf40452";
        const string FirstTurnHash = "95d8763d79c42f246d2e7c4c8fd4eddc21436b5e153aed4f76cb21f88e4afd5e";
        const string StartTurnHash = "4885b0d55cba6f10c07c6b0d401428b52927475ded687c7c2d2491de3078423e";
        const string RefreshTurnStartHash = "ad5372e659d52e8aae44f5d76a06e371615735bdc7442672ee6b48ee04e23ad3";
        const string OccurrenceHash = "111971b578c6b35cac1955bd535f34246c2b848e45fabc5d856ba291a71c307a";
        const string RecurrentFormsHash = "6446ad76e676b8f623aae1d4647a575f7425f30ab74d28e764b91fe8665547df";
        const string InterruptionHash = "ec1f1322df174c3840eaf69ffbbec0a2e7c87fdac34b64c235ca767fc62ad7bc";
        const string CheckRecurrentHash = "f5c35cb65142cf47b0890716f6b19678668befca5ffb60da68088573f3aac5a3";
        const string SpellApplyHash = "2d8bb83d444312abf5782cb6b3f0158293eaa74a7fbea4a45390f86247f76af4";
        const string PowerApplyHash = "68ebc813355b4cf4aa7013a17431a96482adb37ae1f4162830bb3aaff06243ac";
        const string RechargeHash = "858424a39fa212d8dc34911955a3baaed68f7e48eee689674d6c435a785d5b60";
        const string TurnInitializeHash = "6c4e6f0946fad0dfcbf741ba90edaa8d2d3233292229379020e93b9bc71cbc19";
        const string EnvironmentApplyHash = "48abc8dedd7b9944e405da0169684fce31cc5ed7bb87bd3d4d7254cd751843a2";

        internal const string TurnStartSite = "Battle.TurnStart";
        internal const string LingeringSite = "Effect.Lingering";
        internal const string RecurrentSite = "Condition.RecurrentForms";
        internal const string DamageSaveSite = "Condition.DamageSave";

        static MethodInfo nextTurn, firstTurn, startTurn, refreshTurnStart, occurrences, recurrentForms, interruptionMethod, checkRecurrent,
            spellApply, powerApply, effectSave, applyForms, rollSave, beginNext, beginFirst, recharge, turnInitialize, rawDie, rechargeRoll, behaviorRoll, environmentApply;
        static FieldInfo activeContender, initiative, matchingInterruption, accountedProviders;
        [ThreadStatic] static int startingTurn;

        internal static bool Preflight(out string error)
        {
            error = null;
            try
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                nextTurn = typeof(GameLocationBattle).GetMethod("NextTurn", any, null, Type.EmptyTypes, null);
                firstTurn = typeof(GameLocationBattle).GetMethod("StartFirstTurnOfRound", any, null, Type.EmptyTypes, null);
                startTurn = typeof(GameLocationCharacter).GetMethod("StartBattleTurn", any, null, new[] { typeof(int) }, null);
                refreshTurnStart = typeof(GameLocationCharacter).GetMethod("RefreshEffectsForBattleTurnStart", any, null, Type.EmptyTypes, null);
                occurrences = typeof(RulesetActor).GetMethod("ProcessConditionsMatchingOccurenceType", any, null, new[] { typeof(RuleDefinitions.TurnOccurenceType), typeof(ulong) }, null);
                recurrentForms = typeof(RulesetActor).GetMethod("ExecuteRecurrentForms", any, null, new[] { typeof(RulesetCondition) }, null);
                interruptionMethod = typeof(RulesetActor).GetMethod("ProcessConditionsMatchingInterruption", any, null, new[] { typeof(RuleDefinitions.ConditionInterruption), typeof(int) }, null);
                checkRecurrent = typeof(RulesetCharacter).GetMethod("CheckRecurrentEffects", any, null, new[] { typeof(List<RulesetEffect>), typeof(RuleDefinitions.RecurrentEffect), typeof(int3) }, null);
                spellApply = typeof(RulesetEffectSpell).GetMethod("ApplyEffectOnCharacter", any, null, new[] { typeof(RulesetActor), typeof(bool), typeof(int3) }, null);
                powerApply = typeof(RulesetEffectPower).GetMethod("ApplyEffectOnCharacter", any, null, new[] { typeof(RulesetActor), typeof(bool), typeof(int3) }, null);
                effectSave = typeof(RulesetEffect).GetMethod("TryRollSavingThrow", any);
                applyForms = typeof(RulesetImplementationManager).GetMethod("ApplyEffectForms", any);
                rollSave = typeof(RulesetActor).GetMethod("RollSavingThrow", any);
                recharge = typeof(RulesetCharacter).GetMethod("RechargePowersForTurnStart", any, null, Type.EmptyTypes, null);
                turnInitialize = typeof(BattleState_TurnInitialize).GetMethod("Begin", any, null, new[] { typeof(object[]) }, null);
                rawDie = typeof(RuleDefinitions).GetMethod("RollDie", BindingFlags.Static | BindingFlags.Public);
                rechargeRoll = typeof(TurnRolls).GetMethod(nameof(RechargeRoll), BindingFlags.Static | BindingFlags.NonPublic);
                environmentApply = typeof(RulesetEffectEnvironment).GetMethod("ApplyEffectOnCharacter", any, null, new[] { typeof(RulesetActor), typeof(bool), typeof(int3) }, null);
                behaviorRoll = typeof(TurnRolls).GetMethod(nameof(BehaviorRoll), BindingFlags.Static | BindingFlags.NonPublic);
                beginNext = typeof(TurnRolls).GetMethod(nameof(BeginNextTurn), BindingFlags.Static | BindingFlags.NonPublic);
                beginFirst = typeof(TurnRolls).GetMethod(nameof(BeginFirstTurn), BindingFlags.Static | BindingFlags.NonPublic);
                activeContender = AccessTools.Field(typeof(GameLocationBattle), "activeContender");
                initiative = AccessTools.Field(typeof(GameLocationBattle), "initiativeSortedContenders");
                matchingInterruption = AccessTools.Field(typeof(RulesetActor), "matchingInterruption");
                accountedProviders = AccessTools.Field(typeof(RulesetActor), "accountedProviders");
                if (nextTurn == null || firstTurn == null || startTurn == null || refreshTurnStart == null || occurrences == null || recurrentForms == null ||
                    interruptionMethod == null || checkRecurrent == null || spellApply == null || powerApply == null || effectSave == null || effectSave.GetParameters().Length != 8 ||
                    applyForms == null || applyForms.GetParameters().Length != 10 || rollSave == null || rollSave.GetParameters().Length != 10 ||
                    beginNext == null || beginFirst == null || activeContender == null || activeContender.FieldType != typeof(GameLocationCharacter) ||
                    initiative == null || initiative.FieldType != typeof(List<GameLocationCharacter>) || matchingInterruption == null || matchingInterruption.FieldType != typeof(bool) ||
                    accountedProviders == null || accountedProviders.FieldType != typeof(List<ISavingThrowAffinityProvider>) ||
                    recharge == null || turnInitialize == null || rawDie == null || rechargeRoll == null || behaviorRoll == null || environmentApply == null)
                    throw new InvalidOperationException("The native turn and effect methods changed.");
                Pin(nextTurn, NextTurnHash); Pin(firstTurn, FirstTurnHash); Pin(startTurn, StartTurnHash); Pin(refreshTurnStart, RefreshTurnStartHash);
                Pin(occurrences, OccurrenceHash); Pin(recurrentForms, RecurrentFormsHash); Pin(interruptionMethod, InterruptionHash); Pin(checkRecurrent, CheckRecurrentHash);
                Pin(spellApply, SpellApplyHash); Pin(powerApply, PowerApplyHash); Pin(recharge, RechargeHash); Pin(turnInitialize, TurnInitializeHash); Pin(environmentApply, EnvironmentApplyHash);
                return true;
            }
            catch (Exception failure) { error = failure.Message; return false; }
        }
        static void Pin(MethodBase method, string hash) { NativeMethodPin.Verify(method, hash); }
        internal static void Install(Harmony harmony)
        {
            string error;
            if (nextTurn == null && !Preflight(out error)) throw new InvalidOperationException(error);
            harmony.Patch(nextTurn, transpiler: Own(nameof(NextTurnTranspiler)));
            harmony.Patch(firstTurn, transpiler: Own(nameof(FirstTurnTranspiler)));
            harmony.Patch(startTurn, prefix: Own(nameof(StartTurnPrefix)), finalizer: Own(nameof(StartTurnFinalizer)));
            harmony.Patch(spellApply, prefix: Own(nameof(EffectPrefix)));
            harmony.Patch(powerApply, prefix: Own(nameof(EffectPrefix)));
            harmony.Patch(environmentApply, prefix: Own(nameof(EffectPrefix)));
            harmony.Patch(recurrentForms, prefix: Own(nameof(RecurrentPrefix)));
            harmony.Patch(interruptionMethod, prefix: Own(nameof(InterruptionPrefix)));
            // The turn start's own raw dice: a monster's recharge d6 and the
            // random behaviour die of a condition such as Confusion.
            harmony.Patch(recharge, prefix: Own(nameof(RechargeAnchor)), finalizer: Own(nameof(AnchorFinalizer)), transpiler: Own(nameof(RechargeTranspiler)));
            harmony.Patch(turnInitialize, prefix: Own(nameof(TurnInitializeAnchor)), finalizer: Own(nameof(AnchorFinalizer)), transpiler: Own(nameof(TurnInitializeTranspiler)));
        }
        static IEnumerable<CodeInstruction> FaceCall(IEnumerable<CodeInstruction> instructions, string face, string what)
        {
            var codes = instructions.ToList();
            int count = 0;
            foreach (var code in codes)
                if (code.opcode == OpCodes.Call && Equals(code.operand, rawDie)) { code.operand = typeof(TurnRolls).GetMethod(face, BindingFlags.Static | BindingFlags.Public); count++; }
            if (count != 1) throw new InvalidOperationException("The native " + what + " roll changed.");
            return codes;
        }
        static IEnumerable<CodeInstruction> RechargeTranspiler(IEnumerable<CodeInstruction> instructions) { return FaceCall(instructions, nameof(RechargeFace), "power recharge"); }
        static IEnumerable<CodeInstruction> TurnInitializeTranspiler(IEnumerable<CodeInstruction> instructions) { return FaceCall(instructions, nameof(BehaviorFace), "random behaviour"); }
        // Outside a deferred turn start these are the game's RollDie.
        public static int RechargeFace(RuleDefinitions.DieType diceType, RuleDefinitions.AdvantageType advantageType, out int firstRoll, out int secondRoll, float rollAlterationScore = 0f)
        { return RuntimeRolls.InvocationStaticFace(startTurn, diceType, advantageType, out firstRoll, out secondRoll, rollAlterationScore); }
        public static int BehaviorFace(RuleDefinitions.DieType diceType, RuleDefinitions.AdvantageType advantageType, out int firstRoll, out int secondRoll, float rollAlterationScore = 0f)
        { return RuntimeRolls.InvocationStaticFace(startTurn, diceType, advantageType, out firstRoll, out secondRoll, rollAlterationScore); }
        sealed class AnchorState { internal object Previous; }
        // A power recharge die belongs to the character recharging; the turn
        // initialisation's dice belong to that battle state.
        static void RechargeAnchor(RulesetCharacter __instance, out AnchorState __state) { __state = new AnchorState { Previous = RuntimeRolls.EnterAnchor(RechargeKey(__instance)) }; }
        static void TurnInitializeAnchor(BattleState_TurnInitialize __instance, out AnchorState __state) { __state = new AnchorState { Previous = RuntimeRolls.EnterAnchor(__instance) }; }
        static Exception AnchorFinalizer(Exception __exception, AnchorState __state) { if (__state != null) RuntimeRolls.ExitAnchor(__state.Previous); return __exception; }
        // A per-character anchor object that is not the character itself.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RulesetCharacter, object> rechargeKeys = new System.Runtime.CompilerServices.ConditionalWeakTable<RulesetCharacter, object>();
        internal static object RechargeKey(RulesetCharacter character) { return character == null ? null : rechargeKeys.GetValue(character, ignored => new object()); }
        static HarmonyMethod Own(string name) { return new HarmonyMethod(typeof(TurnRolls).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)); }

        // ---- The start of a battle turn ------------------------------------
        // NextTurn ends: ActiveContender.StartBattleTurn(round); then NextTurn()
        // if it died, else the TurnInitialize state. The call is replaced by
        // BeginNextTurn; when it defers, the original tail is skipped (its copy
        // of the character is popped) and replayed later by NextTurnTail.
        static IEnumerable<CodeInstruction> NextTurnTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        { return ReplaceStart(instructions, generator, beginNext, true); }
        static IEnumerable<CodeInstruction> FirstTurnTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        { return ReplaceStart(instructions, generator, beginFirst, false); }
        static IEnumerable<CodeInstruction> ReplaceStart(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodInfo begin, bool savedCopy)
        {
            var codes = instructions.ToList();
            int index = codes.FindIndex(code => (code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Call) && Equals(code.operand, startTurn));
            if (index < 0 || codes.FindLastIndex(code => (code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Call) && Equals(code.operand, startTurn)) != index || index + 1 >= codes.Count)
                throw new InvalidOperationException("The native turn start call changed.");
            var resume = generator.DefineLabel();
            codes[index + 1].labels.Add(resume);
            var replacement = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, begin),
                new CodeInstruction(OpCodes.Brfalse, resume),
            };
            if (savedCopy) replacement.Add(new CodeInstruction(OpCodes.Pop));
            replacement.Add(new CodeInstruction(OpCodes.Ret));
            var call = codes[index];
            replacement[0].labels.AddRange(call.labels); replacement[0].blocks.AddRange(call.blocks);
            codes.RemoveAt(index);
            codes.InsertRange(index, replacement);
            return codes;
        }
        // Returns true when the start of the turn was deferred.
        static bool BeginNextTurn(GameLocationCharacter character, int round, GameLocationBattle battle)
        {
            if (TryDeferTurnStart(character, round, battle, false)) return true;
            character.StartBattleTurn(round);
            return false;
        }
        static bool BeginFirstTurn(GameLocationCharacter character, int round, GameLocationBattle battle)
        {
            if (TryDeferTurnStart(character, round, battle, true)) return true;
            character.StartBattleTurn(round);
            return false;
        }
        static bool TryDeferTurnStart(GameLocationCharacter character, int round, GameLocationBattle battle, bool first)
        {
            try
            {
                if (!DeferredRolls.CanDefer || character == null || battle == null || character.RulesetCharacter == null) return false;
                if (!Plugin.Settings.RepeatSaves && !Plugin.Settings.LingeringEffects && !Plugin.Settings.Features) return false;
                var parts = TurnStartParts(character, battle);
                if (!AnyManual(parts)) return false;
                return DeferredRolls.TryDefer(TurnStartSite, startTurn, new object[] { character, round },
                    delegate {
                        if (!SameBattle(battle) || character.RulesetCharacter == null || character.RulesetCharacter.IsDead) return null;
                        var current = TurnStartParts(character, battle);
                        return current.Count == 0 ? null : new DeferredRolls.NativeCall(current);
                    },
                    delegate { if (first) FirstTurnTail(character, round, battle); else NextTurnTail(character, round, battle); });
            }
            catch (Exception error) { Plugin.Log("The start of the turn keeps the game's dice: " + error.Message); return false; }
        }
        static bool SameBattle(GameLocationBattle battle)
        {
            var service = ServiceRepository.GetService<IGameLocationBattleService>();
            return service != null && service.IsBattleInProgress && ReferenceEquals(service.Battle, battle);
        }
        // The pinned original tails, replayed once the dice are known.
        static void NextTurnTail(GameLocationCharacter character, int round, GameLocationBattle battle)
        {
            if (!SameBattle(battle)) return;
            // Only another deferred roll can have changed the battle meanwhile;
            // a character it killed is passed over exactly as NextTurn would.
            if (character.RulesetCharacter == null || character.RulesetCharacter.IsDead) { battle.NextTurn(); return; }
            character.StartBattleTurn(round);
            if (character.RulesetCharacter.IsDead) battle.NextTurn();
            else battle.FiniteStateMachine.RequestStateChange(typeof(BattleState_TurnInitialize), new object[0]);
        }
        static void FirstTurnTail(GameLocationCharacter character, int round, GameLocationBattle battle)
        {
            if (!SameBattle(battle)) return;
            if (character.RulesetCharacter == null || character.RulesetCharacter.IsDeadOrUnconscious) { battle.NextTurn(); return; }
            character.StartBattleTurn(round);
            var active = (GameLocationCharacter)activeContender.GetValue(battle);
            foreach (var other in (List<GameLocationCharacter>)initiative.GetValue(battle))
                if (active != other) other.StartBattleTurnOtherContender();
            active = (GameLocationCharacter)activeContender.GetValue(battle);
            if (active == null || active.RulesetCharacter == null || active.RulesetCharacter.IsDeadOrUnconscious) battle.NextTurn();
            else battle.FiniteStateMachine.RequestStateChange(typeof(BattleState_TurnInitialize), new object[0]);
        }
        // While a turn starts natively, nothing inside it may be deferred.
        static void StartTurnPrefix() { startingTurn++; }
        static Exception StartTurnFinalizer(Exception __exception) { if (startingTurn > 0) startingTurn--; return __exception; }

        // Everything StartBattleTurn rolls for the character, in its order:
        // start-of-turn conditions (their recurring forms, then the save to end
        // them), then lingering global and light effects that act at the start
        // of a turn. Controlled proxies keep the game's dice.
        internal static List<DeferredRolls.RollPart> TurnStartParts(GameLocationCharacter character, GameLocationBattle battle = null)
        {
            var parts = new List<DeferredRolls.RollPart>();
            var actor = character.RulesetCharacter;
            if (actor == null) return parts;
            AddConditionParts(parts, actor, RuleDefinitions.TurnOccurenceType.StartOfTurn);
            try { AddRechargeParts(parts, actor); }
            catch (Exception error) { Plugin.Log("Power recharge keeps the game's dice: " + error.Message); }
            if (Plugin.Settings.LingeringEffects)
            {
                AddRecurrentEffects(parts, character.AffectingGlobalEffects, actor, character.LocationPosition);
                AddRecurrentEffects(parts, character.AffectingLightEffects, actor, character.LocationPosition);
            }
            try { AddTurnInitializeParts(parts, actor, battle); }
            catch (Exception error) { Plugin.Log("The turn's behaviour roll keeps the game's dice: " + error.Message); }
            return parts;
        }
        // RulesetCharacter.RechargePowersForTurnStart: a d6 for each used power
        // that recharges on a 6 (or a 5–6), in the power list's order.
        static void AddRechargeParts(List<DeferredRolls.RollPart> parts, RulesetCharacter actor)
        {
            if (!Plugin.Settings.Features || actor.UsablePowers == null) return;
            foreach (var power in actor.UsablePowers.ToArray())
            {
                if (power == null || power.PowerDefinition == null || power.RemainingUses >= power.MaxUses) continue;
                var rate = power.PowerDefinition.RechargeRate;
                if (rate != RuleDefinitions.RechargeRate.D6_6 && rate != RuleDefinitions.RechargeRate.D6_56) continue;
                parts.Add(new DeferredRolls.RollPart(rechargeRoll, new object[] { actor, power, rate == RuleDefinitions.RechargeRate.D6_56 ? 5 : 6 })
                    { Anchor = RechargeKey(actor), Title = "Recharge", Detail = GameNames.SpellName(power.PowerDefinition) });
            }
        }
        // BattleState_TurnInitialize.Begin, right after the turn starts: the
        // first special-behaviour feature decides a random behaviour die
        // (Confusion) or a save to act normally.
        static void AddTurnInitializeParts(List<DeferredRolls.RollPart> parts, RulesetCharacter actor, GameLocationBattle battle)
        {
            if (!Plugin.Settings.RepeatSaves || battle == null || battle.FiniteStateMachine == null || battle.FiniteStateMachine.FiniteStates == null) return;
            FiniteState state;
            if (!battle.FiniteStateMachine.FiniteStates.TryGetValue(typeof(BattleState_TurnInitialize), out state) || !(state is BattleState_TurnInitialize)) return;
            var features = new List<FeatureDefinition>();
            using (var scope = NativeScratchScope.ForActor(actor)) actor.EnumerateFeaturesToBrowse<IActionPerformanceProvider>(features);
            IActionPerformanceProvider provider = null;
            foreach (IActionPerformanceProvider item in features) if (item.SpecialBehaviour != RuleDefinitions.SpecialBehaviour.None) { provider = item; break; }
            if (provider == null) return;
            var condition = actor.FindFirstConditionHoldingFeature(provider as FeatureDefinition);
            string name = condition == null ? "" : GameNames.SpellName(condition.ConditionDefinition);
            if (provider.SpecialBehaviour == RuleDefinitions.SpecialBehaviour.RandomBehaviour)
            {
                if (condition == null || provider.RandomBehaviourOptions == null || provider.RandomBehaviourOptions.Count == 0) return;
                parts.Add(new DeferredRolls.RollPart(behaviorRoll, new object[] { actor, provider.RandomBehaviorDie, BehaviorRanges(provider) })
                    { Anchor = state, Title = string.IsNullOrEmpty(name) ? "Random behaviour" : name, Detail = "What " + GameNames.ActorName(actor) + " does this turn" });
            }
            else if (provider.SpecialBehaviour == RuleDefinitions.SpecialBehaviour.SaveToActNormally && condition != null)
            {
                var save = SaveToActPart(actor, condition);
                if (save != null) { save.Anchor = state; parts.Add(save); }
            }
        }
        static string BehaviorRanges(IActionPerformanceProvider provider)
        {
            var text = new List<string>();
            int low = 1;
            foreach (var option in provider.RandomBehaviourOptions)
            {
                if (option == null || option.Weight <= 0) continue;
                int high = low + option.Weight - 1;
                string what = option.Behaviour == RuleDefinitions.RandomBehaviour.None ? "act normally" : RollText.Words(option.Behaviour.ToString()).ToLowerInvariant();
                if (option.Condition != null && option.Behaviour == RuleDefinitions.RandomBehaviour.ConditionDuringTurn) what = GameNames.SpellName(option.Condition);
                text.Add((low == high ? low.ToString() : low + "–" + high) + ": " + what);
                low = high + 1;
            }
            return string.Join(" · ", text.ToArray());
        }
        // RulesetEffect.ConditionSaveRerollRequested for the effect tracking
        // the condition: its saving throw ability and DC, the bearer's bonus.
        static DeferredRolls.RollPart SaveToActPart(RulesetCharacter actor, RulesetCondition condition)
        {
            var source = RulesetEntity.GetEntity<RulesetCharacter>(condition.SourceGuid);
            var effect = source == null ? null : source.FindEffectTrackingCondition(condition);
            if (effect == null || effect.EffectDescription == null) return null;
            string ability = effect.EffectDescription.SavingThrowAbility;
            if (string.IsNullOrEmpty(ability) || actor.IsAutomaticallyFailingSavingThrow(ability)) return null;
            var modifier = new ActionModifier();
            int bonus = 0;
            string school = effect is RulesetEffectSpell ? ((RulesetEffectSpell)effect).SpellDefinition.SchoolOfMagic : string.Empty;
            var accounted = new List<ISavingThrowAffinityProvider>();
            NativeProbe.Run(delegate {
                bonus = actor.ComputeBaseSavingThrowBonus(ability, modifier.SavingThrowModifierTrends);
                actor.ComputeSavingThrowModifier(ability, EffectForm.EffectFormType.Condition, effect.SourceDefinition != null ? effect.SourceDefinition.Name : string.Empty,
                    school, string.Empty, condition.ConditionDefinition.Name, string.Empty, modifier, accounted);
            }, null, new RulesetActor[] { actor });
            return new DeferredRolls.RollPart(rollSave, new object[] { actor, bonus, ability, effect.SourceDefinition, modifier.SavingThrowModifierTrends,
                modifier.SavingThrowAdvantageTrends, modifier.GetSavingThrowModifier(ability), effect.SaveDC, false, RuleDefinitions.RollOutcome.Neutral, 0 })
                { Title = "Save to act", Detail = GameNames.SpellName(condition.ConditionDefinition) };
        }
        // Planning identities; never called.
        static void RechargeRoll(RulesetCharacter actor, RulesetUsablePower power, int threshold) { }
        static void BehaviorRoll(RulesetCharacter actor, RuleDefinitions.DieType die, string ranges) { }
        internal static bool TryPrepare(NativeInvocation call, NativeRollPlan plan)
        {
            if (call == null) return false;
            if (Equals(call.Method, rechargeRoll))
            {
                var actor = call.Arg("actor") as RulesetCharacter; var power = call.Arg("power") as RulesetUsablePower;
                if (actor == null || power == null || !Plugin.Settings.Features) return true;
                plan.RequireManualState(() => Plugin.Settings.Features);
                int threshold = call.Arg<int>("threshold");
                plan.AddStaticDice(actor, RuleDefinitions.DieType.D6, 1, title: "Recharge",
                    note: GameNames.SpellName(power.PowerDefinition) + " recharges on " + (threshold == 5 ? "a 5 or 6" : "a 6"),
                    hint: "Roll a d6 for the power's recharge.", category: Plugin.FeatureDice);
                plan.Describe(threshold, false, 0);
                return true;
            }
            if (Equals(call.Method, behaviorRoll))
            {
                var actor = call.Arg("actor") as RulesetCharacter;
                if (actor == null || !Plugin.Settings.RepeatSaves) return true;
                plan.RequireManualState(() => Plugin.Settings.RepeatSaves);
                plan.AddStaticDice(actor, call.Arg<RuleDefinitions.DieType>("die"), 1, title: "Random behaviour", note: call.Arg<string>("ranges") ?? "",
                    hint: "The game picks this turn's behaviour from the result.", category: RuleDefinitions.RollContext.SavingThrow);
                return true;
            }
            return false;
        }
        static void AddRecurrentEffects(List<DeferredRolls.RollPart> parts, List<RulesetEffect> effects, RulesetCharacter actor, int3 position)
        {
            if (effects == null) return;
            foreach (var effect in effects.ToArray())
                if (effect != null && effect.EffectDescription != null && (effect.EffectDescription.RecurrentEffect & RuleDefinitions.RecurrentEffect.OnTurnStart) != 0)
                    AddEffectParts(parts, effect, actor, true, position);
        }
        // Conditions processed by ProcessConditionsMatchingOccurenceType for a
        // start-of-turn occurrence (no contextual source).
        static void AddConditionParts(List<DeferredRolls.RollPart> parts, RulesetActor actor, RuleDefinitions.TurnOccurenceType occurrence)
        {
            var categories = actor.ConditionsByCategory;
            if (categories == null) return;
            var matching = new List<RulesetCondition>();
            foreach (var pair in categories)
                if (pair.Value != null)
                    foreach (var condition in pair.Value)
                        if (condition != null && condition.EndOccurence == occurrence) matching.Add(condition);
            foreach (var condition in matching)
            {
                if (Plugin.Settings.LingeringEffects) AddRecurrentFormParts(parts, actor, condition);
                if (condition.HasFinished && !condition.IsDurationDefinedByEffect()) continue;
                if (condition.CanSaveToCancel && condition.HasSaveOverride && Plugin.Settings.RepeatSaves)
                {
                    var save = DeferredRolls.ResolveRepeatSavePart(actor, condition);
                    if (save != null) parts.Add(save);
                }
            }
        }
        // RulesetActor.ExecuteRecurrentForms: a condition's recurring forms
        // (burning, acid, regeneration) applied with the condition's source.
        internal static void AddRecurrentFormParts(List<DeferredRolls.RollPart> parts, RulesetActor actor, RulesetCondition condition)
        {
            var definition = condition.ConditionDefinition;
            if (definition == null || definition.RecurrentEffectForms == null || definition.RecurrentEffectForms.Count == 0) return;
            var service = ServiceRepository.GetService<IRulesetImplementationService>() as RulesetImplementationManager;
            if (service == null) return;
            var source = RulesetEntity.GetEntity<RulesetCharacter>(condition.SourceGuid);
            var formsParams = default(RulesetImplementationDefinitions.ApplyFormsParams);
            using (var scratch = new NativeScratchScope())
            {
                scratch.AddActor(actor); if (source != null) scratch.AddActor(source);
                formsParams.FillSourceAndTarget(source, actor);
                var effect = source == null ? null : source.FindEffectTrackingCondition(condition);
                if (effect != null) formsParams.FillFromActiveEffect(effect);
            }
            if (definition.AmountOrigin == ConditionDefinition.OriginOfAmount.AddDice) formsParams.addDice = condition.Amount;
            formsParams.formAbilityBonus = condition.SourceAbilityBonus;
            parts.Add(new DeferredRolls.RollPart(applyForms, new object[] { service, definition.RecurrentEffectForms, formsParams, null, false, false, false, false, false,
                RuleDefinitions.EffectApplication.All, null }) { Anchor = condition, Title = "Lingering damage", Detail = GameNames.SpellName(definition) });
        }
        // RulesetEffectSpell/Power.ApplyEffectOnCharacter: the target's save,
        // then the effect's forms. The forms are planned as if the save failed,
        // so every die the effect can need is asked for once; dice of forms a
        // successful save negates are simply not used.
        internal static void AddEffectParts(List<DeferredRolls.RollPart> parts, RulesetEffect effect, RulesetActor target, bool hasValidPosition, int3 position)
        {
            if (effect == null || target == null || effect.Terminated || effect.EffectDescription == null) return;
            var service = ServiceRepository.GetService<IRulesetImplementationService>() as RulesetImplementationManager;
            if (service == null) return;
            var spell = effect as RulesetEffectSpell; var power = effect as RulesetEffectPower;
            var environment = effect as RulesetEffectEnvironment;
            if (environment != null) { AddEnvironmentParts(parts, environment, target, service); return; }
            if (spell == null && power == null) return;
            RulesetCharacter caster = spell != null ? spell.Caster : power.User;
            var formsParams = default(RulesetImplementationDefinitions.ApplyFormsParams);
            using (var scratch = new NativeScratchScope())
            {
                scratch.AddActor(target); if (caster != null) scratch.AddActor(caster);
                if (spell != null)
                {
                    if (target.IsImmuneToSpell(spell.SpellDefinition)) return;
                    if (hasValidPosition && caster != null)
                    {
                        var located = GameLocationCharacter.GetFromActor(caster);
                        int total;
                        if (located != null && target.IsImmuneToSpellLevel(position, located.LocationPosition, spell.SpellDefinition.SpellLevel, out total)) return;
                    }
                }
                formsParams.FillSourceAndTarget(caster, target);
                formsParams.FillFromActiveEffect(effect);
            }
            if (spell != null)
            {
                int delta = spell.SpellDefinition.SpellLevel > 0 ? spell.SlotLevel - spell.SpellDefinition.SpellLevel : 0;
                var advancement = effect.EffectDescription.EffectAdvancement;
                formsParams.FillSpecialParameters(false, advancement.ComputeAdditionalDiceBySlotDelta(delta), advancement.ComputeAdditionalHPBySlotDelta(delta),
                    advancement.ComputeAdditionalTempHPBySlotDelta(delta), spell.SlotLevel, null, RuleDefinitions.RollOutcome.Neutral, 0, false, 0, 1, null);
                formsParams.effectSourceType = RuleDefinitions.EffectSourceType.Spell;
            }
            else
            {
                formsParams.FillSpecialParameters(false, 0, 0, 0, 0, null, RuleDefinitions.RollOutcome.Neutral, 0, false, 0, 1, null);
                formsParams.effectSourceType = RuleDefinitions.EffectSourceType.Power;
            }
            string name = spell != null ? GameNames.SpellName(spell.SpellDefinition) : GameNames.SpellName(power.PowerDefinition);
            if (effect.EffectDescription.HasSavingThrow && caster != null)
            {
                formsParams.rolledSaveThrow = true;
                formsParams.saveOutcome = RuleDefinitions.RollOutcome.Failure;
                parts.Add(new DeferredRolls.RollPart(effectSave, new object[] { effect, caster, caster.Side, target, new ActionModifier(), effect.EffectDescription.EffectForms,
                    false, RuleDefinitions.RollOutcome.Neutral, 0 }) { Anchor = effect, Title = "Saving throw", Detail = name });
            }
            parts.Add(new DeferredRolls.RollPart(applyForms, new object[] { service, effect.EffectDescription.EffectForms, formsParams, null, false, false, false, false, false,
                effect.EffectDescription.EffectApplication, effect.EffectDescription.EffectFormFilters }) { Anchor = effect, Title = "Lingering damage", Detail = name });
        }
        // RulesetEffectEnvironment.ApplyEffectOnCharacter: a hazard's save
        // (no caster, the target's opposing side), then its source-less forms.
        static void AddEnvironmentParts(List<DeferredRolls.RollPart> parts, RulesetEffectEnvironment effect, RulesetActor target, RulesetImplementationManager service)
        {
            if (!Plugin.Settings.Traps) return;
            var formsParams = default(RulesetImplementationDefinitions.ApplyFormsParams);
            using (var scratch = new NativeScratchScope())
            {
                scratch.AddActor(target);
                formsParams.FillSourceAndTarget(null, target);
                formsParams.FillFromActiveEffect(effect);
            }
            formsParams.FillSpecialParameters(false, effect.AddDice, 0, 0, 0, new ActionModifier(), RuleDefinitions.RollOutcome.Neutral, 0, false, 0, 1, null);
            formsParams.effectSourceType = RuleDefinitions.EffectSourceType.Environment;
            string name = effect.SourceDefinition == null ? "" : GameNames.SpellName(effect.SourceDefinition);
            if (effect.EffectDescription.HasSavingThrow)
            {
                formsParams.rolledSaveThrow = true;
                formsParams.saveOutcome = RuleDefinitions.RollOutcome.Failure;
                parts.Add(new DeferredRolls.RollPart(effectSave, new object[] { effect, null, RuleDefinitions.GetOpposingSide(target.Side), target, new ActionModifier(),
                    effect.EffectDescription.EffectForms, false, RuleDefinitions.RollOutcome.Neutral, 0 }) { Anchor = effect, Title = "Saving throw", Detail = name });
            }
            parts.Add(new DeferredRolls.RollPart(applyForms, new object[] { service, effect.EffectDescription.EffectForms, formsParams, null, false, false, false, false, false,
                effect.EffectDescription.EffectApplication, effect.EffectDescription.EffectFormFilters }) { Anchor = effect, Title = "Hazard damage", Detail = name });
        }
        // A cheap gate before deferring: is there any die the player rolls?
        static bool AnyManual(List<DeferredRolls.RollPart> parts)
        {
            foreach (var part in parts)
            {
                if (part.Method == applyForms)
                {
                    var p = (RulesetImplementationDefinitions.ApplyFormsParams)part.Args[2];
                    var forms = part.Args[1] as List<EffectForm>;
                    if (forms == null) continue;
                    if (p.sourceCharacter == null)
                    {
                        // Only hazards (environment effects) roll source-less dice manually.
                        if (p.effectSourceType == RuleDefinitions.EffectSourceType.Environment && Plugin.Settings.Traps &&
                            forms.Any(form => form != null && form.FormType == EffectForm.EffectFormType.Damage) && RuntimeRolls.CanManual(p.targetCharacter, Plugin.TrapDice)) return true;
                        continue;
                    }
                    foreach (var form in forms)
                    {
                        if (form == null) continue;
                        if (form.FormType == EffectForm.EffectFormType.Damage && RuntimeRolls.CanManual(p.sourceCharacter, RuleDefinitions.RollContext.MagicDamageValueRoll)) return true;
                        if (form.FormType == EffectForm.EffectFormType.Healing && RuntimeRolls.CanManual(p.sourceCharacter, RuleDefinitions.RollContext.HealValueRoll)) return true;
                        if (form.FormType == EffectForm.EffectFormType.TemporaryHitPoints && RuntimeRolls.CanManual(p.sourceCharacter, RuleDefinitions.RollContext.TemporaryHPValueRoll)) return true;
                    }
                }
                else if (part.Method == rechargeRoll)
                {
                    if (RuntimeRolls.CanManual(part.Args[0] as RulesetActor, Plugin.FeatureDice)) return true;
                }
                else if (part.Method == behaviorRoll)
                {
                    if (RuntimeRolls.CanManual(part.Args[0] as RulesetActor, RuleDefinitions.RollContext.SavingThrow)) return true;
                }
                else
                {
                    var target = part.Method == effectSave ? part.Args[3] as RulesetActor : part.Method == rollSave ? part.Args[0] as RulesetActor : part.Args.Length > 3 ? part.Args[3] as RulesetActor : null;
                    if (target != null && RuntimeRolls.CanManual(target, RuleDefinitions.RollContext.SavingThrow)) return true;
                }
            }
            return false;
        }

        // ---- Lingering effects outside the start of a turn ---------------
        // Entering or moving through an area (Spirit Guardians, Moonbeam,
        // Cloud of Daggers) and effects acting at the end of a turn. The
        // application waits; the movement or turn holds at its next step.
        static bool EffectPrefix(RulesetEffect __instance, RulesetActor targetCharacter, bool hasValidPosition, int3 targetPosition, MethodBase __originalMethod)
        {
            if (startingTurn > 0 || !DeferredRolls.CanDefer || !Plugin.Settings.LingeringEffects || targetCharacter == null || __instance == null) return true;
            try
            {
                var parts = new List<DeferredRolls.RollPart>();
                AddEffectParts(parts, __instance, targetCharacter, hasValidPosition, targetPosition);
                if (!AnyManual(parts)) return true;
                var effect = __instance; var target = targetCharacter; bool valid = hasValidPosition; var position = targetPosition; var method = (MethodInfo)__originalMethod;
                return !DeferredRolls.TryDefer(LingeringSite, method, new object[] { effect, target, valid, position },
                    delegate {
                        if (effect.Terminated) return null;
                        var current = new List<DeferredRolls.RollPart>();
                        AddEffectParts(current, effect, target, valid, position);
                        return current.Count == 0 ? null : new DeferredRolls.NativeCall(current);
                    },
                    delegate { if (!effect.Terminated) method.Invoke(effect, new object[] { target, valid, position }); });
            }
            catch (Exception error) { Plugin.Log("A lingering effect keeps the game's dice: " + error.Message); return true; }
        }
        // A condition's recurring forms at the end of a turn (the start of a
        // turn is part of the deferred turn start).
        static bool RecurrentPrefix(RulesetActor __instance, RulesetCondition rulesetCondition)
        {
            if (startingTurn > 0 || !DeferredRolls.CanDefer || !Plugin.Settings.LingeringEffects || __instance == null || rulesetCondition == null) return true;
            var occurrence = DeferredRolls.CurrentOccurrence;
            if (occurrence != RuleDefinitions.TurnOccurenceType.EndOfTurn && occurrence != RuleDefinitions.TurnOccurenceType.EndOfSourceTurn) return true;
            try
            {
                var parts = new List<DeferredRolls.RollPart>();
                AddRecurrentFormParts(parts, __instance, rulesetCondition);
                if (!AnyManual(parts)) return true;
                var actor = __instance; var condition = rulesetCondition;
                // The original applies them before it removes or saves against
                // the condition; the replay keeps that order in the queue.
                return !DeferredRolls.TryDefer(RecurrentSite, recurrentForms, new object[] { actor, condition },
                    delegate {
                        var current = new List<DeferredRolls.RollPart>();
                        AddRecurrentFormParts(current, actor, condition);
                        return current.Count == 0 ? null : new DeferredRolls.NativeCall(current);
                    },
                    delegate { recurrentForms.Invoke(actor, new object[] { condition }); });
            }
            catch (Exception error) { Plugin.Log("A condition's recurring damage keeps the game's dice: " + error.Message); return true; }
        }

        // ---- Saves when damage interrupts a condition -------------------
        // RulesetActor.ProcessConditionsMatchingInterruption rolls a save for
        // each condition that ends on damage only if the save succeeds (or
        // fails). The whole call waits; its saves are planned in its order.
        static bool InterruptionPrefix(RulesetActor __instance, RuleDefinitions.ConditionInterruption interruption, int amount)
        {
            if (startingTurn > 0 || !DeferredRolls.CanDefer || !Plugin.Settings.DamageSaves || !Plugin.Settings.SavingThrows || __instance == null) return true;
            if (interruption != RuleDefinitions.ConditionInterruption.Damaged && interruption != RuleDefinitions.ConditionInterruption.AttacksAndDamages &&
                interruption != RuleDefinitions.ConditionInterruption.DamagedByFriendly) return true;
            try
            {
                if ((bool)matchingInterruption.GetValue(__instance)) return true;
                if (!RuntimeRolls.CanManual(__instance, RuleDefinitions.RollContext.SavingThrow)) return true;
                var parts = InterruptionParts(__instance, interruption, amount);
                if (parts.Count == 0) return true;
                var actor = __instance; var kind = interruption; int value = amount;
                return !DeferredRolls.TryDefer(DamageSaveSite, interruptionMethod, new object[] { actor, kind, value },
                    delegate {
                        var current = InterruptionParts(actor, kind, value);
                        return current.Count == 0 ? null : new DeferredRolls.NativeCall(current);
                    },
                    delegate { interruptionMethod.Invoke(actor, new object[] { kind, value }); });
            }
            catch (Exception error) { Plugin.Log("A save against a condition keeps the game's dice: " + error.Message); return true; }
        }
        // The RollSavingThrow calls the original loop makes, in its order.
        internal static List<DeferredRolls.RollPart> InterruptionParts(RulesetActor actor, RuleDefinitions.ConditionInterruption interruption, int amount)
        {
            var parts = new List<DeferredRolls.RollPart>();
            if ((bool)matchingInterruption.GetValue(actor)) return parts;
            var seen = new HashSet<RulesetCondition>();
            var categories = actor.ConditionsByCategory;
            if (categories == null) return parts;
            foreach (var pair in categories.ToArray())
            {
                if (pair.Value == null) continue;
                foreach (var condition in pair.Value.ToArray())
                {
                    var definition = condition == null ? null : condition.ConditionDefinition;
                    if (definition == null || !definition.HasSpecialInterruptionOfType(interruption) || seen.Contains(condition)) continue;
                    if (interruption == RuleDefinitions.ConditionInterruption.NoAttackOrDamagedInTurn && condition.FirstRound) continue;
                    if (amount < definition.InterruptionDamageThreshold) continue;
                    seen.Add(condition);
                    if (!definition.InterruptionRequiresSavingThrow || string.IsNullOrEmpty(condition.SaveOverrideAbilityScoreName) &&
                        definition.InterruptionSavingThrowComputationMethod == RuleDefinitions.InterruptionSavingThrowComputationMethod.SaveOverride) continue;
                    var part = InterruptionSave(actor, condition, amount);
                    if (part != null) parts.Add(part);
                }
            }
            return parts;
        }
        static DeferredRolls.RollPart InterruptionSave(RulesetActor actor, RulesetCondition condition, int amount)
        {
            var definition = condition.ConditionDefinition;
            var modifier = new ActionModifier();
            string ability = string.Empty, school = string.Empty; int dc = 0;
            if (definition.InterruptionSavingThrowComputationMethod == RuleDefinitions.InterruptionSavingThrowComputationMethod.SaveOverride)
            { ability = condition.SaveOverrideAbilityScoreName; dc = condition.SaveOverrideDC; }
            else if (definition.InterruptionSavingThrowComputationMethod == RuleDefinitions.InterruptionSavingThrowComputationMethod.LikeConcentration)
            { ability = definition.InterruptionSavingThrowAbility; dc = Math.Max(10, (int)Math.Floor(amount / 2f)); }
            BaseDefinition source = null; SpellDefinition spell; FeatureDefinition feature;
            if (DatabaseRepository.GetDatabase<SpellDefinition>().TryGetElement(condition.SaveOverrideSourceName, out spell)) { source = spell; school = spell.SchoolOfMagic; }
            else if (DatabaseRepository.GetDatabase<FeatureDefinition>().TryGetElement(condition.SaveOverrideSourceName, out feature)) source = feature;
            else if (condition.EffectDefinitionName != null)
            {
                if (DatabaseRepository.GetDatabase<SpellDefinition>().TryGetElement(condition.EffectDefinitionName, out spell)) { source = spell; school = spell.SchoolOfMagic; }
                else if (DatabaseRepository.GetDatabase<FeatureDefinition>().TryGetElement(condition.EffectDefinitionName, out feature)) source = feature;
            }
            int bonus = 0;
            // Feature dice in this calculation are not rolled here: the probe
            // answers them with their minimum and restores every random state.
            var accounted = (List<ISavingThrowAffinityProvider>)accountedProviders.GetValue(actor);
            var saved = accounted == null ? null : accounted.ToArray();
            try
            {
                NativeProbe.Run(delegate {
                    bonus = actor.ComputeBaseSavingThrowBonus(ability, modifier.SavingThrowModifierTrends);
                    actor.ComputeSavingThrowModifier(ability, EffectForm.EffectFormType.Condition, string.Empty, school, string.Empty, definition.Name, string.Empty, modifier, accounted);
                }, null, new[] { actor });
            }
            finally { if (accounted != null) { accounted.Clear(); accounted.AddRange(saved); } }
            if (definition.InterruptionSavingThrowAffinity == RuleDefinitions.AdvantageType.Advantage)
                modifier.SavingThrowAdvantageTrends.Add(new RuleDefinitions.TrendInfo(1, RuleDefinitions.FeatureSourceType.Spell, condition.EffectDefinitionName, actor));
            else if (definition.InterruptionSavingThrowAffinity == RuleDefinitions.AdvantageType.Disadvantage)
                modifier.SavingThrowAdvantageTrends.Add(new RuleDefinitions.TrendInfo(-1, RuleDefinitions.FeatureSourceType.Spell, condition.EffectDefinitionName, actor));
            return new DeferredRolls.RollPart(rollSave, new object[] { actor, bonus, ability, source, modifier.SavingThrowModifierTrends, modifier.SavingThrowAdvantageTrends,
                modifier.GetSavingThrowModifier(ability), dc, false, RuleDefinitions.RollOutcome.Neutral, 0 })
                { Title = "Save to end a condition", Detail = GameNames.SpellName(definition) };
        }
    }
}
