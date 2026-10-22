# Si_Logistics (0.8.1)

Server-side resource transfers, driver rewards, harvester routing and automatic
transport duty, with consolidated controls and a performance/cleanup pass.

## Commands

Every command below works with **/logistics** or **/logi**. Subcommands are
case-insensitive, including Transfershare, Harvesterfix and AItransport.
Admin means Admin Mod Generic permission; server-console callers are allowed.
Successful setting changes save to UserData/Logistics_cfg/Si_Logistics_Config.json.

| Command | Access | Function |
| --- | --- | --- |
| /logistics or /logistics status | Everyone | Settings, effective feature states, reward rates and cargo in your controlled transport. |
| /logistics help | Everyone | List available commands. |
| /logistics on/off | Admin | Master switch for transfers, rewards, Harvesterfix and AItransport. |
| /logistics share <0–1> | Admin | Transport unloading AND independent harvester refinery reward rate; default 0.10. |
| /logistics transfershare <0–1> | Admin | Harvester driver's reward for a harvester-to-transport pickup; default 0.08. |
| /logistics capacity air <n> | Admin | Aircraft cargo capacity; positive integer. |
| /logistics capacity ground <n> | Admin | Ground transport cargo capacity; positive integer. |
| /logistics rate load <n> | Admin | Ground loading units per second; positive integer. |
| /logistics rate unload <n> | Admin | Ground unloading units per second; positive integer. |
| /logistics radius load <metres> | Admin | Player-driven pickup radius; finite number greater than zero. |
| /logistics radius unload <metres> | Admin | Player-driven unloading radius; finite number greater than zero. |
| /logistics cooldown <seconds> | Admin | Delivery cooldown AND delay for new driver reward payout windows; finite, zero or greater. |
| /logistics debug on/off | Admin | Diagnostics; status also dumps detailed harvester/transport state while enabled. |
| /logistics reload | Admin | Reload JSON settings. |
| /logistics harvesterfix on/off | Admin | Harvester controller sub-switch; defaults ON. |
| /logistics aitransport on/off | Admin | Automatic transport duty sub-switch; defaults OFF. |

These are the complete chat controls. The standalone /harvesterfix and
/aitransport commands, bonus toggle and configurable extra command aliases were
removed. Use /logistics status for both feature summaries. Debug status retains
per-vehicle diagnostic dumps without adding separate status commands.

## Master switch and rewards

### Player score rewards

`LogisticsScore` defaults to `true`. Admin commands `/logisticsscore on|off`,
`/scoreunload <0..100>` and `/scoretransfer <0..100>` save their settings to the
Logistics config. `ScoreUnload` defaults to 2 points per 100 Balterium actually
accepted at a refinery from a player-driven transport or harvester.
`ScoreTransfer` defaults to 1 point per 100 Balterium transferred from a
player-driven harvester to a player-driven transport; only the harvester driver
receives transfer score. Autonomous vehicles without player drivers earn no score.

Score is added directly to the normal round score through `Player.Score`, without
chat award messages or a KoH dependency. Existing credit rewards remain separate.
Fractional points carry forward per player and separately for unloads/transfers;
rate changes apply to newly handled cargo. Disabling Logistics score clears these
fractions, but does not remove awarded score. Fractions also clear on disconnect,
match start/end and scene unload. The master Logistics switch also gates scoring.
`/logistics status` shows the score controller and both rates. All mod-owned logging
uses `/logistics debug on|off`, with `Debug` defaulting to `false`.

### Commander score

`CommanderScore.Enabled` defaults to `true`. Admin-only
`/logisticsscorecomander on|off` (also `/logisticsscorecommander`) controls it.
`/logistics status` reports enabled/active state and KoH purchase-hook readiness.
It follows the master Logistics switch, independently of the player delivery score
switch. Awards and penalties update the current commander's normal round score
silently; diagnostic output follows Logistics `Debug` only.

Configure `CommanderScore.Rules` in
`UserData/Logistics_cfg/Si_Logistics_Config.json`, then `/logistics reload`.
The default rules use **resource cost** as object value, round down after division,
and apply a minimum of 1 point:

