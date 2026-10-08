# Pure roll broker tests

Run from the workspace root:

```sh
python3 work/standalone-hardening/tests/engine/run.py
```

The runner accepts `--dotnet /path/to/dotnet/dotnet` and resolves other paths relative to itself. It invokes the C# compiler directly, avoiding the container's SDK CLI process-inspection failure. A standard SDK installation can also run EngineTests.csproj with `dotnet run --project`.

The test executable uses the production RollBroker.cs directly, with no Unity, Harmony, game DLL or GoDice dependency. It verifies exact original/replacement faces; advantage face indices; actor/action/effect/form/role/die/feature/attempt ownership; immutable copied values and identity vectors; changed state snapshots; nested native scopes; automatic-call suppression; one-shot consumption; cancellation, disposal and reset; stale callback rejection; and invalid input rejection.

The broker deliberately does not implement Solasta's trait logic or modify roll totals. RuntimeRolls must snapshot native arguments/features, collect any required replacement faces while the owning coroutine can yield, revalidate the snapshot before native entry, and identify original versus feature-replacement RNG calls explicitly. Every native entry/exit requires a try/finally lease boundary; unprepared nested native calls need SuspendNative or equivalent adapter suppression. A failed broker operation returns native fallback without waiting.

These tests establish the pure ownership contract. Native planner equivalence, Harmony patch stack/type validity and actual game behavior require the adapter's separate native tests.

The final hardening run passes 129 assertions, including request vector limit512, cumulative session/lease limit4096, infinite or throwing producer enumerations, early enumerator disposal, atomic budget rejection, full4096-face one-shot consumption and post-accept stale callback revocation.
