using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Collections;
using HarmonyLib;

namespace ManualDiceRoller
{
    internal static class OuterSites
    {
        internal const string NarrativeCheck = "NarrativePlayer/<PlaySequenceAync>d__33:StartState:019d";
        internal const string GadgetCheck = "FunctorSetGadgetConditionByAbilityCheck/<Execute>d__0:ExecuteCheckOnCharacter:0142";
        internal const string GadgetSave = "FunctorSetGadgetConditionBySavingThrow/<Execute>d__0:ExecuteSaveOnCharacter:004e";
        internal const string SurpriseAttack = "CharacterActionStartBattle/<ExecuteImpl>d__7:TriggerBattleStart:01af";
        internal const string ActionStealth = "CharacterAction/<Execute>d__96:ApplyStealthBreakerBehavior:014b";
        static MethodInfo locationCheckWithRolls, locationCheck, trySave, investigationOverride, triggerBattle, stealthTurnEnd, stealthBreak,
            rulesetCheck, gadgetExecute, gadgetCheckOnCharacter, stealthFunctor;
        static FieldInfo characterLighting, narrativeSequence;
        internal const string TurnEndStealth = "GameLocationCharacter.RefreshStealthForBattleTurnEnd:ComputeStealthBreak";
        internal const string GadgetPerception = "FunctorSetGadgetConditionByAbilityCheck.Execute:Perception";
        internal const string GadgetStealth = "FunctorStealthBreaker.Execute:ComputeStealthBreak";
        const string StealthFunctorHash = "40820b27ed2bd10b07160fa817193cda994d6e398b2622bd960c24ae4f16daeb";
        internal static readonly string[] Ids = { NarrativeCheck, GadgetCheck, GadgetSave, SurpriseAttack, ActionStealth };
        const string StealthBreakHash = "825325d3be834216c079251b7bf6faca75fbbdb2823fd74048f3a1c28f3bd7d1";
        const string StealthBehaviorHash = "6da283082a7638b4b43a5e409b80306fa7131d4c0c1f0c6d941ad77d27f07a39";
        const string GadgetCheckOnCharacterHash = "fd1280e88276b495b14bbbb6d7edfbf86e51259e5b80393e67829de458688d61";
        const string AnswerChoiceBeginHash = "3f27923ec73cea7624e4252f2c8b40a50f13c0bbd877481d12324744be74a2ac";

        internal static void Register()
        {
            RuntimeRolls.RegisterOuterSite(new OuterSite(NarrativeCheck, true, ResolveNarrative) { Title = "Dialogue check", Titler = NarrativeTitle });
            RuntimeRolls.RegisterOuterSite(new OuterSite(GadgetCheck, true, ResolveGadgetCheck));
            RuntimeRolls.RegisterOuterSite(new OuterSite(GadgetSave, true, ResolveGadgetSave));
            RuntimeRolls.RegisterOuterSite(new OuterSite(SurpriseAttack, true, ResolveSurprise) { Title = "Surprise attack" });
            // Every action passes this point; only a hidden actor's stealth
            // check (and the Perception rolled against it) enters a step.
            RuntimeRolls.RegisterOuterSite(new OuterSite(ActionStealth, true, ResolveActionStealth) { SkipUnresolved = true });
        }

