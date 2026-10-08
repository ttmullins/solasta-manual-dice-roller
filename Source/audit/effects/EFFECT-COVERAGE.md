# Standalone effect coverage and executable evidence

Production source: `src/EffectPatches.cs`. This file adds preparation and scopes around the supplied native methods; it does not replace the form dispatcher, native damage/healing calculations, HP mutations, target filtering, damage caches, or notifications.

Supported native await owners are registered by `NativeSiteManifest`: attack/reapply/spend-power `ApplyEffectForms`, and the stock spell/power target/zone/position `ApplyMagicEffect` boundaries. `AssignTarget` is intentionally omitted because its argument preparation includes an impure shared evaluation stack. The Sleep/Color Spray HP-pool boundary is registered at `CharacterActionMagicEffect/<ExecuteImpl>d__45::MoveNext` offset `07c5` (`ApplyTargetFiltering`); its descriptor is owned by `RuntimeRolls`, not this file.

## Descriptor scope

- Pure deterministic batches of damage forms, healing forms, or temporary HP forms; every group is keyed by the actual `EffectForm` object.
- Exact damage raw counts including first-form extra dice, spell advancement exceptions, level/table count changes, critical flags, first-form versatile die, native hero Bardic die override, and first/subsequent damage reroll flag.
- Existing native area damage pool is reused: subsequent targets receive no new request for cached forms. Maximum damage requires no raw draw; an uncached area's ordinary shared pool is still collected before the native maximum-damage branch.
- Healing bonus provider static groups precede the actor's base healing group. Maximized base healing requires no raw draw. Temporary HP uses the native actor context and recipient guard.
- Only stock `CharacterActionCastSpell` and `CharacterActionUsePower` receivers have their native spell parameter fill methods replayed. Unsupported subclasses bypass preparation.

Mixed condition/motion/counter/etc. batches, random or filtered form selection, duplicate form references, source-less traps, proxy-only/self-condition application, void recurrent/campaign/global-effect callers, and synchronous concentration inside damage remain native automatic. No broad donor rewrite or upfront pool for unknown/skipped forms is used. A dice-only batch can have later damage skipped when an earlier form kills its target; the accepted unused form-owned permits are retired at the owning native call exit. Native helper argument validation and feature validation are provided by `RuntimeRolls`.

## Executable native evidence

`tests/effects-parity/EffectParity.cs` compiles the production `EffectPatches.cs` against the pristine supplied game DLL and executes original native methods directly under genuine Harmony **2.3.6.0** on the .NET 8 test host. The original game assembly is loaded without rewriting its file. The test host adaptation only supplies module filenames for MonoMod when `/proc/self/maps` is available but `Process.Modules` is empty.

The latest run passes **113** cases:

- 96 combinations of actual `ApplyEffectForms -> ApplyDamageForm -> RulesetActor.RollDamage -> RollDiceAndSum`: critical/ignore-critical flags, versatile selection, extra dice, and class/class-half/effect-level multipliers.
- 12 actual healing/temporary-HP combinations with differing dice and extra counts.
- One actual additional-healing sequence with two distinct 2d6/2d8 static bonus groups before the base 3d10 actor group.
- Two actual multi-damage batches, including attack-mode first/subsequent reroll and versatile flags.
- Two consecutive actual area target applications with the native static damage cache: only the first application calls the native damage sum; the second descriptor and execution both reuse the cache.

The test also checks the actual Harmony helper scopes during native calls, exceptional scope restoration, normal restoration, and absence of damage-draw ownership inside `InflictDamage`.

Reproduce from workspace root:

```
python3 work/standalone/tests/effects-parity/run.py
```

Coverage limits: the parity fixture's `NativeRollPlan` only records descriptors and `RuntimeRolls` only supplies preparation/scoping stand-ins; it does not test the production broker, accepted-value transport, UI, or native RNG replacement. Most parity runs stop at the native dice sum before mutation. Cache and multi-form runs replace the native dice sum with a deterministic test result and intercept `InflictDamage` before HP mutations. Source/target healing feature enumeration is controlled by an explicit native-method prefix. These tests establish descriptor/native argument parity and helper lifetime; they do not certify live Unity rendering, real HP/concentration callbacks, every spell/power definition, every feature combination, or cross-action mutation of native shared caches.

## Native reference inventory

`inventory.txt` contains the pristine direct and indirect `ApplyEffectForms`, `ApplyMagicEffect`, rest, and spell-pool call sites. The authoritative decompiled source for review is `RulesetImplementationManager.cs` (`ApplyEffectForms`, `ApplyEffectForm`, `ApplyDamageForm`, `ApplyHealingForm`, `ApplyTemporaryHitPointsForm`) and `RulesetActor.cs` (`RollDamage`, `RollDiceAndSum`). These audit files are not runtime replacements.

## Production binding regression and independent final review

`tests/effects-native/run.py` now supplies a separate 24-assertion regression using current production `RuntimeRolls`, `RollBroker`, and `EffectPatches`, the actual pristine native game methods, and UI/raw fallback fixtures. It proves the user-reported Sleep pool-40 behavior, native AddDice aggregation, original sorted target filtering, Auto/disabled fallback, cached second-target no-request/no-consumption, and same-die distinct form ownership with an unowned notification draw between forms. Its README describes terminal HP isolation explicitly.

Final source review verified the interface and implementation `ApplyEffectForms` parameter metadata match, and that both `ApplyMagicEffect` overloads use the native parameter names expected by `NativeInvocation`. The spell/power descriptor now reads the actual stock `activeSpell`/`activePower` fields used by native overrides rather than assuming `ActionParams.RulesetEffect` remains identical. Helper binding uses `EffectForm` identity and exact original damage/healing/temporary-HP helpers; static bonus setup runs at explicit first prefix priority before the production static-sum group check. The production static-sum guard declines ineligible notification draws before examining or revoking form-owned groups.

`Plugin.GamePreflight` requires the exact supplied pristine on-disk SHA and Harmony 2.3.6.0. A loaded `SolastaUnfinishedBusiness` assembly prevents enabling the standalone preview; an assembly loaded later disables manual preparation and retires pending plans. This is a strict compatibility boundary, including UB loaded but subsequently disabled until restart. `Networked` causes manual preparation/consumption to decline; no standalone multiplayer transport is represented as tested.

No additional confirmed binding/cache bug remained in the reviewed source and executable scenarios. Native synchronous concentration, mixed and recurrent effects, final HP callbacks, cross-action cache interactions, third-party modifications other than the named UB guard, and live Unity/input rendering remain outside this certification.
