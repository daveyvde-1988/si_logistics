# Logistics reference

Detailed behavior, configuration and operational notes for Logistics 0.9.3.
Start with the [README](../README.md) or the [purpose and functions overview](Logistics_Overview.md).
The [complete example configuration](../Si_Logistics_Config.example.json) is the reference for current defaults; saved server settings take priority.

## Commands

Every `/logistics` subcommand also works with `/logi`. Subcommands are case-insensitive.
Status and help are public. All setting changes require Admin Mod Generic permission; the server console is also allowed.
Successful setting commands save to `UserData/cfg/Logistics/Si_Logistics_Config.json`.
In the syntax below, choose one value wherever `|` appears; do not type the separator.

| Command | Access | Function and accepted values |
| --- | --- | --- |
| `/logistics` or `/logistics status` | Everyone | Settings, effective feature states, reward rates, active duty totals, refinery count and cargo in your controlled transport. With debug enabled, also writes detailed controller state to the server log. |
| `/logistics help` | Everyone | List available commands. |
| `/logistics on\|off` | Admin | Master switch for transfers, rewards, automation and scoring. |
| `/logistics enabled on\|off` | Admin | Equivalent master-switch syntax. |
| `/logistics harvestershare <0..1>` | Admin | Personal-credit share for player harvester refinery deliveries; default `0.07`. |
| `/logistics transportshare <0..1>` | Admin | Personal-credit share for player transport refinery deliveries; default `0.09`. |
| `/logistics transfershare <0..1>` | Admin | Harvester driver's credit share for a pickup by a player-driven transport; default `0.06`. |
| `/logistics capacity air\|ground <n>` | Admin | Cargo capacity; positive integer. Defaults: air `5000`, ground `3500`. |
| `/logistics rate load\|unload <n>` | Admin | Ground loading or unloading units per second; positive integer. Both default to `300`. |
| `/logistics rate airload\|airunload <n>` | Admin | Air loading or unloading units per second; positive integer. Both default to `500`. |
| `/logistics radius load\|unload <metres>` | Admin | Player pickup or delivery range; finite number greater than zero. Defaults: `45`/`70`. |
| `/logistics radius aiairload\|aiairunload <metres>` | Admin | Assigned AI aircraft pickup or delivery range; `1..2000`. Defaults: `90`/`120`. |
| `/logistics radius aigroundload\|aigroundunload <metres>` | Admin | Assigned AI ground pickup or delivery range; `1..2000`. Defaults: `75`/`80`. |
| `/logistics cooldown <seconds>` | Admin | Delivery cooldown and delay for new driver reward payout windows; finite, zero or greater. Default `20`. |
| `/logistics harvesterfix on\|off` | Admin | Harvester controller sub-switch; default ON. |
| `/logistics aitransport on\|off` | Admin | Automatic transport duty sub-switch; default ON. |
| `/logistics sounds on\|off` | Admin | Transfer start/stop sounds; default ON. |
| `/logistics announce on\|off` | Admin | Personal-credit payout announcements; default ON. |
| `/logistics progress <seconds>` | Admin | Progress-message interval, `0..60`; zero keeps start/stop messages only. Default `2`. |
| `/logistics debug on\|off` | Admin | Mod-owned server diagnostics; default OFF. |
| `/logistics reload` | Admin | Reload and validate the saved JSON configuration. |
| `/logisticsscore on\|off` | Admin | Player delivery/transfer round-score switch; default ON. |
| `/scoreunload <0..100>` | Admin | Player round-score points per 100 Balterium actually accepted at a refinery; default `2`. |
| `/scoretransfer <0..100>` | Admin | Harvester-driver round-score points per 100 Balterium picked up by a player-driven transport; default `1`. |
| `/logisticsscorecommander on\|off` | Admin | Commander round-score switch; default ON. `/logisticsscorecomander` is also accepted. |
| `/wasp` or `/wasp status` | Everyone | Wasp harvest settings, your harvest, deliveries and earned credits this round, and the cargo and Bio Cache cooldown of the Wasp you control. |
| `/wasp help` | Everyone | How to harvest with a Wasp. |
| `/wasp set` | Admin | List every Wasp harvest setting with its value and range. |
| `/wasp set enabled on\|off` | Admin | Wasp harvest sub-switch; default ON. |
| `/wasp set carry <1..100000>` | Admin | Biotics per load, and the most one Bio Cache visit unloads; default `500`. |
| `/wasp set perhit <1..10000>` | Admin | Biotics taken from the patch per extracting hit; default `20`. |
| `/wasp set radius <0.5..50>` | Admin | Metres around the hit point searched for Biotics; default `3`. |
| `/wasp set height <0..100>` | Admin | Greatest height of the hit point above ground that still extracts, in metres; default `4`. |
| `/wasp set deposit <1..10000>` | Admin | Biotics unloaded per deposit tick; default `50`. |
| `/wasp set cooldown <0..3600>` | Admin | Seconds per Bio Cache load, counted from the first deposit tick of a visit; zero disables it. Default `30`. |
| `/wasp set reward <0..1>` | Admin | Pilot's personal-credit share of a Wasp delivery; default `0.4`. |
| `/wasp set notify <0..60>` | Admin | Minimum seconds between two cargo-bar messages per Wasp; default `2`. |

