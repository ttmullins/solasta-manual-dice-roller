using System;
using System.Reflection;
using System.Runtime.Serialization;
using ManualDiceRoller;
using UnityEngine;

namespace ManualDiceRoller
{
    public sealed class TestSettings { public float WindowScale = 1, WindowX = -1, WindowY = -1, HistoryX = -1, HistoryY = -1; public bool AutoRollNotices = true, ShowHistory; }
    public static class Plugin
    {
        public static bool Enabled = true, IsMultiplayer;
        public static TestSettings Settings = new TestSettings();
        public static void SaveSettings() { }
        public static void Error(string value) { throw new Exception(value); }
    }
    public static class RuntimeRolls { public static void Reset() { } }
}

static class UiTests
{
    static int assertions;
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static void Throws(Action action, string message) { bool thrown = false; try { action(); } catch (ArgumentException) { thrown = true; } Check(thrown, message); }
    static void Main()
    {
        var input = new[] { 20, 4, 8 };
        var prompt = new RollPrompt("Attack", "Ellie", "Strength", "Use raw dice", input);
        input[0] = 1; Check(prompt.GetFaces(0) == 20, "Caller cannot mutate prompt ranges");
        var copy = prompt.CopyDieFaces(); copy[1] = 100; Check(prompt.GetFaces(1) == 4, "Returned ranges are immutable");
        Check(prompt.Count == 3 && prompt.Title == "Attack" && prompt.Actor == "Ellie", "Prompt preserves mixed individual dice");
        int[] accepted;
        Check(RollWindow.TryFaces(prompt, new[] { "20", "1", "8" }, out accepted) && accepted[0] == 20 && accepted[1] == 1 && accepted[2] == 8, "Exact raw mixed faces accepted");
        Check(RollWindow.TryFaces(prompt, new[] { " 17 ", "4", "6" }, out accepted) && accepted[0] == 17, "Ordinary pasted whitespace accepted");
        foreach (string[] invalid in new[] { new[] { "40", "4", "8" }, new[] { "0", "4", "8" }, new[] { "-1", "4", "8" }, new[] { "", "4", "8" }, new[] { "1.5", "4", "8" }, new[] { "2147483648", "4", "8" }, new[] { "20+3", "4", "8" }, new[] { "20", "4" } })
            Check(!RollWindow.TryFaces(prompt, invalid, out accepted) && accepted == null, "Invalid/total-only entry rejected");
        Check(!RollWindow.TryFaces(prompt, null, out accepted) && !RollWindow.TryFaces(null, new[] { "1" }, out accepted), "Missing request rejected");
        Throws(() => new RollPrompt("", "", "", "", new int[0]), "Empty prompt rejected");
        Throws(() => new RollPrompt("", "", "", "", new[] { 0 }), "Zero die rejected");
        Throws(() => new RollPrompt("", "", "", "", new int[513]), "Excessive request rejected");

        var pointer = new PointerCapturePolicy();
        Check(!pointer.Blocks(10, false, 1, 0), "Native fresh press passes before prompt");
        Check(pointer.Blocks(10, true, 1, 0), "Modal captures currently held press");
        pointer.Capture(11, 1);
        Check(pointer.Blocks(11, false, 1, 0), "Closing modal still owns current frame");
        Check(pointer.Blocks(12, false, 1, 0), "Held drag stays captured after close");
        Check(pointer.Blocks(13, false, 0, 1), "Captured mouse-up never clicks game behind modal");
        Check(pointer.Blocks(13, false, 0, 1), "Every input consumer blocks captured release frame");
        Check(!pointer.Blocks(14, false, 1, 0), "Next deliberate fresh press reaches game");
        pointer.Capture(20, 3);
        Check(pointer.Blocks(21, false, 2, 1), "First of two held buttons releases safely");
        Check(pointer.Blocks(22, false, 0, 2), "Second captured button also releases safely");
        Check(!pointer.Blocks(23, false, 4, 0), "Fresh unrelated button passes after releases");
        pointer.Capture(30, 7); pointer.Reset();
        Check(!pointer.Blocks(30, false, 1, 0), "Teardown fully releases input ownership");

        // Unity native rendering is deliberately not initialized by this host.
        // An existing modal may be inspected safely with a nonzero cached ptr;
        // closing a different owner's prompt must never touch native input.
        var window = (RollWindow)FormatterServices.GetUninitializedObject(typeof(RollWindow));
        typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, new IntPtr(1));
        typeof(RollWindow).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, window);
        typeof(RollWindow).GetField("visible", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, true);
        typeof(RollWindow).GetField("prompt", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, prompt);
        var stale = new RollPrompt("Old", "Ellie", "", "", 20);
        Check(RollWindow.IsOwned(prompt) && !RollWindow.IsOwned(stale), "Modal identifies exact prompt owner");
        RollWindow.Close(stale); Check(RollWindow.IsVisible && RollWindow.IsOwned(prompt), "Disposed old waiter cannot close new modal");
        typeof(RollWindow).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null);

        // Roll-kind palette and the session history (display data only).
        Check(RollPalette.Kinds.Length == 11 && RollPalette.Normalize(null) == "other" && RollPalette.Normalize("TempHP") == "temphp" && RollPalette.Normalize("unknown") == "other", "Every kind normalises; missing or unknown kinds are other");
        Check(RollPalette.Label("temphp") == "TEMP HP" && RollPalette.Label("death") == "DEATH SAVE" && RollPalette.Label(null) == "ROLL", "Kind badge labels");
        Color heal = RollPalette.Accent("healing"), harm = RollPalette.Accent("damage"), save = RollPalette.Accent("save");
        Check(heal.g > heal.r && heal.g > heal.b && harm.r > harm.g && harm.r > harm.b && save.b > save.r && save.b > save.g, "Healing is green, damage red and saves blue");
        for (int i = 0; i < RollPalette.Kinds.Length; i++)
            for (int j = 0; j < i; j++)
                Check(RollPalette.Accent(RollPalette.Kinds[i]) != RollPalette.Accent(RollPalette.Kinds[j]), "Every kind has its own accent");
        RollHistory.Clear();
        var cure = new RollPrompt("Cure wounds", "Ellie", "", "", 8, 8) { Kind = "healing" };
        var manual = RollHistory.AddManual(cure, new[] { 3, 6 });
        Check(manual.Total == 9 && manual.SourceText == "You" && manual.Summary == "Ellie · 3 + 6" && manual.DiceText == "2d8" && manual.Kind == "healing", "A confirmed roll keeps faces, total and kind");
        var game = RollHistory.AddGame(new RollPrompt("Attack roll", "Ellie", "", "", 20) { Kind = "attack" });
        Check(!game.HasFaces && game.Result == "—", "A roll left to the game starts without faces");
        RollHistory.AddNotice("Attack roll", "Ellie", "attack", new[] { 17 }, 0);
        Check(RollHistory.Count == 2 && game.Total == 17 && game.Source == RollSource.Game, "The game's d20 completes the roll left to the game");
        var remote = RollHistory.AddRemote(cure, "Robin", new[] { 8, 8 });
        Check(remote.SourceText == "Robin" && remote.Total == 16 && object.ReferenceEquals(RollHistory.Entries[0], remote), "Another player's roll is newest");
        for (int i = 0; i < 50; i++) RollHistory.AddRemote(cure, "Robin", new[] { 1, 2 });
        Check(RollHistory.Count == RollHistory.Capacity && RollHistory.Entries[RollHistory.Count - 1].Total == 3, "The history keeps only the newest forty");
        RollHistory.Clear(); Check(RollHistory.Count == 0, "The history clears");
        Console.WriteLine("PASS " + assertions + " UI state assertions using actual Unity/game reference types. Rendering and mouse drag need in-game playtesting.");
    }
}