        internal static bool Preflight(out string error)
        {
            error = null;
            try
            {
                var parameters = typeof(GameLocationCharacter).GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(m => m.Name == "RollAbilityCheck").ToArray();
                locationCheckWithRolls = parameters.SingleOrDefault(m => m.GetParameters().Length == 16);
                locationCheck = parameters.SingleOrDefault(m => m.GetParameters().Length == 12);
                trySave = typeof(IRulesetImplementationService).GetMethod("TryRollSavingThrow");
                investigationOverride = typeof(NarrativeStateAbilityCheck).GetMethod("GetDcInvestigationOverride", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(string) }, null);
                triggerBattle = typeof(IGameLocationBattleService).GetMethod("TriggerBattleStart");
                if (triggerBattle == null) throw new InvalidOperationException("The native battle start signature changed.");
                Expect(triggerBattle, "attacker", "defender", "surprised", "fromAction");
                stealthTurnEnd = typeof(GameLocationCharacter).GetMethod("RefreshStealthForBattleTurnEnd", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                stealthBreak = typeof(GameLocationCharacter).GetMethod("ComputeStealthBreak", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(bool), typeof(ActionModifier), typeof(List<GameLocationCharacter>) }, null);
                if (stealthTurnEnd == null || stealthTurnEnd.ReturnType != typeof(IEnumerator) || stealthBreak == null || stealthBreak.ReturnType != typeof(bool))
                    throw new InvalidOperationException("The native turn-end stealth signatures changed.");
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                rulesetCheck = typeof(RulesetCharacter).GetMethods(any).SingleOrDefault(m => m.Name == "RollAbilityCheck" && m.GetParameters().Length == 18);
                gadgetExecute = typeof(FunctorSetGadgetConditionByAbilityCheck).GetMethod("Execute", any, null, new[] { typeof(FunctorParametersDescription), typeof(Functor.FunctorExecutionContext) }, null);
                gadgetCheckOnCharacter = typeof(FunctorSetGadgetConditionByAbilityCheck).GetMethod("ExecuteCheckOnCharacter", any, null, new[] { typeof(FunctorParametersDescription), typeof(Functor.FunctorExecutionContext), typeof(GameLocationCharacter) }, null);
                characterLighting = AccessTools.Field(typeof(GameLocationCharacter), "lightingState");
                narrativeSequence = AccessTools.Field(typeof(NarrativeState), "narrativeSequence");
                var behavior = typeof(CharacterAction).GetMethod("ApplyStealthBreakerBehavior", any, null, Type.EmptyTypes, null);
                var answerBegin = typeof(NarrativeStateAnswerChoice).GetMethod("Begin", any, null, new[] { typeof(object[]) }, null);
                stealthFunctor = typeof(FunctorStealthBreaker).GetMethod("Execute", any, null, new[] { typeof(FunctorParametersDescription), typeof(Functor.FunctorExecutionContext) }, null);
                var stealthIterator = typeof(FunctorStealthBreaker).GetNestedType("<Execute>d__0", BindingFlags.NonPublic);
                if (stealthFunctor == null || stealthIterator == null) throw new InvalidOperationException("The native stealth breaker functor changed.");
                NativeMethodPin.Verify(stealthIterator.GetMethod("MoveNext", any), StealthFunctorHash);
                if (rulesetCheck == null || gadgetExecute == null || gadgetExecute.ReturnType != typeof(IEnumerator) || gadgetCheckOnCharacter == null || gadgetCheckOnCharacter.ReturnType != typeof(bool) ||
                    characterLighting == null || narrativeSequence == null || narrativeSequence.FieldType != typeof(NarrativeSequence) || behavior == null || answerBegin == null)
                    throw new InvalidOperationException("The native stealth, Perception and dialogue signatures changed.");
                Expect(rulesetCheck, "baseBonus", "abilityScoreName", "proficiencyName", "modifierTrends", "advantageTrends", "rollModifier", "checkDC", "passive", "minRoll",
                    "diceRoll", "firstRoll", "secondRoll", "outcome", "successDelta", "rollDie", "notify", "displayDieOutcome", "minResult");
                NativeMethodPin.Verify(stealthBreak, StealthBreakHash); NativeMethodPin.Verify(behavior, StealthBehaviorHash);
                NativeMethodPin.Verify(gadgetCheckOnCharacter, GadgetCheckOnCharacterHash); NativeMethodPin.Verify(answerBegin, AnswerChoiceBeginHash);
                if (locationCheckWithRolls == null || locationCheck == null || trySave == null || trySave.GetParameters().Length != 21 || investigationOverride == null || investigationOverride.ReturnType != typeof(int))
                    throw new InvalidOperationException("The native dialog/gadget check signatures changed.");
                Expect(locationCheckWithRolls, "abilityScoreName", "proficiencyName", "checkDC", "baseAffinity", "checkModifier", "passive", "minRoll", "baseBonus", "checkRoll", "firstRoll", "secondRoll", "outcome", "successDelta", "rollDie", "notify", "displayDieOutcome");
                Expect(locationCheck, "abilityScoreName", "proficiencyName", "checkDC", "baseAffinity", "checkModifier", "passive", "minRoll", "outcome", "successDelta", "rollDie", "notify", "displayDieOutcome");
                Expect(trySave, "caster", "casterSide", "target", "actionModifier", "hasHitVisual", "hasSavingThrow", "savingThrowAbility", "saveDC", "disableSavingThrowOnAllies",
                    "advantageForEnemies", "ignoreCover", "featureSourceType", "effectForms", "savingThrowAffinitiesBySense", "savingThrowAffinitiesByFamily", "sourceName",
                    "sourceDefinition", "schoolOfMagic", "metamagicOption", "saveOutcome", "saveOutcomeDelta");
                return true;
            }
            catch (Exception failure) { error = failure.Message; return false; }
        }
        static void Expect(MethodInfo method, params string[] names)
        {
            var actual = method.GetParameters().Select(p => p.Name).ToArray();
            if (!actual.SequenceEqual(names)) throw new InvalidOperationException("Unsupported native parameters: " + method.DeclaringType.Name + "." + method.Name);
        }
        static NativeInvocation Inner(NativeInvocation outer, MethodInfo method, params object[] args)
        { return new NativeInvocation(outer.Owner, outer.Site, method, args, outer.Generation); }