The score commands and `/wasp` are separate command roots; `/logi` does not alias them.
Numeric `/wasp set` values also accept a step such as `+50` or `-5`; out-of-range results are rejected, not clamped.
There are no standalone `/harvesterfix` or `/aitransport` commands, personal-bonus toggle or configurable extra command aliases.

## Configuration and migration

Settings are read at startup and on `/logistics reload`. Existing keys override code defaults; missing keys retain defaults.
If the destination is missing, the legacy `UserData/Logistics_cfg/Si_Logistics_Config.json` is imported first.
An existing destination takes precedence. All subsequent saves use the new `UserData/cfg/Logistics/Si_Logistics_Config.json` path.
If neither file exists, Logistics writes a fresh default config, including when a missing file is reloaded during a running session.
An unreadable config falls back to defaults in memory; the error follows the Debug setting.
Setting commands rewrite the known configuration fields, dropping unknown keys.

Commander rules, transport allowlists, reservation/allocation tuning and some presentation options are configured in JSON.
Reload rebuilds harvester reservations only when the validated harvester settings change; changes to rates, sounds or rewards preserve them.
AI collection allowances are reconsidered after reload.

Legacy field handling:

- `PersonalBonus.HarvesterTransferShare` migrates to `TransferShare` only when `TransferShare` is absent.
- `AItransport.DropshipLoadRadius` and `DropshipUnloadRadius` migrate to the corresponding air fields only when those air fields are absent.
- Missing AI ground radius fields inherit existing top-level `LoadRadius`/`UnloadRadius` values when present, otherwise the current AI ground defaults.
- Old `PersonalBonus.Enabled`, `HarvesterDepositShare`, `PayoutDelaySeconds`, `MinBatch` and combined `Share` keys are ignored. Missing split reward shares use current defaults.
- The old uniform `MaximumReservationsPerBalteriumPatch` is replaced by the distance bands below.

## Transfers, control and lifecycle

The default transport allowlist contains `Shuttle`, `Dropship`, `Squad Transport` and `Platoon Hauler`.
Allowlist matching uses the actual game display name, case-insensitively. Additional names should be confirmed against the server's game data or diagnostics.
Aircraft classification uses native `IsFlyingType`; the separate air rate settings apply to both player-driven and assigned AI aircraft.
Player-driven vehicles use `LoadRadius`/`UnloadRadius`; assigned AI vehicles use their separate ground/air radii.
Actual transfer range is a 3D distance, including altitude.

Transfers run on a one-second tick. Loading removes resources from a friendly harvester up to the available source amount, transfer rate and remaining transport capacity.
Unloading uses friendly structures with resource deposit points, respects remaining team storage and subtracts only the amount actually accepted.
Player transfers try unloading before loading. A completed delivery starts the configured cooldown.
Waiting alongside an empty harvester can keep an existing loading session open without repeating its start feedback.

