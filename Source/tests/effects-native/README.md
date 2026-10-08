# Actual native Sleep and effect binding regression

Run from the extracted source project root:

```
python3 tests/effects-native/run.py
```

The test compiles the current production `RuntimeRolls.cs`, `RollBroker.cs`, and `EffectPatches.cs` against the supplied pristine `Assembly-CSharp.dll`, loads that DLL directly, and uses genuine Harmony 2.3.6.0. No game DLL file or native game method is copied into a donor replacement. The bootstrap registers dependency resolution before the first native game type is JIT-resolved.

The latest run passes 71 assertions:

- Native `ApplyTargetFiltering` metadata has the exact expected signature.
- Both a base 5d8 pool and a base 3d8 pool with native `AddDice = 2` request five raw d8 faces in one prompt.
- Entering `[8,8,8,8,8]` produces native `RollMagicalHitPointsPool` result 40 without RNG. The original target filter sorts unsorted HP `[41,25,15]` and spends the pool on HP 15 then 25. Its result is observed, never overwritten.
- Auto/cancel and the disabled spell-pool category run all five native raw-draw call sites. Fixture fallback faces of 4 create pool 20, so native filtering selects only the HP 15 target.
- Two native area applications share the original damage cache. Accepted raw faces 2 and 5 plus the original form bonus 1 produce damage 8 for both targets. The second target has no plan, prompt, raw draw, or accepted-face consumption.
- Separate same-die form objects retain separate groups and accepted inputs. A static native notification roll occurring between their applications uses automatic RNG without stealing or revoking the second form's accepted face.

Unmodified native bodies exercised include `CharacterActionMagicEffect.ApplyTargetFiltering`, `RulesetCharacter.RollMagicalHitPointsPool`, `RuleDefinitions.RollStaticDiceAndSum`, `RulesetImplementationManager.ApplyEffectForms`, `ApplyEffectForm`, `ApplyDamageForm`, and `RulesetActor.RollDamage`, `RollDiceAndSum`, and `RollDie`. Production Harmony instrumentation runs on these original methods.

Fixture boundaries are explicit: UI receives predetermined raw input arrays; raw RNG fallback produces a deterministic face 4; `InflictDamage` records the result and returns before HP/resistance/concentration side effects. Unrelated save/rest adapter branches use inert stand-ins. The CLR host adaptation only supplies module filenames to genuine MonoMod because this sandbox's `Process.Modules` is empty. This verifies native pool filtering, cache behavior, exact permit consumption, and form ownership. It does not verify live Unity scheduling/rendering, concentration, final HP mutations, all spell definitions, network play, or Unfinished Business compatibility.

The separate `tests/effects-parity/EffectParity.cs` runs 123 descriptor/native argument parity cases with capture-only plan/runtime adapters. Those tests establish native count/flag/helper scope parity and must not be confused with this production-broker regression.

Hardening cases additionally exercise healing-provider removal, order changes, replacement, die/count/level changes, and target maximization while input is pending. The baseline clears only the fixture-owned plan validator to show the original transfer of provider A’s accepted face to surviving provider B. Production guards instead use automatic native sums. Original native ReceiveHealing instructions through the HealingReceived event reproduce a provider change between two healing forms; the first form keeps its own accepted inputs and the second uses native automatic dice. The remaining healing HP/logging work is omitted, and Unity integer Clamp/Max use equivalent System.Math operations. Some provider-mutation cases stop at the base healing sum after observing native bonus sums; they do not certify final healing totals.

Other production-broker cases preserve native maximum-family damage and shared cache behavior: a maximum target receives 13 from 2d6+1, while the first area application still caches accepted ordinary pool8 for an ordinary later target. Mixed damage/healing and repeated form references fall back as whole native steps. A terminal damage fixture sets the target dead after the first form, proving the original dispatcher skips the second form and its unused accepted input cannot reach the next action. Preview and validator scopes preserve source/target scratch collection objects, contents, and existing enumerators.

Use `MANUALROLL_TEST_MANAGED`, `MANUALROLL_TEST_DOTNET`, `MANUALROLL_TEST_SDK_ROOT`, and `MANUALROLL_TEST_REFERENCES`, or corresponding runner options, to configure external inputs. In the shared workspace use `python3 work/standalone-hardening/tests/effects-native/run.py`. `python3 tools/FetchDependencies.py` fetches the exact official dependencies without an Unfinished Business archive. A successful manifest requires unchanged compiler source snapshots and unchanged native game/Harmony input hashes through execution.
