// Test-only boundary. Production compiles against genuine game/Unity assemblies.
using System;
using System.Collections.Generic;
using System.Reflection;

public interface IGuiService { bool MouseCursorShown { get; } void ShowMouseCursor(bool value); }
public static class Gui { public static IGuiService GuiService; }
public sealed class FakeCursor : IGuiService
{
    public bool Shown;
    public bool FailRead, FailShow;
    public readonly List<bool> Calls = new List<bool>();
    public bool MouseCursorShown { get { if (FailRead) throw new InvalidOperationException("injected cursor read"); return Shown; } }
    public void ShowMouseCursor(bool value) { Calls.Add(value); if (FailShow) throw new InvalidOperationException("injected cursor show"); Shown = value; }
}

namespace ManualDiceRoller
{
    public sealed class TestSettings { public float WindowScale = 1, WindowX = -1, WindowY = -1, HistoryX = -1, HistoryY = -1; public bool AutoRollNotices = true, ShowHistory; }
    public static class Plugin
    {
        public static bool Enabled = true, IsMultiplayer, FailSave;
        public static TestSettings Settings = new TestSettings();
        public static readonly List<string> Errors = new List<string>();
        public static void SaveSettings() { if (FailSave) throw new InvalidOperationException("injected save"); }
        public static void Error(string value) { Errors.Add(value); }
        public static void Warn(string value) { Errors.Add(value); }
        public static void Log(string value) { Errors.Add(value); }
    }
    public static class RuntimeRolls
    {
        public static int Resets;
        public static void Reset() { Resets++; RollWindow.Close(); }
    }
}

