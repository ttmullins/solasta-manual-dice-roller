using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using ManualDiceRoller;
using UnityEngine.EventSystems;

namespace ManualDiceRoller
{
    // Test-only boundary: the real guard source and game metadata run unchanged;
    // only raw Unity engine polling and unrelated roll installation are replaced.
    public static class RollWindow
    {
        public static bool Blocked;
        public static int CaptureResets;
        public static void Create() { }
        public static void Close() { }
        public static void DestroyWindow() { }
        public static void ResetCapture() { CaptureResets++; }
        public static bool BlocksInput() { return Blocked; }
    }
    public static class RuntimeRolls { public static int Resets; public static void Reset() { Resets++; } public static void Install(HarmonyLib.Harmony value) { } public static void UpdateNetworkCompletion() { } }
    public static class NativePatches
    {
        public static bool Preflight(out string error) { error = null; return true; }
        public static void Install(HarmonyLib.Harmony value) { }
    }
    public static class EffectPatches { public static void Install(HarmonyLib.Harmony value) { } }
    public static class RestPatches { public static bool Preflight(out string error) { error=null; return true; } public static void Install(HarmonyLib.Harmony value) { } }
    internal static class NativeNetworkScheduler { internal static bool Preflight(out string error) { error=null;return true; } internal static void Install(HarmonyLib.Harmony value) { } }
    internal static class ModFeatures { internal static bool Preflight(out string error) { error=null;return true; } internal static void Install(HarmonyLib.Harmony value) { } internal static void Flush() { } internal static int Drops; internal static void Drop() { Drops++; } internal static void Detach() { } }
    internal static class MultiplayerRolls { internal static string Status {get{return "Uninitialized metadata fixture";}} internal static bool Preflight(out string error) { error=null;return true; } internal static void Initialize() { } internal static void Shutdown() { } internal static void Update() { } internal static void Fail(string reason) { } internal static void Rearm() { } }
}

static class InputTests
{
    static int assertions;
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static MethodInfo Guard(string name) { return typeof(InputGuards).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic); }
    static object Invoke(string name, params object[] args) { return Guard(name).Invoke(null, args); }
    static void Main()
    {
        string error; Check(InputGuards.Preflight(out error), "Verified pristine game input metadata: " + error);
        var view = (View)FormatterServices.GetUninitializedObject(typeof(View));
        var flags = new[] { "primaryPointerArmed", "primaryPointerDragged", "secondaryPointerArmed", "secondaryPointerDragged", "tertiaryPointerArmed", "tertiaryPointerDragged" }.Select(name => typeof(View).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)).ToArray();
        foreach (var flag in flags) flag.SetValue(view, true);
        RollWindow.Blocked = true;
        Invoke("ClearViewGestures", view);
        Check(flags.All(flag => !(bool)flag.GetValue(view)), "All native armed/dragged scene gestures cancelled");
        var queue = new Queue<int>(new[] { 1, 8, 42 });
        Check(!(bool)Invoke("CommandsPrefix", queue) && queue.Count == 0, "Queued native keyboard commands discarded while modal owns input");
        var covered = new object[] { false }; Check(!(bool)Guard("CoveredPrefix").Invoke(null, covered) && (bool)covered[0], "Native cursor sees modal coverage");
        var camera = new object[] { false }; Guard("CameraPostfix").Invoke(null, camera); Check((bool)camera[0], "Camera input is claimed by modal without mutating native stored flag");
        var pointer = new PointerEventData(null) { eligibleForClick = true, dragging = true, pointerPressRaycast = new RaycastResult { distance = 9 }, pointerCurrentRaycast = new RaycastResult { distance = 7 } };
        Invoke("CancelPointerState", pointer);
        Check(!pointer.eligibleForClick && !pointer.dragging && pointer.pointerPress == null && pointer.pointerDrag == null && pointer.pointerPressRaycast.distance == 0 && pointer.pointerCurrentRaycast.distance == 0, "Native EventSystem click/drag/raycast state cancelled together");
        Invoke("CancelPointerState", new object[]{null}); Check(true, "Absent native pointer state is harmless during teardown");
        var module = (InputPointerModule)FormatterServices.GetUninitializedObject(typeof(InputPointerModule));
        var hits = new List<RaycastResult> { new RaycastResult { distance = 1 } };
        typeof(InputPointerModule).GetField("raycastResults", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(module, hits);
        var raycast = new object[] { module, null };
        Check(!(bool)Guard("RaycastPrefix").Invoke(null, raycast) && object.ReferenceEquals(raycast[1], hits) && hits.Count == 0, "Native raycast sees no behind-window targets");
        typeof(InputPointerModule).GetField("raycastResults", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(module, null);
        raycast[1]=null;
        Check(!(bool)Guard("RaycastPrefix").Invoke(null, raycast) && raycast[1] is List<RaycastResult> && ((List<RaycastResult>)raycast[1]).Count==0,"Early native module with no raycast cache still safely blocks scene targets");
        typeof(InputPointerModule).GetField("raycastResults", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(module,hits);
        RollWindow.Blocked = false;
        queue.Enqueue(42); Check((bool)Invoke("CommandsPrefix", queue) && queue.Count == 1, "Fresh native commands preserved after close");
        covered[0] = false; Check((bool)Guard("CoveredPrefix").Invoke(null, covered) && !(bool)covered[0], "Native cursor coverage resumes");
        camera[0] = false; Guard("CameraPostfix").Invoke(null, camera); Check(!(bool)camera[0], "Camera guard releases without sticky flag");
        camera[0] = true; Guard("CameraPostfix").Invoke(null, camera); Check((bool)camera[0], "Existing native camera grab remains respected");
        hits.Add(new RaycastResult { distance = 2 }); raycast[1] = null;
        Check((bool)Guard("RaycastPrefix").Invoke(null, raycast) && hits.Count == 1 && raycast[1] == null, "Fresh native raycast resumes untouched");
        Invoke("LifecyclePrefix"); Check(RuntimeRolls.Resets == 1 && RollWindow.CaptureResets == 1, "Location teardown clears pending roll and pointer ownership");
        Check(Plugin.Settings.Attack && Plugin.Settings.Ability && Plugin.Settings.SavingThrows && Plugin.Settings.Initiative && Plugin.Settings.Death && Plugin.Settings.Damage && Plugin.Settings.Healing && Plugin.Settings.SpellPools && Plugin.Settings.HitDice && !Plugin.Settings.EnemyRolls, "Defaults request all supported hero categories only");
        Plugin.Settings.SavingThrows = false; Check(!Plugin.Allows(RuleDefinitions.RollContext.SavingThrow) && !Plugin.Allows(RuleDefinitions.RollContext.ConcentrationCheck) && Plugin.Allows(RuleDefinitions.RollContext.AttackRoll), "Saving throw setting affects both saves and concentration only");
        Plugin.Settings.Damage = false; Check(!Plugin.Allows(RuleDefinitions.RollContext.AttackDamageValueRoll) && !Plugin.Allows(RuleDefinitions.RollContext.MagicDamageValueRoll) && Plugin.Allows(RuleDefinitions.RollContext.HealValueRoll), "Damage setting preserves healing category");
        Plugin.Settings.Healing = false; Check(!Plugin.Allows(RuleDefinitions.RollContext.HealValueRoll) && !Plugin.Allows(RuleDefinitions.RollContext.TemporaryHPValueRoll), "Healing setting covers native temp HP dice");
        Console.WriteLine("PASS " + assertions + " input/lifecycle assertions from shipped guard source and pristine native metadata. Test-only polling boundary avoids claiming engine gameplay.");
    }
}
