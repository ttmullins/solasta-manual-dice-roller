# Production InputGuards callback boundary tests

Run `python work/standalone-hardening/tests/ui-input-boundary/run.py` from the workspace root. A .NET 8 SDK is required; the shared `tools/TestEnvironment.py` resolver supports `--dotnet`, `--sdk-root`, and corresponding `MANUALROLL_TEST_*` overrides. This fixture does not require game DLLs or loader downloads.

Latest verified result: **19 cases, 38 assertions, 0 failures**. The complete current source also compiled successfully against the genuine pinned game/UMM/Harmony runtime references after the module reentry fix.

The runner captures the complete production `InputGuards` class verbatim from `src/Plugin.cs`, adding only imports and its enclosing namespace. SHA-256 values for the production file, captured class, and boundaries are recorded in `source-manifest.json`. It compiles this actual guard source with C# 7.3 into a test host.

The boundary supplies metadata-compatible fake game/input types and deterministic `ExecuteEvents` callbacks. It exercises pointer-up and end-drag callback faults, a throwing warning logger, callback assignments to every captured pointer field, recursive cancellation of the same pointer, reuse after a callback fault, null pointers/dictionaries, dictionary mutation during snapshot cancellation, selection-clearing faults, same-module Process reentry during selection clearing, raycast cache restoration, world gesture cancellation, and lifecycle capture cleanup after a runtime-reset fault. Captured native Process must remain suppressed throughout callback failures and reentry.

The same-module selection reentry case uses a bounded callback that throws after four clears to prevent a host stack overflow while exposing recursive clearing. The real supplied Unity `PointerInputModule.ClearSelection` IL calls `HandlePointerExitAndEnter` for each pointer before clearing its pointer dictionary, so it contains a callback phase before state retirement. This fixture models a callback that synchronously reenters the module during that phase; it does not execute the real native Unity EventSystem.

The callback mutation tests assign the modeled pointer properties/fields from pointer-up/end-drag handlers. They do not invent callback hooks inside Unity's property setters. A pointer-level cancellation guard should revoke all callback changes in a final cleanup; a module-level reentry guard should prevent recursive selection clearing. Null cache restoration and captured-input flags must retain their native ownership relationships.

`results.txt` records the latest run. These tests validate production guard control flow at explicitly fake boundaries. They do not prove actual Unity pointer dispatch, real scene input, physical clicks/dragging, native cursor/camera services, genuine Harmony detours, or live multiplayer behavior. The separate actual-game-metadata input tests and in-game playtests cover different evidence boundaries.
