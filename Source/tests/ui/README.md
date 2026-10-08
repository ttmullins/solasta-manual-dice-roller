# Roll window and input state checks

From the workspace root, run:

```sh
python work/standalone-hardening/tests/ui/run.py
```

An alternate .NET SDK host can be supplied with `--dotnet /absolute/path/to/dotnet`. The shared `tools/TestEnvironment.py` resolver also accepts managed/reference paths and `MANUALROLL_TEST_*` environment overrides; use `--help` for options. The runner uses actual pristine game/Unity references, official Unity Mod Manager, and the official .NET 8 Harmony assembly. It compiles test hosts under `tests/ui/build` and records output in `results.txt`.

`UiTests.cs` compiles the unchanged production `src/RollWindow.cs` with test-only plugin/settings and reset callbacks. Its 94 assertions check immutable mixed die ranges, exact raw-face parsing, invalid/total-only input rejection, captured mouse presses and releases, multiple held buttons, fresh input recovery, teardown, and prompt reference ownership. A stale waiter's `Close(oldPrompt)` cannot close a newer prompt. With the genuine Unity `Color` type they also check the roll-kind palette (normalised kinds, badge labels, distinct accents; healing green, damage red, saves blue) and the session roll history data (confirmed faces and totals, a game roll completed by its d20, other players' entries, the forty-entry cap and clearing). The owner inspection uses an uninitialized Unity component with a test-only cached pointer; it never renders or invokes Unity input.

`InputTests.cs` compiles the unchanged production `src/Plugin.cs`, including `InputGuards`, with test-only raw polling, roll installation and reset callbacks. Its 19 assertions validate exact pristine input/lifecycle method and field metadata; clear all six native scene arm/drag flags; cancel native `PointerEventData` click/drag/raycast state; suppress command queues, cursor coverage and camera controls while captured; return an empty native raycast result list even before the cache exists; tolerate an absent teardown pointer; release guards afterwards; and preserve settings categories and defaults. It also checks that the lifecycle callback requests a roll reset and input ownership reset.

The queue, coverage, camera and raycast guard bodies execute unchanged. Native view gesture clearing and pointer state clearing execute unchanged with actual native field/types. Raw input polling is a deterministic test boundary. The complete view/pointer engine update, event dispatch to actual GameObjects, plugin load/unload, and the implementation behind the runtime reset callback are not executed here. These tests require no module-enumeration shim or live Unity renderer.

The shipped plugin is separately compiled against genuine game, Unity Mod Manager and Harmony assemblies. In-game checks are still required for header drag capture outside the window, text focus, scrolling, cursor restoration, scene transitions, and preventing scene clicks on the frame the modal opens or closes. The assertion counts are state/metadata checks, not a claim of Unity gameplay playtesting.

Separate `tests/ui-render` and `tests/ui-input-boundary` harnesses execute production render/lifecycle and event-callback flow at fake Unity boundaries. They cover render/cursor/persistence failures and reentrant/throwing pointer callbacks, but remain distinct from these actual-reference metadata/state checks.
