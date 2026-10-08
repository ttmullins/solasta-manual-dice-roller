using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ManualDiceRoller;
using UnityEngine;

static class IntegrationTests
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static object Window { get { return typeof(RollWindow).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null); } }
    static T WindowField<T>(string name) { return (T)typeof(RollWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window); }
    static void Reset()
    {
        RuntimeRolls.Reset(); Plugin.Enabled = true; Plugin.Multiplayer = false; Plugin.Logs.Clear();
        TA.DeterministicRandom.Calls = 0; TA.DeterministicRandom.Values.Clear(); GUI.Reset(); Input.Held = Input.Released = 0; Input.FailRead = false;
        Gui.GuiService = new IntegrationGui(); CharacterActionMoveStepJump.Acrobatics = CharacterActionMoveStepJump.Athletics = true; CharacterActionMoveStepJump.Penalty = false;
        ServiceRepository.Settings = new TestSettings(); ServiceRepository.Rules = new TestRules();
    }
    static TA.Coroutine OpenJump(out CharacterActionMoveStepJump action, out IEnumerator owner)
    {
        action = new CharacterActionMoveStepJump(); action.ActingCharacter.RulesetCharacter = new RulesetCharacterHero { Name = "Ellie" };
        owner = action.RollChecksIfNecessary(); var coroutine = new TA.Coroutine(); coroutine.Push(owner);
        for (int i = 0; i < 20 && !RollWindow.Visible; i++) coroutine.Run();
        Check(RollWindow.Visible && NativePatches.HasPending && coroutine.Count >= 4, "Actual TA.Run did not reach production's nested pending roll window");
        Check(action.ActingCharacter.Calls == 0 && TA.DeterministicRandom.Calls == 0, "Waiting production modal executed native dice or its original call");
        return coroutine;
    }
    static IEnumerator YieldChild(TA.Coroutine child) { yield return child; }
    static void Main()
    {
        HarmonyFixtureHost.Initialize(typeof(Harmony).Assembly); string error;
        Check(NativePatches.Preflight(out error), "Actual Jump await preflight failed: " + error);
        var harmony = new Harmony("ManualDiceRoller.CombinedAwaitFixture"); NativePatches.Install(harmony); RuntimeRolls.Install(harmony);
        try {
            Reset(); CharacterActionMoveStepJump action; IEnumerator owner; var coroutine = OpenJump(out action, out owner);
            var prompt = WindowField<RollPrompt>("prompt"); var stale = WindowField<Action<int[]>>("accept");
            var savedChildren = ((System.Collections.Generic.Stack<IEnumerator>)typeof(TA.Coroutine).GetField("enumerators", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(coroutine)).ToArray();
            Check(Gui.GuiService.MouseCursorShown, "Actual production Display did not acquire cursor visibility");
            coroutine.Reset();
            Check(coroutine.Count == 0 && !NativePatches.HasPending, "Actual native Reset did not clear the native stack and owned suspension");
            Check(!RollWindow.Visible && !Gui.GuiService.MouseCursorShown, "Actual TA.Reset left the actual production RollWindow or cursor captured");
            Check(action.ActingCharacter.Calls == 0 && TA.DeterministicRandom.Calls == 0, "Cancellation executed the pristine native ability call");
            stale(new[] { 19 });
            Check(Plugin.Logs.Any(message => message.Contains("Stale or canceled")), "Actual TA.Reset did not reject the removed modal's callback");
            var foreign = new RollPrompt("Other owner", "Robin", "", "", 20); RollWindow.Display(foreign, values => { }, () => { });
            foreach (var child in savedChildren) { var disposable = child as IDisposable; if (disposable != null) disposable.Dispose(); }
            Check(RollWindow.IsOwned(foreign), "Disposing removed native children closed another owner's actual window");
            RollWindow.Close(prompt); Check(RollWindow.IsOwned(foreign), "Reference-owned Close accepted the removed prompt instead of the visible one");
            RollWindow.Close(foreign); Check(!RollWindow.Visible, "Matching actual production Close did not retire the second window");
            TA.DeterministicRandom.Values.Enqueue(6); RuntimeRolls.EnterNative(owner, "Jump:Acrobatics"); int first, second, result;
            try { result = action.ActingCharacter.RulesetCharacter.RollDie(RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AbilityCheck, false, RuleDefinitions.AdvantageType.None, out first, out second); }
            finally { RuntimeRolls.ExitNative(owner, "Jump:Acrobatics"); }
            Check(result == 7 && TA.DeterministicRandom.Calls == 1, "Actual TA.Reset left stale accepted faces available to a native die");
            Reset(); coroutine = OpenJump(out action, out owner); prompt = WindowField<RollPrompt>("prompt");
            ((IDisposable)owner).Dispose();
            Check(!RollWindow.Visible && !NativePatches.HasPending && action.ActingCharacter.Calls == 0, "Direct original iterator disposal did not retire its actual modal and suspension");
            coroutine.Reset(); Check(coroutine.Count == 0 && !RollWindow.Visible, "Reset after owner disposal revived or retained modal state");

            Reset(); action = new CharacterActionMoveStepJump(); action.ActingCharacter.RulesetCharacter = new RulesetCharacterHero(); owner = action.RollChecksIfNecessary();
            var childCoroutine = new TA.Coroutine(); childCoroutine.Push(owner); var outer = new TA.Coroutine(); outer.Push(YieldChild(childCoroutine));
            for (int i = 0; i < 20 && !RollWindow.Visible; i++) outer.Run();
            Check(RollWindow.Visible && NativePatches.HasPending && childCoroutine.Count >= 4, "Actual parent TA.Run did not drive child into the actual modal");
            outer.Reset();
            Check(outer.Count == 0 && childCoroutine.Count > 0 && !NativePatches.HasPending, "Parent Reset did not retire child ownership while preserving its separate stack");
            Check(!RollWindow.Visible && !Gui.GuiService.MouseCursorShown && action.ActingCharacter.Calls == 0, "Parent Reset orphaned child's actual modal or executed native roll");
            childCoroutine.Reset();

            Reset(); coroutine = OpenJump(out action, out owner); Plugin.Enabled = false; NativePatches.Reset();
            Check(!RollWindow.Visible && NativePatches.HasPending && coroutine.Count >= 4, "Disabling removed the native continuation instead of only canceling input ownership");
            TA.DeterministicRandom.Values.Enqueue(19); TA.DeterministicRandom.Values.Enqueue(19);
            for (int i = 0; i < 40 && !coroutine.IsFinished; i++) coroutine.Run();
            Check(coroutine.IsFinished && coroutine.Count == 0 && !NativePatches.HasPending && !RollWindow.Visible, "Disabled pending original action did not finish through its saved native continuation");
            Check(action.ActingCharacter.Calls == 2 && TA.DeterministicRandom.Calls == 2 && action.AbilityCheckRollOutcome == RuleDefinitions.RollOutcome.Success, "Disable/resume changed original Jump ability call count or native outcome");
            Console.WriteLine("PASS: " + checks + " combined actual TA.Run/Reset and Jump iterator assertions through production NativePatches, RuntimeRolls, broker and actual RollWindow ownership.");
        } finally { RuntimeRolls.Reset(); RollWindow.DestroyWindow(); harmony.UnpatchAll(harmony.Id); }
    }
}
