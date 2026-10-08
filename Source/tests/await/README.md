# Native await regression

Run `python3 tests/await/run.py` from the standalone source directory, or supply
`--managed`, `--references`, `--dotnet`, `--sdk-root`, and `--cecil` for your local
build environment. It requires the pinned pristine game assembly, the authentic
Harmony 2.3.6 .NET8 generator dependency, the .NET8 SDK, and Mono.Cecil. No game or
loader binaries are included in the source package.

The runner copies the complete pristine `CharacterActionMoveStepJump` roll-check
factory and generated iterator, plus pristine `TA.Coroutine.Run` and `Reset`, the complete native `CharacterActionCastSpell.CounterEffectAction` factory/iterator,
and the complete native `CharacterActionShove.ExecuteImpl` factory/iterator,
into a small fixture environment. It compiles the production `NativePatches.cs`
and invokes the actual pinned Harmony patch generator and detour implementation.
The sandbox host adapter supplies module filenames from `/proc/self/maps` when
`Process.Modules` is empty; all other MonoMod platform operations are forwarded
to their original implementation.

The current suite passes 162 assertions covering real native sequential waits,
native argument identity, local/out-result preservation, no early or duplicate
native rolls, direct and nested native TA reset cancellation, native disposal,
toggle continuation preservation, original direct/nested native finally cleanup,
failed-cleanup isolation, duplicate-frame disposal, automatic bypass without an added yield,
exception identity and cleanup count, rollback, neutral compatibility retention,
and reload. They also execute the original failed Counterspell reaction path
in both automatic and manually awaited modes, proving original native state1
does not collide with the new continuation state. Separate negative cases reject a changed IL fingerprint, an await inside a synchronous native finally, and an
unproven computed branch-dispatch state assignment, and unsupported byref return.

Exception-handler parity uses an independently compiled iterator with the same
compiler-generated `fault { Dispose(); }` shape. Harmony 2.3.6's normal fault
emitter produces invalid IL in this .NET8 host even for a no-op patch. The narrow
compatibility transpiler turns only the verified fault cleanup into
`catch(object) { Dispose(); throw; }`, preserving exception identity and cleanup
timing. Its separate Harmony identity stays until process exit, allowing a
failed-load rollback to remove all manual-roll patches without regenerating the
invalid raw fault handler.

These checks qualify the generated code and lifecycle behavior in a .NET8 test
host. They do not substitute for live Unity/Mono validation or assert that every
registered game iterator has been executed in a full game session. The supplied
pristine game passes the separate complete 38-site IL preflight.

The runner also loads the complete unchanged game assembly and generates each
of the 23 native owner methods with the production transpiler. With unchanged
Unity dependencies, 21 generate and detour successfully; the attack iterator
and target magic iterator reference `UnityEngine.Component.get_transform`, a
native InternalCall that CoreCLR cannot resolve outside Unity. The second run
uses a test-only copy of `UnityEngine.CoreModule.dll` in which exactly that
getter throws if called. It invokes the actual production installer, generating
all 23 owners and 38 sites plus native disposal/reset hooks, then removes the
main patch identity and repeats preflight with neutral fault compatibility
remaining. No game method is invoked during this generation check. The game
DLL stays byte-identical and the test-only Unity shim is never distributed.

Immediately before each original native call, the generated adapter spills the actual receiver and argument stack, sends an input-only snapshot to the production validator, then reloads the exact same stack. Byref output slots remain synchronous live references and are null in the validation snapshot. The extracted Jump regressions replace the receiver or action modifier during a pending await; the validator is invoked before either native actor can roll, and the original fault/disposal path retires the continuation. Those regressions use a test validator boundary; separate production-runtime fixtures qualify the actual single-player bypass and multiplayer fail-closed policy.

After each unchanged native call the adapter spills its typed return, synchronously copies only the value outputs from its live byref locals, observes those values under the exact invocation lease, and reloads the typed return before the original result consumers. The native Jump cases check its int return and enum/int outputs in original parameter order. A compiled boundary case separately verifies a void return and ref/out mutation; unsupported byref returns fail preflight. The production completion descriptor compares these actual outputs across peers.

The Shove site is an explicit manifest exception for one immutable iterator receiver below the native arguments. Preflight still verifies the whole method hash and additionally proves the exact 15-instruction conditional/call/common bool-fieldsetter shape, branch targets, offsets, native signature, field ownership and accessor types. The adapter discards that proven iterator receiver before waiting and reconstructs the same `ldarg.0` at handoff. It never retains an arbitrary extra stack value. Every other site keeps the normal empty-stack guard; Diamond Soul's different conditional receiver remains unsupported. The extracted complete native Shove iterator is executed unpatched and patched for same-side and incapacitated skips plus true and false native contest returns. Manual suspension/resumption and automatic handoff preserve its common fieldsetter and later motion, Shove, Prone and event behavior; cancellation executes no contest or later effects. Its contest return is a controlled boundary double here; the separate shove-native fixture executes the pristine ResolveRolls/ResolveContestCheck routines.
