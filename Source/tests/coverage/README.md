# Native roll coverage and Bardic Inspiration

This suite inventories direct native dice/RNG consumers and their transitive callers in the pinned pristine main game DLL. It independently checks every registered coroutine call offset and body hash against the real PE image. Interface dispatch and dynamically selected callers are not resolved by the transitive graph; the inventory is a review aid, not an all-spell execution certificate.

The behavior fixture copies the compiled original Bardic Inspiration routine, raw dice routine, actor dice/reroll/sum routines, and prerequisite native methods into a boundary assembly. The production roll broker, runtime adapter, scratch isolation and Bardic shim run under the real Harmony generator. UI, scene services, condition spending and event subscribers are declared host boundaries. Native Bardic branches and notifications are retained; condition removal is counted through the boundary implementation.

Checks cover normal and advantaged inspiration across d6/d8/d10/d12 and three reaction contexts, forced maximum without RNG or prompting, cancellation, live condition/die changes, foreign runtime patches, notification RNG isolation, and unregistered synchronous callers retaining automatic dice.

Run `python3 tests/coverage/run.py`. The helper follows the same environment overrides as the other developer suites. It does not alter or package the supplied game assemblies. The dedicated await suite separately emits and installs all supported native coroutine patches; no suite substitutes for live Windows/Unity/Photon testing.