`/logistics off` releases harvester reservations, returns eligible harvesters to native behavior, stops managed transport duty, ends transfer sessions and pauses reward accrual/payouts and scoring.
Surviving cargo, already-earned pending credits and individual sub-switch settings remain.
Re-enabling resumes eligible harvesting and pending payouts; transports need a fresh Follow assignment to resume AI duty.
The harvesterfix sub-switch does not disable the separate transport transfers or reward functions.

Cargo and automation state are temporary. Match start/end and scene cleanup clear cargo, assignments, pending credits and score remainders.
Cargo is not saved across server restarts. Destroying a transport loses its cargo.
Cancelling AI duty does not discard surviving same-team cargo; a player can still use it.
Disconnect removes that player's reward accounts and fractional score state.

## Personal-credit and player-score rewards

### Personal credits

While the master switch is on, player harvester refinery deliveries earn `PersonalBonus.HarvesterShare` (default `0.07`) and player transport deliveries earn `TransportShare` (default `0.09`).
`TransferShare` (default `0.06`) separately rewards the harvester driver when a player-driven transport collects from it.
These pickup rewards do not credit the transport driver for collection; its delivery reward is earned when resources reach the refinery.
AI-only work earns no personal driver reward.

Credits require a compatible active wallet API: King of the Hill's, or Si_Buy's when King of the Hill does not provide one. They are separate from the team's resource pool and normal round score.
Earned credit amounts accumulate per connected player, retaining fractional credit between batches.
`DeliveryCooldownSeconds` sets the delay for a new payout window; later earnings do not push an existing due time back.
Rate changes affect newly earned rewards. Cooldown changes affect new payout windows and new delivery cooldowns, not timers already scheduled.
Zero cooldown makes a new payout eligible on the next update. Unavailable-provider retries have a minimum one-second interval.
An ambiguous external grant exception does not automatically retry the attempted batch, to avoid duplicate payment.
Successful payout announcements follow `PersonalBonus.AnnounceToPlayer`.

### Player round score

`LogisticsScore` defaults to `true`. `ScoreUnload` defaults to 2 points per 100 Balterium actually accepted at a refinery from a player-driven transport or harvester.
`ScoreTransfer` defaults to 1 point per 100 Balterium transferred from a player-driven harvester to a player-driven transport; only the harvester driver receives pickup score.
Autonomous vehicles without player drivers earn no player score.

Awards update normal `Player.Score` silently and do not require KoH.
Fractional points carry forward per player, separately for unloads and transfers; rate changes apply to newly handled cargo.
Disabling player Logistics score clears its remainders without removing awarded score.
Remainders also clear on disconnect and match/scene cleanup. The master switch gates earning.

## Commander round score

`CommanderScore.Enabled` follows the master switch independently of the player delivery-score switch.
Awards and penalties silently update the team's current commander's normal round score.
`/logistics status` shows enabled/active state and purchase-exclusion hook readiness.
No commander means no award. Only an ongoing server match scores.

Edit `CommanderScore.Rules`, then reload:

| Event | Default rule |
| --- | --- |
| `UnitProduced` | Resource cost / 100 |
| `UnitLost` | Negative resource cost / 100 |
| `BuildingCompleted` | Resource cost / 50 |
| `BuildingLost` | Negative resource cost / 50 |
| `TechnologyReached` | 500 per newly reached tier |
| `EnemyUnitDestroyed` | Enemy unit resource cost / 100 |
| `EnemyBuildingDestroyed` | Enemy structure resource cost / 50 |
| `BalteriumDeposited` | 1 per 1,000 actually accepted at a refinery |

For object-value rules, divide first, round down and apply the configured minimum before the signed multiplier; the default minimum is 1.
Rules support `Enabled`, `Divisor`, `Minimum`, signed `Multiplier` and these `ValueSource` choices:

- `Cost`: the object's resource cost.
- `Score`: the object's native score value.
- `Fixed`: `FixedPoints` times `Multiplier`.
- `Amount`: deposited resource amount divided by `Divisor`, times `Multiplier`.

`Objects` filters by ObjectInfo name or display name; an empty list matches all.
`Overrides` maps those names to exact final signed points, bypassing the formula.
For example, `"Overrides": { "Harvester": -50 }` on a `UnitLost` rule assigns that named object a 50-point penalty.
Names must match actual game data, case-insensitively. Multiple matching rules add together.
Unknown event names/value sources award nothing. An empty rules list disables rule awards.
The `Minimum` and rounding apply to object-value rules; `Fixed` uses its signed configured result directly, and deposit `Amount` rules retain fractional points.

Only successful native production/construction earns build score. Starting units, respawns, failed/cancelled sites and arbitrary mod spawns do not.
Losses are charged to the commander at destruction, including starting units, except units directly controlled or driven by a player at that moment.
Objects bought through compatible `Si_Buy` or legacy KoH purchase handlers are marked at spawn and excluded from production/loss/combat score for their lifetime in the round, including after their buyer leaves.
Logistics observes those purchase scopes through Harmony. Unknown handler signatures or unavailable required hooks block commander scoring so exclusions remain intact.
Absence of a purchase-provider mod by itself does not require one to be installed.

Enemy destruction awards require an AI-controlled attacking unit or an automated structure turret.
Player-controlled and purchased attackers earn no commander kill score; allied/neutral targets earn none.
Player-controlled enemy targets can still earn the attacking commander's reward.
Duplicate construction/destruction callbacks are ignored, and round cleanup earns nothing.

Technology milestones are tracked per team even while scoring is disabled.
Starting tiers and re-reaching a lost tier earn nothing; a multi-tier jump earns 500 for each new tier by default.
Deposit fractions are kept per rule, commander and team; reload, disabling commander scoring, disconnect and round reset clear them.
Deposits include autonomous and player deliveries through the observed refinery paths.
Team-credit grants and harvester-to-transport pickups do not count as refinery deposits.

## Wasp harvest

The master switch and `WaspHarvest.Enabled` must both be enabled. Wasp harvest is server-only; clients need no mod.
A player-flown Wasp harvests Biotics the way the Shrimp does. Each creature named in `WaspHarvest.Units` (default `Wasp`) receives the Shrimp's two native harvesting components, a cargo holder and mining claws, on the server when it spawns.
They are added to the spawned instance after its networked scripts are registered, so no additional network data is produced and clients see a vanilla Wasp.
A creature that already carries these components, or has no primary attack, is left untouched.

A primary-attack hit extracts `ExtractPerHit` Biotics within `ExtractionRadius` of the hit point when a player controls the Wasp, the cargo has room and the hit point is at most `GroundMaxHeight` above terrain or static ground.
AI-controlled Wasps never extract. A hit that finds no Biotics remains a normal attack. Extraction uses the native patch call, so the patch is debited exactly as for a Shrimp.
Extraction is server-side while melee damage is evaluated by the client, so a harvesting swing can still damage what it hits.

To unload, the Wasp must hold still inside a friendly resource deposit point. Among the alien structures only the Bio Cache has one; the Nest does not.
The native deposit tick moves `DepositAmount` Biotics every `TickSeconds`, limited by remaining team storage. The team receives the full amount.
With `DepositCooldownSeconds` above zero, a visit begins at its first deposit tick and unloads at most `MaxCarry`; the next visit can begin once the cooldown has elapsed since that start. Zero removes both limits.

A visit closes when the cargo is empty, the load is used up or deposit ticks have stopped for about three ticks.
The pilot's `RewardShare` of the delivered amount then joins the batched personal-credit payout described below, with the same wallet, payout delay, fractional carry and match limits.
A delivery made while nobody controls the Wasp pays nobody, and a visit still open when the match ends is not rewarded. Wasp deliveries award no round score and do not count for commander `BalteriumDeposited` rules.
The pilot sees a cargo bar while harvesting and unloading, at most one line per `NotifyIntervalSeconds`, plus cargo-full, too-high, cooldown and delivery messages.

