// Test-only boundary. Production compiles against genuine game/Unity assemblies.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace UnityEngine
{
    public class Object
    {
        public HideFlags hideFlags;
        public bool Destroyed;
        public static bool operator ==(Object left, Object right)
        {
            bool leftNull = ReferenceEquals(left, null), rightNull = ReferenceEquals(right, null);
            if (leftNull && rightNull) return true;
            if (leftNull) return right.Destroyed;
            if (rightNull) return left.Destroyed;
            return ReferenceEquals(left, right);
        }
        public static bool operator !=(Object left, Object right) { return !(left == right); }
        public override bool Equals(object value) { return (value == null || value is Object) && this == (Object)value; }
        public override int GetHashCode() { return RuntimeHelpers.GetHashCode(this); }
        public static void DontDestroyOnLoad(Object value) { }
        public static void Destroy(Object value)
        {
            if (value == null || value.Destroyed) return;
            value.Destroyed = true;
            var owner = value as GameObject;
            if (!ReferenceEquals(owner, null)) foreach (var component in owner.Components) InvokeDestroy(component);
            else InvokeDestroy(value);
        }
        static void InvokeDestroy(Object value)
        {
            // Defensive dead-native-reference scenario. Exact real Unity
            // OnDestroy/native retirement ordering is not reproduced here.
            value.Destroyed = true;
            var method = value.GetType().GetMethod("OnDestroy", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method != null) method.Invoke(value, new object[0]);
        }
    }
    public class MonoBehaviour : Object { public GameObject gameObject; }
    public class GameObject : Object
    {
        public readonly List<Object> Components = new List<Object>();
        public GameObject(string name) { }
        public T AddComponent<T>() where T : MonoBehaviour, new() { var value = new T(); value.gameObject = this; Components.Add(value); return value; }
    }
    public enum HideFlags { HideAndDontSave }
    public enum TextAnchor { UpperLeft, MiddleLeft, MiddleCenter, MiddleRight }
    public enum FontStyle { Normal, Bold }
    public enum TextClipping { Overflow, Clip }
    public enum FocusType { Passive }
    public enum TextureFormat { RGBA32 }
    public enum TextureWrapMode { Repeat, Clamp }
    public enum FilterMode { Point, Bilinear }
    public enum ScaleMode { StretchToFill, ScaleAndCrop, ScaleToFit }
    public enum KeyCode
    {
        None = 0, Backspace = 8, Tab = 9, Return = 13, Escape = 27, Space = 32,
        Alpha0 = 48, Alpha1, Alpha2, Alpha3, Alpha4, Alpha5, Alpha6, Alpha7, Alpha8, Alpha9,
        Delete = 127, Keypad0 = 256, Keypad1, Keypad2, Keypad3, Keypad4, Keypad5, Keypad6, Keypad7, Keypad8, Keypad9,
        KeypadEnter = 271, UpArrow = 273, DownArrow = 274, RightArrow = 275, LeftArrow = 276
    }
    public sealed class Font : Object { public string name = "TestFont"; }
    public enum EventType { Layout, Repaint, KeyDown, MouseDown, MouseDrag, MouseUp, ScrollWheel, Used }
    public struct Color
    {
        public float r, g, b, a;
        public Color(float red, float green, float blue, float alpha) { r = red; g = green; b = blue; a = alpha; }
        public static Color white { get { return new Color(1, 1, 1, 1); } }
    }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float horizontal, float vertical) { x = horizontal; y = vertical; }
        public static Vector2 zero { get { return new Vector2(0, 0); } }
        public static Vector2 operator +(Vector2 a, Vector2 b) { return new Vector2(a.x + b.x, a.y + b.y); }
    }
    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float horizontal, float vertical, float w, float h) { x = horizontal; y = vertical; width = w; height = h; }
        public bool Contains(Vector2 point) { return point.x >= x && point.x < x + width && point.y >= y && point.y < y + height; }
    }
    public sealed class RectOffset { public int left, right, top, bottom; public RectOffset(int l, int r, int t, int b) { left = l; right = r; top = t; bottom = b; } }
    public sealed class Texture2D : Object
    {
        public static int Allocations, FailAtAllocation;
        public static readonly List<Texture2D> Created = new List<Texture2D>();
        public int width, height;
        public TextureWrapMode wrapMode;
        public FilterMode filterMode;
        public Texture2D(int w, int h) : this(w, h, TextureFormat.RGBA32, false) { }
        public Texture2D(int w, int h, TextureFormat format, bool mipChain)
        {
            Allocations++;
            if (FailAtAllocation == Allocations) throw new InvalidOperationException("injected texture allocation");
            width = w; height = h; Created.Add(this);
        }
        public void SetPixel(int x, int y, Color color) { }
        public void SetPixels(Color[] colors) { if (colors == null || colors.Length != width * height) throw new ArgumentException("pixel count"); }
        public void Apply(bool mipmaps) { }
        public static void Reset() { Allocations = FailAtAllocation = 0; Created.Clear(); }
    }
    public sealed class GUIStyleState { public Color textColor; public Texture2D background; }
    public sealed class GUIStyle
    {
        public int fontSize;
        public FontStyle fontStyle;
        public bool wordWrap, richText;
        public TextAnchor alignment;
        public TextClipping clipping;
        public RectOffset padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0), border = new RectOffset(0, 0, 0, 0);
        public Font font;
        public GUIStyleState normal = new GUIStyleState(), hover = new GUIStyleState(), active = new GUIStyleState();
        public GUIStyle() { }
        public GUIStyle(GUIStyle other)
        {
            fontSize = other.fontSize; fontStyle = other.fontStyle; wordWrap = other.wordWrap; richText = other.richText; alignment = other.alignment; font = other.font;
            normal.textColor = other.normal.textColor; normal.background = other.normal.background;
            hover.textColor = other.hover.textColor; hover.background = other.hover.background;
            active.textColor = other.active.textColor; active.background = other.active.background;
        }
        public Vector2 CalcSize(GUIContent content)
        {
            if (FailMeasure) throw new InvalidOperationException("injected text measurement");
            return new Vector2((content.text ?? "").Length * Math.Max(1, fontSize) * .55f, Math.Max(1, fontSize) * 1.25f);
        }
        public static GUIStyle none { get { return new GUIStyle(); } }
        public static bool FailMeasure, InvalidMeasure;
        public float CalcHeight(GUIContent content, float width)
        {
            if (FailMeasure) throw new InvalidOperationException("injected text measurement");
            if (InvalidMeasure) return float.NaN;
            int characters = Math.Max(1, (int)(width / Math.Max(1, fontSize * .55f)));
            int lines = 0; foreach (string line in (content.text ?? "").Split('\n')) lines += Math.Max(1, (line.Length + characters - 1) / characters);
            return lines * Math.Max(1, fontSize) * 1.25f;
        }
    }
    public sealed class GUIContent { public readonly string text; public GUIContent(string value) { text = value; } public static GUIContent none { get { return new GUIContent(""); } } }
    public sealed class GUISkin { public GUIStyle label = new GUIStyle(), textField = new GUIStyle(), button = new GUIStyle(); public Font font; }
    public sealed class Event
    {
        public static Event current;
        public EventType type, rawType;
        public KeyCode keyCode;
        public char character;
        public bool shift;
        public int button;
        public Vector2 mousePosition, delta;
        public Event(EventType eventType) { type = rawType = eventType; }
        public void Use() { type = EventType.Used; }
    }
    public static class Screen { public static int width = 1280, height = 720; }
    public static class Time { public static int frameCount = 1; public static float realtimeSinceStartup = 100; }
    public static class Input
    {
        public static int Held, Released;
        public static bool FailRead;
        public static bool GetMouseButton(int index) { if (FailRead) throw new InvalidOperationException("injected input read"); return (Held & (1 << index)) != 0; }
        public static bool GetMouseButtonUp(int index) { if (FailRead) throw new InvalidOperationException("injected input read"); return (Released & (1 << index)) != 0; }
    }
    public static partial class Mathf { public static float Clamp(float value, float minimum, float maximum) { return Math.Max(minimum, Math.Min(maximum, value)); } }
    public static class GUIUtility
    {
        static int hot;
        public static int PeekHotControl { get { return hot; } }
        public static int hotControl { get { RequireContext(); return hot; } set { RequireContext(); hot = value; } }
        static void RequireContext() { if (!GUI.Context) throw new InvalidOperationException("GUIUtility used outside OnGUI"); }
        public static int GetControlID(int hint, FocusType focus) { RequireContext(); return hint; }
        public static void Reset() { hot = 0; }
    }
    public sealed class GuiRecord
    {
        public string Kind, Text;
        public Rect Position;
        public bool Enabled;
        // The style (or tint for textures) the control was drawn with.
        public GUIStyle Style;
        public Color Tint;
        public Texture2D Texture;
        public GuiRecord(string kind, Rect position, string text) { Kind = kind; Position = position; Text = text; Enabled = GUI.enabled; Tint = GUI.color; }
    }
    public static class GUI
    {
        static GUISkin currentSkin = new GUISkin();
        public static bool Context, enabled = true, FailWindow, FailTexture, FailText, FailSkin;
        public static Color color = Color.white;
        public static string Click, FocusedControl, NextName;
        public static Vector2? ClickAt;
        public static int ScrollDepth, BeginCalls, EndCalls;
        public static Rect WindowPosition;
        public static readonly List<GuiRecord> Records = new List<GuiRecord>();
        public static readonly Dictionary<string, string> Edits = new Dictionary<string, string>();
        static void RequireContext() { if (!Context) throw new InvalidOperationException("GUI used outside OnGUI"); }
        public static GUISkin skin { get { RequireContext(); if (FailSkin) throw new InvalidOperationException("injected skin"); return currentSkin; } }
        public static Rect Window(int id, Rect position, Action<int> callback, string text, GUIStyle style)
        {
            RequireContext(); WindowPosition = position;
            if (FailWindow) throw new InvalidOperationException("injected window");
            callback(id); return position;
        }
        public static void DrawTexture(Rect position, Texture2D texture) { RequireContext(); if (FailTexture) throw new InvalidOperationException("injected texture"); if (texture == null) throw new ArgumentNullException("texture"); Records.Add(new GuiRecord("texture", position, "") { Texture = texture }); }
        public static void DrawTexture(Rect position, Texture2D texture, ScaleMode mode, bool alpha) { DrawTexture(position, texture); }
        public static void Box(Rect position, GUIContent content, GUIStyle style)
        {
            RequireContext(); if (FailTexture) throw new InvalidOperationException("injected texture");
            if (style == null) throw new ArgumentNullException("style");
            Records.Add(new GuiRecord("box", position, content == null ? "" : content.text) { Style = style });
        }
        // A slot/selection button with no caption. FailText injects a control fault.
        public static bool Button(Rect position, GUIContent content, GUIStyle style)
        {
            RequireContext(); if (FailText) throw new InvalidOperationException("injected control");
            string text = content == null ? "" : content.text;
            Records.Add(new GuiRecord("slot", position, text) { Style = style });
            if (ClickAt.HasValue && position.Contains(ClickAt.Value) && enabled) { ClickAt = null; return true; }
            return false;
        }
        public static void Label(Rect position, string text, GUIStyle style) { RequireContext(); Records.Add(new GuiRecord("label", position, text) { Style = style }); }
        public static bool Button(Rect position, string text, GUIStyle style)
        {
            RequireContext(); Records.Add(new GuiRecord("button", position, text) { Style = style });
            if (Click == text && enabled) { Click = null; return true; } return false;
        }
        public static void SetNextControlName(string name) { RequireContext(); NextName = name; }
        public static string TextField(Rect position, string text, int maxLength, GUIStyle style)
        {
            RequireContext(); if (FailText) throw new InvalidOperationException("injected text field");
            string edit; if (Edits.TryGetValue(NextName, out edit)) text = edit;
            Records.Add(new GuiRecord("field", position, text)); return text;
        }
        public static Vector2 BeginScrollView(Rect position, Vector2 scroll, Rect view)
        {
            RequireContext(); ScrollDepth++; BeginCalls++; Records.Add(new GuiRecord("scroll", position, "")); return scroll;
        }
        public static void EndScrollView() { RequireContext(); ScrollDepth--; EndCalls++; }
        public static void FocusControl(string name) { RequireContext(); FocusedControl = name; }
        public static void Reset()
        {
            Context = false; enabled = true; FailWindow = FailTexture = FailText = FailSkin = false;
            color = Color.white; Click = FocusedControl = NextName = null; ClickAt = null; ScrollDepth = BeginCalls = EndCalls = 0;
            WindowPosition = new Rect(); Records.Clear(); Edits.Clear(); currentSkin = new GUISkin();
            GUIStyle.FailMeasure = GUIStyle.InvalidMeasure = false;
            GUIUtility.Reset();
        }
    }
}
