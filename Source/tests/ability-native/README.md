# Actual native ability wrappers and prepared plan handoff

Run from the workspace root:

```sh
python3 work/standalone-hardening/tests/ability-native/run.py
```

The runner uses `tools/TestEnvironment.py` and accepts the common `--dotnet`, `--sdk-root`, `--managed`, `--references` and `--cecil` overrides. It creates a private game ABI, copies untouched IL from the verified pristine Assembly-CSharp.dll, then installs the real Harmony production RuntimeRolls and NativeAbilityPlanner against that fixture. RollBroker and NativeScratchScope are the production sources. NativeSavePlanner's exact foreign-method/interface guard helpers are extracted unchanged; its unrelated save producer is a stub.

Copied native methods include both GameLocationCharacter.RollAbilityCheck wrappers, location modifier preparation, native affinity bonus/advantage/context/light calculation, both RulesetCharacter.RollBasicAbilityCheck wrappers, final native RollAbilityCheck, native hero proficiency/base-bonus computation, ComputeAdvantage and actor/raw/static dice processing. See extracted-methods.txt for exact methods and instruction counts.

The latest run passes117 assertions with12 direct pristine-native baseline comparisons covering both location overloads, armor context, three lighting states, expertise and added/subtracted dice. Further assertions exercise Basic automatic result branches and bonuses; stored negative Bardic values without additional draws; odd half-proficiency; constant bonuses; passive checks; foreign Harmony patch rejection; and unrelated raw dice in final native callbacks. Solasta's actual ComputeAdvantage sums signed trends: two advantages plus one disadvantage remain advantage in this native version. The fixture copies this method instead of imposing tabletop cancellation semantics.

Post-accept changes to armor, ability score, proficiency, difficulty, provider identity, lighting or bonus-group operation must invalidate the entire accepted static-bonus/d20 plan before any face consumption. The tests require fresh native RNG for every original stage and exactly one native notification/interruption, thereby testing actual production ownership handoff in addition to preview recording.

The terminal VerifyAbilityCheck result calculation, armor/inventory lookup, condition storage and UI input are explicit test boundaries. Native final RollAbilityCheck still runs its original dice, notifications and interruption sequence. This fixture does not claim full damage/effect application or final ability outcome log/Bardic reaction fidelity, Unity/Windows/Mono behavior or live-game certification. Actual Unity rendering and TA cancellation ownership have separate fixtures.

Results and source-manifest.json record the exact tested source hashes and pristine game hash. Private compile artifacts and the fixture-only real-MonoMod host shim live under build/ and are not production dependencies.

An additional full pristine game-DLL metadata smoke validates location, Basic and stock Hero foreign-method guards against actual game/Unity metadata, rather than only the extracted fixture ABI. It does not execute Unity engine calls.
