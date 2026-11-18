# Logistics
Purpose and functions | Draft overview | Source version 0.9.3 | 11 November 2026

## Why the mod exists
Logistics makes Balterium hauling a useful team role: players can move resources from harvesters to refineries, receive credit for that work, and support a supply network that also operates through AI transports. Its harvester controller coordinates patch assignments and refinery queues to reduce repeated commander intervention. This purpose is inferred from the implemented features.

## Hauling and player rewards
- **Resource transport.** Supported vehicles collect from friendly harvesters and deliver to friendly resource-deposit structures. The default allowlist is Shuttle, Dropship, Squad Transport and Platoon Hauler. Transfers respect cargo capacity, source resources, team storage room and range; deliveries count the amount actually accepted.
- **Personal credits.** Player harvester refinery deliveries earn a 7% share, player transport deliveries earn 9%, and Wasp pilots earn 40% of accepted Biotics. A player harvester driver earns a separate 6% share when a player-driven transport collects its cargo. Si_Buy pays the accumulated rewards with fractional carry. AI-only work earns no personal driver reward.
- **Player round score.** Player deliveries earn 2 points per 100 Balterium accepted at a refinery. Player-to-player pickups earn the harvester driver 1 point per 100 transferred. Separate fractional remainders are retained. This normal round score does not require the personal-credit wallet.

## Commander round score
Configurable rules reward the current commander for supply and team progress, and penalize losses. This controller follows the master switch independently of the player delivery-score switch.

| Event | Default score |
| --- | --- |
| Unit produced / lost | + / - resource cost divided by 100 |
| Building completed / lost | + / - resource cost divided by 50 |
| Enemy unit / building destroyed | + enemy cost divided by 100 / 50 |
| Newly reached technology tier | +500 per new tier |
| Balterium accepted at a refinery | +1 per 1,000 |

Object-cost formulas round down with a minimum magnitude of 1. Rules can use cost, native score, fixed points or deposited amount; filters and named overrides are supported. Purchased objects are excluded. Loss penalties exclude player-controlled units; combat awards require an eligible AI attacker or automated turret. Starting production, repeated tiers, duplicate callbacks and round cleanup do not earn awards. Unknown purchase-handler signatures block commander scoring to preserve purchase exclusions.

## Feedback for drivers
Cargo/status replies, loading and unloading start/stop messages, a progress bar every 2 seconds, optional reward announcements and optional WAV sounds explain what is happening. The tick marks a transfer start; the ding marks its stop or completion. AI transports are silent; missing sound files are skipped. Commands can be hidden from public chat.

## Source basis
Canonical repository: Silica-1283/si_logistics, HEAD 233bc4f. Reviewed 11 November 2026. Transfers/commands: Si_Logistics.cs; rewards: LogisticsRewards.cs and LogisticsScore.cs; commander rules: CommanderScore.cs; automation: HarvesterFix.cs, AITransport.cs and AITransportAllocation.cs; menu/diagnostics: ModUIData.cs, ModUI_Export.cs and LogisticsPerformance.cs. Defaults cross-checked against Si_Logistics_Config.example.json. This draft describes the inspected implementation; no new build or in-game verification was performed for this review.

<!-- pagebreak -->

# Automation and current defaults
Two independent controllers operate beneath the master Logistics switch.

## Harvester coordination
- **Home patches.** AI-controlled human-faction harvesters retain a reserved patch through the refinery round trip. Default automatic reservations are 1 at up to 800m from a friendly refinery, 2 above 800m and below 1,500m, and 3 at 1,500m or farther, or when no refinery exists. Building a closer refinery does not evict existing owners.
- **Refinery queues.** First-in, first-out reservations allow 5 harvesters including the active bay. The first waiting vehicle stands 100m from the approach point; later positions add 50m. An arrival that would join fourth or later considers a shorter queue at a refinery within 800m of the original refinery. Otherwise it queues or waits at its patch when the line is full.
- **Field collection.** Each successful transport pickup renews a 120-second mining/top-up period. AI harvesting resumes where appropriate; depletion immediately cancels the hold. A partially loaded harvester leaving a depleted patch may unload first when the refinery is closer than the next patch.
- **Control and recovery.** Native mining, movement recovery, docking and undocking remain in use. Explicit move/stop/combat orders release managed control; harvest/deposit orders enable it again. Player takeover, destruction, team changes and match/scene cleanup release reservations. Queue checks run every 10 seconds by default.

