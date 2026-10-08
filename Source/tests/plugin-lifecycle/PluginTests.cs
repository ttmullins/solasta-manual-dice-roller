using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using HarmonyLib;
using ManualDiceRoller;
using UnityModManagerNet;

static class PluginTests
{
    const string Owner = "ManualDiceRoller.Standalone", Compatibility = "ManualDiceRoller.FaultCompatibility", Foreign = "UnrelatedMod";
    static int assertions, cases, failures;
    static UnityModManager.ModEntry mod;
    static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static T Field<T>(string name) { return (T)typeof(Plugin).GetField(name, PrivateStatic).GetValue(null); }
    static bool Invoke(string name, params object[] arguments)
    {
        try { return (bool)typeof(Plugin).GetMethod(name, PrivateStatic).Invoke(null, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    static bool Patched(string name, string owner)
    {
        var info = Harmony.GetPatchInfo(typeof(TestTargets).GetMethod(name));
        return info != null && info.Owners.Contains(owner);
    }
    static bool AnyOwnerPatches() { return Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m).Owners.Contains(Owner)); }
    static void Reset()
    {
        Harmony.FailUnpatch = false;
        new Harmony(Owner).UnpatchAll(Owner); new Harmony(Compatibility).UnpatchAll(Compatibility); new Harmony(Foreign).UnpatchAll(Foreign);
        Harmony.UnpatchAttempts = 0;
        foreach (string name in new[] { "loaded", "rollbackFailed" }) typeof(Plugin).GetField(name, PrivateStatic).SetValue(null, false);
        typeof(Plugin).GetField("harmony", PrivateStatic).SetValue(null, null);
        typeof(Plugin).GetField("entry", PrivateStatic).SetValue(null, null);
        typeof(Plugin).GetField("<Enabled>k__BackingField", PrivateStatic).SetValue(null, false);
        TestBoundary.ResetFaults(); mod = new UnityModManager.ModEntry();
    }
    static void Case(string name, Action body)
    {
        Reset(); cases++;
        try { body(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error); }
    }
    static void ClearedCallbacks() { Check(mod.OnToggle == null && mod.OnGUI == null && mod.OnSaveGUI == null && mod.OnUnload == null && mod.OnUpdate == null, "Failed load clears its assigned callbacks"); }
    static void RolledBack()
    {
        Check(!Plugin.Enabled && !Field<bool>("loaded") && Field<Harmony>("harmony") == null, "Failed load clears enabled/loaded/harmony state");
        Check(TestBoundary.Resets == 1 && TestBoundary.Destroys == 1 && TestBoundary.ResetSawDisabled && TestBoundary.DestroySawDisabled, "Rollback attempts independent runtime and UI cleanup while disabled");
        Check(!AnyOwnerPatches(), "Rollback removes all plugin owner patches"); ClearedCallbacks();
    }
    static string Hash(string path) { using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    // Every roll type a preset sets (all serialized bool settings except the
    // multiplayer option and the UI-only notice/history switches).
    static readonly string[] RollTypes = { "Attack", "Ability", "SavingThrows", "Initiative", "Death", "Damage", "Healing", "SpellPools", "HitDice", "EnemyRolls",
        "Concentration", "RepeatSaves", "LingeringEffects", "Stealth", "Traps", "Lore", "Features", "Downtime", "CharacterCreation", "DamageSaves", "HiddenPerception", "LevelUpHitPoints" };
    static readonly string[] UiOnly = { "AutoRollNotices", "WindowScale", "WindowX", "WindowY", "ShowHistory", "HistoryX", "HistoryY", "CollapsedSections" };
    static object Value(ManualDiceRollerSettings settings, string name) { return typeof(ManualDiceRollerSettings).GetField(name).GetValue(settings); }
    static string[] On(ManualDiceRollerSettings settings) { return RollTypes.Where(name => (bool)Value(settings, name)).ToArray(); }
    static void Main(string[] args)
    {
        Case("Successful actual Plugin.Load emits parsed diagnostics", delegate {
            Check(Plugin.Load(mod) && Plugin.Enabled && Field<bool>("loaded"), "Successful load enables only after installation and window creation");
            Check(TestBoundary.Installs.SequenceEqual(new[] { "Native", "Runtime", "Effects", "Rest", "Input", "Network", "Features" }) && TestBoundary.Creates == 1, "All mocked installers and UI creation run");
            Check(TestBoundary.TransportSawEnabled, "Initial transport handshake advertises the enabled profile after patches and UI are installed");
            Check(mod.OnToggle != null && mod.OnGUI != null && mod.OnSaveGUI != null && mod.OnUnload != null && mod.OnUpdate != null, "Success assigns all UMM callbacks");
            var marker = mod.Logger.Lines.Single(s => s.StartsWith("MDR_DIAGNOSTICS "));
            using (var document = JsonDocument.Parse(marker.Substring("MDR_DIAGNOSTICS ".Length))) {
                var json = document.RootElement;
                Check(json.GetProperty("version").GetString() == mod.Info.Version, "Marker identifies mod release version");
                Check(json.GetProperty("plugin_sha256").GetString() == Hash(typeof(Plugin).Assembly.Location), "Marker hashes the actual loaded plugin fixture assembly");
                Check(json.GetProperty("umm_version").GetString() == "0.32.4.0" && json.GetProperty("harmony_version").GetString() == "2.3.6.0", "Marker records loaded UMM and Harmony versions");
                Check(json.GetProperty("unity_version").GetString() == UnityEngine.Application.Version, "Marker JSON escapes Unity boundary text losslessly");
                Check(!json.GetProperty("mono_runtime").GetBoolean() && json.GetProperty("pointer_size").GetInt32() == IntPtr.Size && json.GetProperty("clr_version").GetString() == Environment.Version.ToString(), "Marker records current test host runtime facts without claiming Mono");
                Check((DateTime.UtcNow - DateTime.Parse(json.GetProperty("utc").GetString()).ToUniversalTime()).Duration() < TimeSpan.FromMinutes(1), "Marker timestamp belongs to current session");
                foreach (string prefix in new[] { "umm", "harmony" }) Check(json.GetProperty(prefix + "_sha256").GetString() == Hash(json.GetProperty(prefix + "_path").GetString()), "Marker dependency path and hash agree");
            }
            Check(Plugin.Load(mod) && TestBoundary.Creates == 1, "Repeated Load does not duplicate installation");
        });
        foreach (string step in new[] { "Native", "Runtime", "Effects", "Rest", "Input", "Network", "Features" }) {
            string current = step;
            Case("Partial " + current + " installation rollback", delegate {
                TestBoundary.FailInstall = current; Check(!Plugin.Load(mod), "Injected install failure reports false"); RolledBack();
                Check(Patched("Compatibility", Compatibility), "Separate native fault compatibility owner survives rollback");
            });
        }
        Case("Transport initialization failure independently rolls back", delegate {
            TestBoundary.FailTransportInitialize=true; Check(!Plugin.Load(mod), "Transport initialization failure reports false"); RolledBack();
            Check(TestBoundary.TransportInitializations==1&&TestBoundary.TransportShutdowns==1, "Failed transport attempts its own shutdown exactly once");
        });
        Case("Transport shutdown failure cannot strand installed patches", delegate {
            TestBoundary.FailTransportInitialize=TestBoundary.FailTransportShutdown=true;
            Check(!Plugin.Load(mod), "Transport teardown failure does not escape the load rollback"); RolledBack();
            Check(TestBoundary.TransportShutdowns==1, "Transport teardown was attempted despite its injected failure");
        });
        Case("Multiplayer lifecycle keeps the negotiated build enabled", delegate {
            Check(Plugin.Load(mod), "Multiplayer lifecycle initial load succeeds"); Photon.Pun.PhotonNetwork.InRoom=true;
            Check(!mod.OnToggle(mod,false)&&Plugin.Enabled, "A joined room rejects disabling the negotiated mod");
            Check(!mod.OnUnload(mod)&&Plugin.Enabled, "A joined room rejects unloading the negotiated mod");
            Check(TestBoundary.Resets==0&&TestBoundary.Destroys==0&&TestBoundary.Closes==0, "Rejected room lifecycle operations preserve pending roll resources");
            mod.OnUpdate(mod,.016f); Check(TestBoundary.TransportUpdates==1&&TestBoundary.CompletionUpdates==1, "UMM update pumps transport and native completion independently");
            Photon.Pun.PhotonNetwork.InRoom=false; Check(mod.OnToggle(mod,false)&&!Plugin.Enabled, "Leaving the room permits safe disable");
        });
        Case("Post-install creation failure clears callbacks and patches", delegate {
            TestBoundary.FailCreate = true; Check(!Plugin.Load(mod), "Create failure reports false"); RolledBack();
        });
        Case("Reset and UI failures cannot stop patch rollback", delegate {
            TestBoundary.FailCreate = TestBoundary.FailReset = TestBoundary.FailDestroy = true;
            Check(!Plugin.Load(mod), "Load returns false despite multiple cleanup failures"); RolledBack();
            Check(Patched("Compatibility", Compatibility), "Fault compatibility is retained when cleanup faults");
        });
        Case("Unpatch failure clears flags and callbacks and requires restart", delegate {
            TestBoundary.FailCreate = TestBoundary.FailReset = TestBoundary.FailDestroy = true; Harmony.FailUnpatch = true;
            Check(!Plugin.Load(mod), "Unpatch failure does not escape load failure handler");
            Check(!Plugin.Enabled && !Field<bool>("loaded") && Field<Harmony>("harmony") == null && Field<bool>("rollbackFailed"), "Unpatch failure leaves plugin disabled with restart guard");
            Check(TestBoundary.Resets == 1 && TestBoundary.Destroys == 1 && Harmony.UnpatchAttempts == 1, "All independent cleanup operations are attempted despite faults");
            ClearedCallbacks();
            Check(Patched("Compatibility", Compatibility), "Failed owner removal never targets compatibility owner");
            int installed = TestBoundary.Installs.Count; Check(!Plugin.Load(mod) && TestBoundary.Installs.Count == installed, "Dirty rollback prevents a second partial installation before restart");
        });
        Case("Rollback preserves foreign patches and prior callbacks", delegate {
            Func<UnityModManager.ModEntry, bool, bool> previousToggle = delegate { return true; };
            Action<UnityModManager.ModEntry> previousGui = delegate { };
            mod.OnToggle = previousToggle; mod.OnGUI = previousGui;
            new Harmony(Foreign).Patch(typeof(TestTargets).GetMethod("Foreign"), prefix: new HarmonyMethod(typeof(TestTargets).GetMethod("Prefix")));
            TestBoundary.FailCreate = true; Check(!Plugin.Load(mod), "Injected failure reports false");
            Check(mod.OnToggle == previousToggle && mod.OnGUI == previousGui && mod.OnUnload == null && mod.OnSaveGUI == null && mod.OnUpdate == null, "Rollback restores callbacks that predated this load");
            Check(Patched("Foreign", Foreign) && Patched("Compatibility", Compatibility) && !AnyOwnerPatches(), "Rollback removes only this plugin owner");
        });
        Case("Preflight and settings failures remain disabled", delegate {
            TestBoundary.FailPreflight = true; Check(!Plugin.Load(mod), "Preflight failure reports false"); RolledBack();
            Check(TestBoundary.Installs.Count == 0, "Preflight failure installs no patch");
        });
        Case("Loader settings exception cleans up safely", delegate {
            UnityModManager.ModSettings.FailLoad = true; Check(!Plugin.Load(mod), "Settings load failure reports false"); RolledBack();
        });
        Case("Null loader settings use safe defaults", delegate {
            UnityModManager.ModSettings.NullLoad = true; Check(Plugin.Load(mod) && Plugin.Settings != null, "Null settings do not fail load");
        });
        Case("An empty or outdated Settings.xml is replaced by defaults", delegate {
            string folder = Path.Combine(Path.GetTempPath(), "mdr-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string file = Path.Combine(folder, "Settings.xml");
                foreach (string content in new[] { "", "   ", "<?xml version=\"1.0\"?><ModSettings/>" })
                {
                    Reset(); File.WriteAllText(file, content); mod.Path = folder;
                    Check(Plugin.Load(mod) && Plugin.Enabled, "Load succeeds with an unreadable settings file");
                    Check(!File.Exists(file) && mod.Logger.Lines.Any(line => line.Contains("Replaced an empty or outdated Settings.xml")), "The unreadable file is removed and the repair logged");
                }
                Reset(); File.WriteAllText(file, "<?xml version=\"1.0\"?><ManualDiceRollerSettings><Attack>true</Attack></ManualDiceRollerSettings>"); mod.Path = folder;
                Check(Plugin.Load(mod) && File.Exists(file) && !mod.Logger.Lines.Any(line => line.Contains("Replaced")), "A valid settings file is kept");
            }
            finally { try { Directory.Delete(folder, true); } catch (Exception) { } }
        });
        Case("Disable remains exception-safe and retains continuation patches", delegate {
            Check(Plugin.Load(mod), "Initial load succeeds"); TestBoundary.FailReset = TestBoundary.FailClose = true;
            Check(mod.OnToggle(mod, false) && !Plugin.Enabled, "Disable succeeds even when reset and close throw");
            Check(TestBoundary.Resets == 1 && TestBoundary.Closes == 1 && TestBoundary.ResetSawDisabled, "Disable attempts both cleanups after disabling");
            Check(ModFeatures.Flushes == 1, "Disable finishes queued single-player rolls with game dice");
            Check(AnyOwnerPatches() && Patched("Compatibility", Compatibility) && Field<bool>("loaded"), "Disable preserves continuation and fault compatibility patches");
        });
        Case("Unload refusal survives cleanup and logger exceptions", delegate {
            Check(Plugin.Load(mod), "Initial load succeeds"); TestBoundary.FailReset = TestBoundary.FailDestroy = true; mod.Logger.Fail = true;
            Check(!mod.OnUnload(mod) && !Plugin.Enabled, "Unload returns false and disables despite boundary failures");
            Check(TestBoundary.Resets == 1 && TestBoundary.Destroys == 1 && TestBoundary.DestroySawDisabled, "Unload attempts both cleanups while disabled");
            Check(AnyOwnerPatches() && Patched("Compatibility", Compatibility) && Field<bool>("loaded") && mod.OnUpdate != null, "Refused unload retains patches and loaded callback state");
        });
        Case("Logging exceptions never turn successful load into rollback", delegate {
            mod.Logger.Fail = true; Check(Plugin.Load(mod) && Plugin.Enabled && AnyOwnerPatches(), "Logger failure leaves successful installation enabled");
            Plugin.Log("test"); Plugin.Warn("test"); Plugin.Error("test"); Check(TestBoundary.Resets == 0, "Public logging APIs absorb loader logger failures");
        });
        Case("Runtime diagnostic boundary failure does not fail loaded plugin", delegate {
            UnityEngine.Application.FailVersion = true; Check(Plugin.Load(mod) && Plugin.Enabled && AnyOwnerPatches(), "Unavailable runtime metadata leaves successful installation intact");
            Check(mod.Logger.Lines.Any(s => s.StartsWith("Runtime diagnostics could not be recorded:")), "Metadata failure is reported");
        });
        Case("Unloaded plugin cannot be enabled through stale toggle", delegate {
            Check(!Invoke("Toggle", mod, true) && !Plugin.Enabled, "Enable is refused before completed Load");
        });
        Case("Settings names and defaults are unchanged; UI-only fields are additive", delegate {
            var defaults = new ManualDiceRollerSettings();
            var expected = new Dictionary<string, object> {
                { "Attack", true }, { "Ability", true }, { "SavingThrows", true }, { "Initiative", true }, { "Death", true }, { "Damage", true }, { "Healing", true },
                { "SpellPools", true }, { "HitDice", true }, { "EnemyRolls", false }, { "Concentration", true }, { "Downtime", true }, { "Lore", true }, { "RepeatSaves", true },
                { "LingeringEffects", true }, { "Features", true }, { "Stealth", true }, { "CharacterCreation", true }, { "DamageSaves", false }, { "HiddenPerception", false },
                { "LevelUpHitPoints", false }, { "Traps", true }, { "MultiplayerExtras", true }, { "AutoRollNotices", true }, { "WindowScale", 1f }, { "WindowX", -1f }, { "WindowY", -1f },
                { "ShowHistory", false }, { "HistoryX", -1f }, { "HistoryY", -1f }, { "CollapsedSections", 6 } };
            var fields = typeof(ManualDiceRollerSettings).GetFields(BindingFlags.Instance | BindingFlags.Public);
            Check(fields.Length == expected.Count && fields.All(f => expected.ContainsKey(f.Name)), "Every serialized setting is accounted for");
            foreach (var pair in expected) Check(Value(defaults, pair.Key).Equals(pair.Value), "Default of " + pair.Key);
            var serializer = new XmlSerializer(typeof(ManualDiceRollerSettings));
            var text = new StringWriter(); serializer.Serialize(text, defaults);
            Check(expected.Keys.All(name => text.ToString().Contains("<" + name + ">")), "Every setting keeps its Settings.xml element name");
            var old = (ManualDiceRollerSettings)serializer.Deserialize(new StringReader("<?xml version=\"1.0\"?><ManualDiceRollerSettings><Attack>false</Attack><Traps>false</Traps><WindowScale>1.25</WindowScale></ManualDiceRollerSettings>"));
            Check(!old.Attack && !old.Traps && old.WindowScale == 1.25f && old.Ability && !old.ShowHistory && old.HistoryX == -1 && old.CollapsedSections == 6, "An older Settings.xml loads with UI-only defaults");
        });
        Case("Presets set exactly their roll types", delegate {
            Check(Plugin.PresetNames.SequenceEqual(new[] { "Everything manual", "Party dice (recommended)", "Combat only", "Minimal" }), "Preset buttons");
            var bools = typeof(ManualDiceRollerSettings).GetFields(BindingFlags.Instance | BindingFlags.Public).Where(f => f.FieldType == typeof(bool)).Select(f => f.Name);
            Check(new HashSet<string>(bools).SetEquals(RollTypes.Concat(new[] { "MultiplayerExtras", "AutoRollNotices", "ShowHistory" })), "Presets cover every roll type");
            var expected = new Dictionary<Plugin.RollPreset, string[]> {
                { Plugin.RollPreset.Everything, RollTypes },
                { Plugin.RollPreset.Party, On(new ManualDiceRollerSettings()) },
                { Plugin.RollPreset.Combat, new[] { "Attack", "Ability", "SavingThrows", "Initiative", "Death", "Damage", "Healing", "Concentration", "RepeatSaves", "LingeringEffects", "Features", "Traps", "SpellPools" } },
                { Plugin.RollPreset.Minimal, new[] { "Attack", "Ability", "SavingThrows" } } };
            Check(expected[Plugin.RollPreset.Party].Length == 18 && !expected[Plugin.RollPreset.Party].Contains("EnemyRolls") && !expected[Plugin.RollPreset.Party].Contains("DamageSaves"), "Party dice is the shipped default set");
            foreach (var pair in expected)
            {
                foreach (bool start in new[] { false, true })
                {
                    var settings = new ManualDiceRollerSettings { MultiplayerExtras = false, AutoRollNotices = false, ShowHistory = true, WindowScale = 1.3f, WindowX = 5, HistoryY = 7, CollapsedSections = 0 };
                    foreach (string name in RollTypes) typeof(ManualDiceRollerSettings).GetField(name).SetValue(settings, start);
                    Plugin.ApplyPreset(settings, pair.Key);
                    Check(new HashSet<string>(On(settings)).SetEquals(pair.Value), "Preset " + pair.Key + " sets exactly its roll types");
                    Check(!settings.MultiplayerExtras && !settings.AutoRollNotices && settings.ShowHistory && settings.WindowScale == 1.3f && settings.WindowX == 5 && settings.HistoryY == 7 && settings.CollapsedSections == 0, "Preset " + pair.Key + " leaves window, history and multiplayer options alone");
                    Check(Plugin.ActivePreset(settings) == pair.Key, "The applied preset is shown as active: " + pair.Key);
                }
            }
            Check(Plugin.ActivePreset(new ManualDiceRollerSettings()) == Plugin.RollPreset.Party, "Fresh settings match the recommended preset");
            var custom = new ManualDiceRollerSettings { Stealth = false };
            Check(Plugin.ActivePreset(custom) == null && Plugin.ActivePreset(null) == null, "Any other mix is custom");
        });
        Case("UI-only settings stay out of the multiplayer profile fingerprint", delegate {
            string path = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "src", "MultiplayerRolls.cs");
            string source = File.ReadAllText(path);
            int start = source.IndexOf("static string ProfileFingerprint()", StringComparison.Ordinal);
            int end = start < 0 ? -1 : source.IndexOf("\n        }", start, StringComparison.Ordinal);
            Check(start > 0 && end > start, "The production fingerprint method was found");
            string method = source.Substring(start, end - start);
            var flags = Regex.Matches(method, @"settings\.(\w+)").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Check(flags.Count == 23 && flags.Distinct().Count() == 23, "The fingerprint lists 23 distinct roll settings");
            Check(new HashSet<string>(flags).SetEquals(RollTypes.Concat(new[] { "MultiplayerExtras" })), "The fingerprint covers exactly the roll types and the multiplayer option");
            Check(UiOnly.All(name => !Regex.IsMatch(method, @"\b" + name + @"\b")), "No window, notice or history setting is part of the fingerprint");
        });
        Reset();
        Console.WriteLine((failures == 0 ? "PASS " : "FAIL ") + cases + " actual Plugin lifecycle cases; " + assertions + " assertions; " + failures + " failures.");
        Console.WriteLine("The test uses genuine game metadata with mocked Harmony owner bookkeeping, UMM, UI and installers. It is not actual detour or Windows/Unity Mono loading proof.");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
