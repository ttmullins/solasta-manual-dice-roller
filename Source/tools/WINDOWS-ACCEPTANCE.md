# Actual Windows game acceptance

The developer suites execute controlled fixtures using .NET 8 and real Harmony. The release DLL uses the game's .NET Framework/Unity references. Those are different execution environments. A successful source build, metadata link, generated method or drawing recorder cannot certify Unity Mono JIT behavior, engine rendering, physical input or a real game session.

Use a separate test save and a clean, supported Steam installation on Windows. Install the official Unity Mod Manager 0.32.4 with its Harmony 2.3.6, choose **Solasta**, and install the standalone candidate through UMM. Close the game before updating or removing the mod. Restore any earlier replacement `Assembly-CSharp.dll` using its pristine backup or Steam verification first. The standalone plugin does not perform that migration. The official UMM Assembly entry targets `UnityEngine.UIModule.dll`; its main game starting point does not replace the clean main game DLL.

1. Run `Check-StandaloneSetup.ps1` with the game folder. Start Solasta, confirm the mod is enabled and its successful `MDR_DIAGNOSTICS` entry identifies this candidate. Retain the UMM/game log containing that entry. Do not clear errors just to obtain a passing report.
2. While that Solasta process is running, run the read-only collector below. Choose an output folder outside the game and explicitly specify the log containing the diagnostic marker. The collector creates disk/module/log diagnostics plus an acceptance checklist whose cases all start **pending**. It neither submits rolls nor passes cases automatically.
3. Add your name and the test session's UTC start time to `windows-acceptance.json`. Follow every case in `acceptance-cases.json`. Enter known raw faces and record both the requested dice and the actual game result. Use logs, screenshots or short observation files saved under the report folder as evidence. Keep failed cases failed and pending cases pending.
4. Record outcomes one at a time with the recorder or edit the JSON directly. A pass needs concrete observations and nonempty evidence. A rerun of the collector refreshes diagnostics/log snapshots but preserves an existing checklist. Capture before mod removal; record the removal/restart observation afterward with separate evidence.
5. Return the whole report folder, including retained evidence and logs. The production validator checks the identical DLL/game/loader hashes, a successful marker in the retained log, actual Windows Mono/x64 loaded runtime metadata, marker/session timing, current developer provenance and all manual outcomes. Acceptance remains a tester's observation; the validator does not infer truth from screenshots or turn developer tests into live evidence.

In PowerShell, from the extracted mod's `Source\tools` folder:

```powershell
.\Check-StandaloneSetup.ps1 -GamePath 'C:\Games\Solasta'
.\Collect-WindowsAcceptance.ps1 -GamePath 'C:\Games\Solasta' `
    -OutputDirectory 'C:\SolastaTesting\candidate-0.8.4.2' `
    -LogPaths @('C:\path\to\the\actual\UMM-or-game-log.txt')

# Example only: use the faces and results actually observed in your game.
.\Record-WindowsCase.ps1 `
    -Report 'C:\SolastaTesting\candidate-0.8.4.2\windows-acceptance.json' `
    -Case initiative -Status pass `
    -Observed 'Entered [actual faces]; initiative order and native modifiers were [actual results].' `
    -Evidence @('screenshots/initiative.png', 'observations/initiative.txt')
```

The scripts target Windows PowerShell 5.1 and change only the report directory. This development environment has not executed them inside Windows/Solasta. The collector observes only named Solasta processes and explicitly supplied logs; it does not search personal files. Review supplied logs before sharing them.

For developer/reviewer verification after receiving real Windows evidence:

```sh
python tools/ValidateRelease.py --build build \
  --regressions build/regression/regression-report.json \
  --level production --windows /path/to/report/windows-acceptance.json \
  --output build/production-gate.json
```

Without complete matching actual Windows single-player and coordinated multiplayer evidence, production qualification fails. A candidate can still carry passing developer verification with `--level candidate`; its report explicitly sets `live_windows_mono_qualified` and `live_multiplayer_qualified` to false. The multiplayer protocol requires every room peer to run the exact matching candidate. A missing or mismatched peer blocks manual execution; leave/restart the session as indicated by the runtime status. A native automatic fallback is allowed only when all matching peers agree on the same supported boundary before committing faces.

See `MULTIPLAYER-ACCEPTANCE.md` for the independent multi-machine qualification. The full production command also needs `--multiplayer /path/to/coordinated/multiplayer-acceptance.json`. Do not label a build fully functioning in multiplayer from simulated packet tests alone.
