using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_Logistics
{
    // Adds ResourceHolder + MiningClaws to every matching unit instance on the server.
    //
    // Hook: postfix on Unit.OnPostInit. Its only caller is BaseGameObject.Start, and Unity runs Start
    // after Awake of every component on the object, so NetworkComponent.Awake has already fixed the list
    // of networked scripts. Spawned units are not returned to the prefab pool, so OnPostInit runs once
    // per unit, before its first own update and long before a player's first attack input. The team is
    // assigned by the spawn call before Start as well.
    //
    // Order matters: MiningClaws.OnEnable runs inside AddComponent on an active object. It caches
    // GetComponent<ResourceHolder>(), subscribes to the creature's primary attack and registers its
    // ordered updates. So the holder is added first, then the claws; nothing else is needed for ticking.
    public sealed partial class LogisticsMod
    {
        private static Resource WaspBiotics;
        private static float WaspLastResolve = float.NegativeInfinity;
        private static readonly HashSet<string> WaspSkipLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static void WaspUnitSpawned(Unit unit)
        {
            if (!WaspHarvestActive || !Game.GetIsServer()) return;
            var creature = unit as CreatureDecapod;   // MiningClaws needs a CreatureDecapod on the same object
            if (creature == null || unit.ObjectInfo == null || unit.IsDestroyed ||
                WaspByUnit.ContainsKey(unit.GetInstanceID())) return;
            string name = unit.ObjectInfo.DisplayName ?? string.Empty;
            bool match = false;   // plain loop: this runs on every unit spawn, no closure allocation
            var units = Cfg.WaspHarvest.Units;
            for (int i = 0; i < units.Count && !match; i++) match = string.Equals(units[i].Trim(), name, StringComparison.OrdinalIgnoreCase);
            if (!match) return;

            var go = unit.gameObject;
            if (go.GetComponent<MiningClaws>() != null || go.GetComponent<ResourceHolder>() != null)
            {
                if (WaspSkipLogged.Add(name)) DebugMsg("Wasp harvest: '" + name + "' already has MiningClaws/ResourceHolder; not touched.");
                return;
            }
            if (creature.AttackPrimary == null)
            {
                if (WaspSkipLogged.Add(name)) DebugWarning("Wasp harvest: '" + name + "' has no primary attack; not equipped.");
                return;
            }
            var biotics = ResolveWaspBiotics(unit.Team);
            if (biotics == null)
            {
                WaspWarn("resource", "Wasp harvest: Biotics resource not found yet; '" + name + "' not equipped (retrying on later spawns).");
                return;
            }

            ResourceHolder holder = null;
            MiningClaws claws = null;
            try
            {
                // The claws bind directly to the component. Omitting it from Unit.ResourceHolders
                // preserves the Wasp's vanilla classification for FPS commands and other mods.
                holder = go.AddComponent<ResourceHolder>();
                holder.ResourceType = biotics;
                claws = go.AddComponent<MiningClaws>();
                claws.ExtractOnPrimaryAttack = true;
                ApplyWaspNumbers(claws, holder);
                RegisterWasp(new WaspState
                {
                    Unit = creature, Claws = claws, Holder = holder,
                    ClawsId = claws.GetInstanceID(), UnitId = unit.GetInstanceID()
                });
            }
            catch
            {
                DestroyWaspComponents(claws, holder);
                throw;
            }
        }

        private static void ApplyWaspNumbers(MiningClaws claws, ResourceHolder holder)
        {
            var c = Cfg.WaspHarvest;
            holder.MaxAmount = Math.Max(c.MaxCarry, holder.AmountStored);
            claws.ExtractionRadius = c.ExtractionRadius;
            claws.Tick = c.TickSeconds;
            claws.ExtractAmount = c.ExtractPerHit;
            claws.DepositAmount = c.DepositAmount;
        }

        private static void ApplyWaspConfigToLive()
        {
            if (!WaspHarvestInstalled || !Game.GetIsServer()) return;
            bool active = WaspHarvestActive;
            if (!WaspAIHarvestActive) CancelAllWaspJobs();
            foreach (var state in WaspByClaws.Values)
            {
                try
                {
                    if (!state.Alive) continue;
                    ApplyWaspNumbers(state.Claws, state.Holder);
                    if (state.Claws.enabled == active) continue;
                    if (active && state.VisitOpen) state.LastDeposit = Time.time;
                    state.Claws.enabled = active;
                }
                catch (Exception error) { WaspWarn("apply", "Wasp harvest: applying settings to a live unit failed: " + error.Message); }
            }
            if (!active) return;
            ResumeWaspRewards();
            if (Unit.Units != null)
                foreach (var unit in Unit.Units) WaspSpawnPostfix(unit);
        }

        // Removes the added components from units that still exist (OnDisable unsubscribes the attack hooks).
        private static void StripWaspClaws()
        {
            foreach (var state in WaspByClaws.Values)
            {
                try
                {
                    DestroyWaspComponents(state.Claws, state.Holder);
                }
                catch (Exception error) { WaspWarn("strip", "Wasp harvest: removing claws failed: " + error.Message); }
            }
        }

        private static void DestroyWaspComponents(MiningClaws claws, ResourceHolder holder)
        {
            if (claws)
            {
                claws.enabled = false;
                UnityEngine.Object.Destroy(claws);
            }
            if (holder) UnityEngine.Object.Destroy(holder);
        }

        // Biotics: the Shrimp prefab's cargo resource; else a Resource named like "Biotic"; else the alien
        // team's usable resource. Retried at most every 10 s while nothing is found.
        private static Resource ResolveWaspBiotics(Team team)
        {
            if (WaspBiotics != null) return WaspBiotics;
            float now = Time.realtimeSinceStartup;
            if (now - WaspLastResolve < 10f) return null;
            WaspLastResolve = now;
            string source = null;
            try
            {
                foreach (var info in Resources.FindObjectsOfTypeAll<ObjectInfo>())
                {
                    if (info == null || info.Prefab == null || !string.Equals(info.DisplayName, "Shrimp", StringComparison.OrdinalIgnoreCase)) continue;
                    var holder = info.Prefab.GetComponentInChildren<ResourceHolder>(true);
                    if (holder == null || holder.ResourceType == null) continue;
                    WaspBiotics = holder.ResourceType; source = "Shrimp prefab";
                    break;
                }
            }
            catch (Exception error) { WaspWarn("shrimp", "Wasp harvest: Shrimp template lookup failed: " + error.Message); }
            if (WaspBiotics == null)
            {
                try
                {
                    foreach (var resource in Resources.FindObjectsOfTypeAll<Resource>())
                    {
                        if (resource == null) continue;
                        if ((resource.name ?? string.Empty).IndexOf("Biotic", StringComparison.OrdinalIgnoreCase) < 0 &&
                            (resource.ResourceName ?? string.Empty).IndexOf("Biotic", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        WaspBiotics = resource; source = "resource asset";
                        break;
                    }
                }
                catch (Exception error) { WaspWarn("resourcename", "Wasp harvest: resource lookup failed: " + error.Message); }
            }
            if (WaspBiotics == null && team != null && team.UsableResource != null &&
                (team.TeamShortName ?? string.Empty).StartsWith("Alien", StringComparison.OrdinalIgnoreCase))
            { WaspBiotics = team.UsableResource; source = "team resource"; }
            if (WaspBiotics != null) DebugMsg("Wasp harvest: Biotics resource '" + WaspBiotics.name + "' via " + source + ".");
            return WaspBiotics;
        }
    }
}
