using System;
using System.Collections;
using HarmonyLib;

namespace ManualDiceRoller
{
    // These fixtures exercise single-player code. Entering an uninitialized
    // multiplayer route is a failure, not a simulated successful handshake.
    internal static class MultiplayerRolls
    {
        internal static bool Ready { get { return false; } }
        internal static int LocalActor { get { return 0; } }
        internal static string SessionToken { get { return "offline"; } }
        internal static bool Busy { get { return false; } }
        internal static bool Failed { get; private set; }
        internal static int OwnerFor(RulesetActor actor) { throw new InvalidOperationException("Offline fixture has no player controller."); }
        internal static string Status { get { return "Offline fixture; multiplayer is not initialized"; } }
        internal static void Initialize() { }
        internal static void Update() { }
        internal static void Shutdown() { Reset(); }
        internal static void Reset() { Failed=false; }
        internal static void Fail(string reason) { Failed=true; }
        internal static void Rearm() { }
        internal static void LogDescriptor(string actionKey,string state,string parts) { }
        internal static void LogResult(string actionKey,string result,string parts) { }
        internal static void ResultsDiffered(string actionKey,bool manualDice) { }
        internal static IEnumerator Rendezvous(string actionKey,int ordinal,string signature,RulesetActor actor,RollPrompt prompt,
            bool manualAllowed,Action<int[]> accept,Action<bool> automatic,bool allowAutomaticOnMismatch=false)
        { throw new InvalidOperationException("Offline fixture cannot enter an uninitialized multiplayer rendezvous."); }
    }
    internal static class NetworkLog { internal static string Line(string eventType,params string[] properties) { return "Offline fixture; no network log"; } }
    internal static class NativeNetworkDescriptor
    {
        internal static string ActionKey(NativeInvocation invocation,string randomState="") { return invocation.Site; }
        internal static string Identity(NativeInvocation invocation) { return invocation.Site; }
        internal static string Signature(NativeRollPlan plan) { return "offline"; }
        internal static string CompletedParts(NativeRollPlan plan,string randomState) { return "offline"; }
        internal static string Fallback(string identity,int ordinal) { return "offline"; }
        internal static string PromptSignature(NativeRollPlan plan,NativeRollPlan.Leaf leaf,FaceIdentity[] identities) { return "offline"; }
        internal static string CompletedSignature(NativeRollPlan plan,string randomState) { return "offline"; }
        internal static string Parts(NativeInvocation invocation,string randomState) { return "offline"; }
    }
    internal static class NativeNetworkScheduler
    {
        internal static bool Preflight(out string error) { error=null;return true; }
        internal static void Install(Harmony harmony) { }
        internal static bool Claim(object owner) { return true; }
        internal static bool Claim(object owner,object coroutine) { return true; }
        internal static object CurrentRun { get { return null; } }
        internal static bool InOwnedRun { get { return false; } }
        internal static void Release(object owner) { }
        internal static void Reset() { }
        internal static string RandomStamp() { return "offline"; }
    }
}
