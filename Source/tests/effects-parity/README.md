# Native effect descriptor parity

This development fixture compiles the current production `src/EffectPatches.cs` and executes the supplied pristine native game methods directly under official Harmony 2.3.6.0 on a .NET 8 test host. The game DLL file is never rewritten. The latest run passes **123 cases**.

From the shared workspace:

```
python3 work/standalone-hardening/tests/effects-parity/run.py
```

From an extracted source package, supply external dependencies explicitly:

```
python3 tests/effects-parity/run.py --managed "/path/to/Solasta_COTM/solasta_Data/Managed" --dotnet "/path/to/dotnet" --sdk-root "/path/to/dotnet/sdk/8.0.425" --harmony "/path/to/Harmony/net8.0/0Harmony.dll"
```

Use the supplied pristine game managed assemblies and the official **Harmony 2.3.6.0 net8.0** assembly for the test host. The runtime plugin uses its separately pinned Unity/Mono dependency profile. The fixture verifies the native game file SHA-256 before compilation and requires the expected Harmony version during execution. It needs Python 3 and the .NET 8 SDK; installed SDK paths can be inferred beside `dotnet`, or specified with `--sdk-root`. Run `python3 tests/effects-parity/run.py --help` for the configurable native-input, SDK, Harmony, and build-output paths.

The runner creates compiler response files, logs, and an executable under `build/`, records output in `results.txt`, and hashes the tested sources and external inputs in `source-manifest.json`. Generated binaries, absolute response files, SDKs, game assemblies, Harmony binaries, and decompiled native reference files are not part of the source package.

The cases cover:

- 96 exact argument combinations from original `ApplyEffectForms -> ApplyDamageForm -> RollDamage -> RollDiceAndSum`, varying critical/ignore-critical flags, versatile die, extra dice, and class/class-half/effect-level count multipliers.
- 12 original healing and temporary HP combinations.
- A native additional-healing sequence with distinct 2d6 and 2d8 static bonus groups before base 3d10 healing.
- Two native multi-damage batches, including first/subsequent extra dice, versatile and reroll flags for an item attack.
- Two consecutive native area target applications reusing the game's damage cache.
- Four actual stock CastSpell/UsePower targeted/position overrides, including native active-effect, effect-level, and critical parameter construction.
- Four native hero Bardic damage override/versatile cases, including the stock smaller-die override behavior.
- Two later spell damage-form advancement cases with and without IgnoreSpellAdvancementDamageDice.

The fixture also checks helper eligibility during native calls, normal and exceptional scope cleanup, and absence of damage-draw ownership inside `InflictDamage`. The CLR host adapter preserves actual MonoMod platform behavior; on this Linux sandbox it supplies module filenames from `/proc/self/maps` because `Process.Modules` is empty.

**Coverage limits:** `NativeRollPlan`, `NativeInvocation`, and `RuntimeRolls` in `EffectParity.cs` are capture-only test adapters. This suite tests native descriptor argument parity and helper lifetime; it does not test the production broker, UI, input acceptance, raw-face replacement, multiplayer, or final HP/concentration behavior. Most cases stop at the native dice sum before mutation. Cache and multi-form cases substitute a deterministic sum and intercept `InflictDamage` before HP changes. Healing feature enumeration is controlled by an explicit native-method prefix. These fixture boundaries are deliberate and are not runtime plugin code.

For production ownership and the actual Sleep pool/target-filtering regression, use the separate `tests/effects-native/run.py` suite and its README.

The common environment resolver also accepts `MANUALROLL_TEST_MANAGED`, `MANUALROLL_TEST_DOTNET`, `MANUALROLL_TEST_SDK_ROOT`, and `MANUALROLL_TEST_REFERENCES`. Run `python3 tools/FetchDependencies.py` for exact official references; no Unfinished Business archive is required. The runner compiles byte snapshots and rejects any source/native game/Harmony change after capture. Capture-only runtime adapters always expose the form helper scope and do not implement production policy/state validators.
