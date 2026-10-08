# Combined native coroutine cancellation and actual modal ownership

Run:

```sh
python3 work/standalone-hardening/tests/await/integration/run.py
```

This fixture copies the pristine Jump RollChecksIfNecessary factory/full iterator and TA.Coroutine.Run/Reset, installs production NativePatches, RuntimeRolls and RollBroker, and uses the actual production RollWindow Display/Close/reference ownership. Unity boundary operations are fake; the modal ownership implementation is production code. The location-ability descriptor is an explicit bridge, and the GameLocationCharacter ability boundary consumes actual copied actor RollDie. Native ability preparation itself is tested separately in ../../ability-native.

The latest run passes24 assertions. Actual TA.Run reaches production's pending Collect/CollectGroup/Ask and actual RollWindow without running the original native roll. Actual TA.Reset clears native ownership, closes only its window, restores cursor capture and rejects stale callbacks. Removed nested cleanup cannot close another owner's modal. Tests also cover direct native iterator disposal, nested parent/child TA reset ownership, and disable/resume through the saved native continuation, requiring both original Jump ability calls to complete automatically exactly once.

These are combined executions through actual TA and actual production ownership, rather than separate cancellation and UI simulations. They do not certify Unity engine internals, a live Solasta action, or the final ability producer; those are explicit boundaries. Runtime is .NET8/Linux with real Harmony and a fixture-only module inventory shim. See results.txt, extracted-methods.txt and source-manifest.json for evidence. Build artifacts remain under build/.
