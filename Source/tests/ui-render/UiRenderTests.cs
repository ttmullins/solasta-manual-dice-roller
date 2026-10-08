using System;
using System.Collections.Generic;
using System.Reflection;
using ManualDiceRoller;
using UnityEngine;

static class UiRenderTests
{
    static int assertions, failures, cases;
    static int accepted, automatic;
    static int[] result;
    static bool callbackSawClosed;
    static FakeCursor cursor;
    static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    const string Confirm = RollWindow.ConfirmLabel, Auto = RollWindow.AutomaticLabel;
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static object Window { get { return typeof(RollWindow).GetField("instance", PrivateStatic).GetValue(null); } }
    static T Field<T>(string name) { return (T)typeof(RollWindow).GetField(name, PrivateInstance).GetValue(Window); }
    static string[] Entries { get { return Field<string[]>("entries"); } }
    static int Active { get { return Field<int>("active"); } }
    static void Invoke(string name, params object[] args)
    {
        try { typeof(RollWindow).GetMethod(name, PrivateInstance).Invoke(Window, args); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    static void Frame(Event e)
    {
        Event.current = e; GUI.Context = true; GUI.Records.Clear();
        try { Invoke("OnGUI"); }
        finally { GUI.Context = false; Event.current = null; }
    }
    static void Paint() { Frame(new Event(EventType.Repaint)); }
    static void Type(string text) { foreach (char c in text) Frame(new Event(EventType.KeyDown) { character = c }); }
    static void Key(KeyCode code, bool shift = false) { Frame(new Event(EventType.KeyDown) { keyCode = code, shift = shift }); }
    static void Press(string caption) { Paint(); GUI.Click = caption; Frame(new Event(EventType.MouseUp)); }
    static RollPrompt Display(params int[] faces)
    {
        var prompt = new RollPrompt("ABILITY CHECK", "Ellie", "Dexterity · Acrobatics", "Enter each raw die.", faces);
        RollWindow.Display(prompt, delegate(int[] values) { accepted++; result = values; callbackSawClosed = !RollWindow.Visible; }, delegate { automatic++; callbackSawClosed = !RollWindow.Visible; });
        return prompt;
    }
    static RollPrompt DisplayPrompt(RollPrompt prompt)
    {
        RollWindow.Display(prompt, delegate(int[] values) { accepted++; result = values; callbackSawClosed = !RollWindow.Visible; }, delegate { automatic++; callbackSawClosed = !RollWindow.Visible; });
        return prompt;
    }
    static bool HasText(string kind, string text) { return GUI.Records.Exists(r => (kind == null || r.Kind == kind) && r.Text == text); }
    static GuiRecord Find(string kind, string text) { return GUI.Records.Find(r => (kind == null || r.Kind == kind) && r.Text == text); }
    static bool Same(Color a, Color b) { return Math.Abs(a.r - b.r) < 1e-4f && Math.Abs(a.g - b.g) < 1e-4f && Math.Abs(a.b - b.b) < 1e-4f && Math.Abs(a.a - b.a) < 1e-4f; }
    static double Linear(float c) { return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
    static double Luminance(Color c) { return .2126 * Linear(c.r) + .7152 * Linear(c.g) + .0722 * Linear(c.b); }
    static double Contrast(Color a, Color b) { double x = Luminance(a) + .05, y = Luminance(b) + .05; return Math.Max(x, y) / Math.Min(x, y); }
    static readonly string[][] KindLabels = {
        new[] { "attack", "ATTACK" }, new[] { "check", "CHECK" }, new[] { "save", "SAVE" }, new[] { "death", "DEATH SAVE" },
        new[] { "initiative", "INITIATIVE" }, new[] { "damage", "DAMAGE" }, new[] { "healing", "HEALING" }, new[] { "temphp", "TEMP HP" },
        new[] { "feature", "FEATURE" }, new[] { "pool", "POOL" }, new[] { "other", "ROLL" } };
    static void Reset()
    {
        Input.FailRead = false; Input.Held = Input.Released = 0;
        if (cursor != null) { cursor.FailRead = cursor.FailShow = false; }
        Plugin.FailSave = false; Plugin.Settings = new TestSettings();
        GUI.Context = true;
        try { RollWindow.DestroyWindow(); }
        catch { typeof(RollWindow).GetField("instance", PrivateStatic).SetValue(null, null); }
        finally { GUI.Context = false; }
        RollWindow.ResetCapture(); GUI.Reset(); Texture2D.Reset(); Event.current = null;
        RollWindow.FrameUpdate = null; RollWindow.ThemeProvider = null; RollHistory.Clear();
        Screen.width = 1280; Screen.height = 720; Time.frameCount++; Time.realtimeSinceStartup = 100;
        Plugin.Enabled = true; Plugin.IsMultiplayer = false; Plugin.Errors.Clear(); RuntimeRolls.Resets = 0;
        cursor = new FakeCursor(); Gui.GuiService = cursor;
        accepted = automatic = 0; result = null; callbackSawClosed = false;
    }
    static void Case(string name, Action body)
    {
        Reset(); cases++;
        try { body(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + ": " + error.Message); }
    }
    static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    static bool FiniteRect(Rect value) { return Finite(value.x) && Finite(value.y) && Finite(value.width) && Finite(value.height); }
    static void BeginDrag()
    {
        Frame(new Event(EventType.MouseDown) { button = 0, mousePosition = new Vector2(10, 10) });
        Check(GUIUtility.PeekHotControl != 0, "Header owns drag after press");
    }
    static Vector2 Center(Rect r) { return new Vector2(r.x + r.width / 2, r.y + r.height / 2); }
    static void Main()
    {
        Case("Display outside GUI and raw mixed-face rendering", delegate {
            var prompt = Display(20, 4, 8);
            Check(RollWindow.IsOwned(prompt) && cursor.Shown, "Display opens owned prompt and cursor outside GUI");
            Paint();
            Check(HasText("label", "d20") && HasText("label", "d4") && HasText("label", "d8"), "Every die is labelled with its own type");
            Check(GUI.Records.Exists(r => r.Text == Confirm && !r.Enabled), "Blank raw faces disable Confirm");
            Check(HasText("button", "1") && HasText("button", "20") && !HasText("button", "21"), "The keypad offers exactly the active d20's faces");
            Check(GUI.ScrollDepth == 0 && GUI.BeginCalls == GUI.EndCalls, "Normal rendering balances scrolling");
            Check(Active == 0, "The first die is active");
            Type("20"); Check(Entries[0] == "20" && Active == 1, "A complete d20 face advances to the next die");
            Type("4"); Check(Entries[1] == "4" && Active == 2, "A d4 face commits immediately");
            Paint(); Check(HasText("button", "8") && !HasText("button", "9"), "The keypad follows the active die type");
            Type("8"); Check(Entries[2] == "8", "Final die entered");
            Press(Confirm);
            Check(accepted == 1 && automatic == 0 && callbackSawClosed, "Confirm runs exactly once after close");
            Check(result.Length == 3 && result[0] == 20 && result[1] == 4 && result[2] == 8, "Confirm passes individual raw faces");
            Check(!cursor.Shown && !RollWindow.Visible, "Confirm restores hidden game cursor");
        });
        Case("Typing waits on ambiguous prefixes and commits others", delegate {
            Display(20, 20);
            Type("1"); Check(Entries[0] == "1" && Active == 0, "'1' could still become 10-19, so focus stays");
            Type("5"); Check(Entries[0] == "15" && Active == 1, "'15' commits and advances");
            Type("2"); Check(Entries[1] == "2" && Active == 1, "'2' could still become 20");
            Type("7"); Check(Entries[1] == "7" && Active == 1, "An impossible extension restarts the face");
            Key(KeyCode.Backspace); Check(Entries[1] == "", "Backspace clears a committed face");
            Key(KeyCode.Backspace); Check(Active == 0 && Entries[0] == "", "Backspace on an empty die returns to the previous die");
        });
        Case("Printed zero faces on d10 and d100", delegate {
            Display(10, 100, 100);
            Type("0"); Check(Entries[0] == "10" && Active == 1, "A d10's 0 face is ten");
            Type("0"); Check(Entries[1] == "100" && Active == 2, "A percentile 00 is one hundred");
            Type("42"); Check(Entries[2] == "42", "Two-digit percentile face");
            Key(KeyCode.Return); Check(accepted == 1 && result[0] == 10 && result[1] == 100 && result[2] == 42, "Enter confirms d10/d100 faces");
        });
        Case("Invalid raw face cannot confirm", delegate {
            Display(20); Type("0");
            Paint(); Check(GUI.Records.Exists(r => r.Text == Confirm && !r.Enabled), "Zero on a d20 is not a face");
            Key(KeyCode.Return); Check(RollWindow.Visible && accepted == 0 && Active == 0, "Enter on an invalid face keeps the prompt open on that die");
            Press(Confirm); Check(RollWindow.Visible && accepted == 0, "Disabled Confirm cannot submit");
        });
        Case("Keypad selects faces and advances", delegate {
            Display(6, 6);
            Press("4"); Check(Entries[0] == "4" && Active == 1, "Clicking a face fills the active die and advances");
            Paint(); Check(GUI.Records.Exists(r => r.Kind == "button" && r.Text == "6"), "d6 keypad");
            Press("6"); Check(Entries[1] == "6", "Second face");
            Key(KeyCode.Return); Check(accepted == 1 && result[0] == 4 && result[1] == 6, "Return confirms the keypad entries");
        });
        Case("Tab, arrows, wheel and slot clicks navigate", delegate {
            Display(20, 20, 20);
            Key(KeyCode.Tab); Check(Active == 1, "Tab moves forward");
            Key(KeyCode.Tab, true); Check(Active == 0, "Shift+Tab moves back");
            Key(KeyCode.RightArrow); Check(Active == 1, "Right arrow moves forward");
            Key(KeyCode.LeftArrow); Key(KeyCode.LeftArrow); Check(Active == 2, "Left arrow wraps around");
            Key(KeyCode.UpArrow); Check(Entries[2] == "1", "Up arrow on a blank die starts at 1");
            Key(KeyCode.UpArrow); Key(KeyCode.UpArrow); Key(KeyCode.DownArrow); Check(Entries[2] == "2", "Up/down step the face");
            Paint();
            var slots = GUI.Records.FindAll(r => r.Kind == "slot");
            Check(slots.Count == 3, "Each die is a clickable slot");
            GUI.ClickAt = Center(slots[0].Position); Frame(new Event(EventType.MouseUp));
            Check(Active == 0, "Clicking a slot selects that die");
            Paint(); slots = GUI.Records.FindAll(r => r.Kind == "slot");
            var wheel = new Event(EventType.ScrollWheel) { mousePosition = Center(slots[1].Position), delta = new Vector2(0, -1) }; Frame(wheel);
            Check(Active == 1 && Entries[1] == "1" && wheel.type == EventType.Used, "Mouse wheel selects and steps a die");
            Type("9"); Check(Entries[1] == "9", "Typing after a wheel step replaces the face");
        });
        Case("Advantage, target and modifier context", delegate {
            var prompt = new RollPrompt("Saving throw", "Ellie", "Wisdom save", "", 20, 20) { Advantage = 1, TargetValue = 15 };
            prompt.SetBonus(5); DisplayPrompt(prompt); Paint();
            Check(HasText("box", "ADVANTAGE") && HasText("box", "DC 15") && HasText("box", "Bonus +5") && HasText("box", "Need 10+"), "Advantage pair shows its chips");
            Check(HasText("label", "Roll both d20s — the game keeps the higher") && HasText("label", "SAVING THROW"), "The pair explains which die the game keeps");
            RollWindow.Close();
            prompt = new RollPrompt("Attack roll", "Ellie", "", "", 20, 20) { Advantage = -1, TargetValue = 40, TargetLabel = "AC" };
            prompt.SetBonus(-2); DisplayPrompt(prompt); Paint();
            Check(HasText("box", "DISADVANTAGE") && HasText("box", "AC 40") && HasText("box", "Bonus -2") && HasText("box", "Need a 20"), "Disadvantage and armor class chips");
            Check(HasText("label", "Roll both d20s — the game keeps the lower"), "Disadvantage explains the kept die");
            RollWindow.Close();
            prompt = new RollPrompt("Ability check", "Ellie", "", "", 20) { TargetValue = 5 }; prompt.SetBonus(9); DisplayPrompt(prompt); Paint();
            Check(HasText("box", "Any roll succeeds") && !GUI.Records.Exists(r => r.Text.StartsWith("ADVANTAGE")), "Guaranteed success and no advantage chip for a single die");
            RollWindow.Close();
            DisplayPrompt(new RollPrompt("Damage", "Ellie", "", "", 6, 6)); Paint();
            Check(!GUI.Records.Exists(r => r.Kind == "box" && (r.Text.StartsWith("DC") || r.Text.StartsWith("Bonus"))), "No chips without context");
            RollWindow.Close();
            prompt = new RollPrompt("Saving throw", "Ellie", "", "", 20, 20) { Advantage = 1, TargetValue = 15 }; prompt.SetBonus(5);
            Screen.width = 520; DisplayPrompt(prompt); Paint();
            var chips = GUI.Records.FindAll(r => r.Kind == "box" && r.Text.Length > 0 && r.Text != "ADVANTAGE" ? true : r.Kind == "box" && r.Text == "ADVANTAGE");
            Check(chips.Count == 4 && chips.TrueForAll(c => c.Position.x + c.Position.width <= GUI.WindowPosition.width), "Narrow windows wrap chips instead of dropping them");
        });
        Case("Header options button opens window options and never drags", delegate {
            Display(20); Paint();
            Check(HasText("button", RollWindow.MenuLabel) && HasText("label", "Type or click each face · Tab or arrow keys move · Enter confirms · Esc lets the game roll"), "Options button and keyboard hint");
            var menu = GUI.Records.Find(r => r.Text == RollWindow.MenuLabel).Position;
            Frame(new Event(EventType.MouseDown) { button = 0, mousePosition = Center(menu) });
            Check(GUIUtility.PeekHotControl == 0 && !Field<bool>("dragging"), "Pressing the options button does not start a drag");
            Press(RollWindow.MenuLabel); Paint();
            Check(HasText("button", RollWindow.SmallerLabel) && HasText("button", RollWindow.LargerLabel) && HasText("button", RollWindow.CenterLabel) && HasText("button", RollWindow.NoticesOnLabel), "Options row replaces the hint");
            Press(RollWindow.LargerLabel); Check(Math.Abs(Plugin.Settings.WindowScale - 1.1f) < .001f, "Larger");
            Press(RollWindow.SmallerLabel); Press(RollWindow.SmallerLabel); Check(Math.Abs(Plugin.Settings.WindowScale - .9f) < .001f, "Smaller");
            for (int i = 0; i < 6; i++) Press(RollWindow.SmallerLabel);
            Check(Math.Abs(Plugin.Settings.WindowScale - .75f) < .001f, "Size stays within range");
            Press(RollWindow.NoticesOnLabel); Paint(); Check(!Plugin.Settings.AutoRollNotices && HasText("button", RollWindow.NoticesOffLabel), "Game-roll notices toggle");
            BeginDrag(); Frame(new Event(EventType.MouseDrag) { delta = new Vector2(-300, -200) }); Frame(new Event(EventType.MouseUp));
            Press(RollWindow.CenterLabel); Paint();
            var position = Field<Rect>("position");
            Check(Math.Abs(position.x + position.width / 2 - Screen.width / 2f) < 1 && Math.Abs(position.y + position.height / 2 - Screen.height / 2f) < 1, "Re-center");
            Check(Plugin.Settings.WindowX == position.x && Plugin.Settings.WindowY == position.y, "Centered position is remembered");
            Check(RollWindow.Visible && accepted == 0 && automatic == 0, "Options never answer the roll");
            Press(RollWindow.MenuLabel); Paint(); Check(!HasText("button", RollWindow.SmallerLabel), "Closing the options restores the hint");
            Type("12"); Key(KeyCode.Return); Check(accepted == 1 && result[0] == 12, "The roll still completes");
            Display(20); Paint(); Check(!HasText("button", RollWindow.SmallerLabel), "A new prompt starts with options closed");
        });
        Case("Multiple dice show a running total", delegate {
            Display(6, 6, 6); Type("34"); Paint();
            // 0.8 layout: the count and a larger, kind-coloured total share the band under the dice.
            var count = Find("label", "2 of 3 dice"); var total = Find("label", "Total 7");
            Check(count != null && total != null, "Running total of entered faces");
            Check(total.Style.fontSize > count.Style.fontSize && total.Position.x >= count.Position.x + count.Position.width, "The total is larger and right of the dice count");
            Type("5"); Paint(); Check(HasText("label", "3 of 3 dice") && HasText("label", "Total 12"), "The total follows every entered face");
        });
        Case("Auto blank prompt closes before native callback", delegate {
            Display(20); Press(Auto);
            Check(automatic == 1 && accepted == 0 && callbackSawClosed && !cursor.Shown, "Auto resolves once after cursor restoration");
        });
        Case("Enter and Escape callbacks", delegate {
            Display(20); Type("11"); Key(KeyCode.Return);
            Check(accepted == 1 && result[0] == 11, "Return confirms entered raw face");
            Display(6); Key(KeyCode.Escape);
            Check(automatic == 1 && !RollWindow.Visible, "Escape completes current native step");
        });
        Case("Paired key/character events act once", delegate {
            Display(20, 20, 20);
            Key(KeyCode.Alpha3); Check(Entries[0] == "" && Active == 0, "A digit's key-code event types nothing by itself");
            Frame(new Event(EventType.KeyDown) { character = '\t' }); Check(Active == 0, "A tab character twin does not move twice");
            Frame(new Event(EventType.KeyDown) { character = '\n' }); Check(RollWindow.Visible && accepted == 0, "A newline character twin does not submit");
            Key(KeyCode.Keypad4); Check(Entries[0] == "", "Keypad key codes wait for their character");
            Type("4"); Check(Entries[0] == "4" && Active == 1, "The character event types the face");
        });
        Case("Old owner cannot close successor prompt", delegate {
            var first = Display(20); RollWindow.Close(first);
            var second = Display(6); RollWindow.Close(first);
            Check(RollWindow.IsOwned(second), "Stale close preserves successor owner");
            RollWindow.Close(second); Check(!RollWindow.Visible, "Current owner close succeeds outside GUI");
        });
        Case("Completion callback failure cancels runtime", delegate {
            var prompt = new RollPrompt("ATTACK", "Ellie", "", "", 20);
            RollWindow.Display(prompt, delegate { throw new InvalidOperationException("injected callback"); }, delegate { });
            Type("10"); Press(Confirm);
            Check(!RollWindow.Visible && RuntimeRolls.Resets == 1 && Plugin.Errors.Count > 0, "Failed accepted callback resets after close");
        });
        Case("Header drag accumulates and releases", delegate {
            Display(20); BeginDrag(); var initial = Field<Rect>("position");
            Frame(new Event(EventType.MouseDrag) { delta = new Vector2(37, 19) }); var moved = Field<Rect>("position");
            Check(moved.x == initial.x + 37 && moved.y == initial.y + 19, "Header motion moves window by event delta");
            Frame(new Event(EventType.MouseUp)); Check(GUIUtility.PeekHotControl == 0, "Mouse release drops header ownership");
        });
        Case("Close while dragging outside GUI is lifecycle-safe", delegate {
            var prompt = Display(20); BeginDrag(); RollWindow.Close(prompt);
            Check(!RollWindow.Visible && !cursor.Shown, "Native coroutine teardown closes and restores cursor outside GUI");
            Paint(); Check(GUIUtility.PeekHotControl == 0, "Next GUI pass releases deferred header control");
        });
        Case("Focus loss while dragging outside GUI is lifecycle-safe", delegate {
            Display(20); BeginDrag(); Invoke("OnApplicationFocus", false);
            Check(RollWindow.Visible && !Field<bool>("dragging"), "Focus loss stops drag while preserving roll");
            Paint(); Check(GUIUtility.PeekHotControl == 0, "Focus-loss release clears on next GUI pass");
        });
        Case("Input read failure during close cannot strand callbacks", delegate {
            Display(20); Paint(); Input.FailRead = true; GUI.Click = Auto; Frame(new Event(EventType.MouseUp));
            Check(!RollWindow.Visible && automatic == 1 && callbackSawClosed, "Input capture failure does not stop close/completion");
        });
        Case("Failed close polling retains captured held release", delegate {
            Input.Held = 1; Display(20); Time.frameCount++; Input.FailRead = true;
            RollWindow.Close(); Input.FailRead = false;
            Check(!RollWindow.Visible && RollWindow.BlocksInput(), "Previous held capture survives failed close polling");
            Time.frameCount++; Input.Held = 0; Input.Released = 1;
            Check(RollWindow.BlocksInput(), "Failed close polling still suppresses captured release");
            Time.frameCount++; Input.Released = 0; Input.Held = 1;
            Check(!RollWindow.BlocksInput(), "Next fresh press is available after captured release");
        });
        Case("Cursor restore failure cannot strand callbacks", delegate {
            Display(20); cursor.FailShow = true; Press(Auto);
            Check(!RollWindow.Visible && automatic == 1 && callbackSawClosed, "Cursor restoration failure does not stop state/callback cleanup");
            Check(Field<IGuiService>("cursorOwner") == null, "Failed cursor restoration clears owner reference");
        });
        Case("Settings save failure cannot strand completion", delegate {
            Display(20); Plugin.FailSave = true; Press(Auto);
            Check(!RollWindow.Visible && automatic == 1 && callbackSawClosed, "Persistence failure does not suspend roll");
        });
        Case("Render-window failure falls back to native", delegate {
            Display(20); GUI.FailWindow = true; Paint();
            Check(!RollWindow.Visible && automatic == 1 && !cursor.Shown, "Window failure resolves owner using native path");
            Check(Plugin.Errors.Count > 0, "Render failure is logged");
        });
        Case("Style initialization failure falls back to native", delegate {
            Display(20); GUI.FailSkin = true; Paint();
            Check(!RollWindow.Visible && automatic == 1, "Style failure closes owned request and invokes native");
        });
        Case("Partial texture allocation fault recovers next prompt", delegate {
            Display(20); Texture2D.FailAtAllocation = 3; Paint();
            Check(!RollWindow.Visible && automatic == 1, "Partial texture allocation failure completes native step");
            Check(Texture2D.Created.TrueForAll(t => t.Destroyed), "Partially created textures are released");
            Texture2D.FailAtAllocation = 0; Display(6); Paint();
            Check(RollWindow.Visible && HasText("label", "d6"), "Later prompt renders after partial texture creation failure");
            Press(Auto);
            Check(!RollWindow.Visible && automatic == 2, "Recovered prompt completes normally");
        });
        Case("Control render failure balances scroll and falls back", delegate {
            Display(20, 6); GUI.FailText = true; Paint();
            Check(GUI.ScrollDepth == 0 && GUI.BeginCalls == GUI.EndCalls, "Control exception exits opened scroll view");
            Check(!RollWindow.Visible && automatic == 1 && !cursor.Shown, "Control failure invokes owner-local native path");
        });
        Case("GUI color and enabled state restore on render failure", delegate {
            Display(20); GUI.color = new Color(.2f, .3f, .4f, .5f); GUI.enabled = false; GUI.FailWindow = true;
            Paint();
            Check(GUI.color.r == .2f && GUI.color.a == .5f && !GUI.enabled, "Render failure restores caller GUI state");
        });
        foreach (float malformed in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f, 0f }) {
            float value = malformed;
            Case("Malformed scale remains finite: " + value, delegate {
                Plugin.Settings.WindowScale = value; Display(20); Paint();
                Check(FiniteRect(GUI.WindowPosition) && GUI.WindowPosition.width > 0 && GUI.WindowPosition.height > 0, "Malformed persisted scale cannot poison layout");
            });
        }
        Case("Malformed persisted position remains finite", delegate {
            Plugin.Settings.WindowX = float.NaN; Plugin.Settings.WindowY = float.PositiveInfinity;
            Display(20); Paint(); Check(FiniteRect(GUI.WindowPosition), "Malformed coordinates are replaced with safe position");
        });
        Case("Missing settings falls back without stranded prompt", delegate {
            Plugin.Settings = null; Display(20);
            if (RollWindow.Visible) { Paint(); Check(FiniteRect(GUI.WindowPosition), "Missing settings use safe layout"); }
            else Check(automatic == 1, "Missing settings invoke native fallback");
        });
        Case("Small screens keep the whole window on screen", delegate {
            Screen.width = 640; Screen.height = 480; Display(20, 20); Paint();
            Check(GUI.WindowPosition.width <= Screen.width && GUI.WindowPosition.height <= Screen.height, "Window scales down to a small screen");
        });
        Case("Long prompt and 512 dice stay within a bounded viewport", delegate {
            int[] faces = new int[512]; for (int i = 0; i < faces.Length; i++) faces[i] = 6;
            RollWindow.Display(new RollPrompt(new string('T', 4000), new string('A', 4000), new string('D', 4000), new string('N', 4000), faces), delegate { }, delegate { });
            Paint();
            Check(FiniteRect(GUI.WindowPosition) && GUI.WindowPosition.height <= Screen.height, "Large request window fits screen");
            var scrollRecord = GUI.Records.FindAll(r => r.Kind == "scroll"); var autoRecord = GUI.Records.Find(r => r.Text == Auto);
            var dice = scrollRecord[scrollRecord.Count - 1];
            Check(dice.Position.y + dice.Position.height <= autoRecord.Position.y, "Dice viewport does not overlap footer");
            var key = GUI.Records.Find(r => r.Kind == "button" && r.Text == "1");
            Check(dice.Position.y + dice.Position.height <= key.Position.y, "Dice viewport does not overlap the keypad");
            Check(GUI.ScrollDepth == 0, "Large request balances scrolling");
        });
        Case("Measured wrapped names cannot overlap following text", delegate {
            string longName = new string('A', 100);
            RollWindow.Display(new RollPrompt("ABILITY CHECK", longName, "Dexterity", "Enter each raw die.", 20), values => accepted++, () => automatic++);
            Paint();
            var name = GUI.Records.Find(record => record.Text == longName);
            var detail = GUI.Records.Find(record => record.Text == "Dexterity");
            var explanation = GUI.Records.Find(record => record.Text == "Enter each raw die.");
            Check(name.Position.height > 30 && detail.Position.y >= name.Position.y + name.Position.height, "Wrapped actor height pushes the next row below its measured bounds");
            Check(explanation.Position.y >= detail.Position.y + detail.Position.height, "Following message rows remain disjoint");
            Check(!Field<bool>("scrollText") && GUI.Records.FindAll(record => record.Kind == "scroll").Count == 1, "A moderately wrapped name expands the body without adding a text scrollbar");
        });
        Case("Very long text and dice use separate bounded viewports", delegate {
            string longTitle = new string('T', 2000), longActor = new string('A', 2000), longDetail = new string('D', 2000), longNote = new string('N', 2000);
            var faces = new int[100]; for (int index = 0; index < faces.Length; index++) faces[index] = 6;
            RollWindow.Display(new RollPrompt(longTitle, longActor, longDetail, longNote, faces), values => accepted++, () => automatic++);
            Paint();
            var viewports = GUI.Records.FindAll(record => record.Kind == "scroll");
            Check(Field<bool>("scrollText") && viewports.Count == 2 && Field<float>("textHeight") > Field<float>("textViewportHeight"), "Large text receives its own clipped scroll body");
            Check(viewports[0].Position.y + viewports[0].Position.height <= viewports[1].Position.y, "Text content cannot overlap the separate dice viewport");
            var titleRow = GUI.Records.Find(record => record.Text == longTitle.ToUpperInvariant()); var actorRow = GUI.Records.Find(record => record.Text == longActor);
            var detailRow = GUI.Records.Find(record => record.Text == longDetail); var noteRow = GUI.Records.Find(record => record.Text == longNote);
            Check(actorRow.Position.y >= titleRow.Position.y + titleRow.Position.height && detailRow.Position.y >= actorRow.Position.y + actorRow.Position.height && noteRow.Position.y >= detailRow.Position.y + detailRow.Position.height, "All measured message rows remain disjoint inside their content body");
            var confirmButton = GUI.Records.Find(record => record.Text == Confirm);
            Check(viewports[1].Position.y + viewports[1].Position.height <= confirmButton.Position.y && GUI.WindowPosition.height <= Screen.height && GUI.ScrollDepth == 0, "Dice, fixed completion buttons and panel fit while both scroll scopes balance");
        });
        foreach (bool invalidMeasurement in new[] { false, true }) {
            bool invalid = invalidMeasurement;
            Case("Native text measurement fault retires the owned prompt: " + invalid, delegate {
                Display(20); GUIStyle.FailMeasure = !invalid; GUIStyle.InvalidMeasure = invalid;
                Paint();
                Check(automatic == 1 && accepted == 0 && !RollWindow.Visible && !cursor.Shown, "Unavailable or nonfinite measurement completes exactly one native fallback");
                Check(GUI.ScrollDepth == 0 && RuntimeRolls.Resets == 0, "Text-layout failure leaves no scroll or reset ownership");
            });
        }
        Case("Replaced GUI service is not modified on close", delegate {
            Display(20); var replacement = new FakeCursor(); Gui.GuiService = replacement; RollWindow.Close();
            Check(replacement.Calls.Count == 0 && !RollWindow.Visible, "Close restores only original cursor service");
        });
        Case("Cursor acquisition read failure falls back locally", delegate {
            cursor.FailRead = true; Display(20);
            Check(!RollWindow.Visible && automatic == 1, "Cursor acquisition failure invokes native fallback");
        });
        Case("Cursor acquisition show failure falls back locally", delegate {
            cursor.FailShow = true; Display(20);
            Check(!RollWindow.Visible && automatic == 1, "Cursor display failure invokes native fallback");
        });
        Case("Accepted callback can open successor safely", delegate {
            var next = new RollPrompt("DAMAGE", "Ellie", "", "", 6);
            RollWindow.Display(new RollPrompt("ATTACK", "Ellie", "", "", 20), delegate {
                accepted++; Plugin.Settings.WindowX = 500; Plugin.Settings.WindowY = 20;
                RollWindow.Display(next, delegate { }, delegate { automatic++; });
            }, delegate { });
            Type("12"); Press(Confirm);
            Check(accepted == 1 && RollWindow.IsOwned(next) && cursor.Shown, "Completion can open successor after old owner closes");
            var successorPosition = Field<Rect>("position");
            Check(successorPosition.x == 500 && successorPosition.y == 20, "Old Window return cannot overwrite successor placement");
            Check(Entries.Length == 1 && Entries[0] == "" && Active == 0, "Successor starts with blank entries");
            Type("4"); Press(Auto);
            Check(automatic == 1 && !RollWindow.Visible && !cursor.Shown, "Successor owns its own completion and cursor");
        });
        Case("Destroy while dragging releases ownership outside GUI", delegate {
            Display(20); BeginDrag(); RollWindow.DestroyWindow();
            Check(!RollWindow.Visible && !cursor.Shown, "Destroy closes modal and restores cursor");
            Check(Window == null, "Destroy clears singleton");
        });
        Case("Unexpected engine destruction cancels owned pending request", delegate {
            Display(20); UnityEngine.Object.Destroy(((RollWindow)Window).gameObject);
            Check(!RollWindow.Visible && Window == null && !cursor.Shown, "Engine destruction retires window and cursor");
            Check(automatic == 1 && callbackSawClosed, "Engine destruction invokes native fallback exactly once after close");
        });
        Case("Dead native component still retires managed owner during OnDestroy", delegate {
            Display(20); var dead = (RollWindow)Window; dead.Destroyed = true;
            Check(dead == null && !object.ReferenceEquals(dead, null), "Destroyed Unity-style equality differs from managed identity");
            typeof(RollWindow).GetMethod("OnDestroy", PrivateInstance).Invoke(dead, new object[0]);
            Check(Window == null && automatic == 1 && !cursor.Shown, "Dead native reference cannot bypass cursor and request cleanup");
            Check(typeof(RollWindow).GetField("accept", PrivateInstance).GetValue(dead) == null
                && typeof(RollWindow).GetField("useNative", PrivateInstance).GetValue(dead) == null
                && typeof(RollWindow).GetField("prompt", PrivateInstance).GetValue(dead) == null
                && typeof(RollWindow).GetField("cursorOwner", PrivateInstance).GetValue(dead) == null, "Destroyed owner clears all managed callback/prompt/cursor references");
            typeof(RollWindow).GetMethod("OnDestroy", PrivateInstance).Invoke(dead, new object[0]);
            Check(automatic == 1, "Repeated defensive teardown cannot invoke completion twice");
        });
        Case("Engine-destruction fallback can open a successor", delegate {
            var next = new RollPrompt("DAMAGE", "Ellie", "", "", 6);
            RollWindow.Display(new RollPrompt("ATTACK", "Ellie", "", "", 20), delegate { }, delegate {
                automatic++; RollWindow.Display(next, delegate { }, delegate { });
            });
            UnityEngine.Object.Destroy(((RollWindow)Window).gameObject);
            Check(automatic == 1 && RollWindow.IsOwned(next), "Destroyed owner cannot erase newly created successor");
            Check(!((RollWindow)Window).gameObject.Destroyed, "Successor has a surviving GameObject");
        });
        Case("Disabled and multiplayer requests use native immediately", delegate {
            Plugin.Enabled = false; Display(20); Check(automatic == 1 && !RollWindow.Visible, "Disabled request has no modal");
            Plugin.Enabled = true; Plugin.IsMultiplayer = true; Display(20); Check(automatic == 2 && !RollWindow.Visible, "Multiplayer request has no modal");
        });
        Case("Only an authorized multiplayer owner opens a prompt", delegate {
            Plugin.IsMultiplayer = true;
            var prompt = new RollPrompt("ATTACK ROLL", "Robin", "Multiplayer", "Enter the raw face.", 20);
            RollWindow.Display(prompt, values => { accepted++; result = values; }, () => automatic++, networkAuthorized: true);
            Check(RollWindow.IsOwned(prompt) && Field<bool>("networkAuthorized"), "Owner grant belongs to this prompt");
            Type("17"); Press(Confirm);
            Check(accepted == 1 && result[0] == 17 && automatic == 0 && !RollWindow.Visible, "Authorized owner confirms through the real production UI");
            Display(6); Check(automatic == 1 && !RollWindow.Visible, "The previous owner grant does not authorize another request");
        });
        Case("An offline prompt cannot inherit multiplayer permission", delegate {
            var prompt = new RollPrompt("ABILITY CHECK", "Ellie", "Dexterity", "Enter the raw face.", 20);
            RollWindow.Display(prompt, values => accepted++, () => automatic++, networkAuthorized: true);
            Check(!Field<bool>("networkAuthorized"), "Offline caller has no live network owner grant");
            Plugin.IsMultiplayer = true; Paint();
            Check(automatic == 1 && accepted == 0 && !RollWindow.Visible, "Session transition retires the offline prompt automatically");
        });
        Case("Nonowner waiting banner is decorative and owns no input", delegate {
            Plugin.IsMultiplayer = true;
            var waiting = new RollPrompt("SAVING THROW", "Robin", "Dexterity", "", 20);
            RollWindow.DisplayWaiting(waiting, "Waiting for Robin dice (player 2)");
            var click = new Event(EventType.MouseDown) { button = 0, mousePosition = new Vector2(10, 10) }; Frame(click);
            Check(HasText(null, "SAVING THROW") && HasText(null, "Waiting for Robin dice (player 2)"), "Banner renders actual request and controlling player");
            Check(!RollWindow.Visible && !RollWindow.BlocksInput() && click.type == EventType.MouseDown && GUIUtility.PeekHotControl == 0, "Nonowner banner consumes no pointer, key or capture ownership");
            Check(cursor.Calls.Count == 0 && !cursor.Shown && accepted == 0 && automatic == 0, "Nonowner banner acquires no cursor or completion callback");
            Check(!GUI.Records.Exists(record => record.Kind == "button" || record.Kind == "slot"), "Waiting banner contains no interactive controls");
        });
        Case("Waiting banner close is bound to its request identity", delegate {
            Plugin.IsMultiplayer = true;
            var first = new RollPrompt("ATTACK ROLL", "Robin", "", "", 20);
            var second = new RollPrompt("SPELL DAMAGE", "Ellie", "", "", 6);
            RollWindow.DisplayWaiting(first, "Waiting for Robin"); RollWindow.DisplayWaiting(second, "Waiting for Ellie");
            RollWindow.CloseWaiting(first); Check(object.ReferenceEquals(Field<RollPrompt>("waitingPrompt"), second), "Stale banner close preserves the later request");
            RollWindow.CloseWaiting(second); Check(Field<RollPrompt>("waitingPrompt") == null && !RollWindow.Visible, "Current banner close retires exactly its owner");
        });
        Case("An owner modal takes precedence over a waiting banner", delegate {
            Plugin.IsMultiplayer = true;
            var waiting = new RollPrompt("OTHER PLAYER", "Robin", "", "", 20);
            RollWindow.DisplayWaiting(waiting, "Waiting for Robin");
            var owner = new RollPrompt("YOUR ATTACK", "Ellie", "", "", 20);
            RollWindow.Display(owner, values => accepted++, () => automatic++, networkAuthorized: true); Paint();
            Check(RollWindow.IsOwned(owner) && HasText(null, "YOUR ATTACK") && !HasText(null, "Waiting for Robin"), "The interactive owner prompt renders without a waiting overlay");
            RollWindow.Close(owner); RollWindow.CloseWaiting(waiting); Check(!RollWindow.Visible && Field<RollPrompt>("waitingPrompt") == null, "Both independent request owners retire cleanly");
        });
        Case("Waiting rendering failure leaves the network request untouched", delegate {
            Plugin.IsMultiplayer = true;
            var waiting = new RollPrompt("SPELL DAMAGE", "Robin", "", "", 6);
            RollWindow.DisplayWaiting(waiting, "Waiting for Robin"); GUI.FailTexture = true; Paint();
            Check(Field<RollPrompt>("waitingPrompt") == null && RuntimeRolls.Resets == 0 && accepted == 0 && automatic == 0, "A cosmetic render fault retires only the banner");
            Check(!RollWindow.Visible && cursor.Calls.Count == 0, "Failed banner leaves no input or cursor ownership");
        });
        Case("Game-rolled notices are transient and own no input", delegate {
            RollWindow.ShowNotice("ROLLED BY THE GAME", "Ellie · Concentration: 14"); Paint();
            Check(HasText("label", "ROLLED BY THE GAME") && HasText("label", "Ellie · Concentration: 14"), "Notice renders its roll");
            Check(!RollWindow.Visible && !RollWindow.BlocksInput() && !GUI.Records.Exists(r => r.Kind == "button" || r.Kind == "slot"), "Notice has no controls and blocks nothing");
            for (int i = 0; i < 5; i++) RollWindow.ShowNotice("ROLLED BY THE GAME", "n" + i);
            Paint(); Check(GUI.Records.FindAll(r => r.Kind == "box").Count == 3, "At most three notices are stacked");
            Time.realtimeSinceStartup += 7; Paint();
            Check(!GUI.Records.Exists(r => r.Kind == "box"), "Notices expire");
            Display(20); RollWindow.ShowNotice("ROLLED BY THE GAME", "beside a prompt"); Paint();
            Check(HasText("label", "beside a prompt") && RollWindow.Visible, "A notice can show alongside an open prompt");
        });
        Case("The paused-dice notice stays up longer", delegate {
            RollWindow.ShowNotice("MANUAL DICE PAUSED", "The game rolls until you reload together", null, 20f); Paint();
            Check(HasText("label", "MANUAL DICE PAUSED") && HasText("label", "The game rolls until you reload together") && !RollWindow.BlocksInput(), "The pause notice renders and blocks nothing");
            Time.realtimeSinceStartup += 12; Paint(); Check(HasText("label", "MANUAL DICE PAUSED"), "Still shown after twelve seconds");
            Time.realtimeSinceStartup += 9; Paint(); Check(!GUI.Records.Exists(r => r.Kind == "box"), "Gone after its twenty seconds");
        });
        Case("Notice drawing failure is contained", delegate {
            RollWindow.ShowNotice("ROLLED BY THE GAME", "x"); GUI.FailTexture = true; Paint();
            Check(Plugin.Errors.Count > 0 && Field<object>("notices") != null, "A cosmetic notice fault is logged and cleared");
            GUI.FailTexture = false; Display(20); Paint(); Check(RollWindow.Visible, "Prompts still render after a notice fault");
        });
        Case("Per-frame hook runs and is contained", delegate {
            int calls = 0; RollWindow.Create(); RollWindow.FrameUpdate = () => calls++;
            Invoke("Update"); Invoke("Update"); Check(calls == 2, "The deferred-roll hook runs every frame");
            RollWindow.FrameUpdate = () => { throw new InvalidOperationException("injected hook"); };
            Invoke("Update"); Check(Plugin.Errors.Count > 0, "A failing hook is logged without escaping Update");
        });
        Case("Game fonts are applied and a failing theme falls back", delegate {
            var title = new Font(); var body = new Font();
            RollWindow.ThemeProvider = () => new RollTheme { Title = title, Body = body };
            Display(20); Paint();
            Check(object.ReferenceEquals(Field<GUIStyle>("actor").font, title) && object.ReferenceEquals(Field<GUIStyle>("note").font, body), "Title and body faces come from the theme");
            RollWindow.Close(); Reset();
            RollWindow.ThemeProvider = () => { throw new InvalidOperationException("injected theme"); };
            Display(20); Paint();
            Check(RollWindow.Visible && RollWindow.ThemeProvider == null && Plugin.Errors.Count > 0, "A failing theme is dropped and the prompt still renders");
        });
        // ---- 0.8 UI: kind colours, history panel, waiting banner -------------
        Case("Every roll kind has its own readable accent", delegate {
            Check(RollPalette.Kinds.Length == KindLabels.Length && Array.TrueForAll(KindLabels, pair => Array.IndexOf(RollPalette.Kinds, pair[0]) >= 0), "The palette covers every RollKinds value");
            var panelTop = new Color(.118f, .157f, .196f, 1);
            for (int i = 0; i < KindLabels.Length; i++)
            {
                string kind = KindLabels[i][0];
                Check(RollPalette.Label(kind) == KindLabels[i][1], "Badge label for " + kind);
                Check(Contrast(RollPalette.Accent(kind), panelTop) >= 3 && Contrast(RollPalette.Text(kind), panelTop) >= 4.5, "Accent and tinted text stay readable on the panel: " + kind);
                for (int j = 0; j < i; j++)
                {
                    Color a = RollPalette.Accent(kind), b = RollPalette.Accent(KindLabels[j][0]);
                    Check(Math.Sqrt((a.r - b.r) * (a.r - b.r) + (a.g - b.g) * (a.g - b.g) + (a.b - b.b) * (a.b - b.b)) > .08, "Accents are distinct: " + kind + "/" + KindLabels[j][0]);
                }
            }
            Check(RollPalette.Normalize(null) == "other" && RollPalette.Normalize("") == "other" && RollPalette.Normalize("nonsense") == "other" && RollPalette.Normalize(" Healing ") == "healing", "Missing and unknown kinds are other");
        });
        Case("Kind colours mark the badge, title, rules, active die and total", delegate {
            var badges = new HashSet<Texture2D>(); var slots = new HashSet<Texture2D>(); var rules = new HashSet<Texture2D>();
            foreach (var pair in KindLabels)
            {
                string kind = pair[0]; var text = RollPalette.Text(kind); var accent = RollPalette.Accent(kind);
                DisplayPrompt(new RollPrompt("Cure wounds", "Ellie", "", "", 8, 8) { Kind = kind }); Type("3"); Paint();
                var badge = Find("label", pair[1]);
                Check(badge != null && Same(badge.Style.normal.textColor, text), "Badge text uses the kind tint: " + kind);
                var pill = GUI.Records.Find(r => r.Kind == "box" && r.Text == "" && r.Position.x == badge.Position.x && r.Position.y == badge.Position.y);
                Check(pill != null && pill.Style.normal.background != null, "Badge pill behind its label: " + kind); badges.Add(pill.Style.normal.background);
                Check(Same(Find("label", "CURE WOUNDS").Style.normal.textColor, text), "Title uses the kind tint: " + kind);
                Check(Same(Find("label", "Total 3").Style.normal.textColor, text), "Running total uses the kind tint: " + kind);
                var slotButtons = GUI.Records.FindAll(r => r.Kind == "slot");
                var active = GUI.Records.Find(r => r.Kind == "box" && r.Position.x == slotButtons[1].Position.x && r.Position.y == slotButtons[1].Position.y);
                var idle = GUI.Records.Find(r => r.Kind == "box" && r.Position.x == slotButtons[0].Position.x && r.Position.y == slotButtons[0].Position.y);
                Check(active.Style != idle.Style && active.Style.normal.background != idle.Style.normal.background, "The active die is outlined in the kind accent: " + kind);
                slots.Add(active.Style.normal.background);
                Check(GUI.Records.Exists(r => r.Kind == "texture" && Same(r.Tint, new Color(accent.r, accent.g, accent.b, .55f))), "The active die silhouette is tinted with the accent: " + kind);
                var rule = GUI.Records.FindAll(r => r.Kind == "texture" && r.Position.height <= 3 && r.Position.width > 300);
                Check(rule.Count >= 2 && rule.TrueForAll(r => object.ReferenceEquals(r.Texture, rule[0].Texture)), "The cap and the title rule share the kind accent line: " + kind);
                rules.Add(rule[0].Texture);
                RollWindow.Close();
            }
            Check(badges.Count == KindLabels.Length && slots.Count == KindLabels.Length && rules.Count == KindLabels.Length, "Each kind has its own badge, slot and rule textures");
        });
        Case("The kind badge sits between the title and the options button", delegate {
            DisplayPrompt(new RollPrompt("Cure wounds", "Ellie", "Cleric 3", "", 8) { Kind = "healing" }); Paint();
            var title = Find("label", "CURE WOUNDS"); var badge = Find("label", "HEALING"); var menu = Find("button", RollWindow.MenuLabel);
            Check(title.Position.x + title.Position.width <= badge.Position.x && badge.Position.x + badge.Position.width <= menu.Position.x, "Title, badge and options button never overlap");
            Check(badge.Position.y < 52 && !GUI.Records.Exists(r => r.Kind == "box" && r.Text == "HEALING"), "The badge is in the header and never reads as a chip");
            RollWindow.Close();
            DisplayPrompt(new RollPrompt("Mystery", "Ellie", "", "", 20)); Paint();
            Check(HasText("label", "ROLL"), "A prompt without a kind uses the neutral badge");
        });
        Case("Kind textures are cached per kind and released", delegate {
            DisplayPrompt(new RollPrompt("Cure wounds", "Ellie", "", "", 8) { Kind = "healing" }); Paint();
            int allocations = Texture2D.Allocations; RollWindow.Close();
            DisplayPrompt(new RollPrompt("Healing word", "Ellie", "", "", 8) { Kind = "healing" }); Paint();
            Check(Texture2D.Allocations == allocations, "A second prompt of the same kind reuses its textures"); RollWindow.Close();
            DisplayPrompt(new RollPrompt("Fire bolt", "Ellie", "", "", 8) { Kind = "damage" }); Paint();
            Check(Texture2D.Allocations == allocations + 4, "A new kind creates exactly its badge, slot, rule and stripe"); RollWindow.Close();
            int live = Texture2D.Created.FindAll(t => !t.Destroyed).Count;
            Texture2D.FailAtAllocation = Texture2D.Allocations + 2;
            DisplayPrompt(new RollPrompt("Bless", "Ellie", "", "", 8) { Kind = "feature" }); Paint();
            Check(!RollWindow.Visible && automatic == 1 && Texture2D.Created.FindAll(t => !t.Destroyed).Count == live, "A partial kind allocation releases what it made and falls back");
            Texture2D.FailAtAllocation = 0; DisplayPrompt(new RollPrompt("Bless", "Ellie", "", "", 8) { Kind = "feature" }); Paint();
            Check(RollWindow.Visible && HasText("label", "FEATURE"), "The kind renders on the next prompt");
            RollWindow.DestroyWindow();
            Check(Texture2D.Created.TrueForAll(t => t.Destroyed), "Every kind texture is released with the window");
        });
        Case("History records confirmed rolls and game rolls", delegate {
            var heal = new RollPrompt("Cure wounds", "Ellie", "", "", 8, 8) { Kind = "healing" };
            DisplayPrompt(heal); Type("35"); Press(Confirm);
            Check(accepted == 1 && RollHistory.Count == 1, "A confirmed roll enters the history");
            var entry = RollHistory.Entries[0];
            Check(entry.Source == RollSource.Manual && entry.SourceText == "You" && entry.Kind == "healing" && entry.Title == "Cure wounds" && entry.Actor == "Ellie", "Manual entry keeps title, actor and kind");
            Check(entry.Faces.Length == 2 && entry.Faces[0] == 3 && entry.Faces[1] == 5 && entry.Total == 8 && entry.Result == "8" && entry.Summary == "Ellie · 3 + 5", "Manual entry keeps faces and total");
            var attack = new RollPrompt("Attack roll", "Ellie", "", "", 20) { Kind = "attack" };
            DisplayPrompt(attack); Press(Auto);
            Check(automatic == 1 && RollHistory.Count == 2 && RollHistory.Entries[0].Source == RollSource.Game && !RollHistory.Entries[0].HasFaces && RollHistory.Entries[0].Result == "—", "Let the game roll is recorded as the game's");
            RollHistory.AddNotice("Attack roll", "Ellie", "attack", new[] { 14 }, 0);
            Check(RollHistory.Count == 2 && RollHistory.Entries[0].Faces[0] == 14 && RollHistory.Entries[0].Result == "14" && RollHistory.Entries[0].Title == "Attack roll", "The game's d20 completes the pending entry");
            RollHistory.AddNotice("Saving throw", "Robin", "save", new[] { 7, 15 }, 1);
            var save = RollHistory.Entries[0];
            Check(RollHistory.Count == 3 && save.Source == RollSource.Game && save.SourceText == "Game" && save.Total == 15 && save.Breakdown == "7 and 15, kept higher", "Another game d20 is its own entry with the kept face");
            DisplayPrompt(new RollPrompt("Spell damage", "Ellie", "", "", 6, 6) { Kind = "damage" }); Key(KeyCode.Escape);
            RollHistory.AddNotice("Attack roll", "Ellie", "attack", new[] { 9 }, 0);
            Check(RollHistory.Count == 5 && RollHistory.Entries[1].Title == "Spell damage" && !RollHistory.Entries[1].HasFaces, "Escape is recorded and a damage roll never takes a d20");
            var pair = new RollPrompt("Saving throw", "Ellie", "", "", 20, 20) { Advantage = -1, Kind = "save" };
            DisplayPrompt(pair); Type("1712"); Key(KeyCode.Return);
            Check(RollHistory.Entries[0].Total == 12 && RollHistory.Entries[0].Breakdown == "17 and 12, kept lower", "A disadvantage pair keeps the lower face");
            bool rejected = false; try { RollHistory.AddManual(heal, new[] { 1 }); } catch (ArgumentException) { rejected = true; }
            Check(rejected && RollHistory.Count == 6, "A mismatched face list is rejected");
        });
        Case("History keeps other players' rolls and the newest forty", delegate {
            var prompt = new RollPrompt("Stealth check", "Aria", "", "", 20) { Kind = "check" };
            RollHistory.AddRemote(prompt, "Robin", new[] { 18 });
            var remote = RollHistory.Entries[0];
            Check(remote.Source == RollSource.Player && remote.SourceText == "Robin" && remote.Total == 18 && remote.Kind == "check" && remote.Summary == "Aria · d20", "Another player's agreed faces are recorded with their name");
            int version = RollHistory.Version;
            for (int i = 1; i <= 44; i++) RollHistory.AddRemote(new RollPrompt("Roll " + i, "Aria", "", "", 6, 6, 4), "", new[] { 1, 2, i % 4 + 1 });
            Check(RollHistory.Count == RollHistory.Capacity && RollHistory.Capacity == 40 && RollHistory.Version == version + 44, "History is capped at forty");
            Check(RollHistory.Entries[0].Title == "Roll 44" && RollHistory.Entries[39].Title == "Roll 5" && RollHistory.Entries[0].SourceText == "Another player", "Newest first; the oldest rolls drop off");
            Check(RollHistory.Entries[0].DiceText == "2d6 + d4" && RollHistory.Entries[0].Breakdown == "1 + 2 + 1", "Mixed dice and faces are summarised");
            bool readOnly = false; try { RollHistory.Entries.Add(remote); } catch (NotSupportedException) { readOnly = true; }
            Check(readOnly, "Entries is a read-only view");
            RollHistory.Clear(); Check(RollHistory.Count == 0 && RollHistory.Entries.Count == 0, "Clear empties the history");
        });
        Case("History panel opens from the options menu, newest first", delegate {
            Display(20); Press(RollWindow.MenuLabel); Paint();
            Check(HasText("button", RollWindow.HistoryOffLabel) && !Plugin.Settings.ShowHistory, "The options row offers the history");
            Press(RollWindow.HistoryOffLabel); Paint();
            Check(Plugin.Settings.ShowHistory && HasText("label", RollWindow.HistoryTitle) && HasText("button", RollWindow.HistoryOnLabel), "The menu opens the panel");
            Check(HasText("label", RollWindow.HistoryEmpty) && RollWindow.Visible && accepted == 0 && automatic == 0, "An empty panel explains itself and never answers the roll");
            Type("12"); Key(KeyCode.Return);
            RollHistory.AddRemote(new RollPrompt("Fire bolt", "Aria", "", "", 10) { Kind = "damage" }, "Robin", new[] { 7 });
            Paint();
            var older = Find("label", "ABILITY CHECK"); var newer = Find("label", "Fire bolt");
            Check(older != null && newer != null && newer.Position.y < older.Position.y, "The newest roll is listed first");
            Check(HasText("label", "12") && HasText("label", "7") && HasText("label", "2 rolls") && GUI.Records.Exists(r => r.Kind == "label" && r.Text.StartsWith("Robin · ")), "Rows show results, count and who rolled");
            Check(Same(Find("label", "7").Style.normal.textColor, RollPalette.Text("damage")), "Results use their kind colour");
        });
        Case("History panel scrolls, clears and closes", delegate {
            RollWindow.Create(); Plugin.Settings.ShowHistory = true;
            for (int i = 0; i < 40; i++) RollHistory.AddRemote(new RollPrompt("Roll " + i, "Aria", "", "", 20), "Robin", new[] { i % 20 + 1 });
            Paint();
            Check(GUI.Records.Exists(r => r.Kind == "scroll") && GUI.ScrollDepth == 0, "A long history scrolls inside the panel");
            int rows = GUI.Records.FindAll(r => r.Kind == "label" && r.Text.StartsWith("Roll ")).Count;
            Check(rows >= 8 && rows <= 10, "Only the visible rows are drawn");
            Check(RollWindow.HistoryArea.height <= Screen.height && RollWindow.HistoryArea.x >= 0, "The panel stays on screen");
            Press(RollWindow.HistoryClearLabel); Check(RollHistory.Count == 0 && Plugin.Settings.ShowHistory, "Clear empties the history and keeps the panel");
            Paint(); Check(HasText("label", RollWindow.HistoryEmpty), "Empty state after clearing");
            Press(RollWindow.HistoryCloseLabel); Paint();
            Check(!Plugin.Settings.ShowHistory && !HasText("label", RollWindow.HistoryTitle) && RollWindow.HistoryArea.width == 0, "Close hides the panel");
            Plugin.Settings.ShowHistory = true; Plugin.Enabled = false; Paint();
            Check(!HasText("label", RollWindow.HistoryTitle), "A disabled mod shows no panel");
        });
        Case("History panel owns only the pointer over its own rectangle", delegate {
            RollWindow.Create(); Plugin.Settings.ShowHistory = true; Paint();
            var area = RollWindow.HistoryArea; var inside = Center(area); var outside = new Vector2(area.x + area.width + 60, area.y + 10);
            Check(!RollWindow.BlocksInput(), "A pointer away from the panel is not blocked");
            Frame(new Event(EventType.Repaint) { mousePosition = inside });
            Check(RollWindow.PanelHovered && RollWindow.BlocksInput(), "The pointer over the panel is kept from the game");
            Time.frameCount++; Frame(new Event(EventType.Repaint) { mousePosition = outside });
            Check(!RollWindow.BlocksInput(), "Outside the panel the game keeps its input");
            Time.frameCount++; Frame(new Event(EventType.Repaint) { mousePosition = inside }); Input.Held = 1;
            Check(RollWindow.BlocksInput(), "A press over the panel is captured");
            Time.frameCount++; Frame(new Event(EventType.Repaint) { mousePosition = outside });
            Check(RollWindow.BlocksInput(), "It stays captured while held outside");
            Time.frameCount++; Input.Held = 0; Input.Released = 1; Check(RollWindow.BlocksInput(), "Its release is captured too");
            Time.frameCount++; Input.Released = 0; Input.Held = 1; Check(!RollWindow.BlocksInput(), "A fresh press outside reaches the game");
            Input.Held = 0; Time.frameCount++; Frame(new Event(EventType.Repaint) { mousePosition = inside }); Time.frameCount += 5;
            Check(!RollWindow.BlocksInput(), "A stale hover never blocks");
            var key = new Event(EventType.KeyDown) { keyCode = KeyCode.Escape, character = '\0', mousePosition = inside }; Frame(key);
            Check(key.type == EventType.KeyDown && Plugin.Settings.ShowHistory, "The panel never takes a key");
            var click = new Event(EventType.MouseDown) { button = 0, mousePosition = outside }; Frame(click);
            Check(click.type == EventType.MouseDown && GUIUtility.PeekHotControl == 0, "A click outside the panel is not consumed");
            Display(20); Time.frameCount++; Check(RollWindow.BlocksInput(), "The modal still owns all input");
            RollWindow.Close(); Plugin.Settings.ShowHistory = false; Paint(); Time.frameCount += 3;
            Check(!RollWindow.BlocksInput() && !RollWindow.PanelHovered, "A closed panel blocks nothing");
        });
        Case("History panel header drags and remembers its place", delegate {
            RollWindow.Create(); Plugin.Settings.ShowHistory = true; Paint();
            var area = RollWindow.HistoryArea;
            Frame(new Event(EventType.MouseDown) { button = 0, mousePosition = new Vector2(area.x + 30, area.y + 12) });
            Check(GUIUtility.PeekHotControl != 0, "The panel header owns the drag");
            Frame(new Event(EventType.MouseDrag) { delta = new Vector2(40, 25) });
            Frame(new Event(EventType.MouseUp));
            Check(GUIUtility.PeekHotControl == 0 && Plugin.Settings.HistoryX == area.x + 40 && Plugin.Settings.HistoryY == area.y + 25, "Drag moves and stores the panel");
            Paint(); Check(RollWindow.HistoryArea.x == area.x + 40 && RollWindow.HistoryArea.y == area.y + 25, "The stored place is used");
            Plugin.Settings.HistoryX = float.NaN; Plugin.Settings.HistoryY = 1e9f; Paint();
            Check(FiniteRect(RollWindow.HistoryArea) && RollWindow.HistoryArea.y + RollWindow.HistoryArea.height <= Screen.height, "Malformed stored places stay on screen");
        });
        Case("The modal window keeps fresh gestures over a panel beneath it", delegate {
            // The fake boundary hands screen coordinates to the window callback,
            // so both surfaces sit at the origin to share one press position.
            Plugin.Settings.WindowX = 0; Plugin.Settings.WindowY = 0; Display(20); Paint(); var window = GUI.WindowPosition;
            Plugin.Settings.ShowHistory = true; Plugin.Settings.HistoryX = 0; Plugin.Settings.HistoryY = 0; Paint();
            Check(window.x == 0 && window.y == 0 && RollWindow.HistoryArea.x == 0 && RollWindow.HistoryArea.y == 0, "The panel lies under the window header");
            Frame(new Event(EventType.MouseDown) { button = 0, mousePosition = new Vector2(10, 10) });
            Check(GUIUtility.PeekHotControl != 0 && Field<bool>("dragging") && !Field<bool>("historyDragging"), "The window header, not the panel beneath, takes the drag");
            Frame(new Event(EventType.MouseUp)); Check(GUIUtility.PeekHotControl == 0, "The window drag releases");
            Type("9"); Key(KeyCode.Return); Check(accepted == 1 && result[0] == 9, "Keyboard entry is unchanged with the panel open");
        });
        Case("A history drawing fault closes only the panel", delegate {
            RollWindow.Create(); Plugin.Settings.ShowHistory = true; RollHistory.AddRemote(new RollPrompt("Roll", "Aria", "", "", 20), "Robin", new[] { 4 });
            GUI.FailTexture = true; Paint();
            Check(Plugin.Errors.Count > 0 && !Plugin.Settings.ShowHistory && RollHistory.Count == 1, "The fault is logged and the panel closes");
            GUI.FailTexture = false; Display(20); Paint(); Check(RollWindow.Visible, "Prompts still render");
        });
        Case("Waiting banner names who is rolling, what and which dice", delegate {
            Plugin.IsMultiplayer = true;
            var waiting = new RollPrompt("Stealth check", "Aria", "", "", 20) { Kind = "check", TargetValue = 12 };
            RollWindow.DisplayWaiting(waiting, "Ellie is rolling for Aria…");
            Time.realtimeSinceStartup = 100; Paint();
            Check(HasText("label", "STEALTH CHECK") && HasText("label", "Ellie is rolling for Aria") && HasText("label", "·  d20 · DC 12") && HasText("label", "CHECK"), "Banner shows the roll, its dice and the player");
            Check(Same(Find("label", "STEALTH CHECK").Style.normal.textColor, RollPalette.Text("check")) && Same(Find("label", "CHECK").Style.normal.textColor, RollPalette.Text("check")), "Banner uses the kind colour");
            Check(HasText("label", ".."), "The ellipsis is animated separately from the message");
            Time.realtimeSinceStartup = 100.4f; Paint(); Check(HasText("label", "..."), "The ellipsis advances");
            var banner = GUI.Records.Find(r => r.Kind == "box" && r.Text == "" && r.Position.width > 300);
            Check(Math.Abs(banner.Position.x + banner.Position.width / 2 - Screen.width / 2f) < 1 && banner.Position.y < Screen.height / 4f, "The banner is at the top centre");
            Check(!RollWindow.BlocksInput() && !GUI.Records.Exists(r => r.Kind == "button" || r.Kind == "slot") && cursor.Calls.Count == 0, "The banner owns no input");
            Check(RollWindow.WaitingMessage(null) == "Waiting for dice" && RollWindow.WaitingMessage("Waiting for Robin to roll…") == "Waiting for Robin to roll" && RollWindow.WaitingMessage("Robin is rolling...") == "Robin is rolling", "Message ellipses are replaced by the animation");
            Check(RollWindow.WaitingDetail(new RollPrompt("Fire bolt", "", "", "", 6, 6, 4)) == "2d6 + d4", "Mixed dice are summarised");
            Screen.width = 320; Paint();
            var narrow = GUI.Records.FindAll(r => r.Kind == "label");
            Check(narrow.TrueForAll(r => r.Position.x >= 0 && r.Position.x + r.Position.width <= Screen.width + .5f), "A narrow screen keeps every banner label inside the screen");
        });
        Case("Game-rolled notices carry the kind colour", delegate {
            RollWindow.ShowNotice("ROLLED BY THE GAME", "Ellie · Attack roll: 14", "attack"); Paint();
            var title = Find("label", "ROLLED BY THE GAME");
            Check(title != null && Same(title.Style.normal.textColor, RollPalette.Text("attack")), "Notice title uses the kind tint");
            Check(GUI.Records.FindAll(r => r.Kind == "box").Count == 1 && GUI.Records.Exists(r => r.Kind == "texture"), "One notice box with a kind stripe");
        });
        Reset();
        Console.WriteLine((failures == 0 ? "PASS " : "FAIL ") + cases + " production-window boundary cases; " + assertions + " assertions; " + failures + " failures.");
        Console.WriteLine("Test-only GUI simulation executes production logic. It does not test Unity rendering, physical mouse dragging, or actual Unity GUIUtility context requirements.");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
