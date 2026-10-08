# Manual Dice Roller standalone 0.8.4.2

> **Players:** download the latest `ManualDiceRoller-Standalone-*.zip` from this repository's **Releases** page and follow [Install or update](#install-or-update). Every player in a multiplayer game needs the same version and the same roll types.
>
> **Developers:** the mod's source is in `Source/src` and its tests are in `Source/tests`. See [CONTRIBUTING.md](CONTRIBUTING.md) for how to build and test against your own copy of the game. MIT licensed; Solasta and its files belong to Tactical Adventures and are not included.

## Please read first

- **Made entirely with AI.** I did not write this code myself. Everything here was created with an AI assistant (Anthropic's Claude), working from my requests and from the problems we hit while playing: the mod, the tests, the tools and this documentation.
- **Limited testing.** I have only tested it in a few co-op sessions with one other player. Many rolls, spells and situations have never been tried in a real game. Expect bugs, and keep backups of your saves.
- **No promise of updates.** I don't know how much time I will put into this. Issues and pull requests are welcome, but I may answer slowly or not at all.
- **Take it and make it yours.** The code is MIT licensed: fork it, change it, fix it, republish it or build something new from it. You don't need to ask.

A standalone Unity Mod Manager mod for the supplied Steam Windows build of Solasta. Enter physical dice faces; the original game applies modifiers, features, outcomes, damage, healing and spell target selection. No replacement game DLL and no GoDice support.

**Manual multiplayer is implemented in this build. Every participant must install the same build and select matching roll types before joining.** Beyond its automated tests it has only been played in a few two-player sessions (see above), so please try it on a spare save first.

## What's new in 0.8.4.2

**The last false "games differ" report is gone.** In the fifth live session, once both players had the same roll settings, all 264 shared rolls matched on both computers in random state, characters and results. Three rolls (a Giant Crow's talons and a spell's saving throw) differed only in a tooltip breakdown of the damage bonus. The game reuses damage records from a pool and does not clear that breakdown when copying, so each computer kept whatever an earlier local use (such as a hover tooltip) had left there. The game never uses it for a result, and the mod no longer compares it.

Includes everything in 0.8.4.1. Every player must update to 0.8.4.2 and choose the same roll types.

## What's new in 0.8.4.1

**A mismatch between players is now reported plainly, once.** Manual dice only work when every player has the same mod version, the mod switched on and the same roll types. If they differ, the computer that notices used to stop manual dice and tell the others only that "a room member stopped" them. The host then showed "Manual dice paused" again at every load, with no reason given. Now:

- every computer names the reason ("Another player's game stopped manual dice: …" when it came from someone else), for this and every other known reason;
- a mismatch shows **"MANUAL DICE OFF: versions or roll settings differ"**, once rather than at every reload, and the settings panel says what to match;
- the computer that notices logs both players' profiles.

To fix a mismatch, make sure everyone has the same version (the version line is at the top of each player's `Player.log`, for example `Version '0.8.4.1'`). Then leave the room, set the same roll types on every computer, and join again.

## What's new in 0.8.4.0

**No more false "games differ" reports.** In the third live session, all 223 shared rolls matched on both computers: the same dice, the same random state, the same health before and after. The eight reported differences came from the custom campaign. It gives its own monsters, items and conditions a new random ID on each computer when it loads, and the mod was comparing that ID. The game itself identifies them between players by name, and now the mod does too. The "different attack" seen in the two earlier sessions, which were in the same campaign, was most likely this same ID. The 0.8.3.0 tick freeze still stays: it closes a real gap.

**Prompts say what you are rolling for and what it changes.**

- **Saving throws** name what they are against and what a success does. For example: "Constitution save against Giant Crow's Beak. On a success: Blinded is avoided. Piercing damage applies either way." A save often protects against only part of an attack or spell. That is why a successful save can still leave you taking damage.
- **Trap damage** asked after a saving throw says whether the game will halve it ("Gimli made the saving throw: the game halves this damage").
- **Concentration checks** name the spell you are keeping and the damage you took. They also explain that the damage is already taken either way.
- **Attack rolls** name the weapon and the target.
- **Damage rolls** name the target. They also say when the game will halve the total (a successful save, or half damage on a miss).
- **Death saving throws and initiative** explain what the roll does.

Every player must update to 0.8.4.0 and choose the same roll types.

## What's new in 0.8.3.0

**The games now stay in step.** 0.8.2.0 stopped treating a difference between the two games as fatal, but the games shouldn't differ in the first place. The cause turned out to be the mod itself.

Solasta keeps multiplayer games identical by running every computer's game in the same small steps, called ticks (20 a second), and scheduling every action for a particular tick. While a player entered dice, the mod held that action back, but the ticks kept counting. Each computer let the action go at a slightly different tick, so the next enemy action was worked out on games that were no longer at the same point. That is how one computer ended up with a different attack for the same enemy.

- **Time stands still while a shared roll is decided.** When a roll needs agreement, the mod stops the game's tick count on every computer at the same tick. The players decide the roll, and the game carries on from the next tick on every computer. While someone is entering dice, both games pause together (characters stop moving on every screen) and resume together.
- Each shared roll still needs a quick network round trip, now with the game paused, so busy fights may show very short pauses.
- A roll the game would make while another player's roll is still being decided (rare: a concentration save at that exact moment) uses the game's dice rather than waiting behind it.
- The "Games differ" check from 0.8.2.0 stays, as a safety net. It should now stay quiet.

Every player must update to 0.8.3.0 and choose the same roll types.

## What's new in 0.8.2.0

**The dice agreed; the games didn't.** In the second live test, manual dice paused on an enemy's first attack. The new logs show that both computers had the same random state, the same attacker and target (health, attributes and conditions) and the same bonuses. The only difference was *which attack* the enemy was making: each copy of the game had a different attack mode for it. That is the game's own multiplayer data, not the dice.

So the mod no longer double-checks the game's own data:

- **Players agree on who rolls which dice.** Before a roll, every computer checks only the roll itself (the place in the game, the characters taking part) and the dice to enter (whose they are, which dice, advantage and order). Each game then rolls with its own state, as it would without the mod.
- **Different results are reported, never fatal.** After each roll the games still compare their results. If they differ, the log records it and, for a roll where someone entered dice, a "Games differ" notice appears once per session. Manual dice carry on. If your screens disagree about something, save and load together.
- **Manual dice pause only when they can't work.** That means different builds or settings, a player leaving during a roll, or a character changing hands mid-roll. Two computers rolling for different characters at the same moment also pauses. If the dice to enter differ, the game makes that one roll.
- The log now also names the attack each game is using next to its code, so a difference like this one is readable at a glance.

Every player must update to 0.8.2.0 and choose the same roll types.

## What's new in 0.8.1.0

**Multiplayer no longer kicks you out.** In the first live two-player test, a wolf's attack stopped manual dice, the mod left the room, and Solasta showed "Mod Warning! … instability". Both players' logs showed the same roll at the same moment, described differently on the two computers. That has three fixes:

- **The real cause.** When one computer can't confirm an attack itself yet, the game rolls with the attack bonuses the other computer sent. That network copy drops some details, such as where an advantage came from and the Bless die's type. The mod compared those details too. It now compares only what the game itself keeps in step between players, so this no longer counts as a difference. Bless and Bane dice are found the same way on every computer.
- **A different local plan is not an error.** If two computers prepare the same roll differently, but the roll itself is the same, everyone lets the game roll that one roll. Play continues without a pause.
- **If the games really disagree, nobody is kicked.** Manual dice pause for the session, the game rolls everything, and a notice says "Manual dice paused". The mod no longer leaves the room or raises an error inside the game, so the "Mod Warning" box no longer appears. Save, then load the save together (or rejoin), and manual dice switch back on.

The log now lists the parts of every shared roll (random state, each character's health, attributes and conditions, and each other value) as short codes. If two players' rolls ever differ again, comparing both logs shows exactly which part differed.

Every player must update to 0.8.1.0 and choose the same roll types.

## What's new in 0.8.0.0

**The last tabletop dice are manual.** Every die a player or DM would roll at the table now asks for your faces. Loot tables, which random encounter appears, enemy AI choices and cosmetic randomness stay with the game. Everything below also works in multiplayer with **Use these in multiplayer too**. The setting that controls each roll is shown in brackets.

- **Bless, Bane and similar dice on attacks:** the d4 is asked right after the attack's d20, and the game uses your face instead of the one it rolled earlier. The d20 prompt shows the bonus without that die. (**Attack rolls**)
- **Traps and hazards:** each affected hero's saving throw against a trap, and the trap's damage dice. A trap whose blast hits several heroes asks for its damage once, because the game shares that roll. Lingering hazards (fire, poison clouds) work the same way. (**Traps and hazards**, new, on by default)
- **Spell effects that act on their own:** Flaming Sphere and similar effects now roll as their caster. In multiplayer the caster's player rolls them. (the matching attack and damage settings)
- **Dispel Magic:** the d20 against each spell above the automatic level. You aren't asked when every spell would end anyway. (**Deflect Missiles, spell failure and slot dice**)
- **A bard's penalty die:** when a Cutting Words-style condition lands on a foe, the bard rolls the inspiration die. (**Deflect Missiles, spell failure and slot dice**)
- **A wand's last charge:** the d20 that decides whether an item that crumbles on a 1 survives. (**Deflect Missiles, spell failure and slot dice**)
- **Confusion and similar conditions:** the random behaviour die at the start of the creature's turn, with the result table shown in the prompt. (**Repeat saves at the start and end of a turn**)
- **Food found:** after a successful food search on the world map, the harvest dice are asked as well. A failed search asks nothing more. (**Travel foraging, crafting and scribing**)
- **Noisy objects:** the Stealth checks when a lever, trap or creaking floor can give hidden heroes away. (**Stealth checks**)
- **With "Also ask for enemy and NPC dice" on,** you also roll as the DM. That covers a monster's power recharge d6 at the start of its turn and the creatures' d20 against your lookout when an encounter starts on the world map. In multiplayer the host enters the DM's dice.

**A clearer roll window.** Each kind of roll has its own colour and badge: green for healing, red for damage, blue for saves, amber for checks, gold for attacks and so on. Text is larger, and a running total shows for several dice.

**Roll history.** Open it from the • • • menu or the settings. It lists the last 40 rolls, newest first: your faces, rolls you left to the game, the game's own d20s for your party and, in multiplayer, other players' rolls with their names.

**Multiplayer waiting banner.** While someone else rolls, a banner names the player, the character and the roll.

**Tidier settings.** Four presets: Everything manual, Party dice (the defaults), Combat only and Minimal. Sections fold away and show how many types are on, and each toggle has a one-line description.

Each build only plays with the same build in multiplayer: every player must update to the same version and choose the same roll types.

## What's new in 0.7.0.0

**Almost every remaining roll is now manual.** All of them except the character screens also work in multiplayer with **Use these in multiplayer too**. The setting that controls each one is shown in brackets.

On by default:

- **Start-of-turn saves** — the saves the game rolls at the start of a character's turn to end a condition. The whole start of the turn now waits for your dice, so the game still decides correctly whether the character can act. (**Repeat saves at the start and end of a turn**)
- **Lingering spell and condition damage** — Spirit Guardians, Moonbeam, Cloud of Daggers and similar areas when a creature starts its turn in them or moves into them, plus burning, acid and other recurring condition damage, with the save that goes with it. The save and every damage die are asked for together; dice for damage a successful save cancels are simply not used. (**Lingering spell and condition damage**)
- **Damage for spells with extra effects** — spells that deal damage and also push, knock prone or apply a condition (for example Thunderwave's push or Ray of Frost's slow) now ask for their damage dice too. (**Damage dice**)
- **Stealth checks** — a hidden hero's Stealth after an action that can give them away (as well as at the end of their turn). The enemies' Perception against it is asked for when **Also ask for enemy and NPC dice** is on. (**Stealth checks**)
- **Deflect Missiles, spell failure and slot dice** — the Deflect Missiles reduction die, the d20 when a feature can make your casting fail, and the d20 to keep a spent spell slot. (**Deflect Missiles, spell failure and slot dice**)
- **Crafting while travelling** — the crafter's check during world-map travel. (**Travel foraging, crafting and scribing**)
- **Insight in dialogue** — the Wisdom (Insight) roll that shows the odds of a dialogue option. (**Ability and skill checks**)
- **Ability scores at character creation** (single-player) — six prompts of 4d6; the game drops the lowest die of each and sorts the totals as usual. The reroll button asks again. (**Ability scores at character creation**)

Under the new **Optional rolls** section (off by default, because they change how the game plays):

- **Saves when taking damage ends a condition** — the save the game rolls when damage can end a condition on the creature that was hit.
- **Perception for hidden objects and traps** — the active Perception search for hidden levers, doors and traps. A prompt tells you something is there, which the game normally keeps secret.
- **Roll hit points when levelling up** (single-player) — the game always takes the class die's average; with this on you roll the hit die instead. **Let the game roll** keeps the average.

## What's new in 0.6.0.0

**The extra roll types now work in multiplayer.** Dialogue checks, lock/lever/trap checks and saves, concentration, monster identification, travel foraging, camp crafting and scribing, and every roll type added below now ask the controlling player for their dice in multiplayer as well. Each of these rolls pauses every player's game at the same point, the character's controller enters the dice, every computer checks that it got the same result, and then the game continues. This needs **Use these in multiplayer too** (on by default) and the same build and roll types on every computer. If manual dice ever pause, load the save together to turn them back on, or turn that option off on every computer to fall back to the game's dice for these rolls.

**More rolls are manual (single-player and multiplayer):**

- **Repeat saving throws at the end of a turn** — for example the Wisdom save to break free of Hold Person. Saves at the start of a turn stay with the game, because it decides straight away whether the character can act.
- **Surprise attacks** — when a hidden hero starts a fight with a surprise attack, their Dexterity (Stealth) roll against the enemies' Perception.
- **Staying hidden** — a hidden hero's Dexterity (Stealth) check at the end of their battle turn.
- **Ambush checks while travelling** — the d20 for your best lookout when a random encounter happens on the world map.
- **Falling damage** — the d6s for a character who falls, with any reduction shown.

**Fixes.** A `Settings.xml` left empty by 0.5.0.0 is replaced with defaults instead of being reported as unreadable at every start. The settings panel now has a **More roll types** section with a toggle for each new type.

## What's new in 0.5.1.0

- **Monster identification is manual (single-player).** When the party first sees a new kind of creature, and again after a victory, the game rolls the party's best Wisdom (Survival) to fill in the bestiary. That roll now asks for your d20 ("Monster identification", DC 10 + the creature's challenge rating). An unidentified creature stays "Unknown creature" in the window, as in the game. It can be switched off under **More roll types**.
- **Settings now save.** Earlier builds could never write `Settings.xml` (the log showed "Can't save … both use the XML type name 'ModSettings'"), so window position, size and toggles reset every launch. Fixed.
- **The • • • button works.** It opens a row of window options in place of the keyboard hint: **Smaller**, **Larger**, **Re-center** and **Notices on/off**. Click it again to close.
- **Clearer headings.** Prompts now say what the roll is for: Dialogue check, Foraging, Gathering ingredients, Crafting (with the item), Scribing a spell (with the spell) and Monster identification. Initiative shows your initiative bonus, and death saves show DC 10.

## What's new in 0.5.0.0

**More rolls are manual (single-player).** Several rolls that the game makes in one synchronous step, with nothing for the mod to pause, used to be rolled automatically without a prompt. They now pause the game and ask for your dice:

- **Travel foraging** — the party's Wisdom (Survival) food search and ingredient gathering on the world map. The world-map clock waits while you roll.
- **Dialogue checks** — Persuasion, Intimidation, Deception, Insight and other checks offered in conversations (forced/automatic successes stay native).
- **Interactive objects** — ability checks and saving throws triggered by levers, locks, traps and other gadgets (the Perception path stays automatic).
- **Concentration checks** after a concentrating character takes damage.
- **Camp crafting and spell scribing** — the final check when a crafting or scribing activity completes during a rest.

(Since 0.6.0.0 these also work in multiplayer.) They can be switched off under **More roll types** in the mod settings.

**A new roll window.** The window was redesigned to sit with Solasta's own interface: a dark rounded panel with gold accents, the game's own fonts where available, a die-shaped slot for each die and a clickable keypad of faces. It shows what is being rolled and why — the localized skill or saving throw name, the roller, and chips for advantage/disadvantage, the DC or target AC, the bonus the game will add and the face you need. Advantage rolls explain that the game keeps the higher or lower die, and multi-die rolls show a running total.

Keyboard: type a face (it moves to the next die by itself), **Tab**/**arrow keys** move between dice, **Up/Down** step a value, **Backspace** clears, **Enter** confirms and **Esc** lets the game roll. On a d10 or d100 a lone 0 means 10 or 100.

**Game-roll notices.** When the game still rolls a d20 for one of your heroes (for example a path the mod cannot pause), a short notice in the corner says what was rolled, so nothing happens silently. This can be turned off in settings.

**Stability.** Synchronous game loops that would never let a pause finish are detected, and the mod hands those rolls to the game instead of waiting. Deferred rolls are replayed with the game's dice if the mod is disabled, a planning step fails or you choose "Let the game roll"; they are dropped cleanly when the location changes. The settings panel is grouped into Roll types, Single-player extras and Roll window.

## Install or update

1. Close Solasta and keep a backup of a test save and your previous mod ZIP.
2. Remove the old ManualRoll installation. If an earlier build replaced `Solasta_Data/Managed/Assembly-CSharp.dll`, restore the clean backup or verify Solasta's installed files through Steam before installing UMM.
3. Install official **Unity Mod Manager 0.32.4**, using its Solasta configuration. Its bundled Harmony must be **2.3.6.0**.
4. Install this ZIP using UMM's **Mods** tab. Extraction is optional; for manual installation put the single `ManualDiceRoller` folder under the game's `Mods` folder, with `Info.json` and `ManualDiceRoller.dll` directly inside it.
5. Start Solasta and verify **Manual Dice Roller** is enabled with the version shown at the top of this file. Leave Unfinished Business disabled and test single-player first.

Official loader: https://github.com/newman55/unity-mod-manager

The build verifies the exact supplied original game and Photon assemblies. A different game update, modified main game DLL or networking DLL is refused before installation. Supported `Assembly-CSharp.dll` SHA-256:

```
a87e45cbd187e42e9d64a610850b39edba4896168b14d71ca71c5d32cf9e96ee
```

This ZIP supplies only the mod DLL, metadata, source, documentation and verification evidence. Do not copy its DLL into the game's Managed folder. UMM/Harmony and original game DLLs are not bundled. The read-only setup checker is `Source/tools/Check-StandaloneSetup.ps1`.

## Manual input

- Enter each raw die face, without modifiers or combined totals. Advantage and disadvantage request both faces.
- Native reroll features, including Halfling Lucky, request their replacement faces separately. The original game determines which face and feature effects apply.
- Sleep's 5d8 pool requests five values from 1 to 8. Five 8s produce pool 40; 40 is not a valid individual d8 face.
- **Enter** (or **Confirm roll**) confirms. **Escape** (or **Let the game roll**) discards the current native step's collected inputs and rolls that entire step automatically, including earlier accepted dice in the step.
- Drag the header to move the window. The header's • • • button changes the size, re-centers the window and turns game-roll notices on or off; the same options are in the UMM settings. Party heroes and stock Wild Shape forms are included by default; enemy/NPC prompts are optional.
- The owning modal captures game input through its release frame. Old backslash/debug hotkeys are absent.

## Multiplayer

Install this exact ZIP on **every host and client**, enable it before connecting, and choose the same roll types on every machine. Roll types are locked while connected; leave the room before changing them or toggling the mod. Window position and size are local preferences.

The character's native controlling player enters its dice. The room host enters optional enemy/NPC dice and dice for native AI-controlled heroes. Substituted heroes retain their original hero's controller. An Auto request also goes through the same all-peer agreement.

Finish any pending roll before transferring character control. A controller change during a prompt or between input and native execution pauses manual dice for the session (the game rolls that die); old callbacks cannot submit that character's dice. Transfer control while idle and let the next action establish its current owner.

Rolls the game makes in one synchronous step (concentration, repeat saves, the start of a turn, lingering damage, saves on taking damage, monster identification, travel and camp checks) are handled the same way when **Use these in multiplayer too** is on: every computer skips the same call at the same point, holds all other game actions, the battle state machine, the world-map clock and the game's ticks, and then replays the original call inside one shared step with the agreed faces. Dialogue, gadget, surprise, stealth, spell failure and slot preservation checks wait in their own game routine like the other registered steps. Character-screen rolls (ability scores, level-up hit points) are single-player only. Dice the DM would roll (with enemy dice on) are entered by the room host. A spell's effect proxy belongs to its caster's player. A die that is only needed after a success (food found) is asked for, or skipped, the same way on every computer.

Every registered native step participates, including steps with no manual dice or unsupported effects. A local readiness flag cannot let one machine roll automatically while another waits. The protocol checks the build, loader, networking profile, roster, the roll's identity (site, native method and the characters taking part), its owner, the dice to enter and the ordered raw faces. It does not compare each game's own state before a roll: each game rolls with its own state, as without the mod. Game definitions (attacks, items, conditions) are identified by name, as the game sends them between players; a custom campaign's per-computer IDs are ignored. Inputs are released only after all peers acknowledge the committed decision.

The game advances in lockstep ticks; every shared roll stops the tick count on every computer at the tick that reached it, is decided between ticks, and runs at the next tick on every computer, so each game reaches the same point however long a player takes. Ticks resume only where the game itself decides to run a tick, never between two. Other native action coroutines wait during the owned step. Accepted manual draws still advance the original deterministic random stream with the original die, advantage and alteration parameters. Before another coroutine advances, peers finish the step together and compare the original method's return value and output parameters, relevant actor state, native draw consumption and the resulting stored game random state. A difference is logged (and noticed once per session after a roll with entered dice) but does not stop anything; it is the game's own synchronization. This checks the registered step; it is not a checksum of the entire game world. The banter updater waits during the step. A game-state random draw inside a game tick is part of the lockstep and is allowed; one outside the ticks while a roll is held pauses manual dice and lets the game carry on. These guards preserve the shared draw order and avoid owner-only rest bookkeeping.

If two computers prepare different dice for the same roll, every computer lets the game roll it and play continues. A missing/different mod, invalid owner, changed settings, two computers at different rolls (different characters at the same moment), an active-roll disconnect/host change, a canceled native action, stalled synchronization or a transport failure **pauses** manual multiplayer dice instead: every player stays in the room, nothing is held, the pending roll and every later roll use the game's dice, and a "Manual dice paused" notice appears (the settings panel shows the reason). The mod never leaves the room or throws an error into the game. If the games really did go out of step, the host should save and everyone should load that save together; loading a location together, or a change of players in the room, starts a fresh manual-dice session. Seamless continuation of a roll across host migration is not supported.

Multiplayer ownership, timing, UI, native scheduling and reconnect behavior still need the actual multi-machine tests in `Source/tools/MULTIPLAYER-ACCEPTANCE.md`. Pure simulations do not establish those live results.

## Coverage

| Native path | Behavior |
| --- | --- |
| Weapon and spell attacks | Manual at registered action sites, including the attack's Bless/Bane-style bonus dice; predefined results remain native. Effect proxies (Flaming Sphere and similar) roll as their caster. |
| Initiative and death saves | Manual at registered battle sites; forced/cached initiative remains native. |
| Action ability checks | Registered jump, climb, break-free, sleight-of-hand, stabilize and Counterspell checks; passive checks stay native. |
| Shove contests | Both original contest dice at registered shove/spell boundaries when skill selection can be previewed without random modifier bonuses. |
| Saving throws | Registered attacks, spells, powers and Indomitable/Borrowed Luck retry sites, with read-only original calculation preview. |
| Bardic Inspiration | Manual dice after accepted attack, failed-save and failed-ability reactions, including native advantage. Forced maximum remains native. |
| Damage/healing/temporary HP | Supported deterministic batches whose dice forms are of one kind, including spells that also apply conditions, push or knock prone; native dice counts, critical dice, healing bonuses and shared area damage are preserved. |
| Sleep and similar HP pools | Raw dice before original pool target filtering. |
| Rest hit dice | Original local rest callback in single-player; replicated native rest command in multiplayer. Native spending, healing, advantage, bonuses and the initiating player's callback are preserved. |
| Ability checks in dialogue | The dialogue pauses before the check, and before the Insight roll that shows an option's odds. Forced and automatic successes stay native. |
| Interactive objects (gadgets) | Ability checks and saving throws from levers, locks, traps and similar objects; Stealth checks when an object makes noise; optionally the Perception search for hidden objects. |
| Traps and hazards | Each affected hero's save and the trap's damage dice (a shared sphere trap once), and lingering hazards' saves and damage. |
| Concentration | The check is taken out of the damage step, the game waits, then the original check runs with your d20. |
| Repeat saves | Saves to end a condition at the end of a turn (Hold Person and similar) and at the start of a turn (the whole turn start waits). Optionally the saves when taking damage ends a condition. |
| Lingering effects | Area spells acting at the start of a turn or on entering them, end-of-turn area effects and recurring condition damage, with their saves. |
| Surprise and stealth | The hidden attacker's Stealth when a surprise attack starts a battle, and a hidden hero's Stealth at the end of their turn and after a revealing action. Enemies' Perception is asked for with enemy dice enabled. |
| Class and spell features | Deflect Missiles reduction, spell failure d20, spell-slot preservation d20, Dispel Magic checks, a bard's penalty die on a condition, and a charged item's last-charge d20. |
| Turn start | Start-of-turn saves, lingering damage, random behaviour (Confusion) and, with enemy dice, monster power recharge. |
| Monster identification | First contact and end-of-battle bestiary checks (Wisdom/Survival, DC 10 + CR) by the party's best conscious character. |
| Travel | Food search (Wisdom/Survival) and the food found after a success, ingredient gathering, crafting and the lookout's ambush d20 (with enemy dice, the creatures' d20 too); the world-map clock waits. |
| Camp crafting and scribing | The completion check of a crafting or spell-scribing activity. Forced crafting success stays native. |
| Falling damage | The fall's d6s (cells ÷ 2) once the fall exceeds the game's threshold. |
| Character screens (single-player) | Ability scores at character creation; optionally level-up hit points instead of the average. |
| Other paths | Original automatic rolls (announced by a notice for party d20s) for: saves triggered by damage taken during the start of a turn; the knock-out save of Relentless Rage-style features; rage-end exhaustion saves; save rerolls when a condition is gained; Bardic Inspiration used inside a death save, and the hit dice regained by a death save's stabilisation; regeneration amounts; random condition durations; a forced concentration check from a spell; unsafe Diamond Soul retries; random-bonus shove previews; batches mixing damage with healing; and random selections such as random effect forms, random conditions and push directions. Loot, wealth, encounter tables and AI choices are not dice a player rolls and stay with the game. |

Spell handling uses actual native effect data, rather than a list of spell names. A spell can request a manual attack/save and use automatic dice for an unsupported effect. All native fallback paths are retained. This is not a claim that every spell asset has been exercised or every die in the game is manual. `Source/audit/coverage/COVERAGE-REVIEW.md` documents the exact inspected boundaries and remaining limitations.

Unfinished Business is refused, including a loaded assembly left in the same process after disabling it. Restart after disabling it. Other mods are not certified compatible; a pristine game DLL cannot establish compatibility with their runtime patches.

## Test before using a campaign save

`TestResults.md` identifies the compiled artifact and developer evidence. Production qualification deliberately requires actual Windows single-player **and** coordinated multi-peer acceptance for that same artifact. The included tools collect and validate evidence; they do not run gameplay or mark it passed automatically.

In single-player test the new 0.8.0.0 rolls first: an attack while Blessed, a trap or a burning floor, Flaming Sphere, Dispel Magic, a food search, and the history panel and presets. Then the 0.7.0.0 rolls: a save at the start of a turn to end a condition, Spirit Guardians or Moonbeam at the start of an enemy's turn and when walking into it, burning or acid damage, Thunderwave or Ray of Frost damage, a hidden hero's Stealth after attacking, Deflect Missiles, crafting while travelling, an Insight option in dialogue and rolling ability scores for a new character. Then test travel foraging and a random encounter, meeting a new kind of monster, a dialogue check, Hold Person's end-of-turn save, a surprise attack from hiding, a fall, a lock or trap, a concentration check, camp crafting, initiative, ordinary/paired attacks, Lucky with a raw 1, Sleep with five 8s, Magic Missile, critical damage, multi-target area damage, healing, temporary HP, Bless/Bane saves, failed Counterspell, Bardic reactions, shove contests and resting. Check cancellation, location/reload changes, text layout, dragging and input release.

Then repeat on at least two machines with distinct character controllers. Confirm only the owner can submit, every combat log agrees, the next automatic roll remains synchronized, rest spends exactly one die per peer and only the original requester gets the UI callback. Test Auto, pending disconnection, idle rejoin and mismatched builds/settings using a disposable save. For 0.8.2.0, replay fights in which enemies attack (both live cases that paused were enemy attacks). Manual dice should not pause. If a "Games differ" notice appears, check whether your screens actually disagree. If manual dice ever pause, check that nobody left the room, the game kept rolling, and loading the save together brought manual dice back. Keep both players' logs. Complete the supplied acceptance cases and retain per-peer UMM logs/screenshots.

## Disable or remove

Single-player disabling cancels input and preserves the original suspended action so it can continue automatically. In multiplayer leave the room first. **Close Solasta before updating or removing the mod.** Live unloading is refused because patched native iterators may hold saved locals and continuation states. This standalone build has no replacement main game DLL to restore when removed.

## Source and evidence

`Source/src` contains all runtime source; `Source/tests` contains focused regression code, runners, results and source hashes. `Source/tools/BUILD-NOTES.md` explains reproducible pinned builds. In the release ZIP, `Audit` contains assembly, binding, native IL, networking ABI and regression reports. `build-manifest.json` identifies exact compiled inputs; `CHECKSUMS.sha256` covers packaged contents.

The narrow fault-handler compatibility patch preserves the native cleanup/rethrow behavior on the pinned Harmony generator. It stays in memory until process exit after a failed later load step and writes no game files. Developer tests run in a declared .NET 8 fixture environment, distinct from the game's Windows Unity/Mono runtime.