| Event | Default |
|---|---|
| `UnitProduced` | Cost / 100 |
| `UnitLost` | Negative Cost / 100 |
| `BuildingCompleted` | Cost / 50 |
| `BuildingLost` | Negative Cost / 50 |
| `TechnologyReached` | 500 per newly reached tier |
| `EnemyUnitDestroyed` | Enemy unit Cost / 100 |
| `EnemyBuildingDestroyed` | Enemy structure Cost / 50 |
| `BalteriumDeposited` | 1 per 1,000 actually accepted at a refinery |

Add/remove rules or set each rule's `Enabled`, `Divisor`, `Minimum` and signed
`Multiplier`. `ValueSource` accepts `Cost`, the object's native `Score`, `Fixed`
(uses `FixedPoints` times `Multiplier`), or `Amount` (for refinery deposits).
`Objects` is an optional list of ObjectInfo names or display names; empty matches
all. `Overrides` maps those names to exact final signed points, bypassing the
formula. For example, `"Overrides": { "Harvester": -50 }` on `UnitLost` assigns
a 50-point loss penalty to that named unit. Names must match the actual game data,
case-insensitively. Multiple matching rules add together. Unknown event names or
value sources award nothing. Removing the whole list disables all rule awards.
`Minimum` and rounding apply to object-value rules; `Amount` retains fractional
points and `Fixed` uses its configured signed value directly.

Only successful native production/construction earns build score; starting units,
respawns, failed/cancelled sites and arbitrary mod spawns do not. Losses are charged
to the commander at destruction, including starting units, but excluding units
directly controlled by a player or driven by a player at that moment. KoH `/buy`
units and structures are marked at spawn and excluded from production/loss/combat
score for the round, even after their buyer leaves. Logistics observes KoH purchase
execution with Harmony; its source, DLL and config are unchanged. Commander scoring
is blocked if these hooks cannot be installed, rather than charging purchased units.

Enemy destruction awards require an AI-controlled attacking unit or an automated
structure turret. Player-controlled or KoH-bought attackers earn no commander kill
score; allied/neutral targets earn none. Player-controlled enemy targets can still
earn the attacking commander's reward. Duplicate construction/destruction callbacks
are ignored. Only an ongoing server match scores; round cleanup earns nothing.

Technology milestones are tracked per team, including while scoring is disabled;
starting tiers and re-reaching a lost tier earn nothing. A multi-tier jump earns
500 for each new tier by default. Deposit fractions are retained per rule, commander
and team; reload, disabling commander score, disconnect and round reset clear them.
Deposits include autonomous harvesters/transports and player deliveries through the
existing verified refinery deposit paths. Team-credit grants and harvester-to-
transport pickups do not count as refinery deposits. No commander means no score.

/logistics off hands harvesters back to vanilla, stops managed transport duty,
ends transfer sessions and pauses reward accrual/payouts. Surviving cargo and
already-earned pending rewards remain; feature sub-switch settings are retained.
Re-enabling resumes eligible harvesting and pending payouts, but transports need
a fresh Follow assignment because disabling releases their duty. Match cleanup
still clears temporary cargo/rewards as before.

Rewards are always enabled under the master switch. PersonalBonus.Share is the
single rate for transport deliveries and independently driven harvester refinery
deliveries. PersonalBonus.TransferShare is the separate pickup reward; it retains
the existing harvester-driver recipient and player-driven transfer eligibility.
The Transfershare description in the requested command table duplicated unloading;
this command controls the distinct harvester-to-transport reward, not a second
unloading rate. Both reward types use DeliveryCooldownSeconds for payout batching.
Changing a rate affects newly earned rewards; changing cooldown affects new
reward windows and new delivery cooldowns, not timers already scheduled.
Zero means payout on the next update; unavailable reward-provider retries have a
minimum one-second interval. Rewards still require the existing KoH credit API.

Old PersonalBonus.Enabled, HarvesterDepositShare, PayoutDelaySeconds and MinBatch
keys are removed/ignored. Legacy HarvesterTransferShare migrates to TransferShare
when the new key is absent. Share and cooldown are now the source of truth.
Other tuned settings are preserved, including feature flags already present in
an existing CFG; the ON/OFF defaults above apply to new/missing settings.

