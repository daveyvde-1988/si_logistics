# si_logistics
Logistics mod for Silica

## Harvester fix (0.5.4)

Server-side controller for AI-driven human harvesters. Disabled by default; the
normal Logistics transport transfers and rewards are independent of this switch.
With the fix off, its patches pass through to vanilla harvester AI.

- `/harvesterfix on` and `/harvesterfix off`: require Admin Mod Generic permission;
  save the setting to the config. Disabling releases the controller's reservations
  and hands its waiting vehicles back to native harvesting/deposit orders.
- `/harvesterfix status`: available to everyone; shows settings and reservation counts,
  and writes per-harvester phase, cargo, dock state, queue slot and native bay owners
  to the server log under `[HarvesterFix]`.
- `/logistics reload`: reloads settings and releases existing reservations so the
  new limits apply. Reservations are rebuilt automatically when enabled.

Config: `UserData/Logistics_cfg/Si_Logistics_Config.json`:

```json
"harvesterfix": {
  "Enabled": false,
  "MaximumReservationsPerRefinery": 5,
  "NearPatchDistanceMetres": 200.0,
  "MediumPatchDistanceMetres": 600.0,
  "NearPatchReservations": 1,
  "MediumPatchReservations": 2,
  "FarPatchReservations": 4,
  "TransferWaitingTimeSeconds": 120.0,
  "ReservationCheckTimeSeconds": 10.0,
  "ReservationDistanceIncrementMetres": 50.0
}
```

Newly spawned harvesters get a 10-second grace period, and factory force-movement
is allowed to finish before dispatch. They select a known, non-depleted resource
patch with an available reservation. Reservations are per team and per resource
area, not per resource cell. Each assignment is a persistent home patch: the slot
stays reserved through mining, transport top-ups, travel to the refinery, queuing,
unloading, and the return trip. Changing refineries does not change the home patch.
Depletion, an explicit different harvesting assignment, move/stop/combat orders,
destruction, team changes, player takeover, disabling/reloading the feature, and
match/scene cleanup release the assignment. Manual unload orders preserve it.

Patch limits use horizontal straight-line distance from the patch's SignalCenter
to the closest surviving friendly refinery's position (with a matching resource
deposit point). The inclusive distance bands default to:

| Distance to nearest friendly refinery | Assignments |
| --- | --- |
| Up to 200m | 1 |
| Over 200m, up to 600m | 2 |
| Over 600m, or no friendly refinery | 4 |

The five NearPatch/MediumPatch/FarPatch settings above replace the old uniform
MaximumReservationsPerBalteriumPatch setting. Counts accept 1–32; distances are
nonnegative and the medium threshold cannot be lower than the near threshold.
Limits are calculated for new assignments. Building a closer refinery does not
evict existing owners or cause them to change patches; additional assignments wait
until the new limit allows them. Manual placement can exceed physical occupancy,
but does not create extra automatic reservations. Reservations are in-memory for
the current match, not saved across restarts. Native stuck recovery remains active;
there is no timer that expires a home assignment during a long refinery queue.

Returning harvesters first consider the closest friendly refinery (or an explicitly
requested refinery). If joining would mean fourth place or later, they look for
an alternative with a shorter queue and a free reservation within 800m of that
refinery. Among suitable alternatives they pick the closest to the harvester.
Otherwise they join the original queue, up to the configured hard limit of five;
further harvesters wait at the patch and retry. Existing tickets never reshuffle
because another refinery becomes less busy. New admissions always append to the
back, including adopted native docking vehicles; a late entrant already inside
the bay exits using native undocking before taking its waiting position. Repeating
an unload order for the same refinery preserves the existing queue ticket.
The reservation limit includes the active unloading slot: with defaults, slot 1
approaches/docks, slot 2 waits 100m outside the approach point, slot 3 waits 150m,
slot 4 waits 200m, and slot 5 waits 250m. The first waiting distance is always 100m; the configurable
increment changes only the distances between subsequent waiting vehicles.
A native reservation/occupant outside the controller takes the
front slot. The line retains a common direction derived from the refinery's
native approach point. Waiting vehicles advance on their 10-second checks; the
active reservation is held through undocking. A harvester without an available
refinery slot stays at the patch and retries on the same interval.