        // NarrativePlayer.StartState(state, sequence) -> state.Begin() ->
        // GameLocationCharacter.RollAbilityCheck (16 parameters).
        static string NarrativeTitle(NativeInvocation target)
        { return target != null && target.Method == rulesetCheck && target.Arg<string>("proficiencyName") == "Insight" ? "Reading the conversation" : null; }
        static NativeInvocation ResolveNarrative(NativeInvocation outer)
        {
            var state = outer.Arg("state") as NarrativeState;
            if (state == null) return null;
            var answer = state as NarrativeStateAnswerChoice;
            if (answer != null) return ResolveProfiling(outer, answer);
            bool dialogCheck = state is NarrativeStateAbilityCheck;
            bool gameplayCheck = state is NarrativeStateAbilityCheckActiveGameplay;
            if (!dialogCheck && !gameplayCheck) return null;
            var description = state.NarrativeStateDescription;
            var check = description == null ? null : description.AbilityCheckDescription;
            if (check == null || state.PlayerRolesMap == null) return null;
            WorldLocationCharacter role;
            if (!state.PlayerRolesMap.TryGetValue(description.Role ?? "", out role) || role == null || role.GameLocationCharacter == null) return null;
            var settings = ServiceRepository.GetService<IGameSettingsService>();
            if ((settings != null && settings.ForceSuccessOnDialogRolls) || check.AutomaticSuccess) return null;
            int dc = check.DifficultyClass;
            if (dialogCheck && check.InvestigationDcOverride)
            {
                int replacement = (int)investigationOverride.Invoke(state, new object[] { check.Investigation, check.InvestigationSuspect });
                if (replacement > 0) dc = replacement;
            }
            // Dialog checks pass rollDie/notify false; gameplay checks pass !IsSkipped.
            bool notify = gameplayCheck && !state.IsSkipped;
            var character = role.GameLocationCharacter;
            return Inner(outer, locationCheckWithRolls, character, check.AbilityScoreName, check.ProficiencyName, dc, check.Affinity, new ActionModifier(),
                false, check.MinRoll, 0, 0, 0, 0, RuleDefinitions.RollOutcome.Failure, 0, notify, notify, true);
        }

        // NarrativeStateAnswerChoice.Begin: when an answer leads to a check, the
        // party's best Wisdom (Insight) is rolled once against DC 15 to show
        // the odds ("profiling"). This repeats the state's pure selection: the
        // exit line, which answers lead to a check, automatic results and the
        // party member with the best Insight.
        static NativeInvocation ResolveProfiling(NativeInvocation outer, NarrativeStateAnswerChoice state)
        {
            var description = state.NarrativeStateDescription;
            var sequence = narrativeSequence.GetValue(state) as NarrativeSequence;
            var direction = ServiceRepository.GetService<INarrativeDirectionService>();
            if (description == null || sequence == null || direction == null || state.PlayerRolesMap == null || description.DialogLines == null) return null;
            int exit = -1;
            if (description.UseExitLine)
            {
                bool explored = true; int last = description.DialogLines.Count - 1;
                for (int index = 0; index < description.DialogLines.Count; index++)
                {
                    var line = description.DialogLines[index];
                    if (state.PlayerRolesMap.ContainsKey(line.Role) && !direction.IsTextLineAlreadyExplored(line.TextLine) && index != last) explored = false;
                }
                if (description.AutomaticExitLine) { exit = last; if (explored) return null; }
                else if (!explored && !description.VisibleExitLine) exit = last;
            }
            bool check = false; int answerIndex = 0;
            var children = description.ChildrenNarrativeStates;
            for (int index = 0; index < description.DialogLines.Count && !check; index++)
            {
                if (index != exit && state.PlayerRolesMap.ContainsKey(description.DialogLines[index].Role) && children != null && children.Length > answerIndex &&
                    FindCheck(state, children[answerIndex]) != null) check = true;
                answerIndex++;
            }
            if (!check || sequence.PlayerActors == null) return null;
            foreach (var actor in sequence.PlayerActors)
            {
                var character = actor == null || actor.GameLocationCharacter == null ? null : actor.GameLocationCharacter.RulesetCharacter;
                if (character == null) continue;
                var features = new List<FeatureDefinition>();
                using (var scope = NativeScratchScope.ForActor(character)) character.EnumerateFeaturesToBrowse<IAbilityCheckAffinityProvider>(features);
                foreach (IAbilityCheckAffinityProvider feature in features)
                    if (feature.CanAutomaticallySucceed("Wisdom", "Insight", RuleDefinitions.AbilityCheckContext.DialogProfiling) ||
                        feature.CanAutomaticallyFail("Wisdom", "Insight", RuleDefinitions.AbilityCheckContext.DialogProfiling)) return null;
            }
            var settings = ServiceRepository.GetService<IGameSettingsService>();
            if (settings != null && settings.AlwaysDisplayDialogChances) return null;
            RulesetCharacter roller = null; int best = int.MinValue; List<RuleDefinitions.TrendInfo> trends = null;
            foreach (var actor in sequence.PlayerActors)
            {
                var character = actor.GameLocationCharacter.RulesetCharacter;
                int bonus = character.ComputeBaseAbilityCheckBonus("Wisdom", null, "Insight");
                var advantage = new List<RuleDefinitions.TrendInfo>(); var modifiers = new List<RuleDefinitions.TrendInfo>();
                var features = new List<FeatureDefinition>(); var origins = new Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin>();
                using (var scope = NativeScratchScope.ForActor(character)) character.EnumerateFeaturesToBrowse<IAbilityCheckAffinityProvider>(features, origins);
                foreach (var feature in features)
                {
                    int modifier;
                    ((IAbilityCheckAffinityProvider)feature).ComputeAbilityCheckAdvantageTrendAndModifier("Wisdom", "Insight", advantage, modifiers, out modifier, origins[feature], 0, LocationDefinitions.LightingState.Bright, 0);
                    bonus += modifier;
                }
                if (bonus > best) { roller = character; best = bonus; trends = advantage; }
            }
            if (roller == null) return null;
            return Inner(outer, rulesetCheck, roller, best, "Wisdom", "Insight", null, trends, 0, 15, false, -1, 0, 0, 0, RuleDefinitions.RollOutcome.Failure, 0, false, false, true, 0);
        }
        static NarrativeStateDescription FindCheck(NarrativeStateAnswerChoice state, NarrativeStateDescription child)
        {
            // The state's own recursive search, with the cache reset exactly as
            // Begin resets it before every answer.
            var cache = AccessTools.Field(typeof(NarrativeStateAnswerChoice), "narrativeStateDescriptionCache");
            var guard = AccessTools.Field(typeof(NarrativeStateAnswerChoice), "overflowGuard");
            var set = cache.GetValue(state) as HashSet<NarrativeStateDescription>;
            if (set == null) cache.SetValue(state, set = new HashSet<NarrativeStateDescription>());
            set.Clear(); guard.SetValue(state, 0);
            return state.FindAbilityCheckDescendant(child);
        }