Setting changes apply to Wasps already in the field. While a switch is off, equipped Wasps keep their cargo and can still unload it, but cannot extract; Wasps spawned meanwhile are not equipped.
At match end the added components are removed from surviving units. `/wasp` totals cover the current round.
The standalone `Si_WaspHarvest.dll` provides the same feature and command. When it is loaded, the built-in Wasp harvest stays off and `/logistics` reports the conflict; use only one of the two.
Mods that scan units for resource holders will also see equipped Wasps.

## Harvester coordination

This controller operates on eligible AI-driven human harvesters. The master switch and `harvesterfix.Enabled` must both be enabled.
When disabled, its patches pass through to native harvester AI and waiting managed vehicles are returned to native harvesting/deposit orders.
Native AI owns initial factory exit and harvesting orders; Logistics does not dispatch new/idle harvesters after a startup timer.
It adopts harvesting/deposit work after native AI or a player requests it.

Current JSON defaults, within the main config object:

```json
"harvesterfix": {
  "Enabled": true,
  "MaximumReservationsPerRefinery": 5,
  "NearPatchDistanceMetres": 800.0,
  "MediumPatchDistanceMetres": 1500.0,
  "NearPatchReservations": 1,
  "MediumPatchReservations": 2,
  "FarPatchReservations": 3,
  "TransferWaitingTimeSeconds": 120.0,
  "ReservationCheckTimeSeconds": 10.0,
  "ReservationDistanceIncrementMetres": 50.0
}
```

### Home patches and reservations

Managed harvesters select a discovered, non-depleted resource area with an available reservation.
Reservations are per team and resource area, not individual resource cell.
A home-patch slot remains reserved during mining, transport top-ups, refinery travel, queuing, unloading and the return trip.
Changing refineries or giving a manual unload order preserves the home patch.

Patch limits use horizontal straight-line distance from the patch's `SignalCenter` to the closest surviving friendly refinery with a matching resource deposit point:

| Distance to nearest friendly refinery | Automatic reservations |
| --- | --- |
| Up to and including 800m | 1 |
| Over 800m, below 1,500m | 2 |
| 1,500m and above, or no friendly refinery | 3 |

Limits apply when admitting new assignments. A closer newly built refinery does not evict existing owners or change their home patches; new assignments wait until capacity allows them.
Manual placement can exceed physical occupancy without creating extra automatic reservations or displacing an owner.
Reservations persist for the current match only. There is no timer expiring a home assignment during a long refinery queue, and native stuck recovery remains active.
Depletion, an explicit different harvest assignment, manual move/stop/combat orders, destruction, team changes, player takeover, disabling, changed harvester settings on reload and match/scene cleanup release assignments.

Configuration validation clamps reservation counts to `1..32`, distance thresholds to `0..100000m` with the medium threshold no lower than near, pickup waiting time to `0..3600s`, reservation checks to `1..300s` and queue distance increments to `25..2000m`.
Non-finite values use defaults.

### Refinery selection and queues

Returning harvesters first consider the closest eligible friendly refinery or an explicitly requested one.
If a new arrival would take fourth place or later, it looks for a shorter queue with room at a refinery within 800m of that original refinery.
Among suitable alternatives, it chooses the closest to the harvester.
Otherwise it joins the original line if the configured reservation limit permits it; if no line has room, it waits at its patch and retries.
Existing tickets do not reshuffle as other refineries become less busy.

Admissions append to the back, including adoption of native docking vehicles.
A late entrant already inside a bay undocks natively before taking its waiting position.
Repeating an unload order for the same refinery preserves the ticket.
With defaults, the five slots are the active approach/dock plus waits at 100m, 150m, 200m and 250m outside the native approach point.
The first waiting distance is always 100m; `ReservationDistanceIncrementMetres` changes only later spacing.
A native reservation/occupant outside the controller takes the front slot.
The queue shares a direction derived from the native approach. Waiting vehicles advance on their 10-second checks; the active reservation remains held through undocking.

