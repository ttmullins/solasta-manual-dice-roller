# Actual multiplayer acceptance

Multiplayer developer tests run the production packet protocol and transport with controlled gateways. The build also links against and inspects the supplied pristine Photon libraries. These checks do not connect two Windows game processes and do not certify actual Photon traffic, Unity Mono timing, ownership or native game synchronization.

Use separate test saves on at least two Windows machines. Every room peer must install the exact same candidate DLL, supported game, official UMM/Harmony and roll settings. Keep Unfinished Business and conflicting patches out of this supported test profile. Close Solasta before changing installations. Preserve the previous candidate and saves for rollback.

The runtime only permits a manual request after matching participants negotiate the session and all-peer barriers. An incompatible or missing peer is a blocked compatibility case. Do not bypass that guard or claim unsupported peers will silently remain synchronized. Follow the displayed recovery status for disconnection, roster/master changes and mismatches; establish a fresh matching session before resuming manual play when required.

This candidate uses wire protocol **2**. Handshake acknowledgements bind the current room/profile to a fresh nonce from every participant. Include old-bootstrap replay before current-host discovery in the stale-packet case and controller transfers both while idle and while dice are pending in the ownership case. A pending transfer must stop before the suspended native die is consumed; idle transfer must let the new controller supply the next action. Older protocol-1 readiness logs cannot qualify this build.

1. On each machine, run `Check-StandaloneSetup.ps1`, launch Solasta, enter the same private room and confirm the exact matching session becomes ready on every peer. Record the roster, native actor numbers, master, negotiated session and status. Retain actual logs containing `MDR_DIAGNOSTICS` and `MDR_NETWORK` markers. A session-start marker alone is insufficient evidence that all peers acknowledged readiness.
2. On each peer while that game process runs, use `Collect-WindowsAcceptance.ps1 -PeerName 'Host' -PeerRole host` (or the actual client's name/role), specifying the explicit runtime log. Use a new report directory for each distinct DLL/session. The collector does not search personal files, operate the game or pass cases.
3. Copy each report into a coordinated folder under distinct paths, such as `host/` and `client/`. Add the actual tester and session start to each `windows-acceptance.json`; the multiplayer validator checks their diagnostics but does not require the single-player checklist to be rerun on every participant. Separate complete single-player acceptance is still required for production qualification.
4. Create the pending coordinated checklist with the command below. Use the actual session identifier and native actor numbers from the runtime, not the placeholders. Complete every case in `multiplayer-acceptance-cases.json`. Each case needs all initial participants' observations and nonempty retained evidence. Keep failed and pending outcomes intact.
5. Fill each `recorded_rolls` entry with the actual action identity, sequence, descriptor signature, owner, raw faces and native result from every peer. Record native RNG draw counts and callback counts against a retained unmodified-game baseline. Screenshots of matching damage alone do not establish RNG parity or exactly-once native callbacks. When a diagnostic counter is unavailable, retain a suitable instrumented native baseline/runtime trace and document how the counts were observed; leave the case pending if evidence cannot establish them.
6. Recheck native state after manual rolls and coordinated Auto, then exercise the documented disconnect, rejoin and master-change recovery. Compare subsequent native rolls and next valid manual actions. A connection loss that leaves the game frozen is a failure even if the last submitted face appeared on both screens.
7. Return the whole coordinated folder, including the actual peer logs, diagnostics, observations and native baseline. Production validation checks exact artifact/game/loader/Photon identity, observed running Windows Mono processes, retained all-peer ready markers, session/roster agreement, every case and known-face/native baseline comparisons. It validates recorded evidence consistency; it does not establish that an observation is truthful or automate gameplay.

From the extracted `Source\tools` folder, after copying reports into `C:\SolastaTesting\mp-0.8.4.2\host` and `client`:

```powershell
.\New-MultiplayerAcceptance.ps1 `
    -OutputDirectory 'C:\SolastaTesting\mp-0.8.4.2' `
    -Tester 'Actual tester' -Session 'ACTUAL-NEGOTIATED-SESSION' `
    -SessionStartedUtc '2026-10-06T12:00:00Z' `
    -PeerReports @('host/windows-acceptance.json','client/windows-acceptance.json') `
    -PeerNames @('Actual host','Actual client') `
    -PeerRoles @('host','client') -PeerActors @(1,2)

# Example only: describe what every peer actually observed.
.\Record-WindowsCase.ps1 `
    -Report 'C:\SolastaTesting\mp-0.8.4.2\multiplayer-acceptance.json' `
    -Case mp_ownership -Status pass `
    -Observed 'Actual controller, prompt ownership and native outcomes observed on both peers.' `
    -Evidence @('observations/ownership.txt','host/screenshots/ownership.png','client/screenshots/ownership.png')
```

For developer/reviewer verification:

```sh
python tools/ValidateRelease.py --build build \
  --regressions build/regression/regression-report.json --level production \
  --windows /path/to/single-player/windows-acceptance.json \
  --multiplayer /path/to/coordinated/multiplayer-acceptance.json \
  --output build/production-gate.json
```

The tools and production DLL remain separate. PowerShell syntax is checked by the developer suite, but these scripts have not been executed inside an actual Windows/Solasta session here. A passing simulated transport suite qualifies the candidate for live testing; production and multiplayer qualification remain false until the actual evidence gates pass.

## Extended rolls (0.6.0.0)

With **Use these in multiplayer too** on for every player, also check: a dialogue check made by a hero controlled by the client; a lock or trap check; concentration after damage; Hold Person's end-of-turn save; a surprise attack from hiding and a hidden hero's end-of-turn Stealth; monster identification at the start and end of a battle; a world-map food search and random encounter; camp crafting or scribing; and a fall. Each must prompt only the character's controller, show the waiting message to the others, and continue with matching combat logs on every computer. Then turn the option off on every computer and confirm those rolls are made by the game without any multiplayer stop.

## Extended rolls (0.7.0.0)

With **Use these in multiplayer too** on for every player, also check: a save at the start of a turn to end a condition; Spirit Guardians or Moonbeam at the start of an enemy's turn and when a character walks into it; recurring burning or acid damage; Thunderwave or Ray of Frost damage; a hidden hero attacking and staying hidden; Deflect Missiles; spell failure and slot preservation if a character has such a feature; crafting while travelling; an Insight option in dialogue; and, with the optional settings on for every player, a save when damage ends a condition and a search for a hidden lever or trap. Each must prompt only the character's controller and continue with matching combat logs. Confirm that ability scores and level-up hit points are never asked for while connected.


## Extended rolls (0.8.0.0)

With **Use these in multiplayer too** and **Traps and hazards** on for every player, also check: an attack by a Blessed hero controlled by the client (the d4 follows the d20 and the hit matches on every computer); a trap that hits two heroes controlled by different players (each player rolls their own save, the damage is asked once); a lingering hazard such as a poison cloud; Flaming Sphere or a spiritual weapon moved by the client's caster (the caster's player is prompted); Dispel Magic against a higher-level spell; Cutting Words-style penalty dice; a wand or staff spending its last charge; a confused creature's behaviour die; and a successful and a failed food search while travelling (only the success asks for the harvest dice). With **Also ask for enemy and NPC dice** on for every player, confirm that a monster's recharge d6 and the creatures' ambush d20 are asked of the host only, that the clients see the waiting banner naming the host, and that the combat log matches. Open the roll history on each computer and confirm it lists the other players' rolls with their names. Finally, set **Traps and hazards** differently on one computer and confirm the room refuses the mismatched profile.

