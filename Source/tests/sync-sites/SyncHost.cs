// Test-only boundary for the sync-sites suite. Production sources and the
// pristine game assembly are compiled unchanged; only the plugin's broker,
// window and multiplayer services are replaced by recording doubles.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace ManualDiceRoller
{
    public sealed class TestSettings
    {
        public bool Attack = true, Ability = true, SavingThrows = true, Damage = true, Concentration = true, Downtime = true, Lore = true, RepeatSaves = true, MultiplayerExtras = true, EnemyRolls;
        public bool LingeringEffects = true, Features = true, Stealth = true, CharacterCreation = true, DamageSaves, HiddenPerception, LevelUpHitPoints, Traps = true;
    }
    public static class Plugin
    {
        public static bool Enabled = true, Online, UnsupportedRulesMod;
        public static TestSettings Settings = new TestSettings();
        public static readonly List<string> Logs = new List<string>();
        public static bool Networked() { return Online; }
        internal const RuleDefinitions.RollContext FeatureDice = (RuleDefinitions.RollContext)0x100;
        internal const RuleDefinitions.RollContext TrapDice = (RuleDefinitions.RollContext)0x200;
        public static void Log(string value) { Logs.Add("LOG " + value); }
        public static void Warn(string value) { Logs.Add("WARN " + value); }
        public static void Error(string value) { Logs.Add("ERROR " + value); }
    }
    public static class RollBroker { public const int MaxFacesPerSession = 4096; }
    // Window boundary for the character-screen rolls: prompts are recorded and
    // answered by the test.
    public sealed class RollPrompt
    {
        public readonly string Title, Actor, Detail, Note; public readonly int[] Faces;
        public RollPrompt(string title, string actor, string detail, string note, params int[] dieFaces) { Title = title; Actor = actor; Detail = detail; Note = note; Faces = dieFaces; }
    }
    public static class RollWindow
    {
        public static bool Visible; public static RollPrompt Shown; public static Action<int[]> Accept; public static Action Automatic;
        public static readonly List<RollPrompt> History = new List<RollPrompt>();
        public static void Display(RollPrompt prompt, Action<int[]> accepted, Action automatic) { Visible = true; Shown = prompt; Accept = accepted; Automatic = automatic; History.Add(prompt); }
        public static void Close(RollPrompt prompt) { if (ReferenceEquals(Shown, prompt)) { Visible = false; Shown = null; } }
        public static void Answer(params int[] values) { var accept = Accept; Visible = false; Shown = null; Accept = null; accept(values); }
        public static void LetGameRoll() { var automatic = Automatic; Visible = false; Shown = null; Automatic = null; automatic(); }
        public static void Reset() { Visible = false; Shown = null; Accept = null; Automatic = null; History.Clear(); }
    }
    public static class MultiplayerRolls
    {
        public static bool Failed; public static string Failure;
        public static void Fail(string reason) { Failed = true; Failure = reason; }
    }
    public static class GameNames
    {
        // Unity's object name is an engine call; fixtures without an engine show a placeholder.
        public static string SpellName(BaseDefinition definition) { try { return definition == null ? "" : definition.Name; } catch (Exception) { return "Unnamed"; } }
        public static string Title(BaseDefinition definition) { return SpellName(definition); }
        public static string ActorName(RulesetActor actor) { try { return actor == null ? "" : actor.Name ?? ""; } catch (Exception) { return "Unnamed"; } }
    }

    public sealed class NativeRollPlan
    {
        public sealed class Group
        {
            public RulesetActor Actor; public RuleDefinitions.DieType Die; public RuleDefinitions.RollContext Context; public int Count;
            public RuleDefinitions.AdvantageType Advantage; public bool Proficient, CanReroll, Static, Enumerate; public string Skill, Title, Note, Hint;
            public int DC; public bool HasBonus; public int Bonus;
        }
        public readonly List<Group> Groups = new List<Group>();
        // Composite planning boundary: one leaf per planned die group.
        public sealed class Leaf { public object Anchor; public NativeInvocation PartTarget; public string Title, Note; public Group Group; public bool Static, MasterOwned; public Func<bool> When; public string ActorLabel; }
        public readonly List<Leaf> Leaves = new List<Leaf>();
        public void LabelRange(int start, string title, string detail)
        {
            for (int index = start; index < Leaves.Count; index++)
            {
                if (!string.IsNullOrEmpty(title)) { Leaves[index].Title = title; Leaves[index].Group.Title = title; }
                if (!string.IsNullOrEmpty(detail)) Leaves[index].Note = detail;
            }
        }
        public Func<bool> NativeStateValid, ManualStateValid;
        public void RequireManualState(Func<bool> validator) { var old = ManualStateValid; ManualStateValid = old == null ? validator : (Func<bool>)(() => old() && validator()); }
        public void RequireNativeState(Func<bool> validator) { var old = NativeStateValid; NativeStateValid = old == null ? validator : (Func<bool>)(() => old() && validator()); }
        public void AddActorDice(RulesetActor actor, RuleDefinitions.DieType die, RuleDefinitions.RollContext context, int count,
            RuleDefinitions.AdvantageType advantage = RuleDefinitions.AdvantageType.None, bool proficient = false, string skill = "", bool canReroll = true,
            object form = null, string title = null, string note = null, bool enumerate = true, string hint = null)
        { Groups.Add(new Group { Actor = actor, Die = die, Context = context, Count = count, Advantage = advantage, Proficient = proficient, Skill = skill, CanReroll = canReroll, Title = title, Note = note, Hint = hint, Enumerate = enumerate }); Leaves.Add(new Leaf { Title = title, Group = Groups[Groups.Count - 1] }); }
        public void AddStaticDice(RulesetActor displayActor, RuleDefinitions.DieType die, int count, object form = null, string title = null, string note = null,
            RuleDefinitions.AdvantageType advantage = RuleDefinitions.AdvantageType.None, string role = null, RuleDefinitions.RollContext category = RuleDefinitions.RollContext.None, string hint = null)
        { Groups.Add(new Group { Actor = displayActor, Die = die, Count = count, Advantage = advantage, Static = true, Title = title, Note = note, Hint = hint, Context = category }); Leaves.Add(new Leaf { Title = title, Group = Groups[Groups.Count - 1], Static = true }); }
        // Game master dice: only with enemy dice on.
        public void AddMasterDice(RulesetActor nameActor, string label, RuleDefinitions.DieType die, int count, string title = null, string note = null,
            RuleDefinitions.RollContext category = RuleDefinitions.RollContext.None, string hint = null)
        {
            if (nameActor == null || !RuntimeRolls.CanManualMaster(category) || count <= 0) return;
            Groups.Add(new Group { Actor = nameActor, Die = die, Count = count, Static = true, Title = title, Note = note, Hint = hint, Context = category });
            Leaves.Add(new Leaf { Title = title, Group = Groups[Groups.Count - 1], Static = true, MasterOwned = true, ActorLabel = label });
        }
        // Conditional groups: the condition is recorded for the test to evaluate.
        public void AskWhen(Func<bool> condition) { if (Leaves.Count > 0) Leaves[Leaves.Count - 1].When = condition; }
        public Func<int, bool> Succeeds;
        internal bool CheckSucceeds(int leafIndex) { Calls.Add("succeeds:" + leafIndex); return Succeeds != null && Succeeds(leafIndex); }
        public static readonly List<string> Calls = new List<string>();
        public void Describe(int dc, bool hasBonus, int bonus) { if (Groups.Count == 0) return; var g = Groups[Groups.Count - 1]; g.DC = dc; g.HasBonus = hasBonus; g.Bonus = bonus; }
    }

    public static class RuntimeRolls
    {
        static readonly Dictionary<string, OuterSite> sites = new Dictionary<string, OuterSite>(StringComparer.Ordinal);
        internal static void RegisterOuterSite(OuterSite site) { sites[site.Id] = site; }
        internal static OuterSite FindOuterSite(string id) { OuterSite site; return id != null && sites.TryGetValue(id, out site) ? site : null; }
        [ThreadStatic] internal static int SyncPumpDepth;
        public static bool AllowManual = true;
        public static bool CanManual(RulesetActor actor, RuleDefinitions.RollContext context) { return AllowManual && actor != null; }
        public static bool CanManualMaster(RuleDefinitions.RollContext context) { return AllowManual && Plugin.Settings.EnemyRolls; }
        public static int Max(RuleDefinitions.DieType die) { int index = (int)die; return index >= 0 && index < RuleDefinitions.DiceMaxValue.Length ? RuleDefinitions.DiceMaxValue[index] : 0; }
        internal static Func<bool> ExtendedOnline;
        internal static bool ExtendedOnlineActive { get { return ExtendedOnline != null && ExtendedOnline(); } }
        // Multiplayer barrier state read by the deferred-roll runner.
        public static object NetworkOwner; public static bool NetworkHolding;
        // 0.8.3: no deferral queues behind another pending shared step.
        public static bool NetworkStepOpen = true;
        // Deferred-roll boundary: a scripted wait stands in for the broker/window.
        public static Func<object, string, MethodBase, object[], IEnumerator> Prepare;
        public static readonly List<string> Calls = new List<string>();
        public static object LastOwner; public static MethodBase LastOuter; public static object[] LastOuterArgs;
        public static IEnumerator PrepareNativeRoll(object owner, string site, MethodBase method, object[] args)
        { Calls.Add("prepare:" + site); LastOwner = owner; var prepare = Prepare; return prepare == null ? null : prepare(owner, site, method, args); }
        internal static IEnumerator PrepareDeferredRoll(object owner, string site, MethodBase outerMethod, object[] outerArgs, MethodBase targetMethod, object[] targetArgs, string title, string detail)
        {
            Calls.Add("prepare:" + site); if (title != null || detail != null) Calls.Add("label:" + title + "|" + detail);
            LastOwner = owner; LastOuter = outerMethod; LastOuterArgs = outerArgs;
            var prepare = Prepare; return prepare == null ? null : prepare(owner, site, targetMethod, targetArgs);
        }
        public static int InvocationStaticFace(MethodBase expected, RuleDefinitions.DieType die, RuleDefinitions.AdvantageType advantage, out int first, out int second, float alteration = 0f)
        { Calls.Add("raw:" + expected.Name); first = 11; second = -1; return 11; }
        // Composite planning and anchor boundaries.
        public static Func<NativeInvocation, NativeRollPlan, bool> Planner;
        internal static bool PlanTarget(NativeInvocation target, NativeRollPlan plan) { Calls.Add("plan:" + target.Method.Name); return Planner != null && Planner(target, plan); }
        public static object CurrentAnchor;
        public static object EnterAnchor(object anchor) { Calls.Add("anchor:" + (anchor == null ? "null" : anchor.GetType().Name)); var old = CurrentAnchor; CurrentAnchor = anchor; return old; }
        public static void ExitAnchor(object previous) { CurrentAnchor = previous; }
        public static void ObserveNativeValues(object owner, string site, object result, object[] outputs)
        { Calls.Add("observe:" + site + (outputs == null || outputs.Length != 0 ? ":unexpected" : result == null ? "" : ":" + result)); }
        public static void EnterNative(object owner, string site) { Calls.Add("enter:" + site); }
        public static void ExitNative(object owner, string site) { Calls.Add("exit:" + site); }
        public static void AbortNative(object owner) { Calls.Add("abort"); }
        public static void CancelOwner(object owner) { Calls.Add("cancel"); }
        public static void Reset() { Calls.Clear(); Prepare = null; Planner = null; CurrentAnchor = null; LastOwner = null; LastOuter = null; LastOuterArgs = null; AllowManual = true; SyncPumpDepth = 0; ExtendedOnline = null; NetworkOwner = null; NetworkHolding = false; NetworkStepOpen = true; MultiplayerRolls.Failed = false; }
    }
}
