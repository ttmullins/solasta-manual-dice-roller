# Standalone build and qualification

The candidate uses numeric UMM version **0.8.4.2**. The runtime plugin is compiled against the exact supplied game’s .NET Framework/Unity Mono assemblies, official Unity Mod Manager 0.32.4 and Harmony 2.3.6.0’s net48 build. The .NET 8 SDK is a developer compiler/audit host; no .NET 8 framework reference reaches the plugin. The ZIP contains one runtime DLL and no game, loader, Harmony or GoDice binaries.

Python 3.9 or newer, a stable .NET 8 SDK and the exact pristine Solasta Managed directory are required. The main game SHA256 is `a87e45cbd187e42e9d64a610850b39edba4896168b14d71ca71c5d32cf9e96ee`. `FetchDependencies.py` retrieves checksum-pinned official UMM, Harmony and development-only Mono.Cecil 0.10.4 packages. An Unfinished Business archive is no longer needed. This is not a runtime compatibility claim for Unfinished Business.

From the extracted source folder (containing `src`, `tools` and `tests`):

```sh
python tools/FetchDependencies.py
python tools/RunRegression.py --managed /path/to/Solasta_Data/Managed \
  --dotnet /path/to/dotnet --sdk-root /path/to/dotnet/sdk/8.0.425 \
  --output build/regression --build build
python tools/ValidateRelease.py --build build \
  --regressions build/regression/regression-report.json \
  --level candidate --output build/candidate-gate.json
```

SDK and reference pack selection compares numeric versions and discovers a stable .NET 8 SDK using `dotnet --list-sdks`. Explicit CLI inputs override `MANUALROLL_TEST_DOTNET`, `MANUALROLL_TEST_SDK_ROOT`, `MANUALROLL_TEST_MANAGED`, `MANUALROLL_TEST_REFERENCES` and `MANUALROLL_TEST_CECIL`. Otherwise project-local official references and a development-workspace SDK/input folder are used when available, then PATH’s dotnet. All shipped suite runners use this common resolver. `--source`, `--output` and the same dependency options are available on `Build.py`; `--smoke-only` compiles genuine dependency probes without producing a runtime placeholder.

The orchestrator discovers every non-build `tests/**/run.py`, including nested `await/integration`, executes each in its own process, retains separate logs and checks exact source/tool/test and input hashes before/after. It does not add overlapping assertion counts together as coverage. A partial `--suite` run is useful for development but cannot pass the complete release gate. Tool-policy tests exercise stale source/artifact/logs, incomplete acceptance, CoreCLR markers and old process markers using synthetic verifier fixtures; those fixtures are never live evidence.

`Build.py` compiles immutable runtime and audit-tool snapshots, rejects input/source drift, verifies real assembly identities/member links, checks pure IL without a 32-bit requirement, validates the complete real-metadata Harmony installation/binding inventory with negative self-tests and reads original/current native instructions using the actual Harmony library. Every managed reference is hashed. .NET 8 Harmony emission/execution fixtures validate copied pristine native methods and selected controlled boundaries; suite READMEs and source manifests identify those boundaries.

The development host substitutes `/proc/self/maps` for unavailable sandbox `Process.Modules` enumeration through a fixture-only forwarding adapter. Selected whole-game emission probes also use an explicit test-only Unity getter that throws if executed. Neither adaptation enters the runtime DLL. These probes cannot establish Windows Mono JIT behavior or Unity engine execution. Actual Windows acceptance is required before a production claim; see `WINDOWS-ACCEPTANCE.md`. The candidate gate always sets `live_windows_mono_qualified` and `live_multiplayer_qualified` to false. Pristine PUN, Photon Realtime and Photon client DLLs are independently checksum-pinned and metadata/compile-time ABI audited; these game-supplied libraries are not bundled. Production validation requires matching live diagnostics, retained logs and all tester-recorded game outcomes.

`Package.py --build build --output /path/to/candidate.zip --readme /path/to/README.md --test-results /path/to/TestResults.md --regressions build/regression/regression-report.json` packages a verified candidate and its fresh developer logs. `--qualification production --windows /path/to/windows-acceptance.json --multiplayer /path/to/coordinated/multiplayer-acceptance.json` additionally requires actual matching Windows acceptance plus coordinated host/client multiplayer evidence; absent or pending evidence fails. A production archive retains the exact accepted reports, explicit logs and observation files. The ZIP records qualification explicitly. Publication uses a temporary file, CRC and complete checksum verification, then atomic replacement. `TestPackage.py` tests write/CRC/checksum/sharing failures preserving an existing ZIP, sole-binary/layout invariants and deterministic repackaging.

`Check-StandaloneSetup.ps1` performs a read-only Windows disk check; optional `-ModPath` also checks the installed artifact/Info/build manifest. It cannot prove loaded runtime behavior. `Collect-WindowsAcceptance.ps1` snapshots explicit logs and running Solasta metadata into a separate report directory and creates a pending checklist. `Record-WindowsCase.ps1` records one tester-provided observation at a time. `New-MultiplayerAcceptance.ps1` builds a separate pending report referencing the actual host/client diagnostics and known-face comparison records; it never passes cases automatically. `MULTIPLAYER-ACCEPTANCE.md` lists handshake, ownership, synchronization, native RNG/callback baseline, stale/invalid packet, mismatch, transition and disconnect/rejoin/master-change requirements. Windows tools have not been run inside the actual Windows game environment here.

The official UMM configuration selects **Solasta**, `Solasta.exe`, entry point `[UnityEngine.UIModule.dll]UnityEngine.Canvas.cctor:Before`, and starting point `[Assembly-CSharp.dll]TacticalAdventuresApplication.Start:After`. Assembly injection targets the UI module, leaving the main game hash gate compatible. Restore any previous replacement main game DLL first; close the game before installing, updating or removing the standalone mod. The official loader supplies runtime dependencies; the mod ZIP does not duplicate them.

Official dependency sources and license metadata are pinned in `dependency-lock.json`:

- https://github.com/newman55/unity-mod-manager
- https://www.nuget.org/packages/UnityModManager/0.32.4
- https://www.nuget.org/packages/Lib.Harmony/2.3.6
- https://www.nuget.org/packages/Mono.Cecil/0.10.4
- https://github.com/newman55/unity-mod-manager/wiki/How-to-add-new-game
