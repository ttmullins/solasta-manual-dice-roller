using System;
using System.Collections.Generic;
using System.Reflection;

namespace ManualDiceRoller
{
    public static class Plugin
    {
        public static bool Enabled = true, Multiplayer;
        public static bool IsMultiplayer { get { return Multiplayer; } }
        public static bool UnsupportedRulesMod { get { return false; } }
        public static bool Networked() { return Multiplayer; }
        public static bool Allows(RuleDefinitions.RollContext context) { return true; }
        public sealed class IntegrationSettings
        {
            public bool EnemyRolls = true, SpellPools = true, AutoRollNotices = true;
            public float WindowScale = 1, WindowX = -1, WindowY = -1;
            // UI-only roll history panel settings read by RollWindow.
            public bool ShowHistory; public float HistoryX = -1, HistoryY = -1;
        }
        public static IntegrationSettings Settings = new IntegrationSettings();
        public static readonly List<string> Logs = new List<string>();
        public static void Log(string message) { Logs.Add(message); }
        public static void Warn(string message) { Logs.Add(message); }
        public static void Error(string message) { Logs.Add(message); }
        public static void SaveSettings() { }
    }
    public static class EffectPatches
    {
        // A descriptor bridge only: actual Jump/TA, await rewriting, Runtime,
        // broker and RollWindow are used. GL ability discovery has its own test.
        public static bool TryPrepare(NativeInvocation call, NativeRollPlan plan)
        {
            var actor = call.Receiver as GameLocationCharacter;
            if (actor == null || call.Method.Name != "RollAbilityCheck") return false;
            plan.AddActorDice(actor.RulesetCharacter, RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AbilityCheck, 1,
                call.Arg<RuleDefinitions.AdvantageType>("advantageType"),
                actor.RulesetCharacter.IsProficient(call.Arg<string>("proficiencyName", "")), call.Arg<string>("proficiencyName", ""));
            return true;
        }
        public static bool IsEligibleNativeDraw(RulesetActor actor, RuleDefinitions.RollContext context, bool isStatic) { return true; }
    }
    public static class RestPatches
    {
        public static bool CanConsumeNativeHitDie { get { return false; } }
        public static bool TryPrepare(NativeInvocation call, NativeRollPlan plan) { return false; }
        public static void Reset() { }
    }
    public static class NativeSavePlanner
    {
        public static bool Previewing { get { return false; } }
        public static bool AllowsStaticBonus { get { return true; } }
        public static bool TryPrepare(NativeInvocation call, NativeRollPlan plan) { return false; }
        public static void Install(HarmonyLib.Harmony harmony) { }
    }
    public static class NativeAbilityPlanner
    {
        public static bool Previewing { get { return false; } }
        public static bool AllowsStaticBonus { get { return true; } }
        public static bool TryPrepare(NativeInvocation call, NativeRollPlan plan) { return false; }
        public static void Install(HarmonyLib.Harmony harmony) { }
    }
    internal static class NativeSiteManifest
    {
        internal static void Register()
        {
            var type = typeof(CharacterActionMoveStepJump).GetNestedType("<RollChecksIfNecessary>d__11", BindingFlags.NonPublic);
            var move = type.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var hash = NativePatches.Hash(move.GetMethodBody().GetILAsByteArray());
            NativePatches.Register(new AwaitSite(type.FullName, hash, 0x0070, "Jump:Acrobatics"));
            NativePatches.Register(new AwaitSite(type.FullName, hash, 0x012a, "Jump:Athletics"));
        }
    }
}
