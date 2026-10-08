using System;

namespace UnityEngine
{
    public static partial class Mathf
    {
        public static int Clamp(int value, int minimum, int maximum) { return value < minimum ? minimum : value > maximum ? maximum : value; }
        public static int Max(int first, int second) { return Math.Max(first, second); }
        public static int Min(int first, int second) { return Math.Min(first, second); }
    }
}
public interface IGuiService { bool MouseCursorShown { get; } void ShowMouseCursor(bool shown); }
public sealed class IntegrationGui : IGuiService
{
    public bool MouseCursorShown { get; private set; }
    public int Changes;
    public void ShowMouseCursor(bool shown) { MouseCursorShown = shown; Changes++; }
}
public static class Gui { public static IGuiService GuiService; }
