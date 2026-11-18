using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using SilicaAdminMod;
using UnityEngine;

namespace Si_Logistics
{
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
            if (WaspByClaws.Count == 0 || !WaspByClaws.TryGetValue(__instance.GetInstanceID(), out var state)) return true;
            if (!WaspHarvestActive || !MatchAcceptsRewards) return false;
            try
            {
                var holder = state.Holder;
                if (!state.Alive || !holder.ResourceType) return false;
                Player pilot = state.Unit.ControlledBy;
                if (pilot == null)
                {
                    if (!WaspCanExtract(state, attackPosition) || holder.RemainingSpace <= 0) return false;
                }
                else if (holder.RemainingSpace <= 0 || !WaspNearGround(attackPosition))
                {
                    if (ResourceArea.GetResourceAmount(holder.ResourceType, attackPosition, __instance.ExtractionRadius) > 0)
                    {
                        if (holder.RemainingSpace <= 0) WaspCargoFullHit(state, pilot);
                        else WaspTooHigh(state, pilot);
                    }
                    return false;
                }
                __state = holder.AmountStored;
                return true;
            }
            catch (Exception error)
            {
                WaspWarn("extract", "Wasp harvest extract prefix: " + error.Message);
                return false;
            }
        }

        private static void WaspExtractPostfix(MiningClaws __instance, int __state)
        {
            if (__state < 0 || !WaspHarvestActive) return;
            try
            {
                if (!WaspByClaws.TryGetValue(__instance.GetInstanceID(), out var state) || !state.Alive) return;
                int gained = state.Holder.AmountStored - __state;
                if (gained > 0 && state.Unit.ControlledBy != null) WaspExtracted(state, state.Unit.ControlledBy, gained);
            }
            catch (Exception error) { WaspWarn("extracted", "Wasp harvest extract postfix: " + error.Message); }
        }

        private static bool WaspNearGround(Vector3 point)
        {
            const float lift = 2f;
            int mask = GamePhysics.TRACELAYERMASK_TERRAIN.value | GamePhysics.TRACELAYERMASK_STATIC.value;
            return Physics.Raycast(point + Vector3.up * lift, Vector3.down, out _, Cfg.WaspHarvest.GroundMaxHeight + lift, mask, QueryTriggerInteraction.Ignore);
        }

        // The melee damage call follows the attack-hit delegates, which must still run to extract.
        private static IEnumerable<CodeInstruction> ObserveWaspMelee(IEnumerable<CodeInstruction> instructions)
        {
            var apply = AccessTools.Method(typeof(DamageManager), nameof(DamageManager.ApplyDamage),
                new[] { typeof(Collider), typeof(float), typeof(EDamageType), typeof(GameObject), typeof(Vector3) });
            var replacement = AccessTools.Method(typeof(LogisticsMod), nameof(ApplyWaspMelee));
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (!instruction.Calls(apply)) { yield return instruction; continue; }
                yield return new CodeInstruction(instruction) { opcode = OpCodes.Ldarg_0, operand = null };
                yield return new CodeInstruction(OpCodes.Call, replacement);
                replaced++;
            }
            if (replaced != 1) throw new InvalidOperationException("Expected one native creature melee damage call.");
        }

        private static float ApplyWaspMelee(DamageManager target, Collider collider, float damage, EDamageType type,
            GameObject instigator, Vector3 hitPoint, CreatureAttack attack)
        {
            if (WaspHarvestActive && WaspByUnit.Count != 0 && attack.CreatureOwner && attack.CreatureOwner.Team &&
                target.Team == attack.CreatureOwner.Team && WaspByUnit.ContainsKey(attack.CreatureOwner.GetInstanceID())) return 0f;
            return target.ApplyDamage(collider, damage, type, instigator, hitPoint);
        }

        private static void WaspClientMelee(object sender, OnPreDamageReceivedArgs hit)
        {
            if (!WaspHarvestActive || WaspByUnit.Count == 0 || hit == null || hit.Damage <= 0f || !hit.Instigator) return;
            try
            {
                var wasp = hit.Instigator.GetComponent<CreatureDecapod>();
                var target = hit.DamageManager.Owner;
                if (!wasp || !target || !wasp.Team || target.Team != wasp.Team || target == wasp ||
                    !WaspByUnit.ContainsKey(wasp.GetInstanceID())) return;

                // The client hit packet gives the instigator and damage but no weapon type.
                if (WithinWaspMeleeReach(wasp, wasp.AttackPrimary, target) || WithinWaspMeleeReach(wasp, wasp.AttackSecondary, target))
                    hit.Damage = 0f;
            }
            catch (Exception error) { WaspWarn("client-melee", "Wasp friendly damage filter: " + error.Message); }
        }

        private static bool WithinWaspMeleeReach(CreatureDecapod wasp, CreatureAttack attack, BaseGameObject target)
        {
            if (attack == null || !attack.IsMelee) return false;
            Transform origin = attack.AttackTransform ? attack.AttackTransform : wasp.SwarmBugModel.transform;
            Vector3 position = attack.GetAttackPosition(origin, wasp.GetViewRotation());
            Vector3 local = GameMath.InverseTransformPointUnscaled(target.transform, position);
            const float replicationMargin = 2f;
            float reach = attack.Radius + replicationMargin;
            return (target.PhysicalBounds.ClosestPoint(local) - local).sqrMagnitude <= reach * reach;
        }

        private static IEnumerable<CodeInstruction> ObserveWaspExtraction(IEnumerable<CodeInstruction> instructions)
        {
            var extract = AccessTools.Method(typeof(ResourceArea), nameof(ResourceArea.ExtractResource),
                new[] { typeof(Resource), typeof(Vector3), typeof(int), typeof(float) });
            var replacement = AccessTools.Method(typeof(LogisticsMod), nameof(ExtractWaspBiotics));
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (!instruction.Calls(extract)) { yield return instruction; continue; }
                yield return new CodeInstruction(instruction) { opcode = OpCodes.Ldarg_0, operand = null };
                yield return new CodeInstruction(OpCodes.Call, replacement);
                replaced++;
            }
            if (replaced != 1) throw new InvalidOperationException("Expected one native Biotics extraction call.");
        }
    }
}
