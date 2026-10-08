using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace ManualDiceRoller
{
    public sealed class RollPrompt
    {
        readonly int[] faces;
        public readonly string Title, Actor, Detail, Note;
        public RollPrompt(string title, string actor, string detail, string note, params int[] dieFaces)
        {
            if (dieFaces == null || dieFaces.Length == 0 || dieFaces.Length > 512) throw new ArgumentException("A prompt needs 1–512 dice.", "dieFaces");
            faces = (int[])dieFaces.Clone();
            foreach (int value in faces) if (value < 1 || value > 10000) throw new ArgumentException("Invalid die range.", "dieFaces");
            Title = string.IsNullOrEmpty(title) ? "DICE ROLL" : title;
            Actor = actor ?? ""; Detail = detail ?? ""; Note = note ?? "";
            TargetLabel = "DC";
        }
        public int Count { get { return faces.Length; } }
        public int GetFaces(int index) { return faces[index]; }
        public int[] CopyDieFaces() { return (int[])faces.Clone(); }
        // Display-only context. +1 advantage / -1 disadvantage applies to the
        // first two dice of a single d20 request. Zero target means unknown.
        public int Advantage { get; set; }
        public bool IsReroll { get; set; }
        public int TargetValue { get; set; }
        public string TargetLabel { get; set; }
        public bool HasBonus { get; private set; }
        public int Bonus { get; private set; }
        public void SetBonus(int value) { Bonus = value; HasBonus = true; }
        // Display category (RollKinds) used for colour coding; display only.
        public string Kind { get; set; }
        internal bool AdvantagePair { get { return Advantage != 0 && faces.Length == 2 && faces[0] == faces[1]; } }
    }

    // Optional fonts taken from the running game's own UI.
    public sealed class RollTheme
    {
        public Font Title, Body;
    }

    // A release belongs to the window that captured its press. Once it has
    // been released, a later fresh gesture immediately belongs to the game.
    public sealed class PointerCapturePolicy
    {
        int held, blockedFrame = -1;
        public void Capture(int frame, int buttons) { held = buttons; blockedFrame = frame; }
        public void PreserveFrame(int frame) { blockedFrame = frame; }
        public void Reset() { held = 0; blockedFrame = -1; }
        public bool Blocks(int frame, bool visible, int buttons, int released)
        {
            if (visible) { Capture(frame, buttons); return true; }
            if (blockedFrame == frame) return true;
            if ((held & (buttons | released)) != 0) { held &= buttons; blockedFrame = frame; return true; }
            held = 0; return false;
        }
    }

    // Colour coding of the prompt categories (the RollKinds constants in
    // RuntimeRolls.cs; repeated here so the window compiles on its own).
    // Accents mark badges, borders, the title rule and large numbers; body
    // text stays light on the dark panel. Display only.
    internal static class RollPalette
    {
        internal const string Other = "other";
        internal static readonly string[] Kinds = { "attack", "check", "save", "death", "initiative", "damage", "healing", "temphp", "feature", "pool", Other };
        static readonly string[] Labels = { "ATTACK", "CHECK", "SAVE", "DEATH SAVE", "INITIATIVE", "DAMAGE", "HEALING", "TEMP HP", "FEATURE", "POOL", "ROLL" };
        static readonly Color[] Accents = {
            Rgb(232, 186, 90),  // attack: gold
            Rgb(219, 152, 74),  // check: amber/ochre
            Rgb(108, 164, 238), // save: blue
            Rgb(170, 160, 198), // death save: violet-grey
            Rgb(252, 220, 108), // initiative: bright gold
            Rgb(238, 96, 80),   // damage: ember red
            Rgb(104, 206, 120), // healing: green
            Rgb(78, 198, 188),  // temporary hit points: teal
            Rgb(182, 134, 238), // feature: purple
            Rgb(244, 134, 62),  // pool: orange
            Rgb(150, 166, 182), // other: slate
        };
        static Color Rgb(int r, int g, int b) { return new Color(r / 255f, g / 255f, b / 255f, 1); }
        static int Index(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return Kinds.Length - 1;
            int index = Array.IndexOf(Kinds, kind);
            if (index < 0) index = Array.IndexOf(Kinds, kind.Trim().ToLowerInvariant());
            return index < 0 ? Kinds.Length - 1 : index;
        }
        // Unknown and missing kinds are "other".
        internal static string Normalize(string kind) { return Kinds[Index(kind)]; }
        internal static string Label(string kind) { return Labels[Index(kind)]; }
        internal static Color Accent(string kind) { return Accents[Index(kind)]; }
        // A lighter tint of the accent for coloured text on the dark panel.
        internal static Color Text(string kind)
        {
            var a = Accent(kind);
            return new Color(a.r + (1 - a.r) * .3f, a.g + (1 - a.g) * .3f, a.b + (1 - a.b) * .3f, 1);
        }
    }

    public enum RollSource { Manual, Game, Player }

    // One roll of the session history. Display data only.
    public sealed class RollRecord
    {
        public string Title = "", Actor = "", Detail = "", Kind = RollPalette.Other, Player = "";
        public RollSource Source;
        // Die sizes, and the faces entered or seen (null: the game rolled and
        // the value was not seen).
        public int[] Dice = new int[0], Faces;
        // +1/-1: the game kept the higher/lower of the two d20 faces.
        public int Advantage;
        public int Target;
        public string TargetLabel = "DC";
        public DateTime When = DateTime.Now;
        // A "Let the game roll" choice still waiting for its game d20.
        internal bool Pending;
        public bool HasFaces { get { return Faces != null && Faces.Length > 0; } }
        bool Pair { get { return Advantage != 0 && Faces != null && Faces.Length == 2; } }
        public int Total
        {
            get
            {
                if (!HasFaces) return 0;
                if (Pair) return Advantage > 0 ? Math.Max(Faces[0], Faces[1]) : Math.Min(Faces[0], Faces[1]);
                long sum = 0; foreach (int face in Faces) sum += face;
                return (int)Math.Min(int.MaxValue, sum);
            }
        }
        public string Result { get { return HasFaces ? Total.ToString() : "—"; } }
        // "4 + 6 + 2", "12 and 17, kept higher"; empty for a single die.
        public string Breakdown
        {
            get
            {
                if (!HasFaces || Faces.Length == 1) return "";
                if (Pair) return Faces[0] + " and " + Faces[1] + (Advantage > 0 ? ", kept higher" : ", kept lower");
                const int shown = 8;
                var parts = new string[Math.Min(Faces.Length, shown)];
                for (int i = 0; i < parts.Length; i++) parts[i] = Faces[i].ToString();
                return string.Join(" + ", parts) + (Faces.Length > shown ? " + …" : "");
            }
        }
        public string DiceText { get { return RollHistory.DiceText(Dice); } }
        public string SourceText
        {
            get
            {
                switch (Source)
                {
                    case RollSource.Manual: return "You";
                    case RollSource.Game: return "Game";
                    default: return string.IsNullOrEmpty(Player) ? "Another player" : Player;
                }
            }
        }
        // Second row of a history entry: who, and the faces or dice. Raw faces
        // are never compared with the target: the game adds the modifiers.
        public string Summary
        {
            get
            {
                string what = Breakdown; if (what.Length == 0) what = DiceText;
                if (string.IsNullOrEmpty(Actor)) return what;
                return what.Length == 0 ? Actor : Actor + " · " + what;
            }
        }
    }

    // The session's recent rolls, newest first, for the history panel: faces
    // confirmed in the window, rolls left to the game, the game's own d20s
    // ("rolled by the game" notices) and other players' agreed faces. Never
    // saved, never shared and never read back by any roll.
    public static class RollHistory
    {
        public const int Capacity = 40;
        // A "Let the game roll" entry takes the game's d20 seen this soon after.
        const double PendingSeconds = 30;
        static readonly List<RollRecord> items = new List<RollRecord>();
        static readonly ReadOnlyCollection<RollRecord> view = new ReadOnlyCollection<RollRecord>(items);
        public static IList<RollRecord> Entries { get { return view; } }
        public static int Count { get { return items.Count; } }
        // Changes whenever an entry is added, completed or cleared.
        public static int Version { get; private set; }
        public static RollRecord Add(RollRecord record)
        {
            if (record == null) throw new ArgumentNullException("record");
            items.Insert(0, record);
            if (items.Count > Capacity) items.RemoveRange(Capacity, items.Count - Capacity);
            Version++;
            return record;
        }
        public static void Clear() { items.Clear(); Version++; }
        static RollRecord From(RollPrompt prompt, RollSource source, int[] faces)
        {
            if (prompt == null) throw new ArgumentNullException("prompt");
            if (faces != null && faces.Length != prompt.Count) throw new ArgumentException("Every die of the prompt needs a face.", "faces");
            return new RollRecord {
                Title = prompt.Title, Actor = prompt.Actor, Detail = prompt.Detail, Kind = RollPalette.Normalize(prompt.Kind),
                Source = source, Dice = prompt.CopyDieFaces(), Faces = faces == null ? null : (int[])faces.Clone(),
                Advantage = prompt.AdvantagePair ? Math.Sign(prompt.Advantage) : 0,
                Target = prompt.TargetValue, TargetLabel = prompt.TargetLabel ?? "DC" };
        }
        // Faces the local player confirmed in the roll window.
        public static RollRecord AddManual(RollPrompt prompt, int[] faces)
        {
            if (faces == null) throw new ArgumentNullException("faces");
            return Add(From(prompt, RollSource.Manual, faces));
        }
        // "Let the game roll". A d20 request is completed by the game's own
        // d20 when it is seen (AddNotice).
        public static RollRecord AddGame(RollPrompt prompt)
        {
            var record = From(prompt, RollSource.Game, null);
            record.Pending = prompt.GetFaces(0) == 20 && (prompt.Count == 1 || prompt.AdvantagePair);
            return Add(record);
        }
        // Another player's faces, agreed by every peer.
        public static RollRecord AddRemote(RollPrompt prompt, string player, int[] faces)
        {
            if (faces == null) throw new ArgumentNullException("faces");
            var record = From(prompt, RollSource.Player, faces);
            record.Player = player ?? "";
            return Add(record);
        }
        // A d20 the game rolled for a party member. It completes the newest
        // entry when that is the same character's pending "Let the game roll".
        public static RollRecord AddNotice(string title, string actor, string kind, int[] faces, int advantage)
        {
            if (faces == null || faces.Length < 1 || faces.Length > 2) throw new ArgumentException("A game d20 has one or two faces.", "faces");
            kind = RollPalette.Normalize(kind); actor = actor ?? "";
            int sign = faces.Length == 2 ? Math.Sign(advantage) : 0;
            var dice = new int[faces.Length]; for (int i = 0; i < dice.Length; i++) dice[i] = 20;
            var latest = items.Count > 0 ? items[0] : null;
            if (latest != null && latest.Pending && latest.Source == RollSource.Game && latest.Kind == kind && latest.Actor == actor
                && Math.Abs((DateTime.Now - latest.When).TotalSeconds) <= PendingSeconds)
            {
                latest.Faces = (int[])faces.Clone(); latest.Dice = dice; latest.Advantage = sign; latest.Pending = false;
                Version++;
                return latest;
            }
            return Add(new RollRecord { Title = string.IsNullOrEmpty(title) ? "Game roll" : title, Actor = actor, Kind = kind,
                Source = RollSource.Game, Dice = dice, Faces = (int[])faces.Clone(), Advantage = sign });
        }
        // "d20", "2d20", "2d6 + d4".
        public static string DiceText(int[] dice)
        {
            if (dice == null || dice.Length == 0) return "";
            var sizes = new List<int>(); var counts = new List<int>();
            foreach (int size in dice)
            {
                int index = sizes.IndexOf(size);
                if (index < 0) { sizes.Add(size); counts.Add(1); } else counts[index]++;
            }
            var parts = new string[sizes.Count];
            for (int i = 0; i < parts.Length; i++) parts[i] = (counts[i] > 1 ? counts[i].ToString() : "") + "d" + sizes[i];
            return string.Join(" + ", parts);
        }
    }

    public sealed class RollWindow : MonoBehaviour
    {
        const int WindowId = 1297239901, DragHint = 1297239902, HistoryDragHint = 1297239903;
        const float Width = 452, Pad = 22, Slot = 62, SlotGap = 10, SlotHeight = 70, KeyHeight = 34, FooterHeight = 104, SumBand = 26;
        const float HistoryWidth = 350, HistoryHeader = 46, HistoryRow = 50, HistoryRows = 8;
        internal const string ConfirmLabel = "Confirm roll", AutomaticLabel = "Let the game roll", MenuLabel = "• • •";
        internal const string SmallerLabel = "Smaller", LargerLabel = "Larger", CenterLabel = "Re-center", NoticesOnLabel = "Notices: on", NoticesOffLabel = "Notices: off";
        internal const string HistoryOnLabel = "History: on", HistoryOffLabel = "History: off";
        internal const string HistoryTitle = "ROLL HISTORY", HistoryClearLabel = "Clear", HistoryCloseLabel = "Close", HistoryEmpty = "No rolls yet this session.";
        static readonly string[] Ellipsis = { "", ".", "..", "..." };
        static RollWindow instance;
        static readonly PointerCapturePolicy capture = new PointerCapturePolicy();
        // The history panel owns only the pointer over its own rectangle.
        static readonly PointerCapturePolicy panelCapture = new PointerCapturePolicy();
        static bool panelHover;
        static int panelHoverFrame = -10;
        static int deferredDragControl;
        // Per-frame hook for single-player deferred rolls; set by the plugin.
        internal static Action FrameUpdate;
        internal static Func<RollTheme> ThemeProvider;

        RollPrompt prompt;
        RollPrompt waitingPrompt;
        string waitingMessage;
        Action<int[]> accept;
        Action useNative;
        string[] entries;
        int active;
        bool fresh;
        Rect position;
        Vector2 scroll, textScroll, dragDelta, historyScroll;
        bool visible, submitted, dragging, networkAuthorized, menuOpen, recenter, historyDragging;
        int dragControl, historyControl;
        float scale = 1, diceHeight, height;
        float kindHeight, actorHeight, detailHeight, chipsHeight, noteHeight, textHeight, textViewportHeight, diceTop, keypadHeight, totalHeight, badgeWidth;
        bool scrollText;
        int columns = 1, rows = 1;
        IGuiService cursorOwner;
        bool previousCursor;
        GUISkin skin;
        int styleScale, textureGeneration, styleGeneration;
        Font titleFont, bodyFont;
        GUIStyle kind, actor, detail, note, chip, chipGood, chipBad, chipGold, value, valueBad, valueEmpty, dieLabel, key, keySelected, primary, primaryDisabled, secondary, hint, sum, grip, gripOpen, menuKey, menuKeyOn,
            bannerText, bannerLine, bannerDetail, panelStyle, shadowStyle, slotStyle, bannerStyle,
            historyHeading, historyCount, rowTitle, rowMeta, rowSource, historyEmpty;
        readonly List<Texture2D> textures = new List<Texture2D>();
        Texture2D panel, shadow, line, chipTexture, chipGoodTexture, chipBadTexture, chipGoldTexture, slotTexture,
            keyTexture, keyHoverTexture, keyActiveTexture, keySelectedTexture, primaryTexture, primaryHoverTexture, primaryDisabledTexture,
            secondaryTexture, secondaryHoverTexture, bannerTexture, dividerTexture;
        readonly Dictionary<int, Texture2D> dieShapes = new Dictionary<int, Texture2D>();
        // Per-kind accents, created on first use and released with the rest.
        sealed class KindArt
        {
            internal string Kind, Label;
            internal Color Accent, Text;
            internal Texture2D Badge, Slot, Line, Stripe;
            internal GUIStyle BadgeBox, BadgeText, SlotBox, Title, BannerTitle, Total, Result;
            internal int StyleScale = -1, StyleGeneration = -1;
            internal GUISkin Skin;
        }
        readonly Dictionary<string, KindArt> kindArt = new Dictionary<string, KindArt>();
        sealed class Notice { internal string Title, Text, Kind; internal float Shown, Life = 6f; }
        readonly List<Notice> notices = new List<Notice>();

        static readonly Color Parchment = new Color(.953f, .918f, .827f, 1);
        static readonly Color Muted = new Color(.663f, .706f, .741f, 1);
        static readonly Color Dim = new Color(.40f, .45f, .49f, 1);
        static readonly Color Gold = new Color(.851f, .698f, .412f, 1);
        static readonly Color GoldBright = new Color(.910f, .776f, .502f, 1);
        static readonly Color Good = new Color(.56f, .86f, .60f, 1);
        static readonly Color Bad = new Color(.95f, .58f, .53f, 1);
        static readonly Color Ink = new Color(.10f, .08f, .04f, 1);
        static readonly Color Body = new Color(.86f, .84f, .78f, 1);
        static readonly Color PanelTop = new Color(.118f, .157f, .196f, 1), PanelBottom = new Color(.075f, .098f, .125f, 1);

        public static bool Visible { get { return instance != null && instance.visible; } }
        public static bool IsVisible { get { return Visible; } }
        public static bool IsOwned(RollPrompt expected) { return instance != null && instance.visible && object.ReferenceEquals(instance.prompt, expected); }
        public static void Create()
        {
            if (instance != null) return;
            var owner = new GameObject("Manual Dice Roller");
            DontDestroyOnLoad(owner); instance = owner.AddComponent<RollWindow>();
        }
        public static void Display(RollPrompt value, Action<int[]> accepted, Action automatic)
        { Display(value, accepted, automatic, false); }
        internal static void Display(RollPrompt value, Action<int[]> accepted, Action automatic, bool networkAuthorized)
        {
            if (value == null || accepted == null || automatic == null) throw new ArgumentNullException("A roll needs a prompt and both completion callbacks.");
            if (!Plugin.Enabled || Plugin.IsMultiplayer && !networkAuthorized) { automatic(); return; }
            if (Visible) throw new InvalidOperationException("A manual roll window is already open.");
            try {
                Create();
                instance.prompt = value; instance.accept = accepted; instance.useNative = automatic;
                instance.networkAuthorized = Plugin.IsMultiplayer && networkAuthorized;
                instance.entries = new string[value.Count];
                for (int i = 0; i < value.Count; i++) instance.entries[i] = "";
                instance.active = 0; instance.fresh = true;
                instance.submitted = false; instance.scroll = instance.textScroll = Vector2.zero;
                instance.dragging = false; instance.dragDelta = Vector2.zero; instance.menuOpen = false; instance.recenter = false;
                // Mark ownership before acquiring native resources so a partial
                // initialization can release exactly this request.
                instance.visible = true;
                var owner = Gui.GuiService;
                bool previous = owner == null || owner.MouseCursorShown;
                instance.cursorOwner = owner; instance.previousCursor = previous;
                if (owner != null) owner.ShowMouseCursor(true);
                float x = Plugin.Settings == null ? -1 : Plugin.Settings.WindowX;
                float y = Plugin.Settings == null ? -1 : Plugin.Settings.WindowY;
                if (!Finite(x)) x = -1; if (!Finite(y)) y = -1;
                instance.position = new Rect(x < 0 ? Math.Max(0, (Screen.width - Width) / 2) : x, y < 0 ? Math.Max(12, (Screen.height - 420) / 2) : y, Width, 420);
                CaptureFrame();
            } catch (Exception error) {
                Report("The roll window could not open; using native dice: ", error);
                Close(value);
                InvokeCompletion(null, automatic, true);
            }
        }
        public static void Close()
        {
            // Unity's destroyed-object equality must not skip managed cleanup
            // while an engine teardown is retiring this component.
            if (object.ReferenceEquals(instance, null) || !instance.visible) return;
            CaptureFrame(); instance.visible = false; instance.submitted = true;
            instance.accept = null; instance.useNative = null; instance.prompt = null; instance.entries = null; instance.networkAuthorized = false;
            instance.ReleaseDrag();
            var owner = instance.cursorOwner; instance.cursorOwner = null;
            try { if (owner != null && object.ReferenceEquals(owner, Gui.GuiService)) owner.ShowMouseCursor(instance.previousCursor); }
            catch (Exception error) { Report("The game cursor could not be restored: ", error); }
        }
        public static void Close(RollPrompt expected)
        {
            if (IsOwned(expected)) Close();
        }
        // A non-interactive banner while another player enters a roll. The
        // message names who is rolling; the request supplies what and which dice.
        internal static void DisplayWaiting(RollPrompt expected, string message)
        {
            if (expected == null) throw new ArgumentNullException("expected");
            if (!Plugin.Enabled || !Plugin.IsMultiplayer) return;
            try { Create(); instance.waitingPrompt = expected; instance.waitingMessage = WaitingMessage(message); }
            catch (Exception error) { Report("The multiplayer waiting message could not open: ", error); }
        }
        internal static void CloseWaiting(RollPrompt expected)
        {
            if (object.ReferenceEquals(instance, null) || !object.ReferenceEquals(instance.waitingPrompt, expected)) return;
            instance.waitingPrompt = null; instance.waitingMessage = null;
        }
        // The banner animates its own ellipsis.
        internal static string WaitingMessage(string message)
        {
            string text = string.IsNullOrEmpty(message) ? "" : message.Trim();
            for (bool trimmed = true; trimmed;)
            {
                trimmed = false;
                if (text.EndsWith("…", StringComparison.Ordinal)) { text = text.Substring(0, text.Length - 1).TrimEnd(); trimmed = true; }
                else if (text.EndsWith("...", StringComparison.Ordinal)) { text = text.Substring(0, text.Length - 3).TrimEnd(); trimmed = true; }
            }
            return text.Length == 0 ? "Waiting for dice" : text;
        }
        // "Stealth check · d20 · DC 15": what the waiting banner says is rolled.
        internal static string WaitingDetail(RollPrompt value)
        {
            if (value == null) return "";
            string dice = RollHistory.DiceText(value.CopyDieFaces());
            if (value.TargetValue > 0) dice += " · " + (string.IsNullOrEmpty(value.TargetLabel) ? "DC" : value.TargetLabel) + " " + value.TargetValue;
            return dice;
        }
        // A short, non-interactive notice (e.g. a roll the game made by itself).
        internal static void ShowNotice(string title, string text) { ShowNotice(title, text, null); }
        internal static void ShowNotice(string title, string text, string rollKind) { ShowNotice(title, text, rollKind, 6f); }
        internal static void ShowNotice(string title, string text, string rollKind, float seconds)
        {
            try {
                Create();
                var list = instance.notices;
                list.Add(new Notice { Title = title ?? "", Text = text ?? "", Kind = RollPalette.Normalize(rollKind), Shown = Time.realtimeSinceStartup,
                    Life = seconds > 1 && seconds < 120 ? seconds : 6f });
                while (list.Count > 3) list.RemoveAt(0);
            } catch (Exception error) { Report("A roll notice could not be shown: ", error); }
        }
        public static void ResetCapture() { capture.Reset(); panelCapture.Reset(); panelHover = false; }
        public static void DestroyWindow()
        {
            Close(); ResetCapture();
            if (instance == null) return;
            var old = instance; instance = null; Destroy(old.gameObject);
        }
        static int HeldButtons()
        {
            int result = 0; for (int i = 0; i < 3; i++) if (Input.GetMouseButton(i)) result |= 1 << i; return result;
        }
        public static void CaptureFrame()
        {
            try { capture.Capture(Time.frameCount, HeldButtons()); }
            catch (Exception) { capture.PreserveFrame(Time.frameCount); }
        }
        // The pointer was over the open history panel at the last repaint.
        internal static bool PanelHovered { get { return panelHover && Time.frameCount - panelHoverFrame <= 2; } }
        public static bool BlocksInput()
        {
            int released = 0; for (int i = 0; i < 3; i++) if (Input.GetMouseButtonUp(i)) released |= 1 << i;
            int held = HeldButtons(), frame = Time.frameCount;
            // Separate owners: the modal window blocks everything; the history
            // panel blocks only gestures over its own rectangle.
            bool modal = capture.Blocks(frame, Visible, held, released);
            bool panelOwned = panelCapture.Blocks(frame, PanelHovered, held, released);
            return modal || panelOwned;
        }
        public static bool TryFaces(RollPrompt value, string[] text, out int[] values)
        {
            values = null; if (value == null || text == null || value.Count != text.Length) return false;
            var result = new int[text.Length];
            for (int i = 0; i < text.Length; i++) if (!int.TryParse(text[i], out result[i]) || result[i] < 1 || result[i] > value.GetFaces(i)) return false;
            values = result; return true;
        }
        static int StepFace(string text, int maximum, int step)
        {
            int current; if (!int.TryParse(text, out current)) return step > 0 ? 1 : maximum;
            return (int)Math.Max(1, Math.Min(maximum, (long)current + step));
        }
        static int Digits(int maximum) { return maximum.ToString().Length; }
        // Keyboard entry. A face is committed and focus advances as soon as no
        // further digit could produce a valid face (e.g. "3" on a d20). A lone
        // "0" on a d10/d100 is the die's printed 10/00 face.
        void TypeDigit(int digit)
        {
            if (entries == null || active < 0 || active >= entries.Length) return;
            int maximum = prompt.GetFaces(active);
            string current = fresh ? "" : entries[active];
            string next = current + digit;
            int parsed;
            if (!int.TryParse(next, out parsed) || parsed > maximum || next.Length > Digits(maximum)) { next = digit.ToString(); parsed = digit; }
            if (parsed == 0 && next.Length == 1 && (maximum == 10 || maximum == 100)) { next = maximum.ToString(); parsed = maximum; }
            entries[active] = next; fresh = false;
            if (parsed >= 1 && (long)parsed * 10 > maximum) Advance();
        }
        void Backspace()
        {
            if (entries == null || active < 0 || active >= entries.Length) return;
            if (!string.IsNullOrEmpty(entries[active]) && !fresh) { entries[active] = entries[active].Substring(0, entries[active].Length - 1); return; }
            if (!string.IsNullOrEmpty(entries[active])) { entries[active] = ""; fresh = false; return; }
            if (active > 0) { active--; entries[active] = ""; fresh = false; }
        }
        void Select(int index)
        {
            if (entries == null || entries.Length == 0) return;
            active = Math.Max(0, Math.Min(entries.Length - 1, index)); fresh = true;
        }
        void Advance()
        {
            if (entries == null) return;
            for (int step = 1; step <= entries.Length; step++)
            {
                int index = (active + step) % entries.Length;
                int parsed;
                if (!int.TryParse(entries[index], out parsed) || parsed < 1 || parsed > prompt.GetFaces(index)) { Select(index); return; }
            }
            if (active + 1 < entries.Length) Select(active + 1); else fresh = true;
        }
        void SetFace(int face)
        {
            if (entries == null || active < 0 || active >= entries.Length) return;
            entries[active] = face.ToString(); fresh = true; Advance();
        }
        void Complete(bool automatic)
        {
            if (!visible || submitted) return;
            int[] values = null;
            if (!automatic && !TryFaces(prompt, entries, out values))
            {
                // Show the first die that still needs a valid face.
                for (int i = 0; entries != null && i < entries.Length; i++)
                { int parsed; if (!int.TryParse(entries[i], out parsed) || parsed < 1 || parsed > prompt.GetFaces(i)) { Select(i); break; } }
                return;
            }
            submitted = true; var confirmed = accept; var native = useNative; var answered = prompt;
            SavePosition(); Close();
            Record(answered, automatic ? null : values);
            Action confirmation = null;
            if (!automatic) confirmation = delegate { confirmed((int[])values.Clone()); };
            InvokeCompletion(confirmation, native, automatic);
        }
        // The session history is cosmetic: a failure never affects the roll.
        static void Record(RollPrompt answered, int[] values)
        {
            try { if (answered == null) return; if (values == null) RollHistory.AddGame(answered); else RollHistory.AddManual(answered, values); }
            catch (Exception error) { Report("The roll could not be added to the history: ", error); }
        }
        static void InvokeCompletion(Action confirmed, Action native, bool automatic)
        {
            try { if (automatic) native(); else confirmed(); }
            catch (Exception error) {
                Report("The roll could not complete: ", error);
                try { RuntimeRolls.Reset(); } catch (Exception resetError) { Report("The roll session could not be reset: ", resetError); }
            }
        }
        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        static void Report(string message, Exception error) { try { Plugin.Error(message + error); } catch (Exception) { } }
        void ReleaseDrag()
        {
            if (dragging) deferredDragControl = dragControl;
            dragging = false; dragDelta = Vector2.zero;
            ReleaseDeferredDrag();
        }
        void ReleaseHistoryDrag()
        {
            if (historyDragging) deferredDragControl = historyControl;
            historyDragging = false;
            ReleaseDeferredDrag();
        }
        static void ReleaseDeferredDrag()
        {
            if (deferredDragControl == 0) return;
            try { if (GUIUtility.hotControl == deferredDragControl) GUIUtility.hotControl = 0; deferredDragControl = 0; }
            catch (Exception) { /* Lifecycle callbacks may run outside the GUI pass. */ }
        }
        void SavePosition()
        {
            try { if (Plugin.Settings == null) return; Plugin.Settings.WindowX = position.x; Plugin.Settings.WindowY = position.y; Plugin.SaveSettings(); }
            catch (Exception error) { Report("The roll window position could not be saved: ", error); }
        }
        static void SaveSettings()
        {
            try { Plugin.SaveSettings(); }
            catch (Exception error) { Report("The roll window settings could not be saved: ", error); }
        }

        // ---- procedural art -------------------------------------------------
        Texture2D Track(Texture2D texture) { texture.hideFlags = HideFlags.HideAndDontSave; textures.Add(texture); return texture; }
        Texture2D Make(int width, int length, Func<float, float, Color> shade)
        {
            var texture = Track(new Texture2D(width, length, TextureFormat.RGBA32, false));
            var pixels = new Color[width * length];
            for (int y = 0; y < length; y++) for (int x = 0; x < width; x++) pixels[y * width + x] = shade(x + .5f, y + .5f);
            texture.SetPixels(pixels); texture.wrapMode = TextureWrapMode.Clamp; texture.filterMode = FilterMode.Bilinear;
            texture.Apply(false);
            return texture;
        }
        static float Saturate(float value) { return value < 0 ? 0 : value > 1 ? 1 : value; }
        static Color Mix(Color a, Color b, float t) { return new Color(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t); }
        static Color Alpha(Color color, float alpha) { return new Color(color.r, color.g, color.b, alpha); }
        static float RoundedDistance(float x, float y, float width, float length, float radius)
        {
            float qx = Math.Abs(x - width / 2) - (width / 2 - radius), qy = Math.Abs(y - length / 2) - (length / 2 - radius);
            float ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
            return (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - radius;
        }
        // Anti-aliased rounded rectangle for nine-slice drawing (y=0 is bottom).
        Texture2D Rounded(int size, float radius, Color top, Color bottom, Color border, float borderWidth)
        {
            return Make(size, size, (x, y) => {
                float d = RoundedDistance(x, y, size, size, radius);
                float coverage = Saturate(.5f - d);
                if (coverage <= 0) return new Color(0, 0, 0, 0);
                var fill = Mix(bottom, top, y / size);
                float edge = borderWidth <= 0 ? 0 : Saturate(.5f - (Math.Abs(d + borderWidth / 2) - borderWidth / 2));
                var color = Mix(fill, new Color(border.r, border.g, border.b, Math.Max(fill.a, border.a)), edge * border.a);
                color.a *= coverage; return color;
            });
        }
        Texture2D Shadow(int size, float inset, float radius, float softness, float strength)
        {
            return Make(size, size, (x, y) => {
                float d = RoundedDistance(x, y, size, size, radius) + inset;
                float a = d <= 0 ? strength : strength * (float)Math.Exp(-(d * d) / (2 * softness * softness));
                return new Color(0, 0, 0, a);
            });
        }
        static float PolygonDistance(float x, float y, float[] points)
        {
            int count = points.Length / 2; float best = float.MaxValue; bool inside = false;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                float ax = points[j * 2], ay = points[j * 2 + 1], bx = points[i * 2], by = points[i * 2 + 1];
                float ex = bx - ax, ey = by - ay, wx = x - ax, wy = y - ay;
                float t = Saturate((wx * ex + wy * ey) / Math.Max(1e-5f, ex * ex + ey * ey));
                float dx = wx - ex * t, dy = wy - ey * t;
                best = Math.Min(best, dx * dx + dy * dy);
                if ((ay > y) != (by > y) && x < (bx - ax) * (y - ay) / (by - ay) + ax) inside = !inside;
            }
            float distance = (float)Math.Sqrt(best);
            return inside ? -distance : distance;
        }
        static float[] Regular(int sides, double rotation, float radius, float cx, float cy)
        {
            var points = new float[sides * 2];
            for (int i = 0; i < sides; i++)
            {
                double angle = rotation + i * 2 * Math.PI / sides;
                points[i * 2] = cx + radius * (float)Math.Cos(angle); points[i * 2 + 1] = cy + radius * (float)Math.Sin(angle);
            }
            return points;
        }
        // White die silhouette (outline + faint body), tinted when drawn.
        Texture2D DieShape(int faces)
        {
            Texture2D cached;
            int key = faces == 4 || faces == 6 || faces == 8 || faces == 10 || faces == 12 || faces == 20 || faces == 100 ? faces : 0;
            if (dieShapes.TryGetValue(key, out cached) && cached != null) return cached;
            const int size = 96; const float c = size / 2f, r = size * .44f, stroke = 3.2f;
            float[] shape; float[] inner = null;
            switch (key)
            {
                case 4: shape = Regular(3, Math.PI / 2, r, c, c - 4); break;
                case 6: shape = Regular(4, Math.PI / 4, r * 1.05f, c, c); break;
                case 8: shape = Regular(4, Math.PI / 2, r, c, c); break;
                case 10: case 100: shape = new[] { c, c + r, c + r * .92f, c + r * .12f, c, c - r, c - r * .92f, c + r * .12f }; break;
                case 12: shape = Regular(5, Math.PI / 2, r, c, c); inner = Regular(5, -Math.PI / 2, r * .42f, c, c); break;
                case 20: shape = Regular(6, Math.PI / 2, r, c, c); inner = Regular(3, -Math.PI / 2, r * .52f, c, c); break;
                default: shape = Regular(24, 0, r, c, c); break;
            }
            var texture = Make(size, size, (x, y) => {
                float d = PolygonDistance(x, y, shape);
                float outline = Saturate(stroke / 2 - Math.Abs(d) + .5f);
                float body = d < 0 ? .10f : 0;
                if (inner != null && d < 0)
                {
                    float i = PolygonDistance(x, y, inner);
                    outline = Math.Max(outline, .55f * Saturate(1.4f - Math.Abs(i) + .5f));
                }
                float a = Math.Max(outline, body * Saturate(.5f - d));
                return new Color(1, 1, 1, a);
            });
            dieShapes[key] = texture; return texture;
        }
        Texture2D Line(int width, Color color)
        {
            return Make(width, 1, (x, y) => {
                float t = x / width; float fade = Saturate(Math.Min(t, 1 - t) * 5);
                return new Color(color.r, color.g, color.b, color.a * fade);
            });
        }
        // A short vertical accent bar with softened ends.
        Texture2D Stripe(Color color)
        {
            return Make(4, 32, (x, y) => {
                float t = y / 32f; float fade = Saturate(Math.Min(t, 1 - t) * 8);
                return Alpha(Mix(color, new Color(1, 1, 1, 1), t * .15f), fade);
            });
        }
        void Textures()
        {
            if (panel != null) return;
            try {
                panel = Rounded(64, 12, Alpha(PanelTop, .985f), Alpha(PanelBottom, .985f), new Color(.80f, .65f, .38f, .62f), 1.6f);
                shadow = Shadow(96, 18, 16, 9, .55f);
                line = Line(128, new Color(.80f, .65f, .38f, .55f));
                chipTexture = Rounded(32, 9, new Color(.19f, .235f, .28f, 1), new Color(.16f, .20f, .24f, 1), new Color(1, 1, 1, .06f), 1);
                chipGoodTexture = Rounded(32, 9, new Color(.14f, .27f, .18f, 1), new Color(.11f, .22f, .15f, 1), new Color(.56f, .86f, .60f, .35f), 1);
                chipBadTexture = Rounded(32, 9, new Color(.31f, .15f, .15f, 1), new Color(.25f, .12f, .12f, 1), new Color(.95f, .58f, .53f, .35f), 1);
                chipGoldTexture = Rounded(32, 9, new Color(.24f, .20f, .13f, 1), new Color(.19f, .16f, .10f, 1), new Color(.85f, .70f, .41f, .55f), 1);
                slotTexture = Rounded(48, 10, new Color(.086f, .114f, .141f, 1), new Color(.063f, .086f, .106f, 1), new Color(1, 1, 1, .08f), 1.2f);
                keyTexture = Rounded(32, 8, new Color(.16f, .20f, .245f, 1), new Color(.13f, .17f, .205f, 1), new Color(1, 1, 1, .05f), 1);
                keyHoverTexture = Rounded(32, 8, new Color(.22f, .28f, .33f, 1), new Color(.18f, .23f, .28f, 1), new Color(.85f, .70f, .41f, .35f), 1);
                keyActiveTexture = Rounded(32, 8, new Color(.85f, .70f, .41f, 1), new Color(.74f, .58f, .32f, 1), new Color(1, 1, 1, .1f), 1);
                keySelectedTexture = Rounded(32, 8, new Color(.30f, .25f, .15f, 1), new Color(.25f, .21f, .12f, 1), new Color(.91f, .78f, .50f, .8f), 1.2f);
                primaryTexture = Rounded(32, 9, new Color(.886f, .741f, .451f, 1), new Color(.741f, .576f, .310f, 1), new Color(1, .94f, .78f, .35f), 1);
                primaryHoverTexture = Rounded(32, 9, new Color(.945f, .812f, .529f, 1), new Color(.804f, .643f, .365f, 1), new Color(1, .96f, .84f, .5f), 1);
                primaryDisabledTexture = Rounded(32, 9, new Color(.22f, .25f, .28f, 1), new Color(.19f, .22f, .25f, 1), new Color(1, 1, 1, .05f), 1);
                secondaryTexture = Rounded(32, 9, new Color(.12f, .155f, .19f, 1), new Color(.10f, .13f, .16f, 1), new Color(.55f, .62f, .68f, .45f), 1);
                secondaryHoverTexture = Rounded(32, 9, new Color(.17f, .21f, .25f, 1), new Color(.14f, .18f, .22f, 1), new Color(.85f, .70f, .41f, .55f), 1);
                bannerTexture = Rounded(48, 14, Alpha(PanelTop, .96f), Alpha(PanelBottom, .96f), new Color(.80f, .65f, .38f, .55f), 1.4f);
                dividerTexture = Line(128, new Color(1, 1, 1, .07f));
                textureGeneration++;
            } catch {
                ReleaseTextures();
                throw;
            }
        }
        void ReleaseTextures()
        {
            foreach (var texture in textures) if (texture != null) {
                try { Destroy(texture); } catch (Exception error) { Report("A roll window texture could not be released: ", error); }
            }
            textures.Clear(); dieShapes.Clear(); kindArt.Clear();
            panel = shadow = line = chipTexture = chipGoodTexture = chipBadTexture = chipGoldTexture = slotTexture = null;
            keyTexture = keyHoverTexture = keyActiveTexture = keySelectedTexture = primaryTexture = primaryHoverTexture = primaryDisabledTexture = null;
            secondaryTexture = secondaryHoverTexture = bannerTexture = dividerTexture = null;
        }
        // Textures of one roll kind. A failure releases only what this call made.
        KindArt Art(string rollKind)
        {
            string name = RollPalette.Normalize(rollKind);
            KindArt art;
            if (!kindArt.TryGetValue(name, out art) || art.Badge == null)
            {
                int first = textures.Count;
                try {
                    var accent = RollPalette.Accent(name);
                    art = new KindArt { Kind = name, Label = RollPalette.Label(name), Accent = accent, Text = RollPalette.Text(name) };
                    art.Badge = Rounded(32, 9, Mix(PanelTop, accent, .24f), Mix(PanelBottom, accent, .18f), Alpha(accent, .75f), 1.2f);
                    art.Slot = Rounded(48, 10, Mix(new Color(.11f, .13f, .14f, 1), accent, .08f), Mix(new Color(.08f, .095f, .10f, 1), accent, .05f), Alpha(accent, .95f), 2f);
                    art.Line = Line(128, Alpha(accent, .8f));
                    art.Stripe = Stripe(accent);
                } catch {
                    for (int i = textures.Count - 1; i >= first; i--) {
                        try { if (textures[i] != null) Destroy(textures[i]); } catch (Exception error) { Report("A roll window texture could not be released: ", error); }
                        textures.RemoveAt(i);
                    }
                    throw;
                }
                kindArt[name] = art;
            }
            if (art.StyleScale != styleScale || art.StyleGeneration != styleGeneration || !object.ReferenceEquals(art.Skin, skin))
            {
                var label = skin.label;
                art.BadgeBox = Plain(art.Badge, 8); art.SlotBox = Plain(art.Slot, 12);
                art.BadgeText = Text(label, bodyFont, 11, art.Text, TextAnchor.MiddleCenter, true, false);
                art.Title = Text(label, bodyFont, 13, art.Text, TextAnchor.MiddleLeft, true, true);
                art.BannerTitle = Text(label, bodyFont, 12, art.Text, TextAnchor.MiddleLeft, true, false);
                art.Total = Text(label, titleFont, 17, art.Text, TextAnchor.MiddleRight, true, false);
                art.Result = Text(label, titleFont, 20, art.Text, TextAnchor.MiddleRight, true, false);
                art.StyleScale = styleScale; art.StyleGeneration = styleGeneration; art.Skin = skin;
            }
            return art;
        }
        GUIStyle Text(GUIStyle source, Font font, int size, Color color, TextAnchor alignment, bool bold, bool wrap)
        {
            var result = new GUIStyle(source);
            if (font != null) result.font = font;
            result.fontSize = Math.Max(8, (int)Math.Round(size * scale));
            result.normal.textColor = color; result.normal.background = null;
            result.hover.textColor = color; result.hover.background = null;
            result.active.textColor = color; result.active.background = null;
            result.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            result.wordWrap = wrap; result.richText = false; result.clipping = TextClipping.Clip;
            result.alignment = alignment; result.padding = new RectOffset(0, 0, 0, 0); result.margin = new RectOffset(0, 0, 0, 0);
            result.border = new RectOffset(0, 0, 0, 0);
            return result;
        }
        GUIStyle Box(GUIStyle source, Font font, int size, Color color, Texture2D normal, Texture2D hover, Texture2D pressed, int border, bool bold)
        {
            var result = Text(source, font, size, color, TextAnchor.MiddleCenter, bold, false);
            result.normal.background = normal; result.hover.background = hover ?? normal; result.active.background = pressed ?? hover ?? normal;
            result.border = new RectOffset(border, border, border, border);
            result.padding = new RectOffset(Math.Max(2, (int)(8 * scale)), Math.Max(2, (int)(8 * scale)), 0, 0);
            return result;
        }
        void Styles()
        {
            Textures();
            int keyScale = (int)(scale * 1000); var currentSkin = GUI.skin;
            if (kind != null && keyScale == styleScale && skin == currentSkin && styleGeneration == textureGeneration) return;
            ResolveFonts();
            var label = currentSkin.label; var button = currentSkin.button;
            // Body copy is light on the dark panel; accents are kept for badges,
            // borders and large numbers (see RollPalette).
            var newKind = Text(label, bodyFont, 13, Gold, TextAnchor.MiddleLeft, true, true);
            var newActor = Text(label, titleFont, 23, Parchment, TextAnchor.MiddleLeft, true, true);
            var newDetail = Text(label, bodyFont, 16, Body, TextAnchor.MiddleLeft, false, true);
            var newNote = Text(label, bodyFont, 13, Muted, TextAnchor.UpperLeft, false, true);
            var newChip = Box(label, bodyFont, 13, Body, chipTexture, null, null, 8, true);
            var newChipGood = Box(label, bodyFont, 13, Good, chipGoodTexture, null, null, 8, true);
            var newChipBad = Box(label, bodyFont, 13, Bad, chipBadTexture, null, null, 8, true);
            var newChipGold = Box(label, bodyFont, 13, GoldBright, chipGoldTexture, null, null, 8, true);
            var newValue = Text(label, titleFont, 28, new Color(.98f, .92f, .78f, 1), TextAnchor.MiddleCenter, true, false);
            var newEmpty = Text(label, titleFont, 22, Dim, TextAnchor.MiddleCenter, false, false);
            var newDieLabel = Text(label, bodyFont, 11, Muted, TextAnchor.MiddleCenter, true, false);
            var newKey = Box(button, bodyFont, 15, new Color(.88f, .87f, .82f, 1), keyTexture, keyHoverTexture, keyActiveTexture, 7, true);
            newKey.active.textColor = Ink;
            var newKeySelected = Box(button, bodyFont, 15, GoldBright, keySelectedTexture, keySelectedTexture, keyActiveTexture, 7, true);
            newKeySelected.active.textColor = Ink;
            var newPrimary = Box(button, titleFont, 16, Ink, primaryTexture, primaryHoverTexture, primaryHoverTexture, 8, true);
            var newSecondary = Box(button, bodyFont, 14, new Color(.82f, .86f, .89f, 1), secondaryTexture, secondaryHoverTexture, secondaryHoverTexture, 8, false);
            var newHint = Text(label, bodyFont, 12, new Color(.50f, .55f, .59f, 1), TextAnchor.MiddleCenter, false, true);
            var newSum = Text(label, bodyFont, 12, Muted, TextAnchor.MiddleRight, false, false);
            var newBannerText = Text(label, bodyFont, 14, Parchment, TextAnchor.MiddleLeft, false, true);
            var newBannerLine = Text(label, bodyFont, 14, Parchment, TextAnchor.MiddleLeft, false, false);
            var newBannerDetail = Text(label, bodyFont, 12, Muted, TextAnchor.MiddleLeft, false, false);
            var newValueBad = Text(label, titleFont, 28, Bad, TextAnchor.MiddleCenter, true, false);
            var newPrimaryDisabled = Box(button, titleFont, 16, Dim, primaryDisabledTexture, primaryDisabledTexture, primaryDisabledTexture, 8, true);
            var newGrip = Text(button, null, 13, Muted, TextAnchor.MiddleCenter, true, false);
            newGrip.hover.textColor = GoldBright;
            var newGripOpen = Text(button, null, 13, GoldBright, TextAnchor.MiddleCenter, true, false);
            var newMenuKey = Box(button, bodyFont, 12, new Color(.86f, .87f, .84f, 1), keyTexture, keyHoverTexture, keyActiveTexture, 7, false);
            var newMenuKeyOn = Box(button, bodyFont, 12, GoldBright, keySelectedTexture, keyHoverTexture, keyActiveTexture, 7, true);
            newMenuKey.padding = newMenuKeyOn.padding = new RectOffset(Math.Max(2, (int)(5 * scale)), Math.Max(2, (int)(5 * scale)), 0, 0);
            var newHistoryHeading = Text(label, bodyFont, 12, Gold, TextAnchor.MiddleLeft, true, false);
            var newHistoryCount = Text(label, bodyFont, 12, Muted, TextAnchor.MiddleLeft, false, false);
            var newRowTitle = Text(label, bodyFont, 14, Parchment, TextAnchor.MiddleLeft, true, false);
            var newRowMeta = Text(label, bodyFont, 12, Muted, TextAnchor.MiddleLeft, false, false);
            var newRowSource = Text(label, bodyFont, 11, new Color(.56f, .61f, .65f, 1), TextAnchor.MiddleRight, false, false);
            var newHistoryEmpty = Text(label, bodyFont, 13, Muted, TextAnchor.MiddleCenter, false, true);
            var newPanel = Plain(panel, 14); var newShadow = Plain(shadow, 34); var newSlot = Plain(slotTexture, 12);
            var newBanner = Plain(bannerTexture, 14);
            kind = newKind; actor = newActor; detail = newDetail; note = newNote; chip = newChip; chipGood = newChipGood; chipBad = newChipBad; chipGold = newChipGold;
            value = newValue; valueBad = newValueBad; valueEmpty = newEmpty; dieLabel = newDieLabel; key = newKey; keySelected = newKeySelected;
            primary = newPrimary; primaryDisabled = newPrimaryDisabled; secondary = newSecondary; grip = newGrip; gripOpen = newGripOpen; menuKey = newMenuKey; menuKeyOn = newMenuKeyOn;
            hint = newHint; sum = newSum; bannerText = newBannerText; bannerLine = newBannerLine; bannerDetail = newBannerDetail;
            historyHeading = newHistoryHeading; historyCount = newHistoryCount; rowTitle = newRowTitle; rowMeta = newRowMeta; rowSource = newRowSource; historyEmpty = newHistoryEmpty;
            panelStyle = newPanel; shadowStyle = newShadow; slotStyle = newSlot; bannerStyle = newBanner;
            skin = currentSkin; styleScale = keyScale; styleGeneration = textureGeneration;
        }
        static GUIStyle Plain(Texture2D background, int border)
        {
            var result = new GUIStyle();
            result.normal.background = background; result.border = new RectOffset(border, border, border, border);
            result.padding = new RectOffset(0, 0, 0, 0); result.margin = new RectOffset(0, 0, 0, 0);
            return result;
        }
        void ResolveFonts()
        {
            titleFont = bodyFont = null;
            var provider = ThemeProvider;
            if (provider == null) return;
            try { var theme = provider(); if (theme != null) { titleFont = theme.Title; bodyFont = theme.Body ?? theme.Title; if (titleFont == null) titleFont = bodyFont; } }
            catch (Exception error) { Report("The game fonts could not be read; using the default font: ", error); ThemeProvider = null; }
        }
        Rect R(float x, float y, float width, float length) { return new Rect(x * scale, y * scale, width * scale, length * scale); }
        // Unscaled width of a single-line label.
        float TextWidth(GUIStyle style, string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            float measured = style.CalcSize(new GUIContent(text)).x / scale;
            if (!Finite(measured) || measured < 0) throw new InvalidOperationException("The native text width is unavailable.");
            return measured;
        }
        float BadgeWidth(KindArt art) { return Math.Max(44, TextWidth(art.BadgeText, art.Label) + 18); }
        // A kind badge: a tinted pill with its label (drawn as two controls so
        // the pill itself never reads as a text box).
        static void DrawBadge(Rect area, KindArt art)
        {
            GUI.Box(area, GUIContent.none, art.BadgeBox);
            GUI.Label(area, art.Label, art.BadgeText);
        }

        // ---- layout ---------------------------------------------------------
        string KindText { get { return prompt.Title.ToUpperInvariant(); } }
        string Explanation { get { return string.IsNullOrEmpty(prompt.Note) ? "Enter each raw die face. The game adds modifiers and applies features." : prompt.Note; } }
        bool HasChips { get { return prompt.AdvantagePair || prompt.TargetValue > 0 || prompt.HasBonus; } }
        // The title row leaves room for the kind badge and the options button.
        float TitleWidth(float width) { return Math.Max(40, width - 62 - badgeWidth); }
        float MeasureText(GUIStyle style, string text, float width, float minimum)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            float measured = style.CalcHeight(new GUIContent(text), width * scale);
            if (!Finite(measured) || measured < 0) throw new InvalidOperationException("The native text height is unavailable.");
            return Math.Max(minimum, measured / scale);
        }
        void MeasureRows(float width)
        {
            var art = Art(prompt.Kind);
            badgeWidth = BadgeWidth(art);
            kindHeight = Math.Max(16, MeasureText(art.Title, KindText, TitleWidth(width), 16));
            actorHeight = MeasureText(actor, prompt.Actor, width, 30);
            detailHeight = MeasureText(detail, prompt.Detail, width, 22);
            chipsHeight = HasChips ? LayoutChips(0, 0, width, false) : 0;
            noteHeight = MeasureText(note, Explanation, width, 18);
            textHeight = kindHeight + 6 + actorHeight + (detailHeight > 0 ? detailHeight + 2 : 0) + (chipsHeight > 0 ? chipsHeight + 8 : 0) + noteHeight + 6;
            if (!Finite(textHeight)) throw new InvalidOperationException("The native text layout is too large.");
        }
        int Keys
        {
            get
            {
                if (entries == null || active < 0 || active >= prompt.Count) return 0;
                int maximum = prompt.GetFaces(active);
                return maximum <= 20 ? maximum : 0;
            }
        }
        float KeypadHeight()
        {
            int keys = Keys; if (keys == 0) return 0;
            int perRow = keys <= 12 ? keys : 10;
            int lines = (keys + perRow - 1) / perRow;
            return lines * KeyHeight + (lines - 1) * 6 + 12;
        }
        void Layout(float availableHeight)
        {
            float inner = Width - 2 * Pad;
            columns = Math.Max(1, (int)((inner + SlotGap) / (Slot + SlotGap)));
            if (prompt.AdvantagePair) columns = 2;
            rows = (prompt.Count + columns - 1) / columns;
            keypadHeight = KeypadHeight();
            MeasureRows(inner);
            float slotsNeeded = rows * SlotHeight + (rows - 1) * SlotGap + (SumVisible ? SumBand : 0) + (prompt.AdvantagePair ? 18 : 0);
            // Header, at least one row of dice, keypad and footer stay visible;
            // long names/messages receive a bounded text viewport of their own.
            float maximum = Math.Max(64, Math.Min(240, availableHeight - 70 - SlotHeight - keypadHeight - FooterHeight));
            scrollText = textHeight > maximum;
            if (scrollText) MeasureRows(inner - 20);
            textViewportHeight = Math.Min(textHeight, maximum);
            diceTop = 22 + textViewportHeight + 12;
            diceHeight = Math.Max(SlotHeight, Math.Min(Math.Min(4 * SlotHeight + 3 * SlotGap + SumBand, slotsNeeded), availableHeight - diceTop - keypadHeight - FooterHeight));
            totalHeight = diceTop + diceHeight + keypadHeight + FooterHeight;
        }
        bool SumVisible { get { return prompt.Count > 1 && !prompt.AdvantagePair; } }

        void Update()
        {
            var hook = FrameUpdate;
            if (hook == null) return;
            try { hook(); }
            catch (Exception error) { Report("A pending manual roll could not advance: ", error); }
        }
        void OnGUI()
        {
            ReleaseDeferredDrag();
            try { RenderNotices(); }
            catch (Exception error) { Report("A roll notice could not be drawn: ", error); notices.Clear(); }
            try { RenderHistory(); }
            catch (Exception error) { Report("The roll history could not be drawn: ", error); HideHistoryAfterFault(); }
            if (!visible || prompt == null) {
                var waiting = waitingPrompt;
                if (waiting == null) return;
                try { RenderWaiting(waiting); }
                catch (Exception error) { Report("The multiplayer waiting message could not be drawn: ", error); CloseWaiting(waiting); }
                return;
            }
            var expected = prompt;
            try { Render(expected); }
            catch (Exception error) {
                Report("The roll window could not be drawn; using native dice: ", error);
                if (IsOwned(expected)) Complete(true);
            }
        }
        // One scale for every surface keeps the cached styles stable.
        static float CurrentScale()
        {
            float requested = Plugin.Settings == null ? 1 : Plugin.Settings.WindowScale;
            if (!Finite(requested) || requested <= 0) requested = 1;
            return Math.Max(.05f, Math.Min(Math.Max(.75f, Math.Min(1.5f, requested)), Math.Min(Math.Max(1, Screen.width - 24) / Width, Math.Max(1, Screen.height - 24) / 380f)));
        }
        void RenderNotices()
        {
            if (notices.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            notices.RemoveAll(n => now - n.Shown > n.Life || now < n.Shown - 1);
            if (notices.Count == 0) return;
            scale = CurrentScale(); Styles();
            float s = scale;
            float width = Math.Min(Screen.width - 24, 400 * s), length = 58 * s, x = Screen.width - width - 18 * s, y = 18 * s;
            if (visible || waitingPrompt != null) y = Math.Max(y, Screen.height - (length + 10 * s) * notices.Count - 18 * s);
            var previous = GUI.color;
            try {
                foreach (var item in notices)
                {
                    float age = now - item.Shown, alpha = Saturate(Math.Min(age * 6, (item.Life - age) * 1.5f));
                    GUI.color = new Color(1, 1, 1, alpha);
                    var art = Art(item.Kind);
                    GUI.Box(new Rect(x, y, width, length), GUIContent.none, bannerStyle);
                    GUI.DrawTexture(new Rect(x + 11 * s, y + 11 * s, 3 * s, length - 22 * s), art.Stripe);
                    GUI.Label(new Rect(x + 24 * s, y + 9 * s, width - 40 * s, 17 * s), item.Title, art.BannerTitle);
                    GUI.Label(new Rect(x + 24 * s, y + 28 * s, width - 40 * s, 24 * s), item.Text, bannerText);
                    y += length + 10 * s;
                }
            } finally { GUI.color = previous; }
        }
        void RenderWaiting(RollPrompt expected)
        {
            if (!Plugin.Enabled || !Plugin.IsMultiplayer) { CloseWaiting(expected); return; }
            scale = CurrentScale(); Styles();
            float s = scale;
            float width = Math.Min(Screen.width - 24, 470 * s), length = 70 * s;
            float x = Math.Max(0, (Screen.width - width) / 2), y = Math.Min(64 * s, Math.Max(0, Screen.height - length));
            var art = Art(expected.Kind);
            var previousColor = GUI.color;
            try {
                // Decorative labels and textures do not claim a click, key,
                // pointer gesture, or game cursor from the observing peer.
                GUI.color = Color.white;
                GUI.Box(new Rect(x, y, width, length), GUIContent.none, bannerStyle);
                GUI.DrawTexture(new Rect(x + 12 * s, y + 12 * s, 3 * s, length - 24 * s), art.Stripe);
                float badge = BadgeWidth(art) * s, left = x + 26 * s, right = x + width - 16 * s;
                DrawBadge(new Rect(right - badge, y + 12 * s, badge, 20 * s), art);
                // What is rolled: the request title, then its dice and target.
                string title = expected.Title.ToUpperInvariant(), dice = WaitingDetail(expected);
                float room = Math.Max(0, right - badge - 10 * s - left);
                float titleWidth = Math.Min(room, TextWidth(art.BannerTitle, title) * s + 2);
                GUI.Label(new Rect(left, y + 12 * s, titleWidth, 20 * s), title, art.BannerTitle);
                if (dice.Length > 0 && room - titleWidth > 24 * s)
                    GUI.Label(new Rect(left + titleWidth + 8 * s, y + 12 * s, room - titleWidth - 8 * s, 20 * s), "·  " + dice, bannerDetail);
                // Who is rolling, with an animated ellipsis.
                string message = waitingMessage ?? "Waiting for dice";
                float messageRoom = Math.Max(0, right - left - 24 * s);
                float messageWidth = Math.Min(messageRoom, TextWidth(bannerLine, message) * s + 2);
                GUI.Label(new Rect(left, y + 37 * s, messageWidth, 22 * s), message, bannerLine);
                int phase = (int)(Math.Max(0, Time.realtimeSinceStartup) * 2.5f) % Ellipsis.Length;
                if (phase > 0) GUI.Label(new Rect(left + messageWidth, y + 37 * s, 24 * s, 22 * s), Ellipsis[phase], bannerLine);
            } finally { GUI.color = previousColor; }
        }

        // ---- roll history panel ---------------------------------------------
        static bool HistoryShown { get { var settings = Plugin.Settings; return settings != null && settings.ShowHistory && Plugin.Enabled; } }
        internal static Rect HistoryArea { get { return instance == null ? new Rect() : instance.historyRect; } }
        Rect historyRect;
        void HideHistoryAfterFault()
        {
            panelHover = false; ReleaseHistoryDrag();
            try { if (Plugin.Settings != null) Plugin.Settings.ShowHistory = false; } catch (Exception) { }
        }
        void RenderHistory()
        {
            if (!HistoryShown) { panelHover = false; historyRect = new Rect(); if (historyDragging) ReleaseHistoryDrag(); return; }
            var e = Event.current;
            var settings = Plugin.Settings;
            scale = CurrentScale(); Styles();
            float s = scale;
            int count = RollHistory.Count;
            float width = Math.Min(Math.Max(1, Screen.width - 24), HistoryWidth * s);
            float body = count == 0 ? 52 : Math.Min(count, HistoryRows) * HistoryRow;
            float length = Math.Min((HistoryHeader + body + 12) * s, Math.Max(1, Screen.height - 24));
            float x = Finite(settings.HistoryX) && settings.HistoryX >= 0 ? settings.HistoryX : 18 * s;
            float y = Finite(settings.HistoryY) && settings.HistoryY >= 0 ? settings.HistoryY : 96 * s;
            x = Mathf.Clamp(x, 0, Math.Max(0, Screen.width - width)); y = Mathf.Clamp(y, 0, Math.Max(0, Screen.height - length));
            historyRect = new Rect(x, y, width, length);
            if (e != null && e.type == EventType.Repaint) { panelHover = historyRect.Contains(e.mousePosition); panelHoverFrame = Time.frameCount; }
            // The modal window stays on top: a fresh gesture over it never
            // reaches the panel beneath (a gesture already in progress does).
            if (e != null && visible && e.type != EventType.Repaint && e.type != EventType.Layout && position.Contains(e.mousePosition) && GUIUtility.hotControl == 0) return;
            // Header drag (outside its buttons) moves the panel.
            historyControl = GUIUtility.GetControlID(HistoryDragHint, FocusType.Passive);
            float buttonWidth = 58 * s, buttonTop = y + 11 * s;
            var close = new Rect(x + width - 14 * s - buttonWidth, buttonTop, buttonWidth, 24 * s);
            var clear = new Rect(close.x - 6 * s - buttonWidth, buttonTop, buttonWidth, 24 * s);
            var header = new Rect(x, y, width, HistoryHeader * s);
            if (e != null)
            {
                bool onButtons = clear.Contains(e.mousePosition) || close.Contains(e.mousePosition);
                if (e.type == EventType.MouseDown && e.button == 0 && header.Contains(e.mousePosition) && !onButtons && GUIUtility.hotControl == 0)
                { GUIUtility.hotControl = historyControl; historyDragging = true; e.Use(); }
                else if (historyDragging && e.type == EventType.MouseDrag && GUIUtility.hotControl == historyControl)
                { settings.HistoryX = Mathf.Clamp(x + e.delta.x, 0, Math.Max(0, Screen.width - width)); settings.HistoryY = Mathf.Clamp(y + e.delta.y, 0, Math.Max(0, Screen.height - length)); e.Use(); }
                else if (historyDragging && e.rawType == EventType.MouseUp)
                { if (GUIUtility.hotControl == historyControl) GUIUtility.hotControl = 0; historyDragging = false; SaveSettings(); e.Use(); }
            }
            var previousColor = GUI.color;
            try {
                GUI.color = Color.white;
                GUI.Box(historyRect, GUIContent.none, panelStyle);
                float heading = Math.Min(TextWidth(historyHeading, HistoryTitle) * s + 2, Math.Max(0, clear.x - x - 24 * s));
                GUI.Label(new Rect(x + 18 * s, y + 13 * s, heading, 20 * s), HistoryTitle, historyHeading);
                if (count > 0) GUI.Label(new Rect(x + 18 * s + heading + 8 * s, y + 13 * s, Math.Max(0, clear.x - x - heading - 32 * s), 20 * s), count == 1 ? "1 roll" : count + " rolls", historyCount);
                if (count > 0 && GUI.Button(clear, HistoryClearLabel, menuKey)) { RollHistory.Clear(); historyScroll = Vector2.zero; return; }
                if (GUI.Button(close, HistoryCloseLabel, menuKey)) { settings.ShowHistory = false; panelHover = false; ReleaseHistoryDrag(); SaveSettings(); return; }
                GUI.DrawTexture(new Rect(x + 14 * s, y + (HistoryHeader - 4) * s, width - 28 * s, Math.Max(1, s)), line);
                var viewport = new Rect(x + 8 * s, y + HistoryHeader * s, width - 16 * s, Math.Max(0, length - (HistoryHeader + 8) * s));
                if (count == 0) { GUI.Label(viewport, HistoryEmpty, historyEmpty); return; }
                DrawHistoryRows(viewport, count);
            } finally { GUI.color = previousColor; }
        }
        void DrawHistoryRows(Rect viewport, int count)
        {
            float s = scale, row = HistoryRow * s, content = count * row;
            bool overflow = content > viewport.height + .5f;
            float width = viewport.width - (overflow ? 16 * s : 0);
            historyScroll = GUI.BeginScrollView(viewport, historyScroll, new Rect(0, 0, width, content));
            try {
                var list = RollHistory.Entries;
                int first = Math.Max(0, (int)(historyScroll.y / Math.Max(1, row)) - 1);
                int last = Math.Min(count - 1, (int)((historyScroll.y + viewport.height) / Math.Max(1, row)) + 1);
                for (int i = first; i <= last && i < list.Count; i++)
                {
                    var record = list[i]; float top = i * row;
                    var art = Art(record.Kind);
                    GUI.DrawTexture(new Rect(6 * s, top + 9 * s, 3 * s, row - 18 * s), art.Stripe);
                    GUI.Label(new Rect(18 * s, top + 6 * s, Math.Max(0, width - 104 * s), 20 * s), record.Title, rowTitle);
                    GUI.Label(new Rect(width - 84 * s, top + 4 * s, 76 * s, 24 * s), record.Result, art.Result);
                    string source = record.SourceText + " · " + record.When.ToString("HH:mm");
                    GUI.Label(new Rect(18 * s, top + 27 * s, Math.Max(0, width - 130 * s), 17 * s), record.Summary, rowMeta);
                    GUI.Label(new Rect(width - 118 * s, top + 27 * s, 110 * s, 17 * s), source, rowSource);
                    if (i < count - 1) GUI.DrawTexture(new Rect(12 * s, top + row - 1, Math.Max(0, width - 24 * s), 1), dividerTexture);
                }
            } finally { GUI.EndScrollView(); }
        }

        void Render(RollPrompt expected)
        {
            if (!Plugin.Enabled || Plugin.IsMultiplayer && !networkAuthorized) { Complete(true); return; }
            scale = CurrentScale();
            Styles();
            float availableHeight = Math.Max(1, Screen.height - 24) / scale;
            Layout(availableHeight);
            height = totalHeight;
            position.width = Width * scale; position.height = height * scale;
            position.x = Mathf.Clamp(position.x, 0, Math.Max(0, Screen.width - position.width)); position.y = Mathf.Clamp(position.y, 0, Math.Max(0, Screen.height - position.height));
            if (HandleKeys(Event.current)) { if (!IsOwned(expected)) return; }
            var priorColor = GUI.color; bool enabled = GUI.enabled;
            try {
                GUI.color = Color.white; GUI.enabled = true;
                if (Event.current.type == EventType.Repaint)
                {
                    float spread = 26 * scale;
                    GUI.Box(new Rect(position.x - spread, position.y - spread * .6f, position.width + 2 * spread, position.height + 2 * spread), GUIContent.none, shadowStyle);
                }
                var drawnPosition = GUI.Window(WindowId, position, DrawWindow, "", GUIStyle.none);
                if (!IsOwned(expected)) return;
                position = drawnPosition;
                position.x = Mathf.Clamp(position.x + dragDelta.x, 0, Math.Max(0, Screen.width - position.width)); position.y = Mathf.Clamp(position.y + dragDelta.y, 0, Math.Max(0, Screen.height - position.height)); dragDelta = Vector2.zero;
                if (recenter)
                {
                    recenter = false;
                    position.x = Math.Max(0, (Screen.width - position.width) / 2); position.y = Math.Max(0, (Screen.height - position.height) / 2);
                    SavePosition();
                }
            } finally { GUI.color = priorColor; GUI.enabled = enabled; }
        }
        // Returns true when the event was consumed.
        bool HandleKeys(Event e)
        {
            if (e == null || e.type != EventType.KeyDown) return false;
            // Unity reports a key press as a key-code event and, for printable
            // keys, a separate character event. Commands act on the key code;
            // faces are typed from the character; the paired twin is consumed.
            var code = e.keyCode; char character = e.character;
            if (code == KeyCode.Return || code == KeyCode.KeypadEnter) { e.Use(); Complete(false); return true; }
            if (code == KeyCode.Escape) { e.Use(); Complete(true); return true; }
            if (code == KeyCode.Backspace || code == KeyCode.Delete) { e.Use(); Backspace(); return true; }
            if (code == KeyCode.Tab) { e.Use(); if (e.shift) Select(active - 1 < 0 ? entries.Length - 1 : active - 1); else Select((active + 1) % entries.Length); return true; }
            if (code == KeyCode.RightArrow || code == KeyCode.Space) { e.Use(); Select((active + 1) % entries.Length); return true; }
            if (code == KeyCode.LeftArrow) { e.Use(); Select(active - 1 < 0 ? entries.Length - 1 : active - 1); return true; }
            if (code == KeyCode.UpArrow) { e.Use(); entries[active] = StepFace(entries[active], prompt.GetFaces(active), 1).ToString(); fresh = true; return true; }
            if (code == KeyCode.DownArrow) { e.Use(); entries[active] = StepFace(entries[active], prompt.GetFaces(active), -1).ToString(); fresh = true; return true; }
            if (character >= '0' && character <= '9') { e.Use(); TypeDigit(character - '0'); return true; }
            if (character != '\0' || code >= KeyCode.Alpha0 && code <= KeyCode.Alpha9 || code >= KeyCode.Keypad0 && code <= KeyCode.Keypad9) { e.Use(); return true; }
            return false;
        }
        void DrawWindow(int id)
        {
            var e = Event.current;
            dragControl = GUIUtility.GetControlID(DragHint, FocusType.Passive);
            var art = Art(prompt.Kind);
            GUI.Box(R(0, 0, Width, height), GUIContent.none, panelStyle);
            // The kind's accent caps the panel and underlines the request text.
            GUI.DrawTexture(R(18, 2, Width - 36, 2), art.Line);
            var grab = R(0, 0, Width, 52);
            // The options button sits in the header; clicks on it never start a drag.
            var menuArea = R(Width - Pad - 46, 14, 46, 26);
            if (e.type == EventType.MouseDown && e.button == 0 && grab.Contains(e.mousePosition) && !menuArea.Contains(e.mousePosition) && GUIUtility.hotControl == 0) { GUIUtility.hotControl = dragControl; dragging = true; e.Use(); }
            else if (dragging && GUIUtility.hotControl == dragControl && e.type == EventType.MouseDrag) { dragDelta += e.delta; e.Use(); }
            else if (dragging && e.rawType == EventType.MouseUp) { if (GUIUtility.hotControl == dragControl) GUIUtility.hotControl = 0; dragging = false; SavePosition(); e.Use(); }
            float inner = Width - 2 * Pad;
            if (scrollText) {
                textScroll = GUI.BeginScrollView(R(Pad, 22, inner, textViewportHeight), textScroll, R(0, 0, inner - 20, textHeight));
                try { DrawText(0, 0, inner - 20, art); }
                finally { GUI.EndScrollView(); }
            } else DrawText(Pad, 22, inner, art);
            DrawBadge(R(Width - Pad - 54 - badgeWidth, 17, badgeWidth, 22), art);
            GUI.DrawTexture(R(Pad, diceTop - 7, inner, 1), art.Line);
            DrawDice(inner, art);
            DrawKeypad(inner);
            DrawFooter(inner);
            if (!IsOwned(prompt)) return;
            if (GUI.Button(menuArea, MenuLabel, menuOpen ? gripOpen : grip)) menuOpen = !menuOpen;
        }
        // What is rolled (kind-coloured title), who rolls it, then why.
        void DrawText(float x, float y, float width, KindArt art)
        {
            // The header is the drag handle; its badge and options button are drawn by DrawWindow.
            GUI.Label(R(x, y, TitleWidth(width), kindHeight), KindText, art.Title);
            y += kindHeight + 6;
            GUI.Label(R(x, y, width, actorHeight), prompt.Actor, actor); y += actorHeight;
            if (detailHeight > 0) { y += 2; GUI.Label(R(x, y, width, detailHeight), prompt.Detail, detail); y += detailHeight; }
            if (chipsHeight > 0) { y += 8; LayoutChips(x, y, width, true); y += chipsHeight; }
            y += 6;
            GUI.Label(R(x, y, width, noteHeight), Explanation, note);
        }
        // Context chips, wrapped onto further rows when they do not fit.
        List<KeyValuePair<string, GUIStyle>> Chips()
        {
            var chips = new List<KeyValuePair<string, GUIStyle>>();
            if (prompt.AdvantagePair) chips.Add(new KeyValuePair<string, GUIStyle>(prompt.Advantage > 0 ? "ADVANTAGE" : "DISADVANTAGE", prompt.Advantage > 0 ? chipGood : chipBad));
            if (prompt.TargetValue > 0) chips.Add(new KeyValuePair<string, GUIStyle>((string.IsNullOrEmpty(prompt.TargetLabel) ? "DC" : prompt.TargetLabel) + " " + prompt.TargetValue, chipGold));
            if (prompt.HasBonus) chips.Add(new KeyValuePair<string, GUIStyle>("Bonus " + (prompt.Bonus >= 0 ? "+" : "-") + Math.Abs(prompt.Bonus), chip));
            if (prompt.HasBonus && prompt.TargetValue > 0 && prompt.Count <= 2 && prompt.GetFaces(0) == 20)
            {
                int need = prompt.TargetValue - prompt.Bonus;
                chips.Add(new KeyValuePair<string, GUIStyle>(need <= 1 ? "Any roll succeeds" : need > 20 ? "Need a 20" : "Need " + need + "+", chip));
            }
            return chips;
        }
        float ChipWidth(KeyValuePair<string, GUIStyle> item)
        {
            float measured = item.Value.CalcSize(new GUIContent(item.Key)).x / scale;
            if (!Finite(measured) || measured < 0) throw new InvalidOperationException("The native chip width is unavailable.");
            return Math.Max(44, measured + 24);
        }
        float LayoutChips(float x, float y, float width, bool draw)
        {
            float cursor = x, top = y;
            foreach (var item in Chips())
            {
                float measured = Math.Min(width, ChipWidth(item));
                if (cursor > x && cursor + measured > x + width) { cursor = x; top += 32; }
                if (draw) GUI.Box(R(cursor, top, measured, 26), new GUIContent(item.Key), item.Value);
                cursor += measured + 8;
            }
            return top - y + 26;
        }
        void DrawDice(float inner, KindArt art)
        {
            var e = Event.current;
            float contentHeight = rows * SlotHeight + (rows - 1) * SlotGap + (prompt.AdvantagePair ? 18 : 0);
            float rowWidth = Math.Min(prompt.Count, columns) * (Slot + SlotGap) - SlotGap;
            float offset = Math.Max(0, (inner - rowWidth) / 2);
            float viewport = diceHeight - (SumVisible ? SumBand : 0);
            scroll = GUI.BeginScrollView(R(Pad, diceTop, inner, viewport), scroll, R(0, 0, inner - 20, contentHeight));
            try {
                for (int i = 0; i < prompt.Count; i++)
                {
                    int row = i / columns, column = i % columns, maximum = prompt.GetFaces(i);
                    float left = offset + column * (Slot + SlotGap), top = row * (SlotHeight + SlotGap);
                    var area = R(left, top, Slot, SlotHeight);
                    bool selected = i == active;
                    if (GUI.Button(area, GUIContent.none, GUIStyle.none)) Select(i);
                    GUI.Box(area, GUIContent.none, selected ? art.SlotBox : slotStyle);
                    var shape = DieShape(maximum);
                    var tint = GUI.color;
                    GUI.color = selected ? Alpha(art.Accent, .55f) : new Color(.62f, .69f, .74f, .30f);
                    GUI.DrawTexture(R(left + 7, top + 4, Slot - 14, SlotHeight - 22), shape, ScaleMode.ScaleToFit, true);
                    GUI.color = tint;
                    string text = entries[i];
                    int parsed; bool valid = int.TryParse(text, out parsed) && parsed >= 1 && parsed <= maximum;
                    var face = R(left, top + 4, Slot, SlotHeight - 22);
                    if (string.IsNullOrEmpty(text)) GUI.Label(face, "–", valueEmpty);
                    else GUI.Label(face, text, valid ? value : valueBad);
                    GUI.Label(R(left, top + SlotHeight - 18, Slot, 14), "d" + maximum, dieLabel);
                    if (e.type == EventType.ScrollWheel && area.Contains(e.mousePosition))
                    { Select(i); entries[i] = StepFace(entries[i], maximum, e.delta.y > 0 ? -1 : 1).ToString(); e.Use(); }
                }
                if (prompt.AdvantagePair)
                {
                    float labelTop = SlotHeight + 2;
                    GUI.Label(R(offset, labelTop, rowWidth, 16), prompt.Advantage > 0 ? "Roll both d20s — the game keeps the higher" : "Roll both d20s — the game keeps the lower", hint);
                }
            } finally { GUI.EndScrollView(); }
            if (SumVisible)
            {
                // Running total of the faces entered so far, in the kind's colour.
                int total = 0, filled = 0;
                for (int i = 0; i < prompt.Count; i++) { int parsed; if (int.TryParse(entries[i], out parsed) && parsed >= 1 && parsed <= prompt.GetFaces(i)) { total += parsed; filled++; } }
                string totalText = "Total " + total;
                float top = diceTop + diceHeight - SumBand + 2, totalWidth = Math.Min(inner * .5f, TextWidth(art.Total, totalText) + 4);
                GUI.Label(R(Pad + inner - totalWidth, top, totalWidth, SumBand - 4), totalText, art.Total);
                GUI.Label(R(Pad, top, Math.Max(0, inner - totalWidth - 14), SumBand - 4), filled + " of " + prompt.Count + " dice", sum);
            }
        }
        void DrawKeypad(float inner)
        {
            int keys = Keys; if (keys == 0) return;
            int perRow = keys <= 12 ? keys : 10;
            float top = diceTop + diceHeight + 8;
            float width = (inner - (perRow - 1) * 6) / perRow;
            int current; int.TryParse(entries[active], out current);
            for (int face = 1; face <= keys; face++)
            {
                int index = face - 1, row = index / perRow, column = index % perRow;
                var area = R(Pad + column * (width + 6), top + row * (KeyHeight + 6), width, KeyHeight);
                if (GUI.Button(area, face.ToString(), face == current ? keySelected : key)) SetFace(face);
            }
        }
        void DrawFooter(float inner)
        {
            float top = diceTop + diceHeight + keypadHeight + 12;
            int[] values; bool valid = TryFaces(prompt, entries, out values);
            bool prior = GUI.enabled;
            GUI.enabled = prior && valid;
            if (GUI.Button(R(Pad, top, inner * .60f, 40), ConfirmLabel, valid ? primary : primaryDisabled)) { GUI.enabled = prior; Complete(false); return; }
            GUI.enabled = prior;
            if (GUI.Button(R(Pad + inner * .60f + 10, top, inner * .40f - 10, 40), AutomaticLabel, secondary)) { Complete(true); return; }
            if (!menuOpen) { GUI.Label(R(Pad, top + 48, inner, 30), "Type or click each face · Tab or arrow keys move · Enter confirms · Esc lets the game roll", hint); return; }
            // Window options (the header's • • • button), sized to their labels.
            var settings = Plugin.Settings;
            bool notices = settings == null || settings.AutoRollNotices, history = settings != null && settings.ShowHistory;
            var labels = new[] { SmallerLabel, LargerLabel, CenterLabel, notices ? NoticesOnLabel : NoticesOffLabel, history ? HistoryOnLabel : HistoryOffLabel };
            var widths = new float[labels.Length]; float used = 0;
            for (int i = 0; i < labels.Length; i++) { widths[i] = TextWidth(menuKey, labels[i]) + 12; used += widths[i]; }
            float room = inner - (labels.Length - 1) * 6, extra = (room - used) / labels.Length, cursor = Pad;
            for (int i = 0; i < labels.Length; i++)
            {
                float width = extra >= 0 ? widths[i] + extra : room / labels.Length;
                var area = R(cursor, top + 50, width, 26); cursor += width + 6;
                bool on = i == 3 && notices || i == 4 && history;
                if (!GUI.Button(area, labels[i], i >= 3 && on ? menuKeyOn : menuKey)) continue;
                switch (i)
                {
                    case 0: Resize(-.1f); break;
                    case 1: Resize(.1f); break;
                    case 2: recenter = true; break;
                    case 3: if (settings != null) { settings.AutoRollNotices = !notices; SaveSettings(); } break;
                    case 4: if (settings != null) { settings.ShowHistory = !history; SaveSettings(); } break;
                }
            }
        }
        static void Resize(float delta)
        {
            var settings = Plugin.Settings; if (settings == null) return;
            float current = Finite(settings.WindowScale) && settings.WindowScale > 0 ? settings.WindowScale : 1;
            settings.WindowScale = Mathf.Clamp((float)Math.Round((current + delta) * 20) / 20f, .75f, 1.5f);
            Plugin.SaveSettings();
        }
        void OnApplicationFocus(bool focused)
        {
            if (focused) return;
            ReleaseDrag(); ReleaseHistoryDrag(); capture.Reset(); panelCapture.Reset(); panelHover = false;
        }
        void OnDestroy()
        {
            // Explicit teardown closes before destroying. An unexpected engine
            // destruction must also release the outstanding broker request.
            if (object.ReferenceEquals(instance, this)) {
                var native = visible ? useNative : null;
                if (visible) SavePosition();
                Close(); waitingPrompt = null; waitingMessage = null; instance = null; capture.Reset(); panelCapture.Reset(); panelHover = false;
                if (native != null) InvokeCompletion(null, native, true);
            }
            notices.Clear();
            ReleaseTextures();
        }
    }
}
