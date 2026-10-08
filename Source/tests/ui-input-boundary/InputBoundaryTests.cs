using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ManualDiceRoller;
using UnityEngine;
using UnityEngine.EventSystems;

static class InputBoundaryTests
{
    static int assertions, cases, failures;
    static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static object Invoke(string name, params object[] values)
    {
        try { return typeof(InputGuards).GetMethod(name, PrivateStatic).Invoke(null, values); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    static PointerEventData Armed()
    {
        return new PointerEventData { eligibleForClick = true, dragging = true, pointerPress = new GameObject(), rawPointerPress = new GameObject(), pointerDrag = new GameObject(), pointerPressRaycast = new RaycastResult { distance = 9 }, pointerCurrentRaycast = new RaycastResult { distance = 7 } };
    }
    static void Revoked(PointerEventData pointer)
    {
        Check(!pointer.eligibleForClick && !pointer.dragging && pointer.pointerPress == null && pointer.rawPointerPress == null && pointer.pointerDrag == null && pointer.pointerPressRaycast.distance == 0 && pointer.pointerCurrentRaycast.distance == 0, "Every captured pointer field is revoked");
    }
    static void Reset()
    {
        Plugin.FailWarn = false; Plugin.Warnings.Clear(); RollWindow.Blocked = true; RollWindow.CaptureResets = 0;
        RuntimeRolls.FailReset = false; RuntimeRolls.Resets = 0; ExecuteEvents.Releases = ExecuteEvents.Drags = 0;
        string error; if (!InputGuards.Preflight(out error)) throw new Exception("Fixture metadata preflight: " + error);
    }
    static void Case(string name, Action body)
    {
        Reset(); cases++;
        try { body(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + ": " + error.Message); }
    }
    static void Main()
    {
        Case("Fixture binding preflight and install", delegate {
            var harmony = new Harmony(); InputGuards.Install(harmony);
            Check(harmony.Patches == 9 && harmony.Prefixes == 8 && harmony.Postfixes == 1, "Actual installer binds intended fake metadata methods");
        });
        Case("Normal pointer cancellation dispatches both releases", delegate {
            var pointer = Armed(); Invoke("CancelPointerState", pointer); Revoked(pointer);
            Check(ExecuteEvents.Releases == 1 && ExecuteEvents.Drags == 1, "Captured press and drag release once");
        });
        Case("Pointer-up callback throw does not prevent end-drag cleanup", delegate {
            var pointer = Armed(); pointer.pointerPress.PointerUp = delegate { throw new InvalidOperationException("injected pointer up"); };
            Invoke("CancelPointerState", pointer); Revoked(pointer);
            Check(ExecuteEvents.Releases == 1 && ExecuteEvents.Drags == 1 && Plugin.Warnings.Count == 1, "End drag continues after pointer up fault");
        });
        Case("End-drag callback throw still revokes gesture", delegate {
            var pointer = Armed(); pointer.pointerDrag.EndDrag = delegate { throw new InvalidOperationException("injected end drag"); };
            Invoke("CancelPointerState", pointer); Revoked(pointer); Check(Plugin.Warnings.Count == 1, "End drag fault is reported");
        });
        Case("Both handlers and logger can fail safely", delegate {
            var pointer = Armed(); pointer.pointerPress.PointerUp = delegate { throw new Exception("up"); }; pointer.pointerDrag.EndDrag = delegate { throw new Exception("drag"); }; Plugin.FailWarn = true;
            Invoke("CancelPointerState", pointer); Revoked(pointer); Check(Plugin.Warnings.Count == 2, "Both faults are independently reported despite logger failure");
        });
        Case("Pointer-up callback reenters cancellation without recursion", delegate {
            var pointer = Armed(); pointer.pointerPress.PointerUp = delegate(PointerEventData p) { Invoke("CancelPointerState", p); p.eligibleForClick = true; p.rawPointerPress = new GameObject(); };
            Invoke("CancelPointerState", pointer); Revoked(pointer);
            Check(ExecuteEvents.Releases == 1 && ExecuteEvents.Drags == 1, "Reentered cancellation does not repeat handlers");
        });
        Case("End-drag callback reenters and mutates every pointer field", delegate {
            var pointer = Armed(); pointer.pointerDrag.EndDrag = delegate(PointerEventData p) {
                Invoke("CancelPointerState", p); p.eligibleForClick = p.dragging = true; p.pointerPress = p.rawPointerPress = p.pointerDrag = new GameObject();
                p.pointerPressRaycast = new RaycastResult { distance = 13 }; p.pointerCurrentRaycast = new RaycastResult { distance = 17 };
            };
            Invoke("CancelPointerState", pointer); Revoked(pointer); Check(ExecuteEvents.Releases == 1 && ExecuteEvents.Drags == 1, "Callback assignments cannot preserve a captured gesture");
        });
        Case("Reentrancy owner is retired after callback fault", delegate {
            var pointer = Armed(); pointer.pointerPress.PointerUp = delegate { throw new Exception("up"); }; Invoke("CancelPointerState", pointer);
            pointer.eligibleForClick = pointer.dragging = true; pointer.pointerPress = pointer.pointerDrag = new GameObject(); Invoke("CancelPointerState", pointer); Revoked(pointer);
            Check(ExecuteEvents.Releases == 2 && ExecuteEvents.Drags == 2, "Same pointer can be canceled again after faulty callback");
        });
        Case("Null pointer and null pointer dictionary are tolerated", delegate {
            Invoke("CancelPointerState", new object[] { null });
            var module = new InputPointerModule { Data = null }; Check(!(bool)Invoke("PointerPrefix", module) && module.Clears == 1, "Missing pointer dictionary still clears selection and suppresses Process");
        });
        Case("Pointer dictionary mutation cannot break snapshot cancellation", delegate {
            var module = new InputPointerModule(); var first = Armed(); var second = Armed();
            module.Data[1] = first; module.Data[2] = second; module.Data[3] = null;
            first.pointerPress.PointerUp = delegate { module.Data.Clear(); module.Data[4] = Armed(); };
            Check(!(bool)Invoke("PointerPrefix", module), "Snapshot cancellation still suppresses native Process"); Revoked(first); Revoked(second);
            Check(module.Clears == 1, "Selection is cleared after mutated dictionary");
        });
        Case("ClearSelection throw cannot allow native Process", delegate {
            var pointer = Armed(); var module = new InputPointerModule { FailSelection = true }; module.Data[1] = pointer;
            Check(!(bool)Invoke("PointerPrefix", module), "Selection callback fault still suppresses Process"); Revoked(pointer); Check(Plugin.Warnings.Count == 1, "Selection fault is reported");
            module.FailSelection = false;
            Check(!(bool)Invoke("PointerPrefix", module) && module.Clears == 2, "Module reentry owner is retired after selection fault");
        });
        Case("ClearSelection callback reenters Process without recursive selection clearing", delegate {
            var module = new InputPointerModule(); module.Data[1] = Armed();
            module.SelectionHook = delegate {
                // A bounded adversarial callback prevents an actual stack
                // overflow while exposing repeated same-module reentry.
                if (module.Clears >= 4) throw new InvalidOperationException("fixture reentry limit");
                Check(!(bool)Invoke("PointerPrefix", module), "Reentered captured Process remains suppressed");
            };
            Check(!(bool)Invoke("PointerPrefix", module), "Outer captured Process remains suppressed");
            Check(module.Clears == 1, "ClearSelection callbacks cannot recursively clear the same input module");
        });
        Case("A different input module remains independently cancellable", delegate {
            var outer = new InputPointerModule(); var inner = new InputPointerModule(); var first = Armed(); var second = Armed();
            outer.Data[1] = first; inner.Data[1] = second;
            outer.SelectionHook = delegate { Check(!(bool)Invoke("PointerPrefix", inner), "Separate captured module is still canceled"); };
            Check(!(bool)Invoke("PointerPrefix", outer) && outer.Clears == 1 && inner.Clears == 1, "Module guard applies to exact reentry owner");
            Revoked(first); Revoked(second);
        });
        Case("Unblocked Process leaves gesture and selection untouched", delegate {
            var pointer = Armed(); var module = new InputPointerModule(); module.Data[1] = pointer; RollWindow.Blocked = false;
            Check((bool)Invoke("PointerPrefix", module) && pointer.eligibleForClick && pointer.dragging && module.Clears == 0 && ExecuteEvents.Releases == 0, "Fresh native Process passes unchanged");
        });
        Case("Null raycast cache is recreated and supplied", delegate {
            var module = new InputPointerModule(); var parameters = new object[] { module, null };
            Check(!(bool)Invoke("RaycastPrefix", parameters), "Captured raycast is suppressed");
            Check(module.Cache != null && module.Cache.Count == 0 && object.ReferenceEquals(parameters[1], module.Cache), "Captured raycast gets an empty non-null cache");
        });
        Case("Existing raycast cache is cleared without replacing identity", delegate {
            var module = new InputPointerModule { Cache = new List<RaycastResult> { new RaycastResult { distance = 8 } } }; var cache = module.Cache; var parameters = new object[] { module, null };
            Check(!(bool)Invoke("RaycastPrefix", parameters) && cache.Count == 0 && object.ReferenceEquals(cache, parameters[1]), "Captured raycast preserves cleared native cache identity");
        });
        Case("Lifecycle reset fault still releases input capture", delegate {
            RuntimeRolls.FailReset = true; bool thrown = false; try { Invoke("LifecyclePrefix"); } catch (InvalidOperationException) { thrown = true; }
            Check(thrown && RuntimeRolls.Resets == 1 && RollWindow.CaptureResets == 1, "Capture reset runs in finally when native runtime cleanup fails");
        });
        Case("A location change re-arms paused multiplayer dice", delegate {
            int before = MultiplayerRolls.Rearms; Invoke("LifecyclePrefix");
            Check(RuntimeRolls.Resets == 1 && MultiplayerRolls.Rearms == before + 1 && RollWindow.CaptureResets == 1, "Loading together starts a fresh multiplayer dice session after a pause");
        });
        Case("World drag and selection gestures are revoked", delegate {
            var cursor = new CursorLocationExploration(); var view = new View(); view.CursorService.CurrentCursor = cursor;
            Check(!(bool)Invoke("ViewPrefix", view) && !view.HasGesture && view.CameraController.Resets == 1 && !cursor.MainDrag && !cursor.Dragging && !cursor.Rectangle.gameObject.Active, "Modal cancels fake native scene/camera/selection gesture fields");
        });
        Case("Null command queue is suppressed safely", delegate {
            Check(!(bool)Invoke("CommandsPrefix", new object[] { null }), "Captured null queue cannot run native command handling");
        });
        Console.WriteLine((failures == 0 ? "PASS " : "FAIL ") + cases + " actual InputGuards boundary cases; " + assertions + " assertions; " + failures + " failures.");
        Console.WriteLine("Test-only callback and metadata boundaries validate production guard control flow; no native Unity EventSystem or physical input execution is claimed.");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
