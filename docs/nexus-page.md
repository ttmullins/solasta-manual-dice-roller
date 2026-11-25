# Nexus Mods page: paste-ready text

Copy each block into the matching field. Nexus descriptions use BBCode, so the description block is BBCode, not Markdown. Update the version numbers, file name and SHA-256 for each new release.

## Mod name

```
Manual Dice Roller
```

## Version

```
0.8.4.2
```

## Brief overview (summary)

```
Roll real dice in Solasta. When the game would roll for your party, a window asks for the faces you rolled, and the game applies every bonus, advantage, feature and outcome itself. Works in co-op. Requires Unity Mod Manager. Not compatible with Unfinished Business.
```

## Detailed description (BBCode)

```
[size=5][b]Manual Dice Roller[/b][/size]

Roll your own physical dice in Solasta. When the game would roll for one of your characters, a window asks for the face you rolled. The game then applies all of its own rules (modifiers, advantage, features, saves, damage) exactly as it would with its own dice. The mod only replaces the raw die faces.

[b][color=#e06666]Please read first[/color][/b]
[list]
[*][b]Made with AI.[/b] The code, tests and documentation were written with an AI assistant (Anthropic's Claude), working from my requests and from problems we hit while playing.
[*][b]Limited testing.[/b] Tested in single-player and in a few co-op sessions with one other player. Many rolls, spells and situations have never been tried in a real game. Expect bugs and back up your saves.
[*][b]Not compatible with Unfinished Business.[/b] The mod refuses to run while Unfinished Business is loaded. Other mods have not been tested.
[*][b]No promise of updates.[/b] It's MIT licensed: fork it, fix it, change it or republish it.
[/list]

[line]

[size=4][b]Requirements[/b][/size]
[list]
[*]Solasta: Crown of the Magister, current Steam version for Windows. The mod checks the game's files and won't load on a different game version or a modified game.
[*][url=https://github.com/newman55/unity-mod-manager]Unity Mod Manager 0.32.4[/url], set up for Solasta (it includes Harmony 2.3.6.0).
[/list]

[size=4][b]Install or update[/b][/size]
[list=1]
[*]Close Solasta and back up your saves.
[*]If an older ManualRoll build replaced the game's Assembly-CSharp.dll, restore the original first (Steam: verify the game files).
[*]Install Unity Mod Manager 0.32.4 for Solasta.
[*]Drag this ZIP into Unity Mod Manager's [b]Mods[/b] tab (or extract the ManualDiceRoller folder into the game's Mods folder).
[*]Start Solasta, check that [b]Manual Dice Roller[/b] is enabled, and pick which rolls you want to enter by hand in its settings.
[/list]
Always close Solasta before updating or removing the mod.

[line]

[size=4][b]How it works[/b][/size]
[list]
[*]Enter each raw die face, with no modifiers or totals. Advantage and disadvantage ask for both dice.
[*]Reroll features such as Halfling Lucky ask for the replacement die separately.
[*][b]Enter[/b] confirms. [b]Escape[/b] (or [i]Let the game roll[/i]) lets the game roll that step instead.
[*]Drag the window by its header. The [b]• • •[/b] menu changes its size, re-centres it, opens the roll history and turns game-roll notices on or off.
[*]Presets: Everything manual, Party dice (default), Combat only and Minimal.
[*]Your party (including Wild Shape forms) is included by default. Enemy and NPC dice are optional, so you can also roll as the DM.
[/list]

[size=4][b]Rolls you can enter by hand[/b][/size]
[list]
[*]Weapon and spell attacks, including Bless/Bane-style bonus dice
[*]Saving throws, ability checks and shove contests
[*]Damage, healing and temporary hit points, including critical hits and area spells
[*]Initiative, death saves and concentration checks
[*]Bardic Inspiration, Deflect Missiles, Dispel Magic, spell failure and other class features
[*]Sleep and similar hit-point pools
[*]Hit dice when resting
[*]Ability checks in dialogue (the conversation waits for you)
[*]Locks, levers, traps and other interactive objects; trap and hazard saves and damage
[*]Start-of-turn and end-of-turn saves, lingering spell areas and recurring damage
[*]Surprise and Stealth
[*]Monster identification, travel (food, gathering, crafting, ambush) and camp crafting
[*]Falling damage
[*]Single-player only: ability scores at character creation and, optionally, level-up hit points
[/list]
A few rolls still use the game's dice, such as some saves triggered during the start of a turn, random condition durations and random selections. Loot, encounter tables and AI choices stay with the game. The full list is in the README on GitHub.

[line]

[size=4][b]Multiplayer[/b][/size]
[list]
[*][b]Every player needs this exact version and the same roll settings[/b], set before joining. If they differ, manual dice switch off and the game rolls (you'll see "MANUAL DICE OFF").
[*]Each player enters the dice for the characters they control. The host enters enemy and NPC dice when that option is on.
[*]While someone is entering dice, every player's game pauses together and resumes together.
[*]Leave the room before changing roll settings.
[/list]

[line]

[size=4][b]Found a problem?[/b][/size]
Please report it on GitHub rather than in the comments here, so it can be tracked:
[url=https://github.com/ttmullins/solasta-manual-dice-roller/issues]GitHub issues[/url]

Include:
[list]
[*]the mod version (the line [i]Version '…'[/i] near the top of Player.log);
[*]single-player or multiplayer, and what you were doing;
[*]your Player.log, from %USERPROFILE%\AppData\LocalLow\Tactical Adventures\Solasta\ (check it for anything you'd rather not share). For multiplayer, attach every player's log from the same session.
[/list]

[size=4][b]Source code and licence[/b][/size]
Source, tests and full documentation: [url=https://github.com/ttmullins/solasta-manual-dice-roller]github.com/ttmullins/solasta-manual-dice-roller[/url]
MIT licensed. Solasta and its files belong to Tactical Adventures and are not included in this download.
```

