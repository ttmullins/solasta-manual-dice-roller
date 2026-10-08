# Connected-process multiplayer/native integration

This suite runs two or four independent .NET 8 processes. Each process compiles
the actual production protocol, queued Photon-compatible transport, multiplayer
facade, RuntimeRolls, broker, scratch isolation and native scheduler. The raw
actor/static dice, rerolls, HP-pool sum, deterministic random state and TA.Run
methods are copied from the checksum-pinned original game by the existing native
IL extractors. The tests route real Photon EventData through an explicit local
gateway, with delayed frames, loss, duplication and reordering.

The GUI, player-controller service, upstream native action driver and full-scene
descriptors are declared fixtures. Original planner/UI/descriptor/await behavior
has independent suites. The Unity RNG external API is a reproducible substitute;
stored state restore/record and raw dice evaluation are original game IL. The
scheduler preflight hashes are verified by the separate genuine-assembly suite;
the integration uses the remapped TA.Run method and original production hooks.

Checks cover owner-only input, manual Lucky replacements, paired advantage,
Sleep's five raw d8s producing 40, damage-trait replacements, static bonus dice,
native synchronous concentration fallback, Auto during a Lucky replacement,
Karmic sampling, and agreement of every next unprompted die and stored RNG state.
Two further rooms (two and three peers) drive every step through the production
deferred-roll pattern instead of an awaited iterator: PrepareDeferredRoll with a
skipped void original and its probed inner call, a dedicated runner coroutine,
the void-result observation and the completion barrier, while a separate native
coroutine is held by the deferred hold and must not advance until release.
Negative connected rooms must stop all peers before consuming any dice for a
profile mismatch, controller mismatch, or pending controller transfer. A changed
native result must stop every peer at completion, before its next automatic die.
This is connected-process development evidence, not a live Photon connection,
full Unity scene, Windows Mono session, or multiplayer gameplay certification.