Refinery-local storage fullness does not affect selection or queue admission;
native resource distribution handles storage. If total team storage is full,
native unloading must wait for room. Distance means geometric distance, not estimated driving time. Resource
selection respects the team's discovered resource areas. Waiting positions are
projected onto terrain and checked against vehicle navigation; an invalid point
causes waiting/retry rather than movement to an invalid destination. Complex
terrain and player traffic still require in-game verification.

Each successful Logistics pickup restarts the harvester's 120-second grace period.
For an AI vehicle at a resource patch, a pickup also resumes harvesting after a
manual stop/move and restores its native harvest order if necessary. Human-driven
harvesters retain driver control. Repeated transfer ticks extend the timer without
repeatedly restarting movement. Status logs include remaining wait time, patch
reservation, mining and extraction state.
At the patch it continues mining when there is space, waits when full, and stays
even if the patch becomes depleted. After the grace period, normal return/patch
selection resumes when the timer expires. A partially loaded harvester
leaving a depleted patch unloads first when an eligible refinery is closer than
the next available patch; this applies to any nonempty partial load, including
half-full harvesters. This feature does not automate the transport's driving.

Native mining, local movement recovery, docking and undocking are retained.
Player-driven vehicles are excluded. Explicit harvest and deposit orders both
use automation. An immediate explicit move/stop/combat command releases automation
for that vehicle; a harvest or deposit command enables it again. Active
managed assignments reject competing autonomous AI orders. Existing non-harvest
and non-deposit orders are preserved when the feature is enabled, except completed
nearby fallback moves on full, stationary harvesters. Destruction, team changes,
player takeover and match/scene changes release reservations.

Version 0.5.1 fixes queue admission when clicking occupied refineries: the native
target resolver otherwise rejects their bays and turns the click into a move.
It also adopts existing docking vehicles, reconciles native reservations so only
the admitted queue head reserves a bay, and retries stopped full harvesters.
Native refinery approach points are no longer rejected by the ordinary navigation
point test; queue waiting positions still use navigation checks, with up to 40m
of lateral adjustment. Queue transitions and blocked/rejected orders are logged.
The physical unloading/docking behaviour still needs in-game verification.

### Deployment and focused in-game checks

Build using `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`.
Output: `bin/Release/netstandard2.1/Si_Logistics.dll`. Install only this DLL into
the server's `Mods` directory while the server is stopped. It loads at next start.
Compilation does not establish in-game behaviour.

1. Confirm status starts OFF and ordinary harvesting remains vanilla; enable it.
2. Build multiple harvesters: check factory exit, 10-second delay, distinct travel
   reservations with the 1/2/4 distance bands. Confirm the same home patch stays
   reserved while its owner unloads and is selected again on its return.
3. Send at least six full harvesters towards one refinery: check the active bay,
   100m/150m/200m/250m waiting spots, FIFO advancement and the sixth waiting at its
   patch when there is no suitable alternative. Add a less busy refinery within
   800m and check diversion for arrivals that would otherwise join fourth or later.
4. Drain a full AI harvester with a transport and verify that it mines back towards
   full. Repeat within 120 seconds, then leave: it should resume normal routing
   120 seconds after the last successful transfer. Also check a manually stopped
   AI harvester at a patch, and a patch that runs out during the waiting period.
5. Deplete a patch with a half-loaded harvester and compare a nearer refinery with
   a nearer available resource patch. Check full storage and destroyed refineries.
6. Take control, issue manual orders, then disable/re-enable the feature; confirm
   reservations are released and waiting vehicles resume native behaviour.

For queue retesting, first fill several harvesters with the fix off and occupy all
refineries; enable the fix and check that they retry. Then issue deposit orders to
several harvesters at the same refinery. After the first unloads and clears the
bay, check the next vehicle advances within the configured check interval. If one
stalls, run `/harvesterfix status` before moving it so the log captures the state.