        // FunctorSetGadgetConditionByAbilityCheck.ExecuteCheckOnCharacter for a
        // non-Perception check (the IL 0142 call). The native method always rolls
        // with ActingCharacters[0].
        static NativeInvocation ResolveGadgetCheck(NativeInvocation outer)
        {
            var parameters = outer.Arg("functorParameters") as FunctorParametersDescription;
            var context = outer.Arg("context") as Functor.FunctorExecutionContext;
            var acting = outer.Arg("actingCharacter") as GameLocationCharacter;
            if (parameters == null || context == null || acting == null || !context.Async || context.IsReplaying) return null;
            var check = parameters.AbilityCheck;
            if (check == null || check.ProficiencyName == "Perception") return null;
            if (parameters.ActingCharacters == null || parameters.ActingCharacters.Count == 0 || parameters.FailedCharacters == null) return null;
            var settings = ServiceRepository.GetService<IGameSettingsService>();
            if (parameters.FailedCharacters.Contains(acting.Guid) && (settings == null || !settings.AuthorizeRetryOnGadgets)) return null;
            int dc = check.DifficultyClass;
            if (parameters.VariableIntParameter1.behaviour != GadgetDefinitions.GadgetVariableBehaviour.Disabled)
                dc = parameters.SourceGadget.GetVariableOverride(parameters.VariableIntParameter1).valueInt;
            int minRoll = check.MinRoll;
            if (parameters.VariableIntParameter2.behaviour != GadgetDefinitions.GadgetVariableBehaviour.Disabled)
                minRoll = parameters.SourceGadget.GetVariableOverride(parameters.VariableIntParameter2).valueInt;
            var roller = parameters.ActingCharacters[0];
            if (roller == null) return null;
            return Inner(outer, locationCheck, roller, check.AbilityScoreName, check.ProficiencyName, dc, check.Affinity, new ActionModifier(),
                false, minRoll, RuleDefinitions.RollOutcome.Neutral, 0, !check.Silent, !check.Silent, true);
        }

