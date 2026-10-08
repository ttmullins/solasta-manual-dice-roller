using System;
using System.Collections;
using System.Collections.Generic;

namespace ManualDiceRoller
{
    public static class Plugin
    {
        public static bool Enabled = true; public static bool Multiplayer; public static TestSettings Settings = new TestSettings();
        public static bool UnsupportedRulesMod => false;
        public sealed class TestSettings { public bool EnemyRolls = true; public bool SpellPools = true; }
        public static bool Networked() { return Multiplayer; }
        public static bool Allows(RuleDefinitions.RollContext context) { return true; }
        public static List<string> Logs = new List<string>(); public static void Log(string message) { Logs.Add(message); }
        public static void Warn(string message) { Logs.Add(message); }
    }
    public sealed class RollPrompt
    {
        public readonly string Title, Actor, Detail, Note; public readonly int[] DieFaces;
        public RollPrompt(string title, string actor, string detail, string note, params int[] dieFaces)
        { Title = title; Actor = actor; Detail = detail; Note = note; DieFaces = (int[])dieFaces.Clone(); }
    }
    public static class RollWindow
    {
        public static bool IsVisible;
        public static bool Visible { get { return IsVisible; } }
        public static RollPrompt CurrentPrompt;
        public static Queue<int> Inputs = new Queue<int>(); public static List<RollPrompt> Prompts = new List<RollPrompt>();
        public static Action<RollPrompt> BeforeAccept; public static bool CancelNext;
        public static bool MutateAfterAccept;
        public static Action<int[]> Accept; public static Action Native;
        public static void Display(RollPrompt prompt, Action<int[]> accept, Action useNative)
        {
            if (IsVisible) throw new InvalidOperationException("A manual roll window is already open.");
            IsVisible = true; CurrentPrompt = prompt; Accept = accept; Native = useNative; Prompts.Add(prompt);
            if (BeforeAccept != null) BeforeAccept(prompt);
            if (CancelNext) { CancelNext = false; Close(); useNative(); return; }
            var values = new int[prompt.DieFaces.Length];
            for (int i = 0; i < values.Length; i++) values[i] = Inputs.Count == 0 ? prompt.DieFaces[i] : Inputs.Dequeue();
            accept(values);
            if (MutateAfterAccept && values.Length > 0) values[0] = 1;
        }
        public static void Close() { IsVisible = false; CurrentPrompt = null; Accept = null; Native = null; }
        public static void Close(RollPrompt expected) { if (IsVisible && ReferenceEquals(CurrentPrompt, expected)) Close(); }
    }
    public static class EffectPatches
    {
        public static Func<NativeInvocation, NativeRollPlan, bool> Prepare;
        public static bool TryPrepare(NativeInvocation call, NativeRollPlan plan) { return Prepare != null && Prepare(call, plan); }
        public static bool IsEligibleNativeDraw(RulesetActor actor, RuleDefinitions.RollContext context, bool isStatic) { return true; }
    }
    public static class RestPatches
    {
        public static bool CanConsumeNativeHitDie { get { return false; } }
        public static bool TryPrepare(NativeInvocation call, NativeRollPlan plan) { return false; }
        public static void Reset() { }
    }
}
