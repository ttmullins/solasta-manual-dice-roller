using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityModManagerNet;

namespace ManualDiceRoller
{
    [Serializable]
    // The XML serializer needs a type name that differs from UMM's own
    // base class name "ModSettings"; otherwise settings can never be saved.
    public sealed class ManualDiceRollerSettings : UnityModManager.ModSettings
    {
        public bool Attack = true, Ability = true, SavingThrows = true, Initiative = true, Death = true;
        public bool Damage = true, Healing = true, SpellPools = true, HitDice = true;
        public bool EnemyRolls;
        // Single-player additions: concentration checks inside damage, downtime
        // checks (travel foraging/gathering, camp crafting/scribing) and
        // bestiary monster identification.
        public bool Concentration = true, Downtime = true, Lore = true, RepeatSaves = true;
        // 0.7: lingering area/condition damage and its saves (Spirit Guardians,
        // Moonbeam, burning), class/spell feature dice (Deflect Missiles, spell
        // failure, slot preservation), stealth checks and the Perception rolled
        // against them, and character creation ability scores.
        public bool LingeringEffects = true, Features = true, Stealth = true, CharacterCreation = true;
        // Off by default: saves that end a condition when the character takes
        // damage (they interrupt an attack), Perception checks that reveal a
        // hidden object (the prompt itself gives the object away) and rolled
        // level-up hit points (the game itself always takes the average).
        public bool DamageSaves, HiddenPerception, LevelUpHitPoints;
        // 0.8: saves and damage dice of traps and environmental hazards (the
        // affected hero's player rolls them).
        public bool Traps = true;
        // Lets the extras above (and dialogue/gadget checks) run in multiplayer.
        // Part of the shared multiplayer profile, like every roll type.
        public bool MultiplayerExtras = true;
        public bool AutoRollNotices = true;
        public float WindowScale = 1, WindowX = -1, WindowY = -1;
        // UI only (never part of the multiplayer profile): the session roll
        // history panel and its position, and the settings sections folded
        // away (bits of Plugin.SettingsSection; "More" and "Optional" start folded).
        public bool ShowHistory;
        public float HistoryX = -1, HistoryY = -1;
        public int CollapsedSections = 6;
        public override void Save(UnityModManager.ModEntry entry) { Save(this, entry); }
    }

    public static class Plugin
    {
        const string HarmonyId = "ManualDiceRoller.Standalone";
        const string PristineGame = "a87e45cbd187e42e9d64a610850b39edba4896168b14d71ca71c5d32cf9e96ee";
        static UnityModManager.ModEntry entry;
        static Harmony harmony;
        static bool loaded;
        static bool rollbackFailed;
        public static bool Enabled { get; private set; }
        public static ManualDiceRollerSettings Settings { get; private set; } = new ManualDiceRollerSettings();
        public static bool IsMultiplayer { get { return Networked(); } }