        // ---- Stealth checks -------------------------------------------------
        // GameLocationCharacter.ComputeStealthBreak(roll: true, ...) is how a
        // hidden character stays hidden: in battle, on the hidden character's
        // own turn, every valid detector rolls Wisdom (Perception) (enemy dice:
        // planned only with enemy rolls on); then the hidden character rolls
        // Dexterity (Stealth) against the best result. It runs at the end of
        // the character's battle turn and after an action that can give the
        // character away. Both places wait for these dice inside their own
        // coroutine, then run the unchanged original call.
        //
        // RefreshStealthForBattleTurnEnd is exactly
        //   if (Stealthy && ComputeStealthBreak(true, new ActionModifier(), null))
        //       yield return TA.Coroutine.WaitForSeconds(2f);
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(stealthTurnEnd, prefix: new HarmonyMethod(typeof(OuterSites).GetMethod(nameof(StealthTurnEndPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(gadgetExecute, prefix: new HarmonyMethod(typeof(OuterSites).GetMethod(nameof(GadgetExecutePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(stealthFunctor, prefix: new HarmonyMethod(typeof(OuterSites).GetMethod(nameof(StealthFunctorPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
        }
        // FunctorStealthBreaker (a noisy lever, a creaking floor): every acting
        // character (or every conscious party member) checks whether it stays
        // hidden. The replacement repeats the loop and waits for each check.
        static bool StealthFunctorPrefix(FunctorStealthBreaker __instance, FunctorParametersDescription functorParameters, Functor.FunctorExecutionContext context, ref IEnumerator __result)
        {
            if (__instance == null || functorParameters == null || !functorParameters.BoolParameter || !Plugin.Enabled || Plugin.UnsupportedRulesMod || !Plugin.Settings.Ability || !Plugin.Settings.Stealth) return true;
            if (Plugin.Networked() && !RuntimeRolls.ExtendedOnlineActive) return true;
            __result = StealthFunctor(functorParameters);
            return false;
        }
        static IEnumerator StealthFunctor(FunctorParametersDescription functorParameters)
        {
            bool roll = functorParameters.BoolParameter;
            List<GameLocationCharacter> characters;
            bool party = functorParameters.ActingCharacters.Count <= 0;
            if (party)
            {
                var service = ServiceRepository.GetService<IGameLocationCharacterService>();
                if (service == null) yield break;
                characters = new List<GameLocationCharacter>(service.PartyCharacters);
            }
            else characters = new List<GameLocationCharacter>(functorParameters.ActingCharacters);
            foreach (var character in characters)
            {
                if (party && (character == null || !character.Valid || character.RulesetCharacter.IsDeadOrDyingOrUnconscious)) continue;
                var owner = new object();
                IEnumerator wait = null;
                try
                {
                    var parts = StealthParts(character, new ActionModifier(), null);
                    if (parts != null)
                        wait = RuntimeRolls.PrepareDeferredRoll(owner, GadgetStealth, stealthBreak, new object[] { character, roll, null, null }, DeferredRolls.CompositeMethod, new object[] { parts.ToArray() }, null, null);
                }
                catch (Exception error) { Plugin.Log("Manual preparation skipped at " + GadgetStealth + ": " + error.Message); wait = null; }
                if (wait != null) yield return wait;
                RuntimeRolls.EnterNative(owner, GadgetStealth);
                bool completed = false;
                try
                {
                    bool broken = character.ComputeStealthBreak(roll, new ActionModifier(), null);
                    RuntimeRolls.ObserveNativeValues(owner, GadgetStealth, broken, new object[0]);
                    completed = true;
                }
                finally { if (completed) RuntimeRolls.ExitNative(owner, GadgetStealth); else RuntimeRolls.AbortNative(owner); }
            }
        }
        static bool StealthTurnEndPrefix(GameLocationCharacter __instance, ref IEnumerator __result)
        {
            if (__instance == null || !Plugin.Enabled || Plugin.UnsupportedRulesMod || !Plugin.Settings.Ability || !Plugin.Settings.Stealth) return true;
            __result = StealthTurnEnd(__instance);
            return false;
        }
        static IEnumerator StealthTurnEnd(GameLocationCharacter character)
        {
            if (!character.Stealthy) yield break;
            var owner = new object();
            IEnumerator wait = null;
            List<DeferredRolls.RollPart> parts = null;
            try { parts = StealthParts(character, new ActionModifier(), null); }
            catch (Exception error) { Plugin.Log("Manual preparation skipped at " + TurnEndStealth + ": " + error.Message); }
            if (parts != null)
            {
                try { wait = RuntimeRolls.PrepareDeferredRoll(owner, TurnEndStealth, stealthBreak, new object[] { character, true, null, null }, DeferredRolls.CompositeMethod, new object[] { parts.ToArray() }, null, null); }
                catch (Exception error) { Plugin.Log("Manual preparation skipped at " + TurnEndStealth + ": " + error.Message); wait = null; }
            }
            if (wait != null) yield return wait;
            bool broken;
            RuntimeRolls.EnterNative(owner, TurnEndStealth);
            bool completed = false;
            try
            {
                broken = character.Stealthy && character.ComputeStealthBreak(true, new ActionModifier(), null);
                RuntimeRolls.ObserveNativeValues(owner, TurnEndStealth, broken, new object[0]);
                completed = true;
            }
            finally { if (completed) RuntimeRolls.ExitNative(owner, TurnEndStealth); else RuntimeRolls.AbortNative(owner); }
            if (broken) yield return TA.Coroutine.WaitForSeconds(2f);
        }
        // The stealth check of the hidden character at the end of its battle turn.
        internal static NativeInvocation ResolveTurnEndStealth(GameLocationCharacter character)
        {
            var parts = StealthParts(character, new ActionModifier(), null);
            return parts == null ? null : parts[parts.Count - 1].ToInvocation(TurnEndStealth);
        }
        // CharacterAction.ApplyStealthBreakerBehavior after the action itself.
        static NativeInvocation ResolveActionStealth(NativeInvocation outer)
        {
            if (!Plugin.Settings.Stealth || !Plugin.Settings.Ability) return null;
            var action = outer.Receiver as CharacterAction;
            var acting = action == null ? null : action.ActingCharacter;
            if (acting == null || !acting.Stealthy || action.ActionDefinition == null) return null;
            var behavior = action.ActionDefinition.StealthBreakerBehavior;
            if (behavior == ActionDefinitions.StealthBreakerBehavior.None) return null;
            if (behavior != ActionDefinitions.StealthBreakerBehavior.Roll && behavior != ActionDefinitions.StealthBreakerBehavior.RollIfTargets) return null;
            if (action.ActionParams.ActionModifiers.Count == 0) return null;
            List<GameLocationCharacter> targets = null;
            var service = ServiceRepository.GetService<IGameLocationBattleService>();
            if (behavior == ActionDefinitions.StealthBreakerBehavior.RollIfTargets)
            {
                if (service == null) return null;
                // The same target gathering the original performs next (it marks
                // the targets' groups as aggressed; doing so twice changes nothing).
                targets = new List<GameLocationCharacter>();
                service.GatherRelevantOffensiveActionTargets(action.ActionParams, targets);
                if (targets.Count == 0) return null;
            }
            var parts = StealthParts(acting, new ActionModifier(), targets);
            return parts == null ? null : new NativeInvocation(outer.Owner, outer.Site, DeferredRolls.CompositeMethod, new object[] { parts.ToArray() }, outer.Generation);
        }
        // The dice ComputeStealthBreak(roll: true, modifier, detectorsWithAdvantage)
        // makes, in its order; null when nothing would be rolled.
        internal static List<DeferredRolls.RollPart> StealthParts(GameLocationCharacter character, ActionModifier modifier, List<GameLocationCharacter> detectorsWithAdvantage)
        {
            if (character == null || !character.Stealthy || character.RulesetCharacter == null || character.CharactersInNoiseRange == null) return null;
            var detectors = new List<GameLocationCharacter>(character.CharactersInNoiseRange);
            if (detectorsWithAdvantage != null) foreach (var extra in detectorsWithAdvantage) if (!detectors.Contains(extra)) detectors.Add(extra);
            var parts = new List<DeferredRolls.RollPart>();
            var battle = ServiceRepository.GetService<IGameLocationBattleService>();
            bool contest = battle != null && battle.IsBattleInProgress && battle.Battle != null && battle.Battle.ActiveContender == character;
            bool any = false;
            string hidden = GameNames.ActorName(character.RulesetCharacter);
            foreach (var detector in detectors)
            {
                if (detector == null || !character.IsCharacterValidToAttemptStealthBreak(detector)) continue;
                any = true;
                if (!contest) continue;
                var ruleset = detector.RulesetCharacter;
                var check = new ActionModifier();
                int baseBonus = 0, passive = 0;
                bool light = false;
                var advantage = detectorsWithAdvantage != null && detectorsWithAdvantage.Contains(detector);
                NativeProbe.Run(delegate {
                    detector.ComputePassivePerceptionOnTarget(character, out light);
                    passive = 10 + ruleset.ComputeBaseAbilityCheckBonus("Wisdom", null, "Perception");
                    check.Reset();
                    baseBonus = ruleset.ComputeBaseAbilityCheckBonus("Wisdom", check.AbilityCheckModifierTrends, "Perception");
                    if (advantage) check.AbilityCheckAdvantageTrends.Add(new RuleDefinitions.TrendInfo(1, RuleDefinitions.FeatureSourceType.CharacterFeature, "Unknown", null));
                    if (light) check.AbilityCheckAdvantageTrends.Add(new RuleDefinitions.TrendInfo(-1, RuleDefinitions.FeatureSourceType.Lighting, characterLighting.GetValue(character).ToString(), null));
                    detector.ComputeAbilityCheckActionModifier("Wisdom", "Perception", check);
                    character.ComputeAbilityCheckActionModifier("Wisdom", "Perception", check, 16);
                }, null, new RulesetActor[] { ruleset, character.RulesetCharacter });
                parts.Add(new DeferredRolls.RollPart(rulesetCheck, new object[] { ruleset, baseBonus, "Wisdom", "Perception", check.AbilityCheckModifierTrends, check.AbilityCheckAdvantageTrends,
                    check.AbilityCheckModifier, 0, false, 0, 0, 0, 0, RuleDefinitions.RollOutcome.Success, 0, true, false, false, passive })
                    { Title = "Spotting a hidden creature", Detail = hidden });
            }
            if (!any) return null;
            var stealth = new ActionModifier();
            stealth.Reset();
            var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
            if (positioning != null && positioning.IsNextToWall(character.LocationPosition))
            {
                // Feature dice in this preparation are not rolled here: the probe
                // gives them their minimum and restores every random state.
                var prepared = stealth;
                NativeProbe.Run(delegate { character.ComputeAbilityCheckActionModifier("Dexterity", "Stealth", prepared, 32); }, null, new RulesetActor[] { character.RulesetCharacter });
            }
            // The DC is the detectors' best result, rolled just before.
            parts.Add(new DeferredRolls.RollPart(locationCheck, new object[] { character, "Dexterity", "Stealth", 0, RuleDefinitions.AdvantageType.None, stealth,
                false, -1, RuleDefinitions.RollOutcome.Success, 0, true, true, true }) { Title = "Staying hidden" });
            return parts;
        }

        // ---- Optional: Perception against hidden gadgets ------------------
        // FunctorSetGadgetConditionByAbilityCheck.Execute's Perception branch
        // tries each acting character in a loop (a native finally region). The
        // replacement repeats that loop and waits before each character's
        // original ExecuteCheckOnCharacter whenever it would actually roll.
        static bool GadgetExecutePrefix(FunctorSetGadgetConditionByAbilityCheck __instance, FunctorParametersDescription functorParameters, Functor.FunctorExecutionContext context, ref IEnumerator __result)
        {
            if (__instance == null || functorParameters == null || context == null || !Plugin.Enabled || Plugin.UnsupportedRulesMod) return true;
            if (!Plugin.Settings.HiddenPerception || !Plugin.Settings.Ability || !context.Async || context.IsReplaying) return true;
            if (functorParameters.ActingCharacters == null || functorParameters.ActingCharacters.Count == 0 || functorParameters.AbilityCheck == null ||
                functorParameters.AbilityCheck.ProficiencyName != "Perception") return true;
            __result = PerceptionLoop(__instance, functorParameters, context);
            return false;
        }
        static IEnumerator PerceptionLoop(FunctorSetGadgetConditionByAbilityCheck functor, FunctorParametersDescription parameters, Functor.FunctorExecutionContext context)
        {
            foreach (var acting in parameters.ActingCharacters.ToArray())
            {
                if (acting.RulesetCharacter.CanRevealHiddenObjects())
                {
                    var gadget = !parameters.BoolParameter ? parameters.TargetGadget : parameters.SourceGadget;
                    int index = Array.IndexOf(gadget.ConditionChoices(), parameters.TargetConditionState.name);
                    gadget.GameGadget.SetCondition(index, parameters.TargetConditionState.state, parameters.ActingCharacters);
                    yield break;
                }
                var owner = new object();
                IEnumerator wait = null;
                NativeInvocation target = null;
                try { target = ResolveGadgetPerception(parameters, acting); }
                catch (Exception error) { Plugin.Log("Manual preparation skipped at " + GadgetPerception + ": " + error.Message); }
                if (target != null)
                {
                    try { wait = RuntimeRolls.PrepareDeferredRoll(owner, GadgetPerception, gadgetCheckOnCharacter, new object[] { functor, parameters, context, acting }, target.Method, target.Args, "Searching", null); }
                    catch (Exception error) { Plugin.Log("Manual preparation skipped at " + GadgetPerception + ": " + error.Message); wait = null; }
                }
                if (wait != null) yield return wait;
                bool found;
                RuntimeRolls.EnterNative(owner, GadgetPerception);
                bool completed = false;
                try
                {
                    found = (bool)gadgetCheckOnCharacter.Invoke(functor, new object[] { parameters, context, acting });
                    RuntimeRolls.ObserveNativeValues(owner, GadgetPerception, found, new object[0]);
                    completed = true;
                }
                catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
                finally { if (completed) RuntimeRolls.ExitNative(owner, GadgetPerception); else RuntimeRolls.AbortNative(owner); }
                if (found) yield break;
            }
        }
        // ExecuteCheckOnCharacter's pure decision: a retry rule, the passive
        // score (with the lighting and preparation it applies first) and only
        // then an active roll by the first acting character.
        internal static NativeInvocation ResolveGadgetPerception(FunctorParametersDescription parameters, GameLocationCharacter acting)
        {
            var check = parameters.AbilityCheck;
            var failed = acting.AlertPerception ? parameters.FailedCharactersAlternate : parameters.FailedCharacters;
            var settings = ServiceRepository.GetService<IGameSettingsService>();
            if (failed != null && failed.Contains(acting.Guid) && (settings == null || !settings.AuthorizeRetryOnGadgets)) return null;
            int dc = check.DifficultyClass;
            if (parameters.VariableIntParameter1.behaviour != GadgetDefinitions.GadgetVariableBehaviour.Disabled)
                dc = parameters.SourceGadget.GetVariableOverride(parameters.VariableIntParameter1).valueInt;
            int minRoll = check.MinRoll;
            if (parameters.VariableIntParameter2.behaviour != GadgetDefinitions.GadgetVariableBehaviour.Disabled)
                minRoll = parameters.SourceGadget.GetVariableOverride(parameters.VariableIntParameter2).valueInt;
            var modifier = new ActionModifier();
            bool passive = false;
            if (!check.IgnorePassive)
            {
                passive = !acting.AlertPerception;
                GameGadget.LightingInformation lighting;
                if (parameters.SourceGadget != null && parameters.SourceGadget.GameGadget != null &&
                    parameters.SourceGadget.GameGadget.LightingInformationPerFlow.TryGetValue(parameters.ListenerIndex, out lighting))
                {
                    float distance = float.MaxValue;
                    foreach (var position in lighting.perceptionBox.EnumerateAllPositionsWithin())
                        distance = Math.Min(distance, (acting.LocationPosition - position).magnitude);
                    acting.ComputeLightingModifierForLightingState(distance, lighting.lightingState, modifier);
                }
            }
            if (settings != null && settings.AutoDetectTraps) return null;
            if (!passive && !check.IgnorePassive)
            {
                int score = 10 + acting.RulesetCharacter.ComputeBaseAbilityCheckBonus(check.AbilityScoreName, null, check.ProficiencyName);
                var prepared = modifier;
                NativeProbe.Run(delegate { acting.PrepareActionModifier(check.AbilityScoreName, check.ProficiencyName, check.Affinity, prepared, 8); }, null, new RulesetActor[] { acting.RulesetCharacter });
                var advantage = RuleDefinitions.ComputeAdvantage(modifier.AbilityCheckAdvantageTrends);
                if (advantage == RuleDefinitions.AdvantageType.Advantage) score += 5;
                else if (advantage == RuleDefinitions.AdvantageType.Disadvantage) score -= 5;
                if (score >= dc) return null;
            }
            // A passive roll uses no die.
            if (passive) return null;
            var roller = parameters.ActingCharacters[0];
            if (roller == null || roller.RulesetCharacter == null) return null;
            return new NativeInvocation(new object(), GadgetPerception, locationCheck, new object[] { roller, check.AbilityScoreName, check.ProficiencyName, dc, check.Affinity, modifier,
                false, minRoll, RuleDefinitions.RollOutcome.Neutral, 0, !check.Silent, !check.Silent, true }, 0);
        }

        // CharacterActionStartBattle -> IGameLocationBattleService.TriggerBattleStart
        // -> GameLocationBattle.ComputeSurprisedEnemies: a surprise attack from
        // a hidden attacker rolls the attacker's Dexterity (Stealth) once,
        // against which each enemy then rolls Perception (enemy dice stay
        // native unless they are planned elsewhere).
        static NativeInvocation ResolveSurprise(NativeInvocation outer)
        {
            var service = outer.Receiver as IGameLocationBattleService;
            var attacker = outer.Arg("attacker") as GameLocationCharacter;
            var defender = outer.Arg("defender") as GameLocationCharacter;
            if (service == null || attacker == null || defender == null || service.IsBattleInProgress) return null;
            if (!outer.Arg<bool>("surprised") || !outer.Arg<bool>("fromAction")) return null;
            if (defender.Side == RuleDefinitions.Side.Ally || attacker.RulesetCharacter == null) return null;
            var modifier = new ActionModifier();
            modifier.Reset();
            var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
            if (positioning != null && positioning.IsNextToWall(attacker.LocationPosition))
            {
                // Feature dice in this preparation are not rolled here: the probe
                // gives them their minimum and restores every random state.
                var prepared = modifier;
                NativeProbe.Run(delegate { attacker.ComputeAbilityCheckActionModifier("Dexterity", "Stealth", prepared, 32); }, null, new RulesetActor[] { attacker.RulesetCharacter });
            }
            return Inner(outer, locationCheck, attacker, "Dexterity", "Stealth", 0, RuleDefinitions.AdvantageType.None, modifier,
                false, -1, RuleDefinitions.RollOutcome.Success, 0, false, true, false);
        }

        // FunctorSetGadgetConditionBySavingThrow.ExecuteSaveOnCharacter: a trap
        // or hazard save through the ordinary native save calculation.
        static NativeInvocation ResolveGadgetSave(NativeInvocation outer)
        {
            var parameters = outer.Arg("functorParameters") as FunctorParametersDescription;
            var context = outer.Arg("context") as Functor.FunctorExecutionContext;
            if (parameters == null || context == null || !context.Async || context.IsReplaying) return null;
            var check = parameters.AbilityCheck;
            if (check == null || parameters.ActingCharacters == null || parameters.ActingCharacters.Count == 0 || parameters.ActingCharacters[0] == null) return null;
            var target = parameters.ActingCharacters[0].RulesetCharacter;
            var service = ServiceRepository.GetService<IRulesetImplementationService>();
            if (target == null || service == null) return null;
            int dc = check.DifficultyClass;
            if (parameters.VariableIntParameter1.behaviour != GadgetDefinitions.GadgetVariableBehaviour.Disabled)
                dc = parameters.SourceGadget.GetVariableOverride(parameters.VariableIntParameter1).valueInt;
            return Inner(outer, trySave, service, null, RuleDefinitions.Side.Enemy, target, new ActionModifier(), false, true, check.AbilityScoreName, dc,
                false, false, false, RuleDefinitions.FeatureSourceType.Base, new List<EffectForm>(), null, null, string.Empty, parameters.GadgetDefinition,
                string.Empty, null, RuleDefinitions.RollOutcome.Neutral, 0);
        }
    }
}
