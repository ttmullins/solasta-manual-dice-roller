using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

namespace HarmonyLib
{
    public sealed class HarmonyMethod { public MethodInfo Method; public HarmonyMethod(MethodInfo method) { Method = method; } }
    public sealed class Harmony
    {
        public int Patches, Prefixes, Postfixes;
        public void Patch(MethodBase target, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        { Patches++; if (prefix != null) Prefixes++; if (postfix != null) Postfixes++; }
    }
}
namespace UnityEngine
{
    public struct Vector3 { }
    public sealed class GameObject
    {
        public bool Active = true;
        public Action<PointerEventData> PointerUp, EndDrag;
        public void SetActive(bool value) { Active = value; }
    }
    public sealed class RectTransform { public GameObject gameObject = new GameObject(); }
}
namespace UnityEngine.EventSystems
{
    public struct RaycastResult { public float distance; }
    public sealed class PointerEventData
    {
        public bool eligibleForClick, dragging;
        public GameObject pointerPress, rawPointerPress, pointerDrag;
        public RaycastResult pointerPressRaycast, pointerCurrentRaycast;
    }
    public class PointerInputModule
    {
        protected Dictionary<int, PointerEventData> m_PointerData = new Dictionary<int, PointerEventData>();
        public Dictionary<int, PointerEventData> Data { get { return m_PointerData; } set { m_PointerData = value; } }
        public int Clears;
        public bool FailSelection;
        public Action SelectionHook;
        protected void ClearSelection()
        {
            Clears++; if (FailSelection) throw new InvalidOperationException("injected clear selection");
            if (SelectionHook != null) SelectionHook();
        }
    }
    public static class ExecuteEvents
    {
        public const string pointerUpHandler = "pointer up", endDragHandler = "end drag";
        public static int Releases, Drags;
        public static void Execute(GameObject target, PointerEventData pointer, string handler)
        {
            if (handler == pointerUpHandler) { Releases++; if (target.PointerUp != null) target.PointerUp(pointer); }
            else { Drags++; if (target.EndDrag != null) target.EndDrag(pointer); }
        }
    }
}
public sealed class CameraController { public int Resets; public void ResetInputs() { Resets++; } }
public sealed class CursorService { public object CurrentCursor; }
public class CursorLocation
{
    bool mainDragStarted = true;
    public bool MainDrag { get { return mainDragStarted; } }
}
public sealed class CursorLocationExploration : CursorLocation
{
    bool isDragging = true;
    RectTransform selectionRectangle = new RectTransform();
    public bool Dragging { get { return isDragging; } }
    public RectTransform Rectangle { get { return selectionRectangle; } }
}
public sealed class View
{
    bool primaryPointerArmed = true, primaryPointerDragged = true, secondaryPointerArmed = true, secondaryPointerDragged = true, tertiaryPointerArmed = true, tertiaryPointerDragged = true;
    public CameraController CameraController = new CameraController();
    public CursorService CursorService = new CursorService();
    public bool HasGesture { get { return primaryPointerArmed || primaryPointerDragged || secondaryPointerArmed || secondaryPointerDragged || tertiaryPointerArmed || tertiaryPointerDragged; } }
    void HandleInputs() { }
}
public sealed class DebugOverlayManager { bool IsMouseCovered(Vector3 position) { return false; } }
public sealed class GuiManager
{
    void OnCommandJustActivated(Queue<int> commands) { }
    public bool GrabbedCameraControls { get { return false; } }
}
public sealed class InputPointerModule : PointerInputModule
{
    List<RaycastResult> raycastResults;
    public List<RaycastResult> Cache { get { return raycastResults; } set { raycastResults = value; } }
    public void Process() { }
    List<RaycastResult> RaycastPointer() { return raycastResults; }
}
public sealed class GameLocationManager { void EnterLocation() { } void ExitLocation() { } }
public sealed class GameManager { void DestroyGame() { } }
namespace ManualDiceRoller
{
    public static class Plugin
    {
        public static bool FailWarn;
        public static readonly List<string> Warnings = new List<string>();
        public static void Warn(string message) { Warnings.Add(message); if (FailWarn) throw new InvalidOperationException("injected warning"); }
    }
    public static class RollWindow
    {
        public static bool Blocked = true;
        public static int CaptureResets;
        public static bool BlocksInput() { return Blocked; }
        public static void ResetCapture() { CaptureResets++; }
    }
    public static class ModFeatures
    {
        public static int Drops;
        public static void Drop() { Drops++; }
    }
    public static class MultiplayerRolls
    {
        public static int Rearms;
        public static void Rearm() { Rearms++; }
    }
    public static class RuntimeRolls
    {
        public static bool FailReset;
        public static int Resets;
        public static void Reset() { Resets++; if (FailReset) throw new InvalidOperationException("injected runtime reset"); }
    }
}
