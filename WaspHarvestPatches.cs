using System;
using UnityEngine;

namespace Si_Logistics
{
    // Harmony patches of Wasp harvest (installed by InstallWaspHarvest):
    //
    //   Unit.OnPostInit                 postfix  equip matching creatures (WaspHarvestInjector.cs)
    //   MiningClaws.OnExtractAttackHit  prefix   added claws only: skip unless a player controls the
    //                                            creature, the cargo has room and the hit is near the ground
    //                                   postfix  cargo-bar chat line when the cargo grew
    //   ResourceHolder.RetrieveResource prefix   added holders only: Bio Cache cooldown; cap the request to
    //                                            what is left of the visit's one load, or return 0
    //                                   postfix  amount that left the cargo on this deposit tick
    //
    // On a unit holder, RetrieveResource is called only by the native deposit tick in
    // MiningClaws.OrderedUpdate, which already clamps the request to the team's remaining capacity.
    // That tick is the method the harvester-reward transpiler in LogisticsRewards.cs observes; its
    // receipt is opened only for UnitType.Harvester, so a Wasp is never paid twice.
    //
    // Claws and holders that were not added here (every Shrimp, every Harvester) leave after one
    // dictionary lookup. Each body is guarded: an exception must never reach the game's update loop.
    public sealed partial class LogisticsMod
    {
        private static void WaspSpawnPostfix(Unit __instance)
        {
            try { WaspUnitSpawned(__instance); }
            catch (Exception error) { WaspWarn("spawn", "Wasp harvest: equipping a unit failed: " + error.Message); }
        }

        private static bool WaspExtractPrefix(MiningClaws __instance, Vector3 attackPosition, out int __state)
        {
            __state = -1;
            bool ours = false;
            try
            {
                if (WaspByClaws.Count == 0 || !WaspByClaws.TryGetValue(__instance.GetInstanceID(), out var state)) return true;
                ours = true;
                if (!WaspHarvestActive) return false;
                var creature = __instance.Creature;
                var holder = state.Holder;
                if (creature == null || holder == null || holder.ResourceType == null) return false;
                Player pilot = creature.ControlledBy;
                if (pilot == null) return false;   // players only: an AI-controlled Wasp never extracts

                bool resourceHere = ResourceArea.GetResourceAmount(holder.ResourceType, attackPosition, __instance.ExtractionRadius) > 0;
                if (holder.RemainingSpace <= 0)
                {
                    if (resourceHere) WaspCargoFullHit(state, pilot);
                    return false;
                }
                if (!resourceHere) return true;   // nothing to take: the native code extracts 0 and the hit stays a normal attack
                if (!WaspNearGround(attackPosition))
                {
                    WaspTooHigh(state, pilot);
                    return false;
                }
                __state = holder.AmountStored;
                return true;
            }
            catch (Exception error)
            {
                WaspWarn("extract", "Wasp harvest extract prefix: " + error.Message);
                return !ours;   // never let an added claw extract ungated
            }
        }

        private static void WaspExtractPostfix(MiningClaws __instance, int __state)
        {
            if (__state < 0) return;
            try
            {
                if (!WaspByClaws.TryGetValue(__instance.GetInstanceID(), out var state) || state.Holder == null) return;
                int gained = state.Holder.AmountStored - __state;
                if (gained <= 0) return;
                var creature = __instance.Creature;
                Player pilot = creature != null ? creature.ControlledBy : null;
                if (pilot != null) WaspExtracted(state, pilot, gained);
            }
            catch (Exception error) { WaspWarn("extracted", "Wasp harvest extract postfix: " + error.Message); }
        }

        // Hit point at most GroundMaxHeight above terrain or static geometry. The ray starts 2 m above
        // the hit point, so a point slightly below the surface still counts.
        private static bool WaspNearGround(Vector3 point)
        {
            const float lift = 2f;
            int mask = GamePhysics.TRACELAYERMASK_TERRAIN.value | GamePhysics.TRACELAYERMASK_STATIC.value;
            return Physics.Raycast(point + Vector3.up * lift, Vector3.down, out _, Cfg.WaspHarvest.GroundMaxHeight + lift, mask, QueryTriggerInteraction.Ignore);
        }

        private static bool WaspRetrievePrefix(ResourceHolder __instance, ref int amount, ref int __result)
        {
            try
            {
                if (WaspByHolder.Count == 0 || !WaspByHolder.TryGetValue(__instance.GetInstanceID(), out var state)) return true;
                int allowed = WaspAllowedDeposit(state, amount);
                if (allowed <= 0 && amount > 0) { __result = 0; return false; }
                amount = allowed;
                return true;
            }
            catch (Exception error)
            {
                WaspWarn("retrieve", "Wasp harvest retrieve prefix: " + error.Message);
                return true;
            }
        }

        private static void WaspRetrievePostfix(ResourceHolder __instance, int __result)
        {
            if (__result <= 0 || WaspByHolder.Count == 0) return;   // nothing left the cargo (blocked by the prefix, or empty)
            try { if (WaspByHolder.TryGetValue(__instance.GetInstanceID(), out var state)) WaspDeposited(state, __result); }
            catch (Exception error) { WaspWarn("retrieved", "Wasp harvest retrieve postfix: " + error.Message); }
        }
    }
}