Refinery-local storage fullness does not affect selection or admission. Native resource distribution handles storage; full total team storage makes native unloading wait.
Waiting positions are projected onto terrain and checked against vehicle navigation, with up to 40m lateral adjustment.
An invalid waiting point causes waiting/retry. Native refinery approach points are allowed through to native docking rather than rejected by the ordinary waiting-point navigation test.
Complex terrain, traffic and physical docking still need in-game verification.

### Field pickups and manual control

Each successful Logistics pickup restarts the 120-second grace period.
At a productive resource patch, an eligible AI harvester resumes mining/top-up work after a manual stop/move and restores its native harvest order if needed.
Repeated transfer ticks extend the timer without repeatedly restarting movement. Human-driven harvesters retain driver control.
While the patch remains productive, the harvester mines when it has space and waits when full.
Depletion immediately cancels the grace period; subsequent pickups from the depleted patch cannot renew it.
A nonempty partial load leaving a depleted patch unloads first when an eligible refinery is closer than the next available patch, or when no next patch is available.

Native mining, local movement recovery, docking and undocking remain in use.
Explicit harvest/deposit orders use managed automation. Immediate explicit move/stop/combat orders release it, and a later harvest/deposit order enables it again.
Managed assignments reject competing autonomous AI orders. Existing other orders remain unmanaged when the feature is enabled, except completed nearby fallback moves on full stationary harvesters.

## Automatic AI transport duty

The master switch and `AItransport.Enabled` must both be enabled.
Give an eligible AI transport an immediate Follow/right-click order on a friendly harvester; the accepted native friendly-target order is `Defend`, and explicit Defend also enrolls.
Queued orders do not enroll. Unassigned transports retain native behavior.
A new accepted commander order cancels duty; a fresh Follow can enroll again with the clicked harvester as the initial preference, subject to collection limits and productive-patch eligibility.

Current JSON defaults, within the main config object:

