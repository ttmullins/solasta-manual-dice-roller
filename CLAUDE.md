# Manual Dice Roller (Solasta mod)

A standalone Unity Mod Manager mod for Solasta: Crown of the Magister (Steam, Windows). Players enter physical dice faces and the original game code applies every rule. Current release: 0.8.4.2 (GitHub release `v0.8.4.2`).

The owner does not write code; everything here was built with Claude. Explain results in plain language and keep the README's player-facing tone.

## Layout

- `Source/src/`: runtime mod code. `Plugin.cs` is the entry point, `NativeSiteManifest.cs` lists the pinned game coroutine sites, `info.template.json` is the UMM metadata (the version lives here and in the README title).
- `Source/tests/<suite>/`: regression suites. Each `run.py` regenerates `results.txt` and `source-manifest.json`.
- `Source/tools/`: build (`Build.py`), regression (`RunRegression.py`), packaging (`Package.py`), release gate (`ValidateRelease.py`), Windows acceptance scripts. `BUILD-NOTES.md` has the details.
- `Source/audit/`: coverage, effects and multiplayer reviews.
- `README.md` (player docs and changelog), `TestResults.md` (verification notes per version), `CONTRIBUTING.md` (ground rules).
- `docs/nexus-page.md`: paste-ready Nexus Mods page text and upload steps.

## First-time setup on this computer

Do this once, at the start of the first session, and tell the owner what passed:

1. Check Python 3.9+ (`py --version` or `python --version`) and the .NET 8 SDK (`dotnet --list-sdks`). If the SDK is missing: `winget install Microsoft.DotNet.SDK.8`.
2. Find the game's Managed folder. Usually `C:\Program Files (x86)\Steam\steamapps\common\Slasta_COTM\Solasta_Data\Managed`; otherwise look in the Steam library folders listed in `C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf`.
3. Check the SHA-256 of `Assembly-CSharp.dll` in that folder is `a87e45cbd187e42e9d64a610850b39edba4896168b14d71ca71c5d32cf9e96ee` (`Get-FileHash`). If it differs, the game updated or was modified; stop and tell the owner.
4. From `Source`: `python tools/FetchDependencies.py` (downloads checksum-pinned UMM 0.32.4, Harmony 2.3.6 and Mono.Cecil).
5. From `Source`: `python tools/RunRegression.py --managed "<Managed folder>" --output build/regression --build build`. All 23 suites passed on Linux on 2026-10-08 (about 2 minutes). Output goes to `Source/build/` (git-ignored).

The test fixtures were developed and run on Linux only, so a suite may fail on Windows for an environment reason (for example the fixture host's `/proc/self/maps` adapter in `tools/HarmonyFixtureHost.cs`). If that happens, explain the failure to the owner first; never weaken or skip a test to get green. The owner does not want WSL installed. Two options to offer: make the test host work on Windows (a separate, tested change), or run the regression in a Claude Code cloud session with a zip of the Managed folder uploaded (`apt-get install dotnet-sdk-8.0` there; Microsoft's .NET download host is blocked in cloud sessions).

## Working rules

- Never commit game files, decompiled game code, logs or DLLs (`.gitignore` blocks the common cases).
- The game decides outcomes: the mod only replaces raw die faces. Replicated native logic is pinned by IL hash; a game update must be refused, not misread.
- Multiplayer must stay deterministic. Changes to when or how shared rolls run need tests in `Source/tests/network-*`.
- Tests come with changes: commit the regenerated `results.txt` and `source-manifest.json` of affected suites.
- Source manifests also record hashes of the .NET reference assemblies, which differ between SDK builds. A manifest that changes only in `net8-reference/*` entries after a run, with no real change, is environment noise: discard it (`git checkout -- <file>`) rather than commit it.
- Releases: bump the version in `Source/src/info.template.json`, the README title and "What's new", add a `TestResults.md` section, run the full regression, package with `Package.py` (see `BUILD-NOTES.md`), publish a GitHub release with the ZIP, then upload the same ZIP to Nexus (see `docs/nexus-page.md`).

## Open items and decisions (as of 2026-11-25)

- **Nexus Mods:** page text is ready in `docs/nexus-page.md`. Not yet confirmed whether the page has been published. Once it is, add its link to the README's "Players" note and the GitHub release notes.
- **Unfinished Business compatibility: tabled by the owner.** Findings from a 2026-10 comparison with UB 1.5.97.127 (same game version 1.5.97):
  - UB patches 46 of the 109 game methods this mod hooks, and fully replaces 20 of them with its own copies (attack, magic effect, magic attack and effects on positions, spend power, spell and power counterspell, shove, break free, climb, jump, reapply effect, gadget checks and saves, environment effects, failed-ability-check handling, condition interruptions, Bardic die, spell slots, level-up hit die). 27 of the 46 await sites sit in replaced code and would never run.
  - UB transpiles `RulesetActor.RollDie`, `RollDiceAndSum` and `ResolveContestCheck`, which `NativePatches.Preflight` refuses. Its `RollDie` adds Elven Accuracy (a third die drawn straight from `DeterministicRandom`) and `IModifyDiceRoll` hooks that change die type/advantage before and faces after a roll.
  - UB has its own dice calls (about 45 `RollDie` calls in 26 files), "alter outcome" reactions after rolls, and an optional single-player PCG random generator (`EnablePcgRandom`).
  - UB's replacement attack coroutine mirrors the native order (attack roll, save, apply effects), so sites could be mapped. Options considered: allow UB with partial coverage; full support pinned to one UB version (re-pin per UB release, UB DLL as a build input, UB version/settings in the multiplayer handshake); or contribute manual dice to UB itself.
