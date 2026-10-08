# Native saving throw preview checks

From the workspace root, run:

```sh
python work/standalone-hardening/tests/save/run.py
```

The runner uses the shared `tools/TestEnvironment.py` resolver for the .NET 8 SDK, pristine game DLLs, official Harmony 2.3.6.0 test assembly, and Mono.Cecil. Run `--help` for explicit SDK/managed/reference paths; `MANUALROLL_TEST_*` environment overrides are also supported. It writes separate test assemblies under `tests/save/build`; it does not change the game DLL or plugin package. Results and extracted-method inventory are in `results.txt` and `extracted-methods.txt`.

The production `src/NativeSavePlanner.cs` and `src/NativeScratchScope.cs` run unchanged. `NativeInvocation` is taken directly from `src/RuntimeRolls.cs`. The fixture copies the full pristine IL, local variables, exception handlers and native parameter names for:

- `RulesetImplementationManager.TryRollSavingThrow` (450 instructions).
- `RulesetEffect.TryRollSavingThrow` (138 instructions), including native knowledge-based save DC preparation.
- `RulesetActor.FindFirstConditionHoldingFeature` and `ConditionDefinition.HasFeature`.
- `RulesetActor.IsAutomaticallyFailingSavingThrow`, `ComputeSavingThrowModifier`, `RollSavingThrow`, `RollDie`, `RerollDieAsNeeded` and `RollDiceAndSum`.
- `RulesetCharacter.ComputeSavingThrowModifierImposedOnTarget`.
- `FeatureDefinitionSavingThrowAffinity.ComputeSavingThrowModifier` and `CanPerformSaveForAbilityScore`.
- `RuleDefinitions.RollDie` and `RollStaticDiceAndSum`.

The 103 assertions cover Bless and Bane dice counts, provider deduplication across forms, restricted forms, replaced saving throw ability, advantage, source DC modifiers, Careful Spell, automatic failure, disabled ally saves, effect overrides, preserved caller modifiers, late exceptions/recovery, and nested sum ownership. Scratch isolation preserves the exact original collection references, cache contents and already-active enumerators on success and failure, including original-form actor enumeration. The actual manager receiver is isolated even if a different manager is registered as a service. Controller-based monster saving throws safely retain native rolls because the conjurer is outside the preview scope.

Foreign entry, leaf, provider, property, raw-die, postfix, nested condition enumeration/membership, difficulty settings, bestiary lookup and concrete lore getter patches are rejected before preview side effects. Spoofing the plugin's Harmony owner ID does not bypass the check. The real native call still executes its own patched methods normally, with no duplicate preview invocation. An unchanged accepted-plan validator rechecks the same isolated native calculation with no RNG or outcome events. Removing/reordering providers, changing a same-die/count Bless or Bane group, changing the native base bonus, or adding a foreign patch invalidates the whole plan before handoff. Aggregate preview dice are bounded before an excessive provider sequence can complete.

The fixture records plan groups and invokes the registered production state validator rather than running the broker, UI or accepted-face lease. Thus the save fixture proves rejection, not the runtime implementation of whole-plan native fallback. Its actor feature enumeration, attribute lookup, base saving throw bonus, settings/services, definition identity, modifier clone, bestiary/service storage, original-form enumeration forwarding, advantage helper, event log and condition interruption are test boundaries. In particular, hero proficiency calculation and live conditions/scene state are not reproduced. RNG is a queue-backed boundary, while the native raw-die method that consumes it is copied unchanged. Native save events and interruption requests are counted at these boundaries.

A second host loads the complete actual pristine game and Unity assemblies. It verifies that the production foreign-patch guard accepts stock method metadata, without executing Unity engine calls. This caught an overly broad inherited Unity getter check that the extracted fixture could not expose.

Real Harmony generates and executes the patched test methods. `tools/HarmonyFixtureHost.cs` adapts only module filename enumeration because this sandbox returns no `Process.Modules`; it forwards the actual MonoMod platform operations and is never shipped. These checks do not establish live Unity gameplay, multiplayer, visual UI or compatibility with other rule mods.

The pre-fix executable failures for active enumerator invalidation and foreign nested-helper preview execution are preserved in `baseline-scratch-failure.txt` and `baseline-foreign-helper-failure.txt`.

The guard checks verified native selectors and provider interface targets, including location save-context, inventory/armor database and effect DC getters. It is conservative compatibility filtering, not a formal transitive call-graph proof or certification for arbitrary other mods. Those location/inventory/repertoire selector additions are exercised by actual metadata validation; the full live scene calculation is not simulated in this fixture.
