# Contributing

Thanks for helping. Bug reports from real games are as valuable as code.

## Reporting a problem

Open an issue with:

- the mod version (the line `[ManualDiceRoller] Version '…'` near the top of `Player.log`);
- single-player or multiplayer, and what you were doing;
- for multiplayer, the `Player.log` of **every** player from the same session. The `MDR_NETWORK` lines let us compare what each computer did, roll by roll.

`Player.log` is in `%USERPROFILE%\AppData\LocalLow\Tactical Adventures\Solasta\`. Check it for anything you would rather not share before attaching it.

## Building

You need Python 3.9+, the .NET 8 SDK and your own copy of Solasta. The build checks the game's `Assembly-CSharp.dll` against the supported version (see `README.md`). From the `Source` folder:

```sh
python tools/FetchDependencies.py
python tools/RunRegression.py --managed "/path/to/Solasta/Solasta_Data/Managed" \
  --output build/regression --build build
```

`FetchDependencies.py` downloads checksum-pinned Unity Mod Manager, Harmony and Mono.Cecil packages. The game's own DLLs are read from your installation and are never copied into the repository. `Source/tools/BUILD-NOTES.md` has the details, including packaging (`Package.py`) and the release gate (`ValidateRelease.py`).

To iterate on part of the code, run selected suites with `--suite <name>` (for example `--suite network-protocol`). Before a pull request, run the full regression: every suite must pass.

## Ground rules

- **No game files.** Do not commit Solasta's DLLs, decompiled game source, assets or logs. `.gitignore` blocks the common cases.
- **The game decides outcomes.** The mod only replaces raw die faces and lets the original game code apply every rule. New roll sites follow the existing planners, and replicated native logic is pinned by IL hash so a game update is refused rather than misread.
- **Multiplayer must stay deterministic.** Anything that changes when or how a shared roll runs needs a test in `tests/network-*` and, ideally, a live two-player check (`Source/tools/MULTIPLAYER-ACCEPTANCE.md`).
- **Tests come with changes.** Each suite's `run.py` regenerates its `results.txt` and `source-manifest.json`; commit those along with your change.
- Releases are built with `Package.py` and published as GitHub Releases, not committed.

By contributing you agree that your contribution is licensed under the MIT License in `LICENSE`.
