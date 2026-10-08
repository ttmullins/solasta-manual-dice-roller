using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ManualDiceRoller
{
    // Rolls that the game makes synchronously, with no coroutine to suspend:
    // concentration checks inside damage, travel foraging, camp crafting and
    // scribing, bestiary monster identification and repeat saving throws at
    // the end of a turn. The original call is skipped; the game's action
    // coroutines, battle state machine, banter and world-map clock are held;
    // the player enters the dice; then the very same original method is run
    // again with the entered faces. Planning is done from the original
    // calculation when the entry reaches the front of the queue. Disabling the
    // mod, choosing "Let the game roll", or any planning failure replays the
    // original method with the game's own dice.
    //
    // Multiplayer (only when every peer's profile enables extended rolls):
    // every peer skips the same call at the same deterministic point. The
    // front entry then runs in a dedicated native coroutine, so the native
    // multiplayer scheduler claims it like an awaited action step: admission,
    // owner input, the shared face vector, the replay and the completion
    // comparison all use the existing protocol.
    internal static class DeferredRolls
    {
        internal sealed class NativeCall
        {
            internal readonly MethodBase Method; internal readonly object[] Args;
            // Window heading and detail for the d20 (display only).
            internal string Title, Detail;
            // A replay that makes several rolls (a turn start, a travel hour with
            // two crafters) plans each native call as its own part.
            internal readonly List<RollPart> Parts;
            internal NativeCall(MethodBase method, object[] args) { Method = method; Args = args; }
            internal NativeCall(List<RollPart> parts) { Parts = parts; }
        }
        // One native call inside a composite replay. Its dice are bound to the
        // anchor (the condition or effect the game is processing), when given.
        internal sealed class RollPart
        {
            internal readonly MethodBase Method; internal readonly object[] Args;
            internal object Anchor; internal string Title, Detail;
            internal RollPart(MethodBase method, object[] args) { Method = method; Args = args; }
            internal NativeInvocation ToInvocation(string site) { return new NativeInvocation(new object(), site, Method, Args, 0); }
        }
        // The planning identity of a composite replay; never called.
        internal static void CompositeRoll(RollPart[] parts) { }
        internal static readonly MethodInfo CompositeMethod = typeof(DeferredRolls).GetMethod(nameof(CompositeRoll), BindingFlags.Static | BindingFlags.NonPublic);
        sealed class Entry
        {
            internal object Owner; internal string Site;
            // The skipped original call: its identity and (void) result are
            // the native invocation the multiplayer protocol compares.
            internal MethodBase Outer; internal object[] OuterArgs;
            internal Func<NativeCall> Resolve; internal Action Replay;
            internal Stack<IEnumerator> Wait; internal bool Prepared;
            internal bool Networked; internal TA.Coroutine Runner; internal bool Finished;
            internal object Key;
        }
        const int MaxQueued = 64;
        static readonly List<Entry> queue = new List<Entry>();
        [ThreadStatic] static Entry replaying;
        static bool updating;

        internal static bool Holding { get { return queue.Count > 0; } }
        internal static bool Replaying { get { return replaying != null; } }
        internal static int Pending { get { return queue.Count; } }
        // Multiplayer entries hold the game's ticks until they finish.
        internal static bool NetworkedPending { get { return queue.Any(entry => entry.Networked); } }
        // The multiplayer runner of the front entry may advance while held.
        internal static bool IsRunner(object coroutine)
        { return coroutine != null && queue.Count > 0 && ReferenceEquals(queue[0].Runner, coroutine); }

        static MethodInfo checkConcentration, rollConcentration, foodSearch, ingredientGathering, rulesetAbilityCheck,
            basicCheckWithBonus, basicCheck, campUpdate, campaignUpdateTime, identify, saveToCancel, occurrences, battleUpdate, trySave,
            encounter, rollSurprise, canBeSurprised, buildParty, ambushCheck, travelCrafting, foodYield;
        static FieldInfo remaining, recipe, scribedSpell, travelParty;

        internal static bool Preflight(out string error)
        {
            error = null;
            try
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                checkConcentration = typeof(GameLocationCharacter).GetMethod("CheckConcentration", any, null, new[] { typeof(RulesetCharacter), typeof(int), typeof(string), typeof(bool) }, null);
                rollConcentration = typeof(RulesetCharacter).GetMethod("RollConcentrationCheck", any, null, new[] { typeof(int), typeof(string), typeof(RuleDefinitions.RollOutcome).MakeByRefType() }, null);
                foodSearch = typeof(GameTravelScheduler).GetMethod("ExecuteFoodSearch", any, null, new[] { typeof(int), typeof(TravelEventDefinition) }, null);
                ingredientGathering = typeof(GameTravelScheduler).GetMethod("ExecuteIngredientGathering", any, null, new[] { typeof(int), typeof(TravelEventDefinition) }, null);
                rulesetAbilityCheck = typeof(RulesetCharacter).GetMethods(any).SingleOrDefault(m => m.Name == "RollAbilityCheck" && m.DeclaringType == typeof(RulesetCharacter));
                basicCheckWithBonus = typeof(RulesetCharacter).GetMethods(any).SingleOrDefault(m => m.Name == "RollBasicAbilityCheck" && m.GetParameters().Length == 8);
                basicCheck = typeof(RulesetCharacter).GetMethods(any).SingleOrDefault(m => m.Name == "RollBasicAbilityCheck" && m.GetParameters().Length == 6);
                campUpdate = typeof(GameCampActivity).GetMethod("Update", any, null, new[] { typeof(int) }, null);
                campaignUpdateTime = typeof(GameCampaign).GetMethod("UpdateTime", any, null, new[] { typeof(float) }, null);
                remaining = AccessTools.Field(typeof(GameCampActivity), "remainingDurationSeconds");
                recipe = AccessTools.Field(typeof(GameCampActivityCrafting), "recipeDefinition");
                scribedSpell = AccessTools.Field(typeof(GameCampActivityScribeSpell), "spellDefinition");
                identify = typeof(GameLoreManager).GetMethod("RollIMonsterdentificationCheck", any, null, new[] { typeof(GameBestiaryEntry), typeof(bool) }, null);
                saveToCancel = typeof(RulesetActor).GetMethod("SaveToCancelCondition", any, null, new[] { typeof(RulesetCondition) }, null);
                occurrences = typeof(RulesetActor).GetMethod("ProcessConditionsMatchingOccurenceType", any, null, new[] { typeof(RuleDefinitions.TurnOccurenceType), typeof(ulong) }, null);
                battleUpdate = typeof(GameLocationBattle).GetMethod("Update", any, null, Type.EmptyTypes, null);
                trySave = typeof(IRulesetImplementationService).GetMethod("TryRollSavingThrow");
                encounter = typeof(GameTravelScheduler).GetMethod("ExecuteEncounter", any, null, new[] { typeof(int), typeof(TravelEventDefinition) }, null);
                rollSurprise = typeof(GameTravelScheduler).GetMethod("RollSurprise", any, null, new[] { typeof(EncounterParametersDescription), typeof(RulesetCharacterHero).MakeByRefType(), typeof(EncounterDefinitions.SurpriseType) }, null);
                canBeSurprised = typeof(GameTravelScheduler).GetMethod("CanHeroBeSurprised", any, null, new[] { typeof(RulesetCharacterHero) }, null);
                buildParty = typeof(GameTravelScheduler).GetMethod("BuildPartyCharactersList", any, null, Type.EmptyTypes, null);
                travelParty = AccessTools.Field(typeof(GameTravelScheduler), "partyCharacters");
                ambushCheck = typeof(DeferredRolls).GetMethod(nameof(TravelAmbushCheck), BindingFlags.Static | BindingFlags.NonPublic);
                foodYield = typeof(DeferredRolls).GetMethod(nameof(FoodYield), BindingFlags.Static | BindingFlags.NonPublic);
                travelCrafting = typeof(GameTravelScheduler).GetMethod("ExecuteCrafting", any, null, new[] { typeof(int), typeof(TravelEventDefinition) }, null);
                if (checkConcentration == null || rollConcentration == null || foodSearch == null || ingredientGathering == null || rulesetAbilityCheck == null ||
                    basicCheckWithBonus == null || basicCheck == null || campUpdate == null || campaignUpdateTime == null ||
                    remaining == null || remaining.FieldType != typeof(int) || recipe == null || recipe.FieldType != typeof(RecipeDefinition) ||
                    scribedSpell == null || scribedSpell.FieldType != typeof(SpellDefinition) || rulesetAbilityCheck.GetParameters().Length != 18 ||
                    identify == null || identify.ReturnType != typeof(void) || saveToCancel == null || saveToCancel.ReturnType != typeof(void) ||
                    occurrences == null || battleUpdate == null || battleUpdate.ReturnType != typeof(void) || trySave == null || trySave.GetParameters().Length != 21 ||
                    encounter == null || rollSurprise == null || canBeSurprised == null || canBeSurprised.ReturnType != typeof(bool) || buildParty == null ||
                    travelParty == null || travelParty.FieldType != typeof(List<GameCampaignCharacter>) || ambushCheck == null || travelCrafting == null || foodYield == null || CompositeMethod == null)
                    throw new InvalidOperationException("The native synchronous roll methods changed.");
                foreach (var method in new MethodInfo[] { checkConcentration, foodSearch, ingredientGathering, campUpdate, identify, saveToCancel, encounter, travelCrafting })
                    if (method.ReturnType != typeof(void) || method.GetParameters().Any(parameter => parameter.ParameterType.IsByRef))
                        throw new InvalidOperationException("A deferred native call is not a void call without outputs: " + method.Name);
                return true;
            }
            catch (Exception failure) { error = failure.Message; return false; }
        }
        internal static void Install(Harmony harmony)
        {
            string error;
            if (checkConcentration == null && !Preflight(out error)) throw new InvalidOperationException(error);
            harmony.Patch(checkConcentration, prefix: Own(nameof(ConcentrationPrefix)));
            harmony.Patch(foodSearch, prefix: Own(nameof(FoodSearchPrefix)));
            harmony.Patch(ingredientGathering, prefix: Own(nameof(IngredientPrefix)));
            harmony.Patch(campUpdate, prefix: Own(nameof(CampUpdatePrefix)));
            harmony.Patch(campaignUpdateTime, prefix: Own(nameof(HeldPrefix)));
            harmony.Patch(identify, prefix: Own(nameof(IdentificationPrefix)));
            harmony.Patch(occurrences, prefix: Own(nameof(OccurrencePrefix)), finalizer: Own(nameof(OccurrenceFinalizer)));
            harmony.Patch(saveToCancel, prefix: Own(nameof(RepeatSavePrefix)));
            harmony.Patch(battleUpdate, prefix: Own(nameof(HeldPrefix)));
            harmony.Patch(encounter, prefix: Own(nameof(EncounterPrefix)));
            harmony.Patch(rollSurprise, transpiler: Own(nameof(SurpriseTranspiler)));
            harmony.Patch(travelCrafting, prefix: Own(nameof(TravelCraftingPrefix)));
        }
        static HarmonyMethod Own(string name) { return new HarmonyMethod(typeof(DeferredRolls).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)); }

        internal static bool CanDefer
        {
            get
            {
                if (replaying != null || NativeProbe.Active || !Plugin.Enabled || Plugin.UnsupportedRulesMod || SyncPumpGuard.Active || queue.Count >= MaxQueued) return false;
                // Every peer evaluates the same shared profile setting here.
                // While manual dice are paused the game keeps its own rolls.
                // A roll reached during another shared step (outside its own
                // native run) keeps the game's dice: queued, it would wait for
                // that step, which needs the tick its queue holds.
                return !Plugin.Networked() || RuntimeRolls.ExtendedOnlineActive && !MultiplayerRolls.Failed && RuntimeRolls.NetworkStepOpen;
            }
        }

        internal static bool TryDefer(string site, MethodBase outer, object[] outerArgs, Func<NativeCall> resolve, Action replay, object key = null)
        {
            if (!CanDefer) return false;
            queue.Add(new Entry { Owner = new object(), Site = site, Outer = outer, OuterArgs = outerArgs, Resolve = resolve, Replay = replay, Networked = Plugin.Networked(), Key = key });
            return true;
        }
        internal static bool IsPending(object key) { return key != null && queue.Any(entry => Equals(entry.Key, key)); }

        // Called once per rendered frame by the roll window's behaviour.
        internal static void Update()
        {
            if (updating || queue.Count == 0) return;
            updating = true;
            try
            {
                for (int budget = 0; budget < 16 && queue.Count > 0; budget++)
                {
                    var entry = queue[0];
                    if (entry.Networked)
                    {
                        if (!AdvanceNetworked(entry)) return;
                        queue.RemoveAt(0);
                        continue;
                    }
                    if (!Plugin.Enabled || Plugin.UnsupportedRulesMod || Plugin.Networked())
                    { queue.RemoveAt(0); Dispose(entry); Finish(entry); continue; }
                    if (!entry.Prepared)
                    {
                        entry.Prepared = true;
                        var wait = Plan(entry);
                        if (wait == null) { queue.RemoveAt(0); Finish(entry); continue; }
                        entry.Wait = new Stack<IEnumerator>(); entry.Wait.Push(wait);
                    }
                    if (Pump(entry)) return;
                    queue.RemoveAt(0); Finish(entry);
                }
            }
            finally { updating = false; }
        }
        // Probes the inner roll and prepares it; null keeps the game's dice.
        static IEnumerator Plan(Entry entry)
        {
            NativeCall call = null;
            try { call = entry.Resolve(); }
            catch (Exception error) { Plugin.Log("Manual preparation skipped at " + entry.Site + ": " + error.Message); }
            if (call == null || call.Parts != null && call.Parts.Count == 0) return null;
            try
            {
                if (call.Parts != null)
                    return RuntimeRolls.PrepareDeferredRoll(entry.Owner, entry.Site, entry.Outer, entry.OuterArgs, CompositeMethod, new object[] { call.Parts.ToArray() }, call.Title, call.Detail);
                return RuntimeRolls.PrepareDeferredRoll(entry.Owner, entry.Site, entry.Outer, entry.OuterArgs, call.Method, call.Args, call.Title, call.Detail);
            }
            catch (Exception error) { Plugin.Log("Manual preparation skipped at " + entry.Site + ": " + error.Message); return null; }
        }
        static bool Pump(Entry entry)
        {
            var stack = entry.Wait;
            try
            {
                for (int budget = 0; budget < 256 && stack.Count > 0; budget++)
                {
                    var top = stack.Peek();
                    if (!top.MoveNext()) { stack.Pop(); var done = top as IDisposable; if (done != null) done.Dispose(); continue; }
                    var child = top.Current as IEnumerator;
                    if (child != null) { stack.Push(child); continue; }
                    return true;
                }
                return stack.Count > 0;
            }
            catch (Exception error)
            {
                Plugin.Warn("A deferred manual roll failed and will use the game's dice: " + error.Message);
                Dispose(entry);
                return false;
            }
        }
        // Multiplayer: the entry runs inside its own native coroutine. Returns
        // true once the entry has finished (or was retired) and can be removed.
        static bool AdvanceNetworked(Entry entry)
        {
            if (!Plugin.Networked())
            {
                // The room is gone: nothing is shared any more, so the original
                // finishes locally with the game's dice.
                if (!entry.Finished) { Dispose(entry); Finish(entry); }
                return true;
            }
            // Paused multiplayer dice: an entry not yet started finishes here
            // with the game's dice, exactly where the game would have run it.
            // A started runner falls back to the game's dice by itself.
            if (MultiplayerRolls.Failed && entry.Runner == null)
            {
                if (!entry.Finished) { Dispose(entry); Finish(entry); }
                return true;
            }
            if (entry.Runner == null) { entry.Runner = new TA.Coroutine(); entry.Runner.Start(RunNetworked(entry)); }
            entry.Runner.Run();
            if (entry.Runner.IsSuspended)
            {
                MultiplayerRolls.Fail("A deferred multiplayer roll failed: " + (entry.Runner.LastException == null ? "unknown error" : entry.Runner.LastException.Message));
                // The skipped original still runs, with the game's dice.
                if (!entry.Finished) { Dispose(entry); Finish(entry); }
                return true;
            }
            return entry.Runner.IsFinished;
        }
        static IEnumerator RunNetworked(Entry entry)
        {
            var wait = Plan(entry);
            if (wait != null) yield return wait;
            Finish(entry);
            // The replay ended inside this run; the scheduler's after-run hook
            // starts the shared completion comparison. Wait for it to release.
            yield return null;
            while (RuntimeRolls.NetworkHolding && ReferenceEquals(RuntimeRolls.NetworkOwner, entry.Owner)) yield return null;
        }
        static void Dispose(Entry entry)
        {
            if (entry.Wait != null)
                while (entry.Wait.Count > 0) { var item = entry.Wait.Pop() as IDisposable; if (item != null) try { item.Dispose(); } catch (Exception) { } }
            try { RuntimeRolls.CancelOwner(entry.Owner); } catch (Exception) { }
        }
        static void Finish(Entry entry)
        {
            if (entry.Finished) return;
            entry.Finished = true;
            replaying = entry;
            bool entered = false, completed = false;
            try
            {
                RuntimeRolls.EnterNative(entry.Owner, entry.Site); entered = true;
                entry.Replay();
                // Every deferred original is a void call without outputs.
                RuntimeRolls.ObserveNativeValues(entry.Owner, entry.Site, null, new object[0]);
                completed = true;
            }
            catch (Exception error)
            {
                var root = error; while (root is TargetInvocationException && root.InnerException != null) root = root.InnerException;
                Plugin.Error("A deferred roll could not finish at " + entry.Site + ": " + root);
            }
            finally
            {
                try { if (entered && completed) RuntimeRolls.ExitNative(entry.Owner, entry.Site); else RuntimeRolls.AbortNative(entry.Owner); }
                catch (Exception error) { Plugin.Warn("A deferred roll scope could not close: " + error.Message); }
                replaying = null;
            }
        }
        // Location/game teardown: the queued originals belong to state that is
        // being destroyed. They are dropped rather than run against it.
        internal static void Drop()
        {
            if (queue.Count == 0) return;
            Plugin.Warn("Dropped " + queue.Count + " pending manual roll(s) during a location change.");
            var items = queue.ToArray(); queue.Clear();
            foreach (var entry in items) { entry.Finished = true; Dispose(entry); }
        }
        // Disable/unload: run every queued original with the game's own dice.
        internal static void Flush()
        {
            if (updating) return;
            var items = queue.ToArray(); queue.Clear();
            foreach (var entry in items) { Dispose(entry); Finish(entry); }
        }
        // The world-map clock and the battle state machine wait for the answer.
        static bool HeldPrefix() { return !Holding || replaying != null; }

        // ---- Concentration (inside damage) -------------------------------
        static bool ConcentrationPrefix(GameLocationCharacter __instance, RulesetCharacter character, int damage, string damageType, bool stillConscious)
        {
            if (!CanDefer || !stillConscious || character == null || damage <= 0) return true;
            try
            {
                var own = __instance.RulesetCharacter;
                if (own == null || own.ConcentratedSpell == null) return true;
                if (!Plugin.Settings.Concentration || !RuntimeRolls.CanManual(character, RuleDefinitions.RollContext.ConcentrationCheck)) return true;
                var settings = ServiceRepository.GetService<IGameSettingsService>();
                if (settings != null && settings.NeverLoseConcentrationOnSpells && character.Side == RuleDefinitions.Side.Ally) return true;
                if (character.IsAutomaticallyFailingSavingThrow("Constitution")) return true;
                if (OverConcentrationThreshold(character) >= damage) return true;
                int dc = damage / 2 > 10 ? damage / 2 : 10;
                var instance = __instance;
                return !TryDefer("Concentration", checkConcentration, new object[] { instance, character, damage, damageType, true },
                    () => instance.RulesetCharacter == null || instance.RulesetCharacter.ConcentratedSpell == null ? null :
                        new NativeCall(rollConcentration, new object[] { character, dc, damageType, RuleDefinitions.RollOutcome.Neutral }) { Detail = "after taking " + damage + " damage" },
                    () => checkConcentration.Invoke(instance, new object[] { character, damage, damageType, true }));
            }
            catch (Exception error) { Plugin.Log("Concentration stays automatic: " + error.Message); return true; }
        }
        static int OverConcentrationThreshold(RulesetCharacter character)
        {
            int threshold = -1;
            var features = new List<FeatureDefinition>();
            using (var scope = NativeScratchScope.ForActor(character))
                character.EnumerateFeaturesToBrowse<FeatureDefinitionMagicAffinity>(features);
            foreach (var feature in features)
            {
                var affinity = feature as FeatureDefinitionMagicAffinity;
                if (affinity != null && affinity.OverConcentrationThreshold > threshold) threshold = affinity.OverConcentrationThreshold;
            }
            return threshold;
        }

        // ---- Travel: food search and ingredient gathering -----------------
        static bool FoodSearchPrefix(GameTravelScheduler __instance, int hour, TravelEventDefinition eventDefinition)
        { return TravelPrefix(__instance, foodSearch, "Travel.FoodSearch", hour, eventDefinition, false); }
        static bool IngredientPrefix(GameTravelScheduler __instance, int hour, TravelEventDefinition eventDefinition)
        { return TravelPrefix(__instance, ingredientGathering, "Travel.IngredientGathering", hour, eventDefinition, true); }
        static bool TravelPrefix(GameTravelScheduler scheduler, MethodInfo handler, string site, int hour, TravelEventDefinition definition, bool ingredients)
        {
            if (!CanDefer || !Plugin.Settings.Downtime || !Plugin.Settings.Ability || scheduler == null || definition == null) return true;
            return !TryDefer(site, handler, new object[] { scheduler, hour, definition }, () => ProbeTravel(scheduler, handler, hour, definition, ingredients),
                () => handler.Invoke(scheduler, new object[] { hour, definition }));
        }
        static NativeCall ProbeTravel(GameTravelScheduler scheduler, MethodInfo handler, int hour, TravelEventDefinition definition, bool ingredients)
        {
            var campaign = Gui.GameCampaign;
            var party = campaign == null ? null : campaign.Party;
            if (party == null || party.CharactersList == null || party.CurrentSegment == null) return null;
            // Without a loot table the original handler makes no roll and goes
            // straight to its journal; never run that part as a probe.
            if (ingredients && (party.CurrentSegment.IngredientLoots == null || party.CurrentSegment.IngredientLoots.Count == 0)) return null;
            var actors = party.CharactersList.Where(c => c != null && c.RulesetCharacter != null).Select(c => (RulesetActor)c.RulesetCharacter).ToArray();
            if (actors.Length == 0) return null;
            var call = ProbeCheck(delegate { handler.Invoke(scheduler, new object[] { hour, definition }); }, actors);
            if (call != null) call.Title = ingredients ? "Gathering ingredients" : "Foraging";
            // Food search: after a successful check the game rolls the food
            // found (the segment's harvest dice), asked only after a success.
            if (call != null && !ingredients && party.CurrentSegment.FoodHarvestDiceNumber > 0 && RuntimeRolls.Max(party.CurrentSegment.FoodHarvestDieType) > 1)
                return new NativeCall(new List<RollPart> {
                    new RollPart(call.Method, call.Args) { Title = call.Title },
                    new RollPart(foodYield, new object[] { call.Args[0], party.CurrentSegment.FoodHarvestDiceNumber, party.CurrentSegment.FoodHarvestDieType, party.CurrentSegment.FoodHarvestAdditiveModifier }) { Title = "Food found" } });
            return call;
        }
        // Runs the original handler up to its ruleset ability check and
        // returns that call without rolling (see NativeProbe).
        static NativeCall ProbeCheck(Action invoke, RulesetActor[] actors)
        {
            var result = NativeProbe.Run(invoke, rulesetAbilityCheck, actors);
            if (result == null || !(result.Receiver is RulesetCharacter) || result.Args == null) return null;
            var args = new object[result.Args.Length + 1];
            args[0] = result.Receiver; Array.Copy(result.Args, 0, args, 1, result.Args.Length);
            return new NativeCall(rulesetAbilityCheck, args);
        }

        // ---- Travel: random encounter surprise ----------------------------
        // GameTravelScheduler.ExecuteEncounter picks an encounter, then
        // RollSurprise rolls a raw d20 for the party's best Perception against
        // a raw d20 for the creatures. The party's die is asked for; the
        // creatures' die stays native.
        static bool EncounterPrefix(GameTravelScheduler __instance, int hour, TravelEventDefinition eventDefinition)
        {
            if (!CanDefer || !Plugin.Settings.Downtime || !Plugin.Settings.Ability || __instance == null || eventDefinition == null) return true;
            var scheduler = __instance; var definition = eventDefinition;
            return !TryDefer("Travel.Encounter", encounter, new object[] { scheduler, hour, definition },
                () => ResolveTravelAmbush(scheduler, hour, definition),
                () => encounter.Invoke(scheduler, new object[] { hour, definition }));
        }
        static NativeCall ResolveTravelAmbush(GameTravelScheduler scheduler, int hour, TravelEventDefinition definition)
        {
            var campaign = Gui.GameCampaign;
            var party = campaign == null ? null : campaign.Party;
            if (party == null || party.CharactersList == null || party.CurrentSegment == null) return null;
            var actors = party.CharactersList.Where(c => c != null && c.RulesetCharacter != null).Select(c => (RulesetActor)c.RulesetCharacter).ToArray();
            if (actors.Length == 0) return null;
            // Runs the original up to RollSurprise; its random encounter and
            // location draws are restored afterwards and repeat identically.
            var result = NativeProbe.Run(delegate { encounter.Invoke(scheduler, new object[] { hour, definition }); }, rollSurprise, actors);
            if (result == null || result.Args == null || result.Args.Length != 3) return null;
            var parameters = result.Args[0] as EncounterParametersDescription;
            if (parameters == null || parameters.EncounterDefinition == null || !(result.Args[2] is EncounterDefinitions.SurpriseType)
                || (EncounterDefinitions.SurpriseType)result.Args[2] != EncounterDefinitions.SurpriseType.RandomRoll) return null;
            int monsters = 0, creatures = int.MinValue;
            foreach (var occurrence in parameters.EncounterDefinition.MonsterOccurences)
                if (occurrence.MonsterDefinition != null)
                {
                    monsters += occurrence.Number;
                    creatures = Math.Max(creatures, occurrence.MonsterDefinition.ComputePerceptionScore());
                }
            if (monsters == 0) return null;
            // The same pure selection RollSurprise makes before its dice.
            RulesetCharacterHero detector = null; int best = int.MinValue;
            buildParty.Invoke(scheduler, null);
            foreach (var member in (List<GameCampaignCharacter>)travelParty.GetValue(scheduler))
            {
                var hero = member.RulesetCharacter as RulesetCharacterHero;
                if (hero == null) return null;
                bool surprisable;
                using (var scope = NativeScratchScope.ForActor(hero)) surprisable = (bool)canBeSurprised.Invoke(scheduler, new object[] { hero });
                if (!surprisable) { detector = hero; break; }
                int bonus = hero.ComputeBaseAbilityCheckBonus("Wisdom", null, "Perception");
                if (bonus > best) { detector = hero; best = bonus; }
            }
            if (detector == null) return null;
            int total = best == int.MinValue ? detector.ComputeBaseAbilityCheckBonus("Wisdom", null, "Perception") : best;
            if (scheduler.IsNavigating && best != int.MinValue)
            {
                int pace = campaign.Party.TravelPace == null ? 0 : campaign.Party.TravelPace.PerceptionModifier;
                if (pace != 0)
                    foreach (var member in (List<GameCampaignCharacter>)travelParty.GetValue(scheduler))
                    {
                        var features = new List<FeatureDefinition>();
                        using (var scope = NativeScratchScope.ForActor(member.RulesetCharacter))
                            member.RulesetCharacter.EnumerateFeaturesToBrowse<FeatureDefinitionTerrainTypeAffinity>(features);
                        if (features.OfType<FeatureDefinitionTerrainTypeAffinity>().Any(f => f.TerrainType == party.CurrentSegment.BiomeDefinition.TerrainType && f.IgnoreTravelPacePerceptionMalus)) { pace = 0; break; }
                    }
                total += pace;
            }
            return new NativeCall(ambushCheck, new object[] { detector, total, creatures }) { Title = "Ambush check", Detail = "Spotting the creatures before they strike" };
        }
        // A planning identity for the food a successful search yields; never called.
        static void FoodYield(RulesetCharacterHero hero, int dice, RuleDefinitions.DieType die, int bonus) { }
        internal static bool TryPlanFoodYield(NativeInvocation call, NativeRollPlan plan)
        {
            if (call == null || !Equals(call.Method, foodYield)) return false;
            var hero = call.Arg("hero") as RulesetCharacterHero;
            // The search's d20 is the last actor die planned before this part.
            int check = -1;
            for (int i = plan.Leaves.Count - 1; i >= 0; i--) if (!plan.Leaves[i].Static) { check = i; break; }
            if (hero == null || check < 0 || !Plugin.Settings.Downtime) return true;
            int start = plan.Leaves.Count;
            plan.AddStaticDice(hero, call.Arg<RuleDefinitions.DieType>("die"), call.Arg<int>("dice"), title: "Food found", note: "Rations from a successful search",
                hint: "Roll the harvest dice. The game adds " + call.Arg<int>("bonus") + " and applies the terrain's yield.", category: RuleDefinitions.RollContext.AbilityCheck);
            if (plan.Leaves.Count == start) return true;
            plan.AskWhen(() => plan.CheckSucceeds(check));
            return true;
        }
        // A planning identity for the raw d20s in RollSurprise; never called.
        static void TravelAmbushCheck(RulesetCharacterHero detector, int bonus, int creatures) { }
        internal static bool TryPlanAmbush(NativeInvocation call, NativeRollPlan plan)
        {
            if (call == null || !Equals(call.Method, ambushCheck)) return false;
            var detector = call.Arg("detector") as RulesetCharacterHero;
            if (detector == null || !RuntimeRolls.CanManual(detector, RuleDefinitions.RollContext.AbilityCheck)) return true;
            plan.AddStaticDice(detector, RuleDefinitions.DieType.D20, 1, title: "Ambush check", note: RollText.Check("Wisdom", "Perception"),
                hint: "Roll for your best lookout. The creatures roll too; beating them by more than 5 surprises them.", category: RuleDefinitions.RollContext.AbilityCheck);
            plan.Describe(0, true, call.Arg<int>("bonus"));
            // The creatures' d20 follows the party's in the same call; it is
            // the game master's die (enemy dice), asked only after the party's.
            int start = plan.Leaves.Count;
            plan.AddMasterDice(detector, "The creatures", RuleDefinitions.DieType.D20, 1, title: "Ambush: the creatures", note: "Their best Perception",
                category: RuleDefinitions.RollContext.AbilityCheck, hint: "Roll for the ambushers. Beating the party by more than 5 surprises the party.");
            if (plan.Leaves.Count > start) plan.Describe(0, true, call.Arg<int>("creatures"));
            return true;
        }
        // Replaces both raw d20 draws in RollSurprise. Outside the replayed
        // encounter (or once the party's die is used) the game rolls itself.
        public static int SurpriseFace(RuleDefinitions.DieType diceType, RuleDefinitions.AdvantageType advantageType, out int firstRoll, out int secondRoll, float rollAlterationScore = 0f)
        { return RuntimeRolls.InvocationStaticFace(encounter, diceType, advantageType, out firstRoll, out secondRoll, rollAlterationScore); }
        static IEnumerable<CodeInstruction> SurpriseTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            var native = AccessTools.Method(typeof(RuleDefinitions), "RollDie");
            int count = 0;
            foreach (var code in codes)
                if (code.opcode == System.Reflection.Emit.OpCodes.Call && Equals(code.operand, native)) { code.operand = AccessTools.Method(typeof(DeferredRolls), nameof(SurpriseFace)); count++; }
            if (count != 2) throw new InvalidOperationException("The native travel surprise roll changed.");
            return codes;
        }

        // ---- Bestiary: monster identification ----------------------------
        // The game identifies a newly revealed monster type at first contact and
        // again after a victory, with the party's best Wisdom (Survival). The
        // result only changes bestiary knowledge and a log line, so it can wait.
        static bool IdentificationPrefix(GameLoreManager __instance, GameBestiaryEntry bestiaryEntry, bool rollDie)
        {
            if (!CanDefer || !Plugin.Settings.Lore || !Plugin.Settings.Ability || ReferenceEquals(__instance, null) || bestiaryEntry == null || bestiaryEntry.MonsterDefinition == null) return true;
            try
            {
                var characters = ServiceRepository.GetService<IGameLocationCharacterService>();
                if (characters == null || characters.PartyCharacters == null) return true;
                bool manual = false;
                foreach (var character in characters.PartyCharacters)
                {
                    var actor = character == null ? null : character.RulesetCharacter;
                    if (actor != null && !actor.IsDeadOrDyingOrUnconscious && RuntimeRolls.CanManual(actor, RuleDefinitions.RollContext.AbilityCheck)) { manual = true; break; }
                }
                if (!manual) return true;
                var manager = __instance; var entry = bestiaryEntry; bool display = rollDie;
                return !TryDefer("Lore.MonsterIdentification", identify, new object[] { manager, entry, display },
                    () => ProbeIdentification(manager, entry, display),
                    () => identify.Invoke(manager, new object[] { entry, display }));
            }
            catch (Exception error) { Plugin.Log("Monster identification stays automatic: " + error.Message); return true; }
        }
        static NativeCall ProbeIdentification(GameLoreManager manager, GameBestiaryEntry entry, bool display)
        {
            var characters = ServiceRepository.GetService<IGameLocationCharacterService>();
            if (characters == null || characters.PartyCharacters == null || entry.MonsterDefinition == null) return null;
            var actors = characters.PartyCharacters.Where(c => c != null && c.RulesetCharacter != null && !c.RulesetCharacter.IsDeadOrDyingOrUnconscious)
                .Select(c => (RulesetActor)c.RulesetCharacter).ToArray();
            if (actors.Length == 0) return null;
            var call = ProbeCheck(delegate { identify.Invoke(manager, new object[] { entry, display }); }, actors);
            if (call != null)
            {
                call.Title = "Monster identification";
                // Before identification the game itself hides the name.
                bool named = false;
                try { named = entry.CanAccess(BestiaryDefinitions.BestiaryAccess.Name); } catch (Exception) { }
                call.Detail = named ? GameNames.SpellName(entry.MonsterDefinition) : "Unknown creature";
            }
            return call;
        }

        // ---- Repeat saving throws at the end of a turn ---------------------
        // RulesetActor.ProcessConditionsMatchingOccurenceType saves to end a
        // condition such as Hold Person. Only end-of-turn occurrences wait: the
        // next turn's start, the battle state machine and every action
        // coroutine are held until the save has been replayed. Start-of-turn
        // saves are not deferred here: the game decides at once whether the
        // character can act, so TurnRolls defers the whole turn start instead.
        [ThreadStatic] static Stack<RuleDefinitions.TurnOccurenceType> occurrenceStack;
        internal static RuleDefinitions.TurnOccurenceType? CurrentOccurrence
        { get { return occurrenceStack == null || occurrenceStack.Count == 0 ? (RuleDefinitions.TurnOccurenceType?)null : occurrenceStack.Peek(); } }
        static void OccurrencePrefix(RuleDefinitions.TurnOccurenceType occurenceType)
        { if (occurrenceStack == null) occurrenceStack = new Stack<RuleDefinitions.TurnOccurenceType>(); occurrenceStack.Push(occurenceType); }
        static Exception OccurrenceFinalizer(Exception __exception)
        { if (occurrenceStack != null && occurrenceStack.Count > 0) occurrenceStack.Pop(); return __exception; }
        static bool RepeatSavePrefix(RulesetActor __instance, RulesetCondition activeCondition)
        {
            if (!CanDefer || !Plugin.Settings.SavingThrows || !Plugin.Settings.RepeatSaves || ReferenceEquals(__instance, null) || activeCondition == null) return true;
            if (occurrenceStack == null || occurrenceStack.Count == 0) return true;
            var occurrence = occurrenceStack.Peek();
            if (occurrence != RuleDefinitions.TurnOccurenceType.EndOfTurn && occurrence != RuleDefinitions.TurnOccurenceType.EndOfSourceTurn) return true;
            try
            {
                if (!activeCondition.HasSaveOverride || string.IsNullOrEmpty(activeCondition.SaveOverrideAbilityScoreName)) return true;
                if (!RuntimeRolls.CanManual(__instance, RuleDefinitions.RollContext.SavingThrow)) return true;
                var actor = __instance; var condition = activeCondition;
                // A save already waiting for this condition covers it.
                if (IsPending(condition)) return false;
                return !TryDefer("Condition.RepeatSave", saveToCancel, new object[] { actor, condition },
                    () => ResolveRepeatSave(actor, condition),
                    delegate { if (HasCondition(actor, condition)) saveToCancel.Invoke(actor, new object[] { condition }); },
                    condition);
            }
            catch (Exception error) { Plugin.Log("Repeat saving throw stays automatic: " + error.Message); return true; }
        }
        static bool HasCondition(RulesetActor actor, RulesetCondition condition)
        {
            var categories = actor.ConditionsByCategory;
            return categories != null && categories.Values.Any(list => list != null && list.Contains(condition));
        }
        // The same save as one part of a composite replay (a turn start).
        internal static RollPart ResolveRepeatSavePart(RulesetActor actor, RulesetCondition condition)
        {
            var call = ResolveRepeatSave(actor, condition);
            return call == null ? null : new RollPart(call.Method, call.Args) { Anchor = condition, Title = call.Title, Detail = call.Detail };
        }
        // The exact call SaveToCancelCondition makes, from the same pure lookups.
        static NativeCall ResolveRepeatSave(RulesetActor actor, RulesetCondition condition)
        {
            if (!HasCondition(actor, condition)) return null;
            var service = ServiceRepository.GetService<IRulesetImplementationService>();
            if (service == null) return null;
            var source = RulesetEntity.GetEntity<RulesetCharacter>(condition.SourceGuid);
            var sourceType = RuleDefinitions.FeatureSourceType.Unknown;
            BaseDefinition definition = null;
            FeatureDefinition feature; MonsterAttackDefinition attack; ItemDefinition item;
            if (DatabaseRepository.GetDatabase<FeatureDefinition>().TryGetElement(condition.SaveOverrideSourceName, out feature))
            { definition = feature; sourceType = RuleDefinitions.FeatureSourceType.ExplicitFeature; }
            else if (DatabaseRepository.GetDatabase<MonsterAttackDefinition>().TryGetElement(condition.SaveOverrideSourceName, out attack))
            { definition = attack; sourceType = RuleDefinitions.FeatureSourceType.MonsterAttack; }
            else if (DatabaseRepository.GetDatabase<ItemDefinition>().TryGetElement(condition.SaveOverrideSourceName, out item))
            { definition = item; sourceType = RuleDefinitions.FeatureSourceType.Equipment; }
            var forms = new List<EffectForm>();
            condition.BuildDummyEffectForms(forms);
            return new NativeCall(trySave, new object[] { service, source, source == null ? RuleDefinitions.Side.Enemy : source.Side, actor, new ActionModifier(),
                false, true, condition.SaveOverrideAbilityScoreName, condition.SaveOverrideDC, false, false, false, sourceType, forms, null, null,
                condition.SaveOverrideSourceName, definition, string.Empty, null, RuleDefinitions.RollOutcome.Neutral, 0 })
                { Title = "Repeat saving throw", Detail = GameNames.SpellName(condition.ConditionDefinition) };
        }

        // ---- Travel: crafting progress ----------------------------------
        // GameTravelScheduler.ExecuteCrafting makes one crafting check for each
        // awake crafter in turn and then writes the journal. Every check is a
        // part of one replay, in the order the original handler makes them.
        static bool TravelCraftingPrefix(GameTravelScheduler __instance, int hour, TravelEventDefinition eventDefinition)
        {
            if (!CanDefer || !Plugin.Settings.Downtime || !Plugin.Settings.Ability || __instance == null || eventDefinition == null) return true;
            try
            {
                var initial = ResolveTravelCrafting();
                if (initial == null) return true;
                var scheduler = __instance; var definition = eventDefinition;
                return !TryDefer("Travel.Crafting", travelCrafting, new object[] { scheduler, hour, definition },
                    ResolveTravelCrafting, () => travelCrafting.Invoke(scheduler, new object[] { hour, definition }));
            }
            catch (Exception error) { Plugin.Log("Travel crafting stays automatic: " + error.Message); return true; }
        }
        internal static NativeCall ResolveTravelCrafting()
        {
            var resting = ServiceRepository.GetService<IGameRestingService>();
            if (resting == null || resting.ScheduledCampActivities == null) return null;
            var settings = ServiceRepository.GetService<IGameSettingsService>();
            if (settings != null && settings.ForceCraftingRollSuccess) return null;
            var parts = new List<RollPart>();
            foreach (var activity in resting.ScheduledCampActivities)
            {
                var crafting = activity as GameCampActivityCrafting;
                var hero = activity == null ? null : activity.Hero;
                if (crafting == null || hero == null || hero.IsDeadOrDyingOrUnconscious || hero.RemainingSleepHours != 0) continue;
                var definition = (RecipeDefinition)recipe.GetValue(crafting);
                if (definition == null || definition.ToolType == null) continue;
                // Every crafter rolls in the original; one the player does not
                // roll for keeps a part with no dice so the order is preserved.
                var trends = new List<RuleDefinitions.TrendInfo>();
                string proficiency; int bonus;
                using (var scope = NativeScratchScope.ForActor(hero))
                    hero.ComputeBestAbilityScoreAndProficiencyForCrafting(definition, trends, out proficiency, out bonus);
                parts.Add(new RollPart(basicCheckWithBonus, new object[] { hero, bonus, trends, definition.ToolType.MainAbilityScore, proficiency,
                    definition.CraftingDC, RuleDefinitions.RollOutcome.Neutral, 0, true }) { Title = "Crafting while travelling", Detail = GameNames.SpellName(definition.CraftedItem) });
            }
            if (!parts.Any(part => RuntimeRolls.CanManual((RulesetActor)part.Args[0], RuleDefinitions.RollContext.AbilityCheck))) return null;
            return new NativeCall(parts);
        }

        // ---- Camp: crafting and spell scribing ---------------------------
        static bool CampUpdatePrefix(GameCampActivity __instance, int deltaTimeSeconds)
        {
            if (!CanDefer || !Plugin.Settings.Downtime || !Plugin.Settings.Ability) return true;
            try
            {
                bool crafting = __instance is GameCampActivityCrafting, scribing = __instance is GameCampActivityScribeSpell;
                if (!crafting && !scribing) return true;
                if ((int)remaining.GetValue(__instance) - deltaTimeSeconds > 0) return true;
                var hero = __instance.Hero;
                if (hero == null || !RuntimeRolls.CanManual(hero, RuleDefinitions.RollContext.AbilityCheck)) return true;
                var settings = ServiceRepository.GetService<IGameSettingsService>();
                if (crafting && settings != null && settings.ForceCraftingRollSuccess) return true;
                var resting = ServiceRepository.GetService<IGameRestingService>();
                if (resting == null || resting.ScheduledCampActivities == null || !resting.ScheduledCampActivities.Contains(__instance)) return true;
                var activity = __instance; int delta = deltaTimeSeconds;
                return !TryDefer(crafting ? "Camp.Crafting" : "Camp.ScribeSpell", campUpdate, new object[] { activity, delta },
                    () => ResolveCamp(activity, resting),
                    delegate {
                        // An earlier replay may have completed and removed it,
                        // exactly as the original hourly loop would have.
                        if (!resting.ScheduledCampActivities.Contains(activity)) return;
                        campUpdate.Invoke(activity, new object[] { delta });
                        if (activity.Completed && resting.ScheduledCampActivities.Contains(activity)) resting.UnregisterCampActivity(activity);
                    });
            }
            catch (Exception error) { Plugin.Log("Camp roll stays automatic: " + error.Message); return true; }
        }
        static NativeCall ResolveCamp(GameCampActivity activity, IGameRestingService resting)
        {
            if (!resting.ScheduledCampActivities.Contains(activity)) return null;
            var hero = activity.Hero; if (hero == null) return null;
            var crafting = activity as GameCampActivityCrafting;
            if (crafting != null)
            {
                var definition = (RecipeDefinition)recipe.GetValue(crafting);
                if (definition == null || definition.ToolType == null) return null;
                var trends = new List<RuleDefinitions.TrendInfo>();
                string proficiency; int bonus;
                using (var scope = NativeScratchScope.ForActor(hero))
                    hero.ComputeBestAbilityScoreAndProficiencyForCrafting(definition, trends, out proficiency, out bonus);
                return new NativeCall(basicCheckWithBonus, new object[] { hero, bonus, trends, definition.ToolType.MainAbilityScore, proficiency,
                    definition.CraftingDC, RuleDefinitions.RollOutcome.Neutral, 0, true }) { Title = "Crafting", Detail = GameNames.SpellName(definition.CraftedItem) };
            }
            var scribe = activity as GameCampActivityScribeSpell;
            if (scribe != null)
            {
                var spell = (SpellDefinition)scribedSpell.GetValue(scribe);
                if (spell == null) return null;
                return new NativeCall(basicCheck, new object[] { hero, "Intelligence", "Arcana", spell.SpellLevel + 10, RuleDefinitions.RollOutcome.Neutral, 0, true })
                    { Title = "Scribing a spell", Detail = GameNames.SpellName(spell) };
            }
            return null;
        }
    }
}