        public static bool Load(UnityModManager.ModEntry mod)
        {
            if (loaded) return true;
            if (mod == null) return false;
            entry = mod; Enabled = false;
            if (rollbackFailed) {
                Error("Manual Dice Roller could not remove all of its patches after a failed load. Restart Solasta before trying again.");
                return false;
            }
            var callbacks = new CallbackSnapshot(mod);
            try {
                if (typeof(Harmony).Assembly.GetName().Version != new Version(2, 3, 6, 0)) {
                    throw new InvalidOperationException("Manual Dice Roller needs Unity Mod Manager 0.32.4 with Harmony 2.3.6.0. Restart the game after updating the loader.");
                }
                bool repaired = RepairSettingsFile(mod);
                Settings = UnityModManager.ModSettings.Load<ManualDiceRollerSettings>(mod) ?? new ManualDiceRollerSettings();
                if (repaired) SaveSettings();
                if (float.IsNaN(Settings.WindowScale) || float.IsInfinity(Settings.WindowScale)) Settings.WindowScale = 1;
                Settings.WindowScale = Math.Max(.75f, Math.Min(1.5f, Settings.WindowScale));
                if (float.IsNaN(Settings.WindowX) || float.IsInfinity(Settings.WindowX)) Settings.WindowX = -1;
                if (float.IsNaN(Settings.WindowY) || float.IsInfinity(Settings.WindowY)) Settings.WindowY = -1;
                string error;
                if (!GamePreflight(out error) || !MultiplayerRolls.Preflight(out error) || !NativePatches.Preflight(out error) || !InputGuards.Preflight(out error) || !RestPatches.Preflight(out error) || !NativeNetworkScheduler.Preflight(out error)
                    || !ModFeatures.Preflight(out error)) {
                    throw new InvalidOperationException("Manual Dice Roller was not enabled: " + error);
                }
                harmony = new Harmony(HarmonyId);
                NativePatches.Install(harmony);
                RuntimeRolls.Install(harmony);
                EffectPatches.Install(harmony);
                RestPatches.Install(harmony);
                InputGuards.Install(harmony);
                NativeNetworkScheduler.Install(harmony);
                ModFeatures.Install(harmony);
                mod.OnToggle = Toggle;
                mod.OnGUI = DrawSettings;
                mod.OnSaveGUI = SaveGui;
                mod.OnUnload = Unload;
                mod.OnUpdate = CheckCompatibility;
                RollWindow.Create();
                // Initialization can immediately advertise to a joined room.
                // Its profile must contain the final enabled state; the catch
                // below still disables and rolls back any failed initialization.
                Enabled = true;
                MultiplayerRolls.Initialize();
                loaded = true;
                LogDiagnostics(mod);
                Log("Manual Dice Roller loaded. Multiplayer requires this exact build and matching roll settings on every peer.");
                return true;
            } catch (Exception error) {
                // Every cleanup is independent: a failed native reset or UI
                // teardown must not leave a partially installed plugin active.
                Enabled = false; loaded = false;
                callbacks.RestoreAssigned(mod);
                Cleanup(ModFeatures.Detach, "The incomplete feature hooks could not be removed: ");
                Cleanup(MultiplayerRolls.Shutdown, "The incomplete multiplayer transport could not be removed: ");
                Cleanup(RuntimeRolls.Reset, "The incomplete roll session could not be reset: ");
                Cleanup(RollWindow.DestroyWindow, "The incomplete roll window could not be removed: ");
                if (harmony != null && !Cleanup(delegate { harmony.UnpatchAll(HarmonyId); }, "The incomplete plugin patches could not be removed: ")) rollbackFailed = true;
                // FaultCompatibility is a separate owner and remains installed
                // so Harmony can rebuild the game's native fault handlers.
                harmony = null; loaded = false;
                Error("Manual Dice Roller could not load: " + error);
                return false;
            }
        }
        sealed class CallbackSnapshot
        {
            readonly Func<UnityModManager.ModEntry, bool, bool> toggle;
            readonly Action<UnityModManager.ModEntry> gui, save;
            readonly Func<UnityModManager.ModEntry, bool> unload;
            readonly Action<UnityModManager.ModEntry, float> update;
            internal CallbackSnapshot(UnityModManager.ModEntry mod)
            { toggle = mod.OnToggle; gui = mod.OnGUI; save = mod.OnSaveGUI; unload = mod.OnUnload; update = mod.OnUpdate; }
            internal void RestoreAssigned(UnityModManager.ModEntry mod)
            {
                if (mod.OnToggle == Toggle) mod.OnToggle = toggle;
                if (mod.OnGUI == DrawSettings) mod.OnGUI = gui;
                if (mod.OnSaveGUI == SaveGui) mod.OnSaveGUI = save;
                if (mod.OnUnload == Unload) mod.OnUnload = unload;
                if (mod.OnUpdate == CheckCompatibility) mod.OnUpdate = update;
            }
        }
        static void SaveGui(UnityModManager.ModEntry mod) { SaveSettings(); }
        static bool Cleanup(Action action, string message)
        {
            try { action(); return true; }
            catch (Exception error) { Error(message + error); return false; }
        }
        static bool GamePreflight(out string error)
        {
            error=null;
            try
            {
                if (UnsupportedRulesMod)
                { error="This standalone preview cannot run with Unfinished Business. Disable it before enabling Manual Dice Roller."; return false; }
                using(var stream=File.OpenRead(typeof(RulesetActor).Assembly.Location))
                using(var hash=SHA256.Create())
                    if(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant()!=PristineGame)
                    { error="This build requires the original supplied game version. Restore the game files with Steam before installing it."; return false; }
                return true;
            }
            catch(Exception exception) { error="Could not verify the game version: "+exception.Message; return false; }
        }
        static bool Toggle(UnityModManager.ModEntry mod, bool enabled)
        {
            if (enabled && !loaded) return false;
            if(Networked()&&enabled!=Enabled) {
                Warn("Leave the multiplayer session before enabling or disabling Manual Dice Roller.");
                return false;
            }
            if(enabled&&UnsupportedRulesMod) { Warn("Disable Unfinished Business before enabling this standalone preview."); return false; }
            Enabled = enabled && loaded;
            if (!Enabled) {
                Cleanup(ModFeatures.Flush, "The pending deferred rolls could not be completed: ");
                Cleanup(RuntimeRolls.Reset, "The disabled roll session could not be reset: ");
                Cleanup(RollWindow.Close, "The disabled roll window could not be closed: ");
            }
            return true;
        }
        static bool Unload(UnityModManager.ModEntry mod)
        {
            if(Networked()) {
                Warn("Leave the multiplayer session and restart Solasta before unloading this plugin.");
                return false;
            }
            Enabled = false;
            Cleanup(ModFeatures.Flush, "The pending deferred rolls could not be completed: ");
            Cleanup(RuntimeRolls.Reset, "The disabled roll session could not be reset: ");
            Cleanup(ModFeatures.Detach, "The feature hooks could not be removed: ");
            Cleanup(RollWindow.DestroyWindow, "The disabled roll window could not be removed: ");
            // A suspended native iterator has added continuation states and
            // locals. Removing its await patch before it resumes would discard
            // the ongoing action. Disabling is safe; unloading needs a restart.
            Warn("Manual rolls are disabled. Close and restart Solasta to unload or update this plugin safely.");
            return false;
        }
        public static bool Networked()
        {
            var networking = ServiceRepository.GetService<INetworkingService>();
            return (networking != null && networking.IsMultiplayerGame) || (Photon.Pun.PhotonNetwork.InRoom && !Photon.Pun.PhotonNetwork.OfflineMode);
        }
        public static bool UnsupportedRulesMod
        { get { return AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name=="SolastaUnfinishedBusiness"); } }
        static void CheckCompatibility(UnityModManager.ModEntry mod,float elapsed)
        {
            MultiplayerRolls.Update();
            RuntimeRolls.UpdateNetworkCompletion();
            if(!Enabled||!UnsupportedRulesMod) return;
            if(Networked()) MultiplayerRolls.Fail("Unfinished Business was loaded during a standalone multiplayer session.");
            Enabled=false; RuntimeRolls.Reset();
            Warn("Manual rolls were disabled because Unfinished Business was loaded. This standalone preview needs its own compatibility integration.");
        }
        // Raw dice of class and spell features (Deflect Missiles, spell
        // failure, slot preservation). The value is unused by the game's enum.
        internal const RuleDefinitions.RollContext FeatureDice = (RuleDefinitions.RollContext)0x100;
        // Damage dice of traps and hazards (no source character).
        internal const RuleDefinitions.RollContext TrapDice = (RuleDefinitions.RollContext)0x200;
        public static bool Allows(RuleDefinitions.RollContext context)
        {
            switch (context) {
                case RuleDefinitions.RollContext.AttackRoll: return Settings.Attack;
                case RuleDefinitions.RollContext.AbilityCheck: return Settings.Ability;
                case RuleDefinitions.RollContext.SavingThrow: return Settings.SavingThrows;
                case RuleDefinitions.RollContext.ConcentrationCheck: return Settings.SavingThrows && Settings.Concentration;
                case RuleDefinitions.RollContext.InitiativeRoll: return Settings.Initiative;
                case RuleDefinitions.RollContext.DeathSavingThrow: return Settings.Death;
                case RuleDefinitions.RollContext.AttackDamageValueRoll:
                case RuleDefinitions.RollContext.MagicDamageValueRoll: return Settings.Damage;
                case RuleDefinitions.RollContext.HealValueRoll:
                case RuleDefinitions.RollContext.TemporaryHPValueRoll: return Settings.Healing;
                case RuleDefinitions.RollContext.None: return Settings.SpellPools || Settings.HitDice;
                case FeatureDice: return Settings.Features;
                case TrapDice: return Settings.Traps && Settings.Damage;
                default: return false;
            }
        }
        // Builds before 0.5.1 could create Settings.xml and then fail to write
        // it, leaving an empty file that UMM reports as unreadable at every
        // start. Such a file holds no settings; it is replaced with defaults.
        static bool RepairSettingsFile(UnityModManager.ModEntry mod)
        {
            try
            {
                if (mod == null || string.IsNullOrEmpty(mod.Path)) return false;
                string file = Path.Combine(mod.Path, "Settings.xml");
                if (!File.Exists(file)) return false;
                string text = File.ReadAllText(file);
                if (text.Trim().Length != 0 && text.IndexOf("<ManualDiceRollerSettings", StringComparison.Ordinal) >= 0) return false;
                File.Delete(file);
                Log("Replaced an empty or outdated Settings.xml with the default settings.");
                return true;
            }
            catch (Exception error) { Warn("Settings.xml could not be checked: " + error.Message); return false; }
        }
        public static void SaveSettings()
        {
            if (entry == null) return;
            try { Settings.Save(entry); }
            catch (Exception error) { Warn("Settings could not be saved: " + error.Message); }
        }
        public static void Log(string value) { try { if (entry != null && entry.Logger != null) entry.Logger.Log(value); } catch (Exception) { } }
        public static void Warn(string value) { try { if (entry != null && entry.Logger != null) entry.Logger.Warning(value); } catch (Exception) { } }
        public static void Error(string value) { try { if (entry != null && entry.Logger != null) entry.Logger.Error(value); } catch (Exception) { } }
        static string Json(string value)
        {
            var result = new StringBuilder("\"");
            foreach (char c in value ?? "") {
                switch (c) {
                    case '\\': result.Append("\\\\"); break;
                    case '"': result.Append("\\\""); break;
                    case '\b': result.Append("\\b"); break;
                    case '\f': result.Append("\\f"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    default: if (c < 32) result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); else result.Append(c); break;
                }
            }
            return result.Append('"').ToString();
        }
        static string AssemblyPath(Assembly assembly) { try { return assembly.Location ?? ""; } catch (Exception) { return ""; } }
        static string AssemblyHash(Assembly assembly)
        {
            try { using (var stream = File.OpenRead(AssemblyPath(assembly))) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
            catch (Exception error) { Warn("A runtime assembly fingerprint could not be read: " + error.Message); return ""; }
        }
        static void LogDiagnostics(UnityModManager.ModEntry mod)
        {
            try {
                var plugin = typeof(Plugin).Assembly; var umm = typeof(UnityModManager).Assembly; var runtimeHarmony = typeof(Harmony).Assembly;
                Log("MDR_DIAGNOSTICS {\"utc\":" + Json(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                    + ",\"version\":" + Json(mod.Info == null ? "" : mod.Info.Version)
                    + ",\"plugin_sha256\":" + Json(AssemblyHash(plugin)) + ",\"plugin_path\":" + Json(AssemblyPath(plugin))
                    + ",\"umm_version\":" + Json(umm.GetName().Version.ToString()) + ",\"umm_sha256\":" + Json(AssemblyHash(umm)) + ",\"umm_path\":" + Json(AssemblyPath(umm))
                    + ",\"harmony_version\":" + Json(runtimeHarmony.GetName().Version.ToString()) + ",\"harmony_sha256\":" + Json(AssemblyHash(runtimeHarmony)) + ",\"harmony_path\":" + Json(AssemblyPath(runtimeHarmony))
                    + ",\"unity_version\":" + Json(Application.unityVersion)
                    + ",\"mono_runtime\":" + (Type.GetType("Mono.Runtime") != null ? "true" : "false")
                    + ",\"pointer_size\":" + IntPtr.Size.ToString(CultureInfo.InvariantCulture)
                    + ",\"clr_version\":" + Json(Environment.Version.ToString()) + "}");
            } catch (Exception error) { Warn("Runtime diagnostics could not be recorded: " + error.Message); }
        }
        // ---- settings page (UI only) -------------------------------------------
        // A preset sets the roll types only. The window, notice and history
        // options and "Use these in multiplayer too" keep their values.
        internal enum RollPreset { Everything, Party, Combat, Minimal }
        internal static readonly string[] PresetNames = { "Everything manual", "Party dice (recommended)", "Combat only", "Minimal" };
        internal static void ApplyPreset(ManualDiceRollerSettings settings, RollPreset preset)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            bool everything = preset == RollPreset.Everything, party = everything || preset == RollPreset.Party, combat = party || preset == RollPreset.Combat;
            // Every preset: attack rolls, ability checks and saving throws.
            settings.Attack = settings.Ability = settings.SavingThrows = true;
            // Combat and broader.
            settings.Initiative = settings.Death = settings.Damage = settings.Healing = combat;
            settings.Concentration = settings.RepeatSaves = settings.LingeringEffects = settings.Features = settings.Traps = settings.SpellPools = combat;
            // Party dice (the defaults) and broader: the remaining standard types.
            settings.HitDice = settings.Stealth = settings.Lore = settings.Downtime = settings.CharacterCreation = party;
            // Everything: enemy dice and the optional rolls.
            settings.EnemyRolls = settings.DamageSaves = settings.HiddenPerception = settings.LevelUpHitPoints = everything;
        }
        // The roll types a preset sets, in a fixed order.
        static bool[] RollTypes(ManualDiceRollerSettings s)
        {
            return new[] { s.Attack, s.Ability, s.SavingThrows, s.Initiative, s.Death, s.Damage, s.Healing, s.SpellPools, s.HitDice, s.EnemyRolls,
                s.Concentration, s.RepeatSaves, s.LingeringEffects, s.Stealth, s.Traps, s.Lore, s.Features, s.Downtime, s.CharacterCreation,
                s.DamageSaves, s.HiddenPerception, s.LevelUpHitPoints };
        }
        static bool[][] presetTypes;
        // The preset whose roll types equal the current ones, if any.
        internal static RollPreset? ActivePreset(ManualDiceRollerSettings settings)
        {
            if (settings == null) return null;
            if (presetTypes == null) {
                var table = new bool[PresetNames.Length][];
                for (int i = 0; i < table.Length; i++) { var probe = new ManualDiceRollerSettings(); ApplyPreset(probe, (RollPreset)i); table[i] = RollTypes(probe); }
                presetTypes = table;
            }
            var current = RollTypes(settings);
            for (int i = 0; i < presetTypes.Length; i++) if (presetTypes[i].SequenceEqual(current)) return (RollPreset)i;
            return null;
        }
        // Foldable settings sections (bits of ManualDiceRollerSettings.CollapsedSections).
        [Flags] internal enum SettingsSection { RollTypes = 1, More = 2, Optional = 4, Multiplayer = 8, Window = 16 }
        static GUIStyle sectionStyle, headingStyle, noteStyle, descriptionStyle, summaryStyle, introStyle;
        static GUISkin settingsSkin;
        static readonly Color SettingsMuted = new Color(.66f, .70f, .74f, 1), SettingsAccent = new Color(.91f, .78f, .50f, 1);
        static void SettingsStyles()
        {
            if (sectionStyle != null && ReferenceEquals(settingsSkin, GUI.skin)) return;
            var label = GUI.skin.label; settingsSkin = GUI.skin;
            int size = label.fontSize > 0 ? label.fontSize : 13;
            headingStyle = new GUIStyle(label) { fontStyle = FontStyle.Bold, fontSize = size + 2, wordWrap = false };
            sectionStyle = new GUIStyle(headingStyle);
            sectionStyle.hover.textColor = SettingsAccent; sectionStyle.active.textColor = SettingsAccent;
            noteStyle = new GUIStyle(label) { wordWrap = true, fontStyle = FontStyle.Italic };
            descriptionStyle = new GUIStyle(label) { wordWrap = true, fontSize = Math.Max(10, size - 1), padding = new RectOffset(22, 4, 0, 0), margin = new RectOffset(4, 4, 0, 6) };
            descriptionStyle.normal.textColor = SettingsMuted;
            summaryStyle = new GUIStyle(label) { wordWrap = false };
            summaryStyle.normal.textColor = SettingsMuted;
            introStyle = new GUIStyle(label) { wordWrap = true };
        }
        static bool Section(SettingsSection section, string title, string summary)
        {
            GUILayout.Space(12);
            // The fold takes effect on the next frame so this frame's layout stays stable.
            bool open = (Settings.CollapsedSections & (int)section) == 0;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button((open ? "▼  " : "►  ") + title, sectionStyle, GUILayout.ExpandWidth(false))) Settings.CollapsedSections ^= (int)section;
            if (!string.IsNullOrEmpty(summary)) GUILayout.Label(summary, summaryStyle, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return open;
        }
        static string OnCount(params bool[] values) { return "·  " + values.Count(v => v) + " of " + values.Length + " on"; }
        static void Note(string text) { GUILayout.Label(text, noteStyle); }
        // A setting and one muted line on what it covers.
        static bool Option(bool value, string label, string description)
        {
            value = GUILayout.Toggle(value, label);
            GUILayout.Label(description, descriptionStyle);
            return value;
        }
        static void BeginColumns() { GUILayout.BeginHorizontal(); GUILayout.Space(14); GUILayout.BeginVertical(GUILayout.Width(360)); }
        static void NextColumn() { GUILayout.EndVertical(); GUILayout.Space(16); GUILayout.BeginVertical(GUILayout.Width(360)); }
        static void EndColumns() { GUILayout.EndVertical(); GUILayout.FlexibleSpace(); GUILayout.EndHorizontal(); }
        static void DrawSettings(UnityModManager.ModEntry mod)
        {
            SettingsStyles();
            var s = Settings;
            bool networked = Networked(), settingsEnabled = GUI.enabled;
            GUILayout.Label(UnsupportedRulesMod ? "Disabled: this version cannot run with Unfinished Business." : networked ? MultiplayerRolls.Status : "Manual rolls for your party. Enter the faces you rolled; the game still applies bonuses, advantage and features.", introStyle);

            // Presets set every roll type below in one click.
            GUILayout.Space(8);
            var active = ActivePreset(s);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Presets", headingStyle, GUILayout.ExpandWidth(false));
            GUILayout.Space(10);
            if (networked) GUI.enabled = false;
            for (int i = 0; i < PresetNames.Length; i++)
            {
                bool current = active.HasValue && (int)active.Value == i;
                if (GUILayout.Toggle(current, PresetNames[i], GUI.skin.button, GUILayout.ExpandWidth(false)) && !current) ApplyPreset(s, (RollPreset)i);
            }
            GUI.enabled = settingsEnabled;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(active.HasValue ? "The roll types below match \"" + PresetNames[(int)active.Value] + "\". Any of them can still be changed one by one."
                : "Custom roll types. A preset replaces only the roll types; window and multiplayer options are kept.", descriptionStyle);
            if (networked) Note("Roll types are locked during multiplayer. Every player must select the same roll types before joining.");

            if (Section(SettingsSection.RollTypes, "Roll types", OnCount(s.Attack, s.Ability, s.SavingThrows, s.Initiative, s.Death, s.Damage, s.Healing, s.SpellPools, s.HitDice, s.EnemyRolls)))
            {
                if (networked) GUI.enabled = false;
                BeginColumns();
                s.Attack = Option(s.Attack, "Attack rolls", "The d20 of weapon and spell attacks.");
                s.Ability = Option(s.Ability, "Ability and skill checks", "Skill, tool and ability checks, including contests.");
                s.SavingThrows = Option(s.SavingThrows, "Saving throws", "Saves against spells, traps and conditions.");
                s.Initiative = Option(s.Initiative, "Initiative", "Each hero's d20 when a fight starts.");
                s.Death = Option(s.Death, "Death saving throws", "The d20 a downed hero rolls each turn.");
                NextColumn();
                s.Damage = Option(s.Damage, "Damage dice", "Weapon, spell and other damage dice.");
                s.Healing = Option(s.Healing, "Healing and temporary hit points", "Healing spells, potions and temporary hit point dice.");
                s.SpellPools = Option(s.SpellPools, "Spell dice pools", "Hit point pools of spells such as Sleep and Color Spray.");
                s.HitDice = Option(s.HitDice, "Rest hit dice", "Hit dice spent to heal during a short rest.");
                s.EnemyRolls = Option(s.EnemyRolls, "Also ask for enemy and NPC dice", "Monsters and characters outside your party roll too.");
                EndColumns();
                GUI.enabled = settingsEnabled;
            }
            if (Section(SettingsSection.More, "More roll types", OnCount(s.Concentration, s.RepeatSaves, s.LingeringEffects, s.Stealth, s.Traps, s.Lore, s.Features, s.Downtime, s.CharacterCreation)))
            {
                if (networked) GUI.enabled = false;
                BeginColumns();
                s.Concentration = Option(s.Concentration, "Concentration checks after damage", "The save to keep a concentration spell going.");
                s.RepeatSaves = Option(s.RepeatSaves, "Repeat saves at the start and end of a turn", "Saves that end Hold Person, fear and similar effects.");
                s.LingeringEffects = Option(s.LingeringEffects, "Lingering spell and condition damage", "Spirit Guardians, Moonbeam, burning and their saves.");
                s.Stealth = Option(s.Stealth, "Stealth checks", "Sneaking, and the Perception rolled against it.");
                s.Traps = Option(s.Traps, "Traps and hazards", "Trap and hazard saves and damage dice.");
                NextColumn();
                s.Lore = Option(s.Lore, "Monster identification (bestiary)", "The check that reveals a creature's entry.");
                s.Features = Option(s.Features, "Deflect Missiles, spell failure and slot dice", "Dice of class and spell features.");
                s.Downtime = Option(s.Downtime, "Travel foraging, crafting and scribing", "Checks made while travelling and in camp.");
                s.CharacterCreation = Option(s.CharacterCreation, "Ability scores at character creation", "Roll a new hero's ability scores with real dice.");
                EndColumns();
                GUI.enabled = settingsEnabled;
            }
            if (Section(SettingsSection.Optional, "Optional rolls", OnCount(s.DamageSaves, s.HiddenPerception, s.LevelUpHitPoints)))
            {
                if (networked) GUI.enabled = false;
                BeginColumns();
                s.DamageSaves = Option(s.DamageSaves, "Saves when taking damage ends a condition", "These saves interrupt the attack that dealt the damage.");
                s.HiddenPerception = Option(s.HiddenPerception, "Perception for hidden objects and traps", "The prompt itself shows that something is there.");
                NextColumn();
                s.LevelUpHitPoints = Option(s.LevelUpHitPoints, "Roll hit points when levelling up", "Replaces the game's fixed average (single-player only).");
                EndColumns();
                GUI.enabled = settingsEnabled;
                Note("Optional rolls change how the game plays: a hidden-object prompt tells you something is there, and rolled hit points replace the game's fixed average (single-player only).");
            }
            if (Section(SettingsSection.Multiplayer, "Multiplayer", networked ? "·  locked during this session" : ""))
            {
                if (networked) GUI.enabled = false;
                BeginColumns();
                s.MultiplayerExtras = Option(s.MultiplayerExtras, "Use these in multiplayer too", "Lets \"More roll types\" and dialogue checks ask for dice online.");
                EndColumns();
                GUI.enabled = settingsEnabled;
                Note("Dialogue checks, trap and lock checks, and the roll types above pause the game while you roll. In multiplayer they need \"Use these in multiplayer too\" on every player's computer; turn it off if a session stops with a dice error.");
                Note("Each character's controlling player enters its dice; everyone else sees who is rolling.");
            }
            if (Section(SettingsSection.Window, "Roll window & history", ""))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(14);
                GUILayout.Label("Size", GUILayout.Width(60));
                s.WindowScale = GUILayout.HorizontalSlider(s.WindowScale, .75f, 1.5f, GUILayout.Width(240));
                GUILayout.Label(Math.Round(s.WindowScale * 100) + "%", GUILayout.Width(55));
                if (GUILayout.Button("Re-center", GUILayout.Width(110))) { s.WindowX = -1; s.WindowY = -1; SaveSettings(); }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
                BeginColumns();
                s.AutoRollNotices = Option(s.AutoRollNotices, "Show a short notice when the game rolls a d20 for your party", "A corner notice for each d20 you did not enter yourself.");
                NextColumn();
                s.ShowHistory = Option(s.ShowHistory, "Show roll history panel", "The last " + RollHistoryCapacity + " rolls this session: yours, the game's and other players'.");
                EndColumns();
                Note("Drag the window's header to move it. Type faces or click them; Tab and the arrow keys move between dice, Enter confirms, and Escape lets the game roll.");
            }
        }
        // Mirrors RollHistory.Capacity (kept local so this class compiles alone).
        const int RollHistoryCapacity = 40;
    }

    // These guards leave Unity's raw input and IMGUI intact. Only the game's
    // scene, command queue and EventSystem are blocked by the visible modal.
    internal static class InputGuards
    {
        static readonly BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly Dictionary<string, MethodInfo> methods = new Dictionary<string, MethodInfo>();
        static FieldInfo[] pointerFlags;
        static FieldInfo mainDrag, selectionDrag, rectangle, pointerData, raycastResults;
        static MethodInfo clearSelection;
        static MethodInfo Method(Type type, string name, Type result, params Type[] parameters)
        {
            var value = type.GetMethod(name, Instance | BindingFlags.Static, null, parameters, null);
            if (value == null || value.ReturnType != result) throw new InvalidOperationException("Unsupported game input method: " + type.Name + "." + name);
            return value;
        }
        static FieldInfo Field(Type type, string name, Type expected)
        {
            var value = type.GetField(name, Instance);
            if (value == null || value.FieldType != expected) throw new InvalidOperationException("Unsupported game input field: " + type.Name + "." + name);
            return value;
        }
        static MethodInfo Lifecycle(Type type, string name)
        {
            var value = type.GetMethods(Instance).SingleOrDefault(m => m.Name == name);
            if (value == null || value.ReturnType != typeof(void)) throw new InvalidOperationException("Unsupported game lifecycle method: " + type.Name + "." + name);
            return value;
        }
        internal static bool Preflight(out string error)
        {
            error = null; methods.Clear();
            try {
                pointerFlags = new[] { "primaryPointerArmed", "primaryPointerDragged", "secondaryPointerArmed", "secondaryPointerDragged", "tertiaryPointerArmed", "tertiaryPointerDragged" }.Select(n => Field(typeof(View), n, typeof(bool))).ToArray();
                mainDrag = Field(typeof(CursorLocation), "mainDragStarted", typeof(bool));
                selectionDrag = Field(typeof(CursorLocationExploration), "isDragging", typeof(bool));
                rectangle = Field(typeof(CursorLocationExploration), "selectionRectangle", typeof(RectTransform));
                pointerData = Field(typeof(PointerInputModule), "m_PointerData", typeof(Dictionary<int, PointerEventData>));
                raycastResults = Field(typeof(InputPointerModule), "raycastResults", typeof(List<RaycastResult>));
                clearSelection = Method(typeof(PointerInputModule), "ClearSelection", typeof(void));
                methods.Add("view", Method(typeof(View), "HandleInputs", typeof(void)));
                methods.Add("covered", Method(typeof(DebugOverlayManager), "IsMouseCovered", typeof(bool), typeof(Vector3)));
                methods.Add("commands", Method(typeof(GuiManager), "OnCommandJustActivated", typeof(void), typeof(Queue<int>)));
                methods.Add("camera", Method(typeof(GuiManager), "get_GrabbedCameraControls", typeof(bool)));
                methods.Add("pointer", Method(typeof(InputPointerModule), "Process", typeof(void)));
                methods.Add("raycast", Method(typeof(InputPointerModule), "RaycastPointer", typeof(List<RaycastResult>)));
                methods.Add("enter", Lifecycle(typeof(GameLocationManager), "EnterLocation"));
                methods.Add("exit", Lifecycle(typeof(GameLocationManager), "ExitLocation"));
                methods.Add("destroy", Lifecycle(typeof(GameManager), "DestroyGame"));
                return true;
            } catch (Exception failure) { error = failure.Message; methods.Clear(); return false; }
        }
        static HarmonyMethod Own(string name) { return new HarmonyMethod(typeof(InputGuards).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)); }
        internal static void Install(Harmony harmony)
        {
            if (methods.Count != 9) throw new InvalidOperationException("Input preflight has not succeeded.");
            harmony.Patch(methods["view"], prefix: Own(nameof(ViewPrefix)));
            harmony.Patch(methods["covered"], prefix: Own(nameof(CoveredPrefix)));
            harmony.Patch(methods["commands"], prefix: Own(nameof(CommandsPrefix)));
            harmony.Patch(methods["camera"], postfix: Own(nameof(CameraPostfix)));
            harmony.Patch(methods["pointer"], prefix: Own(nameof(PointerPrefix)));
            harmony.Patch(methods["raycast"], prefix: Own(nameof(RaycastPrefix)));
            foreach (string key in new[] { "enter", "exit", "destroy" }) harmony.Patch(methods[key], prefix: Own(nameof(LifecyclePrefix)));
        }
        static bool ViewPrefix(View __instance)
        {
            if (!RollWindow.BlocksInput()) return true;
            ClearViewGestures(__instance);
            if (__instance.CameraController != null) __instance.CameraController.ResetInputs();
            var cursor = __instance.CursorService == null ? null : __instance.CursorService.CurrentCursor;
            if (cursor is CursorLocation) mainDrag.SetValue(cursor, false);
            if (cursor is CursorLocationExploration) {
                selectionDrag.SetValue(cursor, false);
                var selection = rectangle.GetValue(cursor) as RectTransform;
                if (selection != null) selection.gameObject.SetActive(false);
            }
            return false;
        }
        static void ClearViewGestures(View view) { foreach (var field in pointerFlags) field.SetValue(view, false); }
        static bool CoveredPrefix(ref bool __result)
        {
            if (!RollWindow.BlocksInput()) return true;
            __result = true; return false;
        }
        static bool CommandsPrefix(Queue<int> __0)
        {
            if (!RollWindow.BlocksInput()) return true;
            if (__0 != null) __0.Clear(); return false;
        }
        static void CameraPostfix(ref bool __result) { if (RollWindow.BlocksInput()) __result = true; }
        static bool PointerPrefix(InputPointerModule __instance)
        {
            if (!RollWindow.BlocksInput()) return true;
            if (!cancellingModules.Add(__instance)) return false;
            try {
                var data = pointerData.GetValue(__instance) as Dictionary<int, PointerEventData>;
                if (data != null) foreach (var pointer in data.Values.ToArray()) CancelPointerState(pointer);
                try { clearSelection.Invoke(__instance, null); }
                catch (Exception error) { InputFailure("The native UI selection could not be cleared: ", error); }
                return false;
            } finally { cancellingModules.Remove(__instance); }
        }
        static readonly HashSet<InputPointerModule> cancellingModules = new HashSet<InputPointerModule>();
        static readonly HashSet<PointerEventData> cancellingPointers = new HashSet<PointerEventData>();
        static void CancelPointerState(PointerEventData pointer)
        {
            if (pointer == null || !cancellingPointers.Add(pointer)) return;
            try {
                pointer.eligibleForClick = false;
                try { if (pointer.pointerPress != null) ExecuteEvents.Execute(pointer.pointerPress, pointer, ExecuteEvents.pointerUpHandler); }
                catch (Exception error) { InputFailure("The native UI pointer release failed: ", error); }
                try { if (pointer.pointerDrag != null && pointer.dragging) ExecuteEvents.Execute(pointer.pointerDrag, pointer, ExecuteEvents.endDragHandler); }
                catch (Exception error) { InputFailure("The native UI drag release failed: ", error); }
            } finally {
                // Handlers can reenter the input module or change the event.
                // Always revoke every captured gesture after their callbacks.
                pointer.eligibleForClick = false;
                pointer.pointerPress = null; pointer.rawPointerPress = null; pointer.pointerDrag = null;
                pointer.dragging = false; pointer.pointerPressRaycast = new RaycastResult(); pointer.pointerCurrentRaycast = new RaycastResult();
                cancellingPointers.Remove(pointer);
            }
        }
        static void InputFailure(string message, Exception error) { try { Plugin.Warn(message + error); } catch (Exception) { } }
        static bool RaycastPrefix(InputPointerModule __instance, ref List<RaycastResult> __result)
        {
            if (!RollWindow.BlocksInput()) return true;
            __result = (List<RaycastResult>)raycastResults.GetValue(__instance);
            if (__result == null) { __result = new List<RaycastResult>(); raycastResults.SetValue(__instance, __result); }
            __result.Clear(); return false;
        }
        // A location load or exit: pending rolls are dropped, and manual
        // multiplayer dice paused earlier start over (the players reloaded).
        static void LifecyclePrefix() { try { ModFeatures.Drop(); RuntimeRolls.Reset(); MultiplayerRolls.Rearm(); } finally { RollWindow.ResetCapture(); } }
    }
}