## Disagreements pause (0.8.1.0)

Replay a battle in which enemies attack after Sleep or another condition, with heroes of both players in reach (the live 0.8.0.0 failure). Every roll must complete without a "Mod Warning" message. If manual dice ever pause, confirm on every computer that: nobody left the room; the game kept playing with its own dice; a "Manual dice paused" notice appeared; the settings panel shows the reason; and loading a save together brings manual dice back (a new `session_ready` line in each log). Then deliberately set **Traps and hazards** differently on one computer before joining. The room must pause manual dice (no prompts) instead of disconnecting. Keep every player's `Player.log`: `roll_descriptor` lines with the same sequence can be compared part by part.

## Same dice, own state (0.8.2.0)

Play a fight with several enemies attacking (both live pauses so far were enemy attacks). Manual dice must not pause. If a "Games differ" notice appears, note whether the two screens really show different results (health, hits), and keep both logs: `roll_descriptor` lines name each game's attack next to its code, and `results_differ` lines mark the roll.


## Games stay in step (0.8.3.0)

Repeat the fight from the two live sessions: Sleep (or another condition) on enemies, then several enemy attacks against heroes of both players, with initiative at the start. While a player enters dice, both games must stand still together (characters stop moving on both screens) and continue together afterwards. Health, hits and the combat log must match on every computer after every attack, and no "Games differ" notice may appear. If one does, keep both `Player.log` files: each `roll_descriptor` line now belongs to the same game tick on every computer, so the first line whose parts differ marks where the games separated.

## Clearer prompts, no false alarms (0.8.4.0)

Play a fight in a custom campaign (the live sessions used one) with monsters whose attacks carry a saving throw, and walk a hero into a damaging trap. No "Games differ" notice may appear. Each saving throw prompt must name what it is against (for example "Constitution save against Giant Crow's Beak") and what a success changes ("On a success: half damage", "Blinded is avoided", "Piercing damage applies either way"). A concentration check must name the spell kept and the damage taken. A trap's damage prompt, after its saving throw, must say whether the game will halve that damage.

## Mismatch reported plainly (0.8.4.1)

Before joining, set one roll type differently on the client (or keep an older build there). Joining must show "MANUAL DICE OFF: versions or roll settings differ" once on each computer, not again at every reload, and the settings panel must explain what to match. Both logs must name the mismatch in their `halted` line (the host's as "Another player's game stopped manual dice: Manual dice profiles or room rosters do not match."), and the detecting computer's `detail` must list both profiles.

## No differences from tooltips (0.8.4.2)

Hover over enemy attacks and spells on one computer only during a fight, then let those enemies attack. No "Games differ" notice and no `results_differ` line may appear.
