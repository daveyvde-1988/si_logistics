# Logistics

Move Balterium from harvesters to refineries, reward the drivers, and keep the
team's supply runs moving. Logistics runs on the Silica dedicated server with
MelonLoader and Admin Mod. Players do not need a client mod.

## Features

- **Player hauling:** load friendly harvester cargo into ground or air transports
  and deliver it to friendly refineries.
- **Driver rewards:** earn personal credits through a compatible King of the Hill
  wallet, plus normal round score independently of that wallet.
- **Harvester coordination:** stable resource-patch assignments, refinery queues
  and continued mining while transports collect cargo.
- **AI hauling:** assign transports to repeated collection and delivery duty.
- **Wasp harvest:** player-flown Wasps harvest Biotics and deliver them to a
  Bio Cache for a personal credit share.
- **Commander score:** configurable rewards for production, construction,
  technology, combat and deliveries, with penalties for losses.
- **Server controls:** configure transfer rates, capacities, ranges and rewards
  through commands, JSON or compatible optional ModUI companions.

## Install and use

1. With the server stopped, place `Si_Logistics.dll` in its `Mods` folder.
2. For optional driver sounds, copy the two WAV files from [sounds/](sounds/)
   into the server's `UserData/sounds/` folder.
3. Start the server. Logistics creates its configuration if none exists.

The default transports are **Shuttle, Dropship, Squad Transport and Platoon
Hauler**. Drive or fly near a friendly harvester to load automatically, then near
a friendly refinery to unload. Transfers respect available resources, cargo
capacity and team storage space.

To automate an AI transport, give it an immediate Follow/right-click order on a
friendly harvester. It continues collection and delivery until another accepted
commander order cancels duty. Harvester coordination is enabled by default.

Alien players can harvest too: fly a **Wasp** low over a Biotics patch and hit
the ground with the primary attack, then hover still inside a friendly Bio Cache
to unload. Only player-flown Wasps harvest; AI Wasps are unchanged.

## Common commands

`/logistics` and `/logistics help` are available to everyone. `/logi` is an alias
for `/logistics` and all its subcommands. Setting changes require Admin Mod
**Generic** permission; server-console callers are also allowed.

| Command | Purpose |
| --- | --- |
| `/logistics` | Show settings, automation status and your transport cargo. |
| `/logistics help` | List commands. |
| `/logistics on` / `/logistics off` | Enable or disable the master switch. |
| `/logistics harvesterfix on` / `/logistics harvesterfix off` | Toggle harvester coordination. |
| `/logistics aitransport on` / `/logistics aitransport off` | Toggle AI transport duty. |
| `/logistics reload` | Apply edited JSON settings. |
| `/logistics debug on` / `/logistics debug off` | Toggle server diagnostics. |
| `/wasp` / `/wasp help` | Show your Wasp harvest and Bio Cache cooldown, or how to harvest. |
| `/wasp set enabled on` / `/wasp set enabled off` | Toggle Wasp harvest. |

For capacities, rates, ranges, reward shares, sound options and scoring commands,
see the [complete command reference](docs/REFERENCE.md#commands).

## Configuration and defaults

Settings are saved in `UserData/cfg/Logistics/Si_Logistics_Config.json`.
Existing values take priority over defaults. Commands save changes immediately;
use `/logistics reload` after editing the JSON manually.

| Setting | Ground | Air |
| --- | --- | --- |
| Cargo capacity | 3,500 | 5,000 |
| Loading / unloading rate | 300 / 300 units per second | 500 / 500 units per second |
| Player pickup / delivery range | 45 / 70m | 45 / 70m |
| AI pickup / delivery range | 75 / 80m | 90 / 120m |

Default personal credit shares are **7%** for harvester refinery deliveries,
**9%** for transport deliveries and **6%** for the harvester driver on eligible
player-to-player pickups. The delivering driver earns **2 points per 100**
accepted at a refinery. Player-to-player pickups award the harvester driver
**1 point per 100** transferred.

A Wasp carries **500** Biotics, takes **20** per hit and unloads one load per
**30 seconds** at a Bio Cache; its pilot earns a **40%** credit share.

The [example configuration](Si_Logistics_Config.example.json) lists all defaults.
The [detailed reference](docs/REFERENCE.md) explains rewards, commander rules,
automation, configuration migration and focused in-game checks.

## Documentation

- [Three-page overview (PDF)](docs/Logistics_Overview.pdf): purpose, functions,
  controls and operating limits.
- [Editable overview](docs/Logistics_Overview.md).
- [Harvester behavior brief (PDF)](docs/Logistics_Harvester_Behaviour_Summary.pdf).
- [Detailed behavior and configuration reference](docs/REFERENCE.md).

## Build from source

Keep this repository under the dedicated server's `Silica-1283/si_logistics`
folder, retain its bundled reference DLLs, and install the .NET SDK. From the
repository folder, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The project targets `netstandard2.1`; the current source version is **0.9.3**.
Output: `bin/Release/netstandard2.1/Si_Logistics.dll`. Building does not install the
mod or verify its in-game behavior.
