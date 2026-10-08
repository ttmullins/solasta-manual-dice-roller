namespace ManualDiceRoller
{
    internal static class ShovePatches
    {
        internal static bool TryPrepare(NativeInvocation invocation,NativeRollPlan plan) { return false; }
        internal static void Install(HarmonyLib.Harmony harmony) { }
    }
}