## File upload

| Field | Value |
| --- | --- |
| File | `ManualDiceRoller-Standalone-0.8.4.2.zip` (download it from the GitHub release; SHA-256 `8d71505a45603b77a550a3f394566a215d9aca66e22f949fbb1f705c97a88d4e`) |
| File name | `Manual Dice Roller 0.8.4.2` |
| Version | `0.8.4.2` |
| Category | Main files |
| Latest version / update the mod's version | Yes |

File description:

```
Install with Unity Mod Manager 0.32.4 (drag the ZIP into its Mods tab). Every player in a co-op game needs this same version and the same roll settings.
```

## Requirements

Unity Mod Manager. If Nexus finds it when you search, add it as a Nexus requirement; otherwise add it as an off-site requirement:

- Name: `Unity Mod Manager 0.32.4`
- Link: `https://github.com/newman55/unity-mod-manager`
- Notes: `Set it up for Solasta. It includes the Harmony version the mod needs.`

## Permissions and credits

The MIT licence already lets anyone reupload, modify or reuse the mod, provided they keep the licence notice. Set the Nexus permissions to match: allow uploading elsewhere, modification, asset use and conversion, all with credit. Commercial use is also allowed under MIT, so allow it unless you'd rather not say so on Nexus (the licence still applies).

Credits / permission notes:

```
Made with an AI assistant (Anthropic's Claude). MIT licensed: you may reupload, modify or reuse it as long as you keep the licence notice (LICENSE in the source). Source: https://github.com/ttmullins/solasta-manual-dice-roller
Unity Mod Manager by newman55. Harmony by Andreas Pardeike. Solasta and its files belong to Tactical Adventures and are not included.
```

## AI content tag

Nexus requires generative-AI tagging. The mod's code was generated with AI, so choose the tier for AI-generated code or in-mod content. Pages that are tagged wrong get moderated.

## Sticky post (optional)

```
Thanks for trying Manual Dice Roller!

Bug reports: please open an issue on GitHub (https://github.com/ttmullins/solasta-manual-dice-roller/issues) with the mod version and your Player.log. For multiplayer, attach every player's log from the same session. I may not see reports left here.

Not compatible with Unfinished Business: the mod refuses to run while it is loaded.

Co-op: everyone needs the same version and the same roll settings, set before joining.
```

## Upload steps

1. Log in to Nexus, open the Solasta: Crown of the Magister section and click Upload (or "Add a mod").
2. Mod details: name, version, closest category (Gameplay, else Utilities or Miscellaneous), summary, description, AI tag, adult content: no.
3. Media: 2–3 screenshots (the dice prompt in combat, the settings panel, the roll history); make the dice prompt the main image.
4. Files: upload the ZIP as a Main file and mark it as the latest version.
5. Requirements: Unity Mod Manager, as above.
6. Permissions and credits, as above.
7. Optional: turn off the Bugs tab and post the sticky note.
8. Publish (the page is hidden until then), then download your own Nexus file and install it through UMM once to check it.

For each new release, upload the new ZIP as the Main file and move the old one to Old files. Co-op players need matching versions, so keep Nexus and GitHub versions identical.
