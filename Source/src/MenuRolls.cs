using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ManualDiceRoller
{
    // Single-player rolls made in the character screens: ability scores at
    // character creation (4d6, the lowest die dropped, six times) and,
    // optionally, hit points on levelling up. The game itself never rolls
    // level-up hit points (it takes the class die's average); a rolled value
    // is a house rule the player opts into. Neither is shared in multiplayer.
    internal static class MenuRolls
    {
        const string RerollHash = "2651c4bcd81cd02c12aecc79902219e38bba6a500b70eb3c56f68829b7785b55";
        const string AddLevelHash = "8bfa09103aad4a832373845594e38cca1f5a2df52a2611ae67abacc4dca7f717";
        sealed class Flow
        {
            internal readonly List<RollPrompt> Prompts = new List<RollPrompt>();
            internal readonly List<int[]> Answers = new List<int[]>();
            internal Action<List<int[]>> Done;
            internal Func<bool> StillValid;
            internal bool Showing;
        }
        static readonly Queue<Flow> flows = new Queue<Flow>();
        static Flow current;
        static MethodInfo reroll, enterStage, sumBest, addLevel, removeLevel, finalize;
        static FieldInfo creationMode, hitPointHistory;
        [ThreadStatic] static Queue<int> manualScores;
        [ThreadStatic] static int replaying, entering;
        sealed class Pending { internal WeakReference Hero; internal int Index, Average; internal CharacterClassDefinition Class; internal int Level; }
        static readonly List<Pending> pending = new List<Pending>();

        internal static bool Preflight(out string error)
        {
            error = null;
            try
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                reroll = typeof(CharacterStageAbilityScoresPanel).GetMethod("OnReRollCb", any, null, Type.EmptyTypes, null);
                enterStage = typeof(CharacterStageAbilityScoresPanel).GetMethod("EnterStage", any, null, Type.EmptyTypes, null);
                creationMode = AccessTools.Field(typeof(CharacterStageAbilityScoresPanel), "creationMode");
                sumBest = typeof(RuleDefinitions).GetMethod("RollDiceAndSumBest", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(int), typeof(RuleDefinitions.DieType), typeof(int) }, null);
                addLevel = typeof(RulesetCharacterHero).GetMethod("AddClassLevel", any, null, new[] { typeof(CharacterClassDefinition) }, null);
                removeLevel = typeof(RulesetCharacterHero).GetMethod("RemoveClassLevel", any, null, Type.EmptyTypes, null);
                finalize = typeof(CharacterBuildingManager).GetMethod("FinalizeCharacter", any, null, new[] { typeof(RulesetCharacterHero), typeof(bool) }, null);
                hitPointHistory = AccessTools.Field(typeof(RulesetCharacterHero), "hitPointsGainHistory");
                if (reroll == null || enterStage == null || creationMode == null || creationMode.FieldType != typeof(bool) || sumBest == null || sumBest.ReturnType != typeof(int) ||
                    addLevel == null || removeLevel == null || finalize == null || hitPointHistory == null || hitPointHistory.FieldType != typeof(List<int>))
                    throw new InvalidOperationException("The native character screen methods changed.");
                NativeMethodPin.Verify(reroll, RerollHash); NativeMethodPin.Verify(addLevel, AddLevelHash);
                return true;
            }
            catch (Exception failure) { error = failure.Message; return false; }
        }
        internal static void Install(Harmony harmony)
        {
            string error;
            if (reroll == null && !Preflight(out error)) throw new InvalidOperationException(error);
            harmony.Patch(reroll, prefix: Own(nameof(RerollPrefix)));
            harmony.Patch(enterStage, prefix: Own(nameof(EnterPrefix)), postfix: Own(nameof(EnterPostfix)), finalizer: Own(nameof(EnterFinalizer)));
            harmony.Patch(sumBest, prefix: Own(nameof(SumBestPrefix)));
            harmony.Patch(addLevel, postfix: Own(nameof(AddLevelPostfix)));
            harmony.Patch(removeLevel, postfix: Own(nameof(RemoveLevelPostfix)));
            harmony.Patch(finalize, postfix: Own(nameof(FinalizePostfix)));
        }
        static HarmonyMethod Own(string name) { return new HarmonyMethod(typeof(MenuRolls).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)); }
        static bool Allowed { get { return Plugin.Enabled && !Plugin.UnsupportedRulesMod && !Plugin.Networked(); } }

        // Called once per frame by the roll window's behaviour.
        internal static void Update()
        {
            if (current == null)
            {
                if (flows.Count == 0) return;
                current = flows.Dequeue();
            }
            var flow = current;
            bool valid;
            try { valid = Allowed && (flow.StillValid == null || flow.StillValid()); }
            catch (Exception) { valid = false; }
            if (!valid)
            {
                if (flow.Showing) RollWindow.Close(flow.Prompts[flow.Answers.Count]);
                current = null; return;
            }
            if (flow.Showing || RollWindow.Visible) return;
            var prompt = flow.Prompts[flow.Answers.Count];
            flow.Showing = true;
            RollWindow.Display(prompt,
                values => {
                    if (!ReferenceEquals(current, flow)) return;
                    flow.Showing = false; flow.Answers.Add((int[])values.Clone());
                    if (flow.Answers.Count < flow.Prompts.Count) return;
                    current = null;
                    try { flow.Done(flow.Answers); }
                    catch (Exception error) { Plugin.Warn("A manual roll could not be applied: " + error.Message); }
                },
                () => {
                    // "Let the game roll" keeps the value the game already has.
                    if (ReferenceEquals(current, flow)) current = null;
                });
        }
        internal static void Reset()
        {
            if (current != null && current.Showing && current.Answers.Count < current.Prompts.Count) RollWindow.Close(current.Prompts[current.Answers.Count]);
            current = null; flows.Clear(); pending.Clear();
        }

        // ---- Ability scores at character creation ------------------------
        // EnterStage makes the first roll itself and then binds the values,
        // so that roll stays the game's; the manual roll replaces it at once.
        // The reroll button asks for the dice before the original runs.
        static void EnterPrefix() { entering++; }
        static Exception EnterFinalizer(Exception __exception) { if (entering > 0) entering--; return __exception; }
        static void EnterPostfix(CharacterStageAbilityScoresPanel __instance)
        {
            if (!Allowed || !Plugin.Settings.CharacterCreation || __instance == null) return;
            try { if ((bool)creationMode.GetValue(__instance)) Ask(__instance); }
            catch (Exception error) { Plugin.Log("Ability scores keep the game's dice: " + error.Message); }
        }
        static bool RerollPrefix(CharacterStageAbilityScoresPanel __instance)
        {
            if (replaying > 0 || entering > 0 || !Allowed || !Plugin.Settings.CharacterCreation || __instance == null) return true;
            // A click while the dice are being entered changes nothing.
            if (current != null || flows.Count > 0) return false;
            Ask(__instance);
            return false;
        }
        static void Ask(CharacterStageAbilityScoresPanel panel)
        {
            if (current != null || flows.Count > 0) return;
            var flow = new Flow();
            for (int index = 0; index < 6; index++)
                flow.Prompts.Add(new RollPrompt("Ability score " + (index + 1) + " of 6", "", "Roll 4d6", "The lowest die is dropped. The game sorts the six totals for you.", 6, 6, 6, 6));
            flow.StillValid = () => panel != null && panel.gameObject != null && panel.gameObject.activeInHierarchy;
            flow.Done = answers => {
                var scores = answers.Select(SumBestThree).ToList();
                manualScores = new Queue<int>(scores);
                replaying++;
                try { reroll.Invoke(panel, null); }
                finally { replaying--; manualScores = null; }
            };
            flows.Enqueue(flow);
        }
        internal static int SumBestThree(int[] faces)
        { return faces.OrderByDescending(face => face).Take(3).Sum(); }
        static bool SumBestPrefix(int diceNumber, RuleDefinitions.DieType diceType, int diceToKeep, ref int __result)
        {
            var scores = manualScores;
            if (replaying == 0 || scores == null || scores.Count == 0 || diceNumber != 4 || diceType != RuleDefinitions.DieType.D6 || diceToKeep != 3) return true;
            __result = scores.Dequeue();
            return false;
        }

        // ---- Optional: rolled hit points on levelling up -----------------
        static void AddLevelPostfix(RulesetCharacterHero __instance, CharacterClassDefinition classDefinition)
        {
            if (!Allowed || !Plugin.Settings.LevelUpHitPoints || __instance == null || classDefinition == null) return;
            try
            {
                int level;
                if (!__instance.ClassesAndLevels.TryGetValue(classDefinition, out level) || level <= 1) return;
                var settings = ServiceRepository.GetService<IGameSettingsService>();
                if (settings != null && settings.MaxHpOnLevelUp) return;
                var history = (List<int>)hitPointHistory.GetValue(__instance);
                if (history == null || history.Count == 0) return;
                pending.RemoveAll(entry => !entry.Hero.IsAlive || ReferenceEquals(entry.Hero.Target, __instance) && entry.Index >= history.Count - 1);
                pending.Add(new Pending { Hero = new WeakReference(__instance), Index = history.Count - 1, Average = history[history.Count - 1], Class = classDefinition, Level = level });
            }
            catch (Exception error) { Plugin.Log("Level-up hit points keep the game's value: " + error.Message); }
        }
        static void RemoveLevelPostfix(RulesetCharacterHero __instance)
        {
            try
            {
                var history = (List<int>)hitPointHistory.GetValue(__instance);
                int count = history == null ? 0 : history.Count;
                pending.RemoveAll(entry => !entry.Hero.IsAlive || ReferenceEquals(entry.Hero.Target, __instance) && entry.Index >= count);
            }
            catch (Exception) { }
        }
        static void FinalizePostfix(RulesetCharacterHero hero, bool allLevelsAtOnce)
        {
            if (hero == null) return;
            var mine = pending.Where(entry => ReferenceEquals(entry.Hero.Target, hero)).ToList();
            pending.RemoveAll(entry => !entry.Hero.IsAlive || ReferenceEquals(entry.Hero.Target, hero));
            if (allLevelsAtOnce || mine.Count == 0 || !Allowed || !Plugin.Settings.LevelUpHitPoints) return;
            foreach (var entry in mine)
            {
                var item = entry;
                int faces = RuntimeRolls.Max(item.Class.HitDice);
                if (faces <= 1) continue;
                var flow = new Flow();
                flow.Prompts.Add(new RollPrompt("Hit points", GameNames.ActorName(hero), GameNames.SpellName(item.Class) + " level " + item.Level,
                    "Roll your hit die. \"Let the game roll\" keeps the game's average of " + item.Average + ".", faces));
                flow.StillValid = () => {
                    var history = (List<int>)hitPointHistory.GetValue(hero);
                    return history != null && item.Index < history.Count && history[item.Index] == item.Average;
                };
                flow.Done = answers => {
                    var history = (List<int>)hitPointHistory.GetValue(hero);
                    int value = answers[0][0];
                    if (history == null || item.Index >= history.Count || value < 1 || value > faces) return;
                    history[item.Index] = value;
                    // As on levelling up: the new maximum, at full health.
                    hero.ComputeHitPoints(true);
                };
                flows.Enqueue(flow);
            }
        }
    }
}
