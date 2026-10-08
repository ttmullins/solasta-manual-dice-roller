using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace ManualDiceRoller
{
    // The game sometimes drives an iterator to completion synchronously
    // (IEnumeratorExtensions.ExecuteUntilDone, TA.Coroutine.RunUntilIsFinished),
    // for example when gadget functors run without animation or are replayed
    // while a save loads. A manual prompt yields until the player answers, so
    // inside such a pump it would never finish and the game would hang.
    // These guards count active synchronous pumps; preparation keeps native
    // dice whenever one is running on this thread.
    internal static class SyncPumpGuard
    {
        internal static bool Active { get { return RuntimeRolls.SyncPumpDepth > 0; } }
        static MethodInfo executeUntilDone, runUntilFinished;

        internal static bool Preflight(out string error)
        {
            error = null;
            try
            {
                var extensions = typeof(RulesetActor).Assembly.GetType("IEnumeratorExtensions", false);
                executeUntilDone = extensions == null ? null : extensions.GetMethod("ExecuteUntilDone", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(IEnumerator) }, null);
                runUntilFinished = typeof(TA.Coroutine).GetMethod("RunUntilIsFinished", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (executeUntilDone == null || executeUntilDone.ReturnType != typeof(void) || runUntilFinished == null || runUntilFinished.ReturnType != typeof(void))
                    throw new InvalidOperationException("The native synchronous coroutine pumps changed.");
                return true;
            }
            catch (Exception failure) { error = failure.Message; return false; }
        }
        internal static void Install(Harmony harmony)
        {
            string error;
            if (executeUntilDone == null && !Preflight(out error)) throw new InvalidOperationException(error);
            var enter = new HarmonyMethod(typeof(SyncPumpGuard).GetMethod(nameof(Enter), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
            var exit = new HarmonyMethod(typeof(SyncPumpGuard).GetMethod(nameof(Exit), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last };
            harmony.Patch(executeUntilDone, prefix: enter, finalizer: exit);
            harmony.Patch(runUntilFinished, prefix: enter, finalizer: exit);
        }
        static void Enter() { RuntimeRolls.SyncPumpDepth++; }
        static Exception Exit(Exception __exception) { if (RuntimeRolls.SyncPumpDepth > 0) RuntimeRolls.SyncPumpDepth--; return __exception; }
    }
}