## CFG-only settings and preserved functions

The existing per-class settings remain available in JSON: airloadunitspersecond
and airunloadunitspersecond (500/s defaults, AI and player aircraft), and the four
AItransport ground/air load/unload radii. Thus the rate commands change ground
rates; radius commands change player ranges. This preserves separate air and AI
settings introduced in 0.7.0. /logistics reload applies edited JSON values.

Other retained options support the mod's behaviour: allowed transport types,
progress chat, optional sounds, reward announcements, hidden command chat,
harvester patch/refinery reservation limits and spacing, pickup waiting time,
and AI transport allocation limits/check intervals. The sections below document
these functions. Reload rebuilds harvester reservations only when validated
harvester settings change; changes to rates, sounds or rewards preserve them.

Sounds are enabled by default in newly generated configs. Copy
`logistics_tick.wav` and `logistics_ding.wav` into the server's `UserData/sounds`
folder; WAV files are loaded at runtime and are not embedded in the DLL. The
controlling player hears a tick on each successful loading transfer and a ding
when cargo becomes full. AI-only transports and unloading are silent. Missing
files are silently skipped. Existing configs retain their `Sounds.Enabled`
value; set it to `true` and run `/logistics reload` to enable sounds there.


## Harvester fix

Server-side controller for AI-driven human harvesters. Enabled by default.
The master Logistics switch must also be enabled. Transport transfers and rewards
are unaffected by the harvesterfix sub-switch.
With the fix off, its patches pass through to vanilla harvester AI.

- `/logistics harvesterfix on` and `/logistics harvesterfix off`: require Admin Mod Generic permission;
  save the setting to the config. Disabling releases the controller's reservations
  and hands its waiting vehicles back to native harvesting/deposit orders.
- `/logistics status`: available to everyone; shows settings and reservation counts,
  and, when debug is enabled, writes per-harvester phase, cargo, dock state, queue slot and native bay owners
  to the server log under `[HarvesterFix]`.
- `/logistics reload`: reloads settings. Changed harvester settings release and
  rebuild reservations when enabled; unrelated setting changes preserve them.

Config: `UserData/Logistics_cfg/Si_Logistics_Config.json`:

```json
"harvesterfix": {
  "Enabled": true,
  "MaximumReservationsPerRefinery": 5,
  "NearPatchDistanceMetres": 400.0,
  "MediumPatchDistanceMetres": 800.0,
  "NearPatchReservations": 1,
  "MediumPatchReservations": 2,
  "FarPatchReservations": 3,
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
destruction, team changes, player takeover, disabling the feature or reloading changed harvester settings, and
match/scene cleanup release the assignment. Manual unload orders preserve it.

Patch limits use horizontal straight-line distance from the patch's SignalCenter
to the closest surviving friendly refinery's position (with a matching resource
deposit point). The distance bands default to (800m belongs to the far band):

| Distance to nearest friendly refinery | Assignments |
| --- | --- |
| Up to and including 400m | 1 |
| Over 400m, below 800m | 2 |
| 800m and above, or no friendly refinery | 3 |

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
only while the patch has resources. Depletion clears the grace period immediately,
and further pickups at that depleted patch cannot restart it. Normal return/patch
selection resumes on the next controller update. A partially loaded harvester
leaving a depleted patch unloads first when an eligible refinery is closer than
the next available patch; this applies to any nonempty partial load, including
half-full harvesters. The separate AI transport prototype below can automate the transport's driving.

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

1. Confirm harvesterfix defaults ON; disable it and verify vanilla harvesting, then re-enable.
2. Build multiple harvesters: check factory exit, 10-second delay, distinct travel
   reservations with the 1/2/3 distance bands. Confirm the same home patch stays
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
stalls, run `/logistics status` before moving it so the log captures the state.

## Automatic AI transport duty (0.7.0)

/logistics aitransport on|off controls transport duty beneath the master switch. On/off requires
Admin Generic permission and persists in the existing JSON CFG. Right-clicking
a friendly harvester with an eligible AI transport enrols it in logistics duty.
The game's accepted immediate Defend order is the native friendly-target Follow
trigger; explicit Defend also enrols. Queued orders do not enrol. Unassigned AI
transports retain vanilla behaviour. A new accepted commander order cancels duty;
a new Follow enrols again with the clicked harvester as the initial preference.
That preference obeys collection limits and productive-patch eligibility.

Duty persists when a harvester is destroyed or changes patch. Collection slots
are reserved immediately, released when leaving for delivery, and reassigned
after full unloading. Productive AI harvesters are ranked by pickup travel,
existing allocation, available cargo and distance to the nearest friendly
refinery. Ground travel receives a higher cost than air travel. Distances are
geometric estimates, not measured route times or guaranteed maximum throughput.
Human-controlled harvesters and harvesters docking/queuing are not automatically
selected. In-transit harvesters with a productive patch can be selected.

| Harvester distance to closest friendly refinery (horizontal) | Ground OR air |
| --- | --- |
| Up to and including 300m | 2 ground OR 1 air |
| Over 300m, up to and including 600m | 3 ground OR 1 air |
| Over 600m | 4 ground OR 2 air |

The allowance is shared: each ground vehicle uses 1/GroundLimit and each air
vehicle uses 1/AirLimit; the total may not exceed 1. For example, the far tier
allows two ground plus one air. This does not add both maximum fleets together.
Zero disables that vehicle class in that band. Band distance is measured from
the harvester's current position. Changing bands/config may release excess
collectors on their next allocation check; cargo stays aboard.

A productive assignment is kept while progress continues. Depletion, patch
changes, target destruction, exceeding the current limit or the progress timeout
trigger reallocation. Progress means cargo received or at least 10m improvement
in the closest approach distance; there is no arbitrary maximum journey time.
A stalled target is excluded for one timeout period before being eligible again.
Partial loads are delivered when no collector slot exists or a reachable refinery
is closer than the next selected harvester. Full loads always finish delivery.
Empty idle transports attempt to park outside refinery transfer/bay traffic and
retry. If no navigable parking point exists they hold in place. No friendly
refinery/full team storage retains cargo and waits rather than discarding it.

AI settings under the existing AItransport category (illustrative defaults):

```json
"AItransport": {
  "Enabled": false,
  "airloadradius": 150.0,
  "airunloadradius": 150.0,
  "groundloadradius": 40.0,
  "groundunloadradius": 65.0,
  "NearHarvesterDistanceMetres": 300.0,
  "MediumHarvesterDistanceMetres": 600.0,
  "NearGroundTransports": 2,
  "NearAirTransports": 1,
  "MediumGroundTransports": 3,
  "MediumAirTransports": 1,
  "FarGroundTransports": 4,
  "FarAirTransports": 2,
  "ReassignmentCheckSeconds": 10.0,
  "NoProgressTimeoutSeconds": 120.0
}
```

Legacy DropshipLoadRadius/DropshipUnloadRadius migrate to the corresponding
new air fields when those fields are absent. Missing ground fields inherit the
ordinary LoadRadius/UnloadRadius from existing configs. Aircraft classification
uses IsFlyingType, covering both factions' allowed aircraft. Player-driven ranges
remain LoadRadius/UnloadRadius. AI ranges apply to destination selection and the
actual transfers, with altitude included in the 3D transfer distance.

At the top level of the Logistics CFG, airloadunitspersecond and
airunloadunitspersecond default to 500 each. These apply to BOTH player-driven
and assigned AI aircraft; ground vehicles retain LoadUnitsPerSecond and
UnloadUnitsPerSecond. Capacity, actual resource accounting, cooldowns and rewards
are unchanged. AI deliveries do not award a driver bonus. Edit CFG then use
/logistics reload; changed harvester settings rebuild reservations and AI
collection allowances are reconsidered. Air rates accept 1–100000, radii 1–2000m,
counts 0–32; invalid non-finite radii/times use defaults.

Each successful pickup still invokes the existing harvester top-up timer when
harvesterfix is enabled, but depleted patches cannot establish or prolong that
hold. AI collectors stop targeting depleted patches even if the harvester has
residual cargo, allowing normal harvesting/refinery routing to resume.

With debug enabled, /logistics status logs duty, collection target/patch, refinery, cargo, effective
radii, band limits and movement status under [AITransport]. Assignment and phase
changes log automatically. Native Move/Stop and collision avoidance still handle
travel. Refinery approach fallbacks may overlap harvester traffic; this is not a
traffic queue controller. Terrain projection cannot guarantee path reachability.
Player takeover, transport destruction, team change, disabling and match/scene
cleanup end duty; surviving same-team cargo remains usable after cancellation.
Assignments/cargo are not saved across matches or server restarts.

### Focused live checks

1. Enrol ground and air vehicles on a near patch; verify shared limits and excess
   vehicles selecting other harvesters or waiting. Repeat at 300m/600m boundaries.
2. Deplete a patch during pickups: harvester leaves without a renewed 120s hold;
   transports reassign or deliver partial loads without requiring new orders.
3. Destroy a collection harvester, then verify its transports stay on duty.
4. Watch several delivery cycles and mixed-faction aircraft loading at 500/s;
   confirm player aircraft rates change but their transfer radii do not.
5. Move/Stop, player takeover, disable/re-enable, full storage, destroyed refinery,
   no productive patches and prolonged blocked paths retain correct cargo/control.

## Performance and cleanup (0.8.1)

- A synchronous transfer tick builds reusable lists of harvesters, transports and
  deposit structures once. Allocation scans use those lists; direct assigned
  transfers inspect only the assigned target. Live team, destruction, cargo,
  productive-patch and reservation checks remain in place.
- Nearest-refinery distances for harvesters are reused within that tick. Lists
  and distances are cleared in a finally block; no world snapshot survives it.
- Identical approach queries may reuse their result only in the same tick/frame,
  for the same transport position, target centre, radius and refinery. There is
  no persistent navigation cache to invalidate after terrain or target changes.
  Candidate scoring skips occupancy work when the candidate already scores worse;
  scoring rules and native-approach fallbacks are unchanged. Stationary failed
  movement searches respect the existing five-second retry interval.
- Reward accounts are examined when the earliest payout is due, not every frame.
  Native deposit tracking reuses a per-thread, bounded pool of cleared receipts,
  retaining nested-call attribution and actual accepted-resource accounting.
  Neither reward rates nor payout-window semantics change.
- Reusable tick scratch collections replace routine list/set allocations. Each
  transport's update is isolated: an exception logs its ID and backs off for ten
  seconds while the rest of the fleet continues. Match/scene cleanup clears the
  failure backoff; cargo accounting is not reset by this recovery.
- Reload compares validated harvester settings and rebuilds reservations only
  when they differ. Master disable still releases both controllers immediately.

Enable /logistics debug on for [LogisticsPerf] summaries every 30 seconds:
transfer tick count, average/max milliseconds, approach search count, navigation
probe count and same-frame cache hits. This measures the transfer/allocation
loop, not the separately patched native mining/harvester updates. Counters are
inactive without debug; verbose existing transfer diagnostics still add overhead
while debugging. Disable debug again after collecting a sample.

The code removes identifiable repeated work, but no measured speedup is claimed:
the available pre-change log was from 0.7.0 and did not directly time this loop.
The release build is compilation validation, not an in-game benchmark. Live
checks: mixed ground/air hauling through depletion, native harvester and pickup
rewards (including zero cooldown), unchanged shared collection limits, and reload
of a non-harvester setting preserving queue order/home patches. Compare populated
matches at similar unit counts when reviewing timing logs.

All mod-owned server logging (including startup, status, warnings and errors) requires the existing `Debug` setting to be enabled. Player chat replies are unaffected. MelonLoader itself may still print mod loading messages.

## Optional ModUI integration (0.9.1)

ModUIData.cs and ModUI_Export.cs describe the current server commands/settings for Si_ServerModUI 2.0.0. No UI DLL is required by Logistics. The new /logistics enabled on|off alias uses the existing validated master handler. Existing config paths, harvester/AI transport, rewards and scoring remain owned here. Purchase exclusions support compatible separate Si_Buy and legacy KoH handlers and fail closed on unknown signatures. This source was built, not installed; user-owned live verification remains required.
