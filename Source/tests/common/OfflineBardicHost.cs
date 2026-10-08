namespace ManualDiceRoller
{
    // Legacy focused fixtures do not contain the Bardic native ABI. Its real
    // adapter and original method are exercised by tests/coverage instead.
    internal static class BardicPatches
    {
        internal static bool TryPrepare(NativeInvocation invocation,NativeRollPlan plan) { return false; }
        internal static void Install(HarmonyLib.Harmony harmony) { }
    }
}