## Automatic transport duty
- **Enrollment.** Give an eligible AI transport an immediate Follow/right-click order on a friendly harvester (the native Defend order). Unassigned transports keep native behavior. A new accepted commander order cancels duty; a fresh Follow can enroll it again. Queued orders do not enroll.
- **Allocation.** Productive AI harvesters are ranked by pickup travel, existing allocation, available cargo and refinery distance. Default bands are up to 400m, above 400m through 800m, and above 800m. Each permits 2/3/4 ground transports OR 1/1/2 air transports. Mixed fleets share one fractional allowance; the far band can hold 2 ground plus 1 air.
- **Continuous service.** Duty survives loss of the collection harvester. Depletion, patch changes, excess allocation or 120 seconds without progress trigger reassignment. Full loads deliver; partial loads may deliver when appropriate. Idle vehicles attempt to park clear of refinery traffic. Missing refineries or full team storage cause waiting with cargo retained.

## Transfer defaults
Existing saved settings take priority. The complete example JSON is the reference for new defaults.

| Setting | Ground | Air |
| --- | --- | --- |
| Cargo capacity | 3,500 | 5,000 |
| Loading / unloading rate | 300 / 300 units per second | 500 / 500 units per second |
| Player pickup / delivery range | 45 / 70m | 45 / 70m |
| AI pickup / delivery range | 75 / 80m | 90 / 120m |

The delivery cooldown is 20 seconds and also sets the delay for new driver payout windows. Transfer ranges use 3D distance, including altitude. Allocation distances are geometric estimates; native pathfinding, terrain and traffic still affect journeys. This is not a general traffic or collision controller.

<!-- pagebreak -->

# Controls and operation
Status and help are public. Setting changes require Admin Mod Generic permission; the server console is also allowed.

## Command reference
Use /logistics before each subcommand below. /logi is an alias for all of these subcommands. Choices are separated by /; n means a positive integer.

| Subcommand | Function / accepted values |
| --- | --- |
| status / help | Settings, feature states, fleet totals, refinery count and your transport cargo / command help |
| on / off; enabled on / off | Master switch (two equivalent forms) |
| harvesterfix on / off; aitransport on / off | Individual automation switches |
| harvestershare; transportshare; transfershare | Follow the chosen name with a reward share from 0 to 1 |
| capacity air / ground n | Cargo capacity |
| rate load / unload / airload / airunload n | Ground or air transfer rate, units per second |
| radius load / unload metres | Player pickup or delivery range, greater than zero |
| radius aiairload / aiairunload metres | AI air range, 1 to 2,000m |
| radius aigroundload / aigroundunload metres | AI ground range, 1 to 2,000m |
| cooldown seconds | Delivery cooldown and new payout delay, zero or greater |
| sounds on / off; announce on / off | Driver sounds / personal reward announcements |
| progress seconds | Progress interval, 0 to 60; zero keeps start/stop messages |
| debug on / off; reload | Diagnostics / reload the saved JSON configuration |

Separate admin commands: /logisticsscore on|off; /scoreunload 0..100; /scoretransfer 0..100; /logisticsscorecommander on|off. The older spelling /logisticsscorecomander remains an alias. Score rates are points per 100 Balterium.

## Configuration and dependencies
- **Saved settings.** UserData/cfg/Logistics/Si_Logistics_Config.json. Commands save changes. Reload applies manual JSON edits. The legacy UserData/Logistics_cfg configuration is imported only when the new destination is missing. Missing settings inherit defaults. Commander rules, transport types, allocation/reservation tuning and some presentation options are JSON settings.
- **Server installation.** Dedicated-server Mono/netstandard2.1 mod using MelonLoader and Admin Mod APIs. Install Si_Logistics.dll in the server Mods folder. Optional sounds come from UserData/sounds/logistics_tick.wav and logistics_ding.wav. Players need no client mod for gameplay.
- **Optional integrations.** Si_Buy supplies personal credits independently of normal player score. Compatible Si_Buy/legacy KoH purchase handlers support commander purchase exclusions. Schema-2 ModUI metadata exposes controls through compatible ServerModUI/ClientModUI companions. Ranks multipliers require a reviewed adapter for the Si_Buy payout path.

## Diagnostics and lifecycle
Debug enables controller/vehicle diagnostics and 30-second transfer/navigation timing summaries. Shared per-tick lists and caches reduce repeated work; an individual transport error receives a 10-second backoff. No measured speedup is claimed here.

Master off releases automation and pauses transfers and reward processing, retaining surviving cargo and earned pending credits. Re-enabling AI duty requires a fresh Follow assignment. Match/scene cleanup clears temporary cargo, assignments and reward/score state; cargo is not saved across server restarts. Destruction loses the vehicle's cargo. Reload preserves harvester reservations unless its settings changed.

