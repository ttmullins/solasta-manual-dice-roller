# Multiplayer protocol regressions

This pure .NET 8 suite compiles production `MultiplayerProtocol.cs` and runs
two- and four-participant schedules with delayed, lost, duplicated and reordered
packets. It checks owner input, explicit shared Auto, immutable decisions,
bounded wire parsing, exact profiles/descriptors/rosters and the all-peer
prepare/commit/release barriers. Randomized schedules are reproducible.

Wire version 2 binds the bootstrap acknowledgements to a fresh nonce from every
participant. `DelayedHandshakeFromOldRoom` first supplies a complete transcript
from an old room to a fresh follower, then the actual new-room handshake. The
old production source failed the first readiness assertion; its retained
failure is `regression-before-stale-bootstrap.txt`. The current source must
reject the old readiness and become ready with the current room. An established
session cannot rebind to an older token.

The runner records current source SHA256 values and its successful output.
Packets and clocks are simulated here. The connected-process suite additionally
joins the transport/facade to native dice and stored RNG IL; neither suite is a
live Photon, Unity or Windows gameplay test.