```json
"AItransport": {
  "Enabled": true,
  "airloadradius": 90.0,
  "airunloadradius": 120.0,
  "groundloadradius": 75.0,
  "groundunloadradius": 80.0,
  "NearHarvesterDistanceMetres": 400.0,
  "MediumHarvesterDistanceMetres": 800.0,
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

### Allocation and continuous service

Duty survives destruction or patch changes of the collection harvester.
Collection slots are reserved immediately, released when leaving for delivery and reassigned after full unloading.
Productive AI harvesters are ranked by pickup travel, existing allocation, available cargo and nearest-friendly-refinery distance; ground travel receives a higher cost than air travel.
Human-controlled harvesters and docking/queued harvesters are not automatically selected. In-transit harvesters with productive patches can be eligible.
Distances are geometric estimates, not measured driving time or a guarantee of maximum throughput.

The horizontal distance from a harvester's current position to its closest friendly refinery determines the default shared collection allowance:

| Harvester-to-refinery distance | Ground OR air allowance |
| --- | --- |
| Up to and including 400m | 2 ground OR 1 air |
| Over 400m, up to and including 800m | 3 ground OR 1 air |
| Over 800m | 4 ground OR 2 air |

Each ground transport consumes `1/GroundLimit`; each aircraft consumes `1/AirLimit`. The sum cannot exceed 1.
The far band therefore allows two ground plus one air, rather than adding both maximum fleets.
Zero disables a vehicle class in a band. Changed distance bands or settings can release excess collectors on the next check; cargo remains aboard.

A productive assignment stays while progress continues.
Depletion, patch change, target destruction, excess allocation or the no-progress timeout triggers reassignment.
Progress means cargo received or at least 10m improvement in closest approach distance; there is no fixed maximum journey time.
A stalled target is avoided for one timeout period before becoming eligible again.
Full loads finish delivery. Partial loads are delivered when no next collection assignment is available or a reachable refinery is at least as close as the next selected harvester.
Empty idle transports try to park outside refinery transfer/bay traffic and retry; if no navigable point exists, they hold in place.
Missing refineries and full team storage retain cargo and cause waiting.
Collectors stop targeting depleted patches even when a harvester has residual cargo, allowing its normal routing to resume.

Native Move/Stop and collision avoidance handle travel. Refinery approach fallbacks can overlap harvester traffic; this controller does not manage a general traffic queue.
Terrain projection cannot guarantee path reachability. Player takeover, transport destruction, team change, disabling and match/scene cleanup end duty.

Configuration validation clamps AI radii to `1..2000m`, allocation distances to `0..100000m` with medium no lower than near, counts to `0..32`, reassignment checks to `1..300s` and no-progress timeout to `10..3600s`.
Air transfer rates are clamped to `1..100000` when configuration is loaded; the rate commands accept positive integers immediately.
Non-finite radii/times use defaults. See the separate player ranges and transfer defaults in the complete example configuration.

## Driver feedback and optional menu

`Chat.ProgressIntervalSeconds` defaults to 2. Zero keeps transfer start/stop messages only.
Start messages identify the cargo total, progress bars show percentages, and a final message reports stop/completion and any delivery cooldown.
The bar length and filled/empty characters are JSON settings. `Commands.HideFromChat` controls whether command text is hidden from public chat.

Sound WAVs are loaded at runtime, not embedded in the DLL.
Copy `sounds/logistics_tick.wav` and `sounds/logistics_ding.wav` into the server's `UserData/sounds` folder.
The controlling player hears the configured `Sounds.OnLoad` at transfer start and `Sounds.OnFull` at stop/completion; the latter is not restricted to a full load.
AI-only transports are silent and missing files are skipped.
Playback uses Admin Mod's voice channel and may show the speaking indicator.

The optional schema-2 `ModUIData()` provider describes existing server settings/commands for compatible ServerModUI/ClientModUI companions.
Status/help/reload appear as actions, feature switches as toggles, and capacities, shares, score rates, ground/air rates, player/AI ranges, cooldown and progress interval as sliders with SET actions.
The same server handlers enforce Generic permission and save changes.
Complex commander rules, allowlists and allocation/reservation tuning remain JSON settings.
Players need no client mod for core gameplay, and Logistics does not require a UI DLL.
Ranks reward multipliers use their own reviewed Logistics DLL hash and earning-site checks; compatibility must be reviewed separately after a DLL change.

## Performance and diagnostics

All mod-owned server logging, including startup, status, warnings and errors, follows `Debug`; player chat replies are unaffected.
MelonLoader can still print its own loading messages.
With debug enabled, `/logistics status` dumps per-harvester phase/cargo/dock state/queue and native bay owners under `[HarvesterFix]`, and duty/target/patch/refinery/cargo/radii/band/movement state under `[AITransport]`.
Controller assignment/phase changes also log while debugging.

Implementation details relevant to diagnosis:

- Player transports try their cached harvester/refinery first each tick, rechecking team, destruction, resource capability and range. Empty/invalid sources lose priority and fall back to the shared list in its existing order, rather than a nearest-target search. An eligible cached target keeps priority in overlapping ranges. AI duty uses its assigned target exclusively.
- Transfer range gates use squared 3D distances. Each synchronous tick reuses lists of harvesters, transports and deposit structures, plus nearest-refinery distances. Eligibility and resource/reservation state remain live; lists and distances clear in a `finally` block.
- Identical approach queries can reuse results only within the same tick/frame for the same transport position, target center, radius and refinery. There is no persistent navigation cache. Candidate scoring skips occupancy work after a candidate already scores worse; stationary failed movement searches retain their five-second retry interval.
- Reward accounts are checked when the earliest payout is due. Native deposit tracking uses a bounded per-thread pool of cleared receipts, preserving nested-call attribution and actual accepted-resource accounting.
- A transport update exception identifies the vehicle in debug output and backs it off for ten seconds while the rest of the fleet continues. Recovery does not reset cargo accounting; match/scene cleanup clears the backoff.

`[LogisticsPerf]` summaries every 30 seconds report tick count, average/maximum milliseconds, approach searches, navigation probes and same-frame approach-cache hits.
They measure the transfer/allocation loop, not separately patched native harvester/mining updates.
Counters are inactive with debug off. Verbose diagnostics add overhead; disable debug after taking a sample.
No measured speedup is claimed. Compare similar populated matches and unit counts when interpreting timing output.

## Focused live checks

See the [README](../README.md#build-from-source) for building and installation.
Build references must come from the dedicated-server Mono assemblies, rather than client IL2CPP assemblies.
Compilation does not establish in-game behavior. The user handles live checks and server lifecycle.

### Harvester and queue checks

1. Disable and re-enable harvesterfix; confirm native harvesting while off and release/recovery of managed vehicles.
2. Build several harvesters. Check native factory departure, patch reservations across the 800m/1,500m boundaries and return to the same home patch after unloading. Build a closer refinery and confirm owners are retained.
3. Send at least six loaded harvesters to one refinery. Check FIFO bay admission, 100m/150m/200m/250m waits and the sixth waiting at its patch when no alternative has room. Add a less busy refinery within 800m and check diversion for a new fourth-or-later arrival.
4. Drain a full AI harvester repeatedly within 120 seconds; confirm mining top-up and normal routing after the last pickup's grace period. Repeat after a manual stop/move and with depletion during the hold.
5. Deplete a half-loaded harvester's patch and compare a nearer refinery with a nearer available patch. Check full team storage and destroyed refineries.
6. Take control, issue manual commands, reload an unrelated setting and toggle the controllers. Confirm correct release of control and preservation of queues/home patches when harvester settings do not change.

For a stalled queue, fill harvesters with the fix off and occupy the refineries, then enable it and issue deposit orders.
After the first vehicle unloads and clears the bay, the next should advance within the check interval.
Run `/logistics status` with debug enabled before moving a stalled vehicle so its state is captured.

### AI transport, rewards and feedback checks

1. Enroll ground and air transports near the 400m/800m allocation boundaries. Check shared mixed-fleet limits, reassignment of excess vehicles, cancellation by new orders and fresh Follow enrollment.
2. Deplete or destroy a collection target. Confirm duty persists, the old target is released and partial cargo is delivered or reassigned without a new commander order.
3. Watch several delivery cycles, including both factions' allowed aircraft. Verify 500/s air and 300/s ground rates, player versus AI ranges and the 20-second delivery cooldown under default settings.
4. Check player harvester deposits, player transport deposits and player-to-player pickups against the separate 7%/9%/6% credit shares and 2/1 round-score rates. Repeat with zero cooldown, full storage and unavailable wallet; confirm actual accepted amounts and fractional carry.
5. Check Move/Stop, player takeover, disabling/re-enabling, missing refineries, no productive patches and blocked paths. Cargo and manual control should remain consistent.
6. Check start/stop sounds, progress interval zero and nonzero, payout announcements and optional ModUI permissions. Compare equivalent populated matches before drawing performance conclusions.

### Wasp harvest checks

1. Fly a Wasp over a Biotics patch and attack the ground. Confirm the cargo bar, the patch amount dropping, the cargo-full message at 500 and no extraction above 4m or by an AI-controlled Wasp.
2. Hover still inside a Bio Cache. Confirm 50 Biotics per second reach the team, the delivery message, the 40% credit share in the next payout and the 30-second cooldown on an immediate second visit. Confirm the Nest accepts nothing.
3. Change a setting with `/wasp set` while a Wasp is airborne and confirm it applies to that Wasp. Toggle Wasp harvest and the master switch; carried cargo should still unload.
4. Join with an unmodded client and confirm the Wasp looks and attacks as usual. Confirm Shrimp harvesting and harvester refinery rewards are unchanged.
