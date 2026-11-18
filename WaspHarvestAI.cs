using System;
using System.Collections.Generic;
using HarmonyLib;
using Silica.AI;
using UnityEngine;

namespace Si_Logistics
{
    public sealed partial class LogisticsMod
    {
        private enum WaspLeg { Seek, ToPatch, Harvest, ToCache, Unload }

        private sealed class WaspJob
        {
            internal WaspState State;
            internal Team Team;
            internal ResourceArea Area, FirstArea;
            internal ResourceDepositPoint Cache;
            internal AIOrder Order;
            internal uint OrderId;
            internal WaspLeg Leg;
            internal Vector3 Goal;
            internal float NextStep, Deadline, LastGain;
            internal int LastCargo;
            internal bool Landing, PreviousForceLand;
        }

        private static readonly Dictionary<int, WaspJob> WaspJobs = new Dictionary<int, WaspJob>();
        private static readonly Dictionary<ResourceArea, WaspJob> WaspReservations = new Dictionary<ResourceArea, WaspJob>();
        private static readonly Dictionary<ResourceArea, float> WaspUnreachable = new Dictionary<ResourceArea, float>();
        private static readonly HashSet<ResourceArea> WaspOccupied = new HashSet<ResourceArea>();
        private static readonly List<ResourceArea> WaspKnownAreas = new List<ResourceArea>();
        private static readonly List<WaspJob> WaspJobSnapshot = new List<WaspJob>();
        private static HarmonyLib.Harmony WaspOrderHarmony;
        private static Action<AIOrderProcessor> WaspClearOrders;
        private static Func<ResourceArea, Vector3, int, float, int> WaspExtractFromArea;
        private static bool WaspOrdersInstalled;
        private static float WaspNextJobTick, WaspNextOccupancy;
        [ThreadStatic] private static Vector3? WaspCommanderClick;
        [ThreadStatic] private static bool WaspIssuingOrder;

        private static void InstallWaspOrders()
        {
            WaspOrderHarmony = new HarmonyLib.Harmony("Si_Logistics.WaspHarvest.Orders");
            try
            {
                var processor = AccessTools.Method(typeof(AIOrderProcessor), nameof(AIOrderProcessor.IssueOrder),
                    new[] { typeof(OrderDefinition), typeof(OrderTarget).MakeByRefType(), typeof(OrderIssueParams).MakeByRefType() });
                var clear = AccessTools.Method(typeof(AIOrderProcessor), "ClearOrders", Type.EmptyTypes) ??
                    throw new MissingMethodException("AIOrderProcessor.ClearOrders()");
                WaspClearOrders = (Action<AIOrderProcessor>)clear.CreateDelegate(typeof(Action<AIOrderProcessor>));
                var areaExtract = AccessTools.DeclaredMethod(typeof(ResourceArea), "ExtractResource",
                    new[] { typeof(Vector3), typeof(int), typeof(float) }) ??
                    throw new MissingMethodException("ResourceArea.ExtractResource(Vector3, int, float)");
                WaspExtractFromArea = (Func<ResourceArea, Vector3, int, float, int>)areaExtract.CreateDelegate(
                    typeof(Func<ResourceArea, Vector3, int, float, int>));
                WaspOrderHarmony.Patch(AccessTools.DeclaredMethod(typeof(MiningClaws), nameof(MiningClaws.OnExtractAttackHit)),
                    transpiler: new HarmonyMethod(typeof(LogisticsMod), nameof(ObserveWaspExtraction)));
                WaspOrderHarmony.Patch(processor,
                    prefix: new HarmonyMethod(typeof(LogisticsMod), nameof(WaspOrderBefore)),
                    postfix: new HarmonyMethod(typeof(LogisticsMod), nameof(WaspOrderAfter)) { priority = HarmonyLib.Priority.First });
                WaspOrderHarmony.Patch(clear,
                    prefix: new HarmonyMethod(typeof(LogisticsMod), nameof(WaspOrdersCleared)));
                var finalize = new HarmonyMethod(typeof(LogisticsMod), nameof(WaspOrderScopeEnded));
                WaspOrderHarmony.Patch(AccessTools.Method(typeof(StrategyMode), nameof(StrategyMode.PerformMoveAttack),
                        new[] { typeof(IReadOnlyList<BaseGameObject>), typeof(Vector3), typeof(Target), typeof(AgentMoveSpeed),
                            typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(OrderDefinition) }),
                    prefix: new HarmonyMethod(typeof(LogisticsMod), nameof(WaspCommanderMove)) { priority = 1300 },
                    finalizer: finalize);
                WaspOrderHarmony.Patch(AccessTools.Method(typeof(Silica.OrderTargetingController), "IssueToObjects",
                        new[] { typeof(OrderDefinition), typeof(IReadOnlyList<BaseGameObject>), typeof(IReadOnlyList<Vector3>),
                            typeof(Target), typeof(AgentMoveSpeed), typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(LogisticsMod), nameof(WaspCommandCardMove)) { priority = 1300 },
                    finalizer: finalize);

                // UnitAI already uses this predicate to yield units owned by Logistics. Extend
                // that existing handoff to active Wasp jobs without requiring a UnitAI update.
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var hooks = assembly.GetType("Si_UnitAI.TacticalHooks", false);
                    if (hooks == null) continue;
                    var busy = AccessTools.DeclaredMethod(hooks, "LogisticsBusy", new[] { typeof(Unit) });
                    if (busy?.ReturnType != typeof(bool)) throw new MissingMethodException("UnitAI LogisticsBusy(Unit)");
                    WaspOrderHarmony.Patch(busy, postfix: new HarmonyMethod(typeof(LogisticsMod), nameof(WaspLogisticsBusy)));
                    break;
                }
                WaspOrdersInstalled = true;
            }
            catch (Exception error)
            {
                WaspOrderHarmony.UnpatchSelf();
                MelonLoader.MelonLogger.Error("Wasp harvest: AI order integration unavailable: " + error.GetBaseException().Message);
            }
        }

        private static void WaspLogisticsBusy(Unit __0, ref bool __result)
        {
            if (!__result && WaspHarvestActive && WaspJobs.Count != 0 && __0)
                __result = WaspJobs.ContainsKey(__0.GetInstanceID());
        }

        private static void WaspCommanderMove(Vector3 __1, Target __2, bool __4, bool __6, bool __7,
            OrderDefinition __8, out Vector3? __state)
        {
            __state = WaspCommanderClick;
            WaspCommanderClick = WaspHarvestActive && !__2 && !__4 && !__6 && !__7 &&
                (!__8 || __8 is MoveOrderDefinition) ? __1 : (Vector3?)null;
        }

        private static void WaspCommandCardMove(OrderDefinition __0, IReadOnlyList<Vector3> __2, bool __5, out Vector3? __state)
        {
            __state = WaspCommanderClick;
            WaspCommanderClick = WaspHarvestActive && __0 is MoveOrderDefinition && !__5 && __2 != null && __2.Count > 0
                ? __2[0] : (Vector3?)null;
        }

        private static Exception WaspOrderScopeEnded(Vector3? __state, Exception __exception)
        {
            WaspCommanderClick = __state;
            return __exception;
        }

        private static bool WaspOrderBefore(AIOrderProcessor __instance, OrderIssueParams __2) =>
            WaspIssuingOrder || !__2.CalledByAI || !WaspHarvestActive || WaspJobs.Count == 0 ||
            !__instance.Owner || !WaspJobs.ContainsKey(__instance.Owner.GetInstanceID());

        private static void WaspOrderAfter(AIOrderProcessor __instance, OrderDefinition __0, OrderIssueParams __2, bool __result)
        {
            if (WaspIssuingOrder || !__result || __2.CalledByAI || !WaspHarvestActive ||
                !MatchAcceptsRewards || WaspByUnit.Count == 0 || !(__instance.Owner is CreatureDecapod unit) ||
                !WaspByUnit.TryGetValue(unit.GetInstanceID(), out var state)) return;
            try
            {
                // Preserve accepted replacement and queued orders. They relinquish the harvest job.
                CancelWaspJob(state.UnitId, false);
                if (!WaspOrdersInstalled || !WaspCommanderClick.HasValue || __2.QueueOrder ||
                    !(__0 is MoveOrderDefinition) || !CanRunWaspJob(state) ||
                    !(__instance.Orders.Count > 0 && __instance.Orders[0] is AIMoveOrder)) return;
                var area = ResourceArea.GetResourceAreaAtPosition(unit.Team, state.Holder.ResourceType, WaspCommanderClick.Value);
                if (!area) return;
                var job = new WaspJob { State = state, Team = unit.Team, FirstArea = area };
                CaptureWaspOrder(job);
                WaspJobs.Add(state.UnitId, job);
                WaspNextOccupancy = 0f;
            }
            catch (Exception error) { WaspWarn("ai-order", "Wasp harvest order: " + error.Message); }
        }

        private static void WaspOrdersCleared(AIOrderProcessor __instance)
        {
            if (!WaspIssuingOrder && WaspJobs.Count != 0 && __instance.Orders.Count > 0 && __instance.Owner)
                CancelWaspJob(__instance.Owner.GetInstanceID(), false);
        }

        private static bool CanRunWaspJob(WaspState state) =>
            state.Alive && !state.Unit.IsDestroyed && state.Unit.IsFlyingType && state.Unit.Team &&
            state.Unit.AIControlled && !state.Unit.ControlledBy && !state.Unit.PlayerControlled &&
            !state.Unit.GetRemotelyControlled() && !state.Unit.InCompartment && state.Unit.AIAgent && state.Unit.OrderAgent;

        private static void UpdateWaspJobs(float now)
        {
            if (WaspJobs.Count == 0 || now < WaspNextJobTick) return;
            WaspNextJobTick = now + 0.25f;
            if (now >= WaspNextOccupancy)
            {
                WaspNextOccupancy = now + 0.75f;
                UpdateWaspOccupancy();
            }
            WaspJobSnapshot.Clear();
            WaspJobSnapshot.AddRange(WaspJobs.Values);
            foreach (var job in WaspJobSnapshot)
            {
                try
                {
                    if (!CanRunWaspJob(job.State) || job.State.Unit.Team != job.Team)
                        CancelWaspJob(job.State.UnitId, false);
                    else if (now >= job.NextStep)
                        StepWaspJob(job, now);
                }
                catch (Exception error)
                {
                    WaspWarn("ai-update", "Wasp harvest AI: " + error.Message);
                    CancelWaspJob(job.State.UnitId, true);
                }
            }
            WaspJobSnapshot.Clear();
        }

        private static void UpdateWaspOccupancy()
        {
            WaspOccupied.Clear();
            if (Unit.Units == null) return;
            foreach (var unit in Unit.Units)
            {
                if (!unit || unit.IsDestroyed || !unit.Team) continue;
                if (unit.AIAgent is AIAlienHarvesterAgent shrimp && shrimp.MiningClaws)
                {
                    if (shrimp.CurrentHarvestArea && !shrimp.CurrentDepositPoint && unit.AIControlled)
                        WaspOccupied.Add(shrimp.CurrentHarvestArea);
                    else if (unit.PlayerControlled)
                        OccupyWaspArea(unit.Team, shrimp.MiningClaws);
                }
                else if (unit.PlayerControlled && WaspByUnit.TryGetValue(unit.GetInstanceID(), out var wasp) &&
                    wasp.Alive && WaspNearGround(wasp.Claws.GetExtractionPoint()))
                    OccupyWaspArea(unit.Team, wasp.Claws);
            }
        }

        private static void OccupyWaspArea(Team team, MiningClaws claws)
        {
            if (!claws.ResourceHolder || !claws.ResourceHolder.ResourceType) return;
            var area = ResourceArea.GetResourceAreaAtPosition(team, claws.ResourceHolder.ResourceType,
                claws.GetExtractionPoint(), distanceTolerance: claws.ExtractionRadius);
            if (area) WaspOccupied.Add(area);
        }

        private static bool WaspPatchAvailable(ResourceArea area, float now) =>
            area && !area.IsEmpty && !WaspOccupied.Contains(area) && !WaspReservations.ContainsKey(area) &&
            (!WaspUnreachable.TryGetValue(area, out float retry) || now >= retry);

        private static ResourceArea ChooseWaspPatch(WaspJob job, float now)
        {
            var preferred = job.FirstArea;
            job.FirstArea = null;
            if (WaspPatchAvailable(preferred, now)) return preferred;
            ResourceArea.GetKnownResourcesAreas(job.Team, job.State.Holder.ResourceType, WaspKnownAreas);
            ResourceArea best = null;
            float distance = float.PositiveInfinity;
            Vector3 position = job.State.Unit.transform.position;
            foreach (var area in WaspKnownAreas)
            {
                if (!WaspPatchAvailable(area, now)) continue;
                float candidate = WaspDistanceSquared(position, area.SignalCenter);
                if (candidate >= distance) continue;
                best = area;
                distance = candidate;
            }
            WaspKnownAreas.Clear();
            return best;
        }

        private static void StepWaspJob(WaspJob job, float now)
        {
            var state = job.State;
            var unit = state.Unit;
            var holder = state.Holder;
            Vector3 position = unit.transform.position;
            if (job.Leg == WaspLeg.Seek)
            {
                if (holder.RemainingSpace <= 0)
                {
                    BeginWaspReturn(job);
                    return;
                }
                var area = ChooseWaspPatch(job, now);
                if (!area)
                {
                    StopWaspOrder(job);
                    if (holder.AmountStored > 0) BeginWaspReturn(job);
                    else job.NextStep = now + 2f;
                    return;
                }
                WaspReservations.Add(area, job);
                job.Area = area;
                var cell = area.GetNearestResourceCell(state.Claws.GetExtractionPoint());
                if (!FlyWaspTo(job, cell, WaspLeg.ToPatch, now)) FailWaspPatch(job, now);
                return;
            }

            if (job.Leg == WaspLeg.ToPatch || job.Leg == WaspLeg.Harvest)
            {
                if (holder.RemainingSpace <= 0 || !job.Area || job.Area.IsEmpty || WaspOccupied.Contains(job.Area))
                {
                    BeginWaspReturn(job);
                    return;
                }
                if (now >= job.Deadline) { FailWaspPatch(job, now); return; }
                if (job.Leg == WaspLeg.ToPatch)
                {
                    float radius = Mathf.Max(state.Claws.ExtractionRadius, unit.AIAgent.FinalWaypointThreshold + 1f);
                    if (!LandWasp(job, position, radius)) return;
                    job.Leg = WaspLeg.Harvest;
                    job.LastCargo = holder.AmountStored;
                    job.LastGain = now;
                    job.Deadline = float.PositiveInfinity;
                    state.Claws.SetMining(true);
                }
                if (holder.AmountStored > job.LastCargo) { job.LastCargo = holder.AmountStored; job.LastGain = now; }
                if (now - job.LastGain < 5f) return;

                // The forward attack point can miss after a patch is partly depleted. Aim the
                // next approach at a live cell and offset the body by the claw's current reach.
                var hit = state.Claws.GetExtractionPoint();
                var cell = job.Area.GetNearestResourceCell(hit);
                var offset = hit - position;
                offset.y = 0;
                if (WaspDistanceSquared(job.Goal, cell - offset) <= 1f ||
                    !FlyWaspTo(job, cell - offset, WaspLeg.ToPatch, now, false)) FailWaspPatch(job, now);
                return;
            }

            if (holder.AmountStored <= 0)
            {
                ClearWaspLanding(job);
                job.Cache = null;
                job.Leg = WaspLeg.Seek;
                return;
            }
            if (!WaspCacheValid(job) || (job.Leg == WaspLeg.ToCache && now >= job.Deadline))
            {
                var previous = job.Cache;
                job.Cache = FindWaspCache(job, position, previous);
                StopWaspOrder(job);
                ClearWaspLanding(job);
                if (!job.Cache || !FlyWaspTo(job, job.Cache.transform.position, WaspLeg.ToCache, now))
                {
                    job.Cache = null;
                    job.NextStep = now + 3f;
                }
                return;
            }
            if (job.Leg == WaspLeg.ToCache)
            {
                float radius = Mathf.Max(1f, job.Cache.DepositRadius * 0.7f);
                if (!LandWasp(job, position, radius)) return;
                job.Leg = WaspLeg.Unload;
                job.Deadline = now + 10f;
            }
            if (!unit.IsFlying && unit.IsGrounded &&
                (position - job.Cache.transform.position).sqrMagnitude < job.Cache.DepositRadius * job.Cache.DepositRadius &&
                job.Team.GetResourceDepositPoint(holder.ResourceType, position))
            {
                // Keep this cache and wait while storage is full or the unload cooldown is active.
                job.Deadline = now + 10f;
                return;
            }
            if (now >= job.Deadline) BeginWaspReturn(job);
        }

        private static ResourceDepositPoint FindWaspCache(WaspJob job, Vector3 position, ResourceDepositPoint previous)
        {
            ResourceDepositPoint best = null, fallback = null;
            float distance = float.PositiveInfinity;
            foreach (var structure in job.Team.Structures)
            {
                if (!structure || structure.IsDestroyed || structure.IsSold) continue;
                foreach (var cache in structure.ResourceDepositPoints)
                {
                    if (!cache || cache.ResourceType != job.State.Holder.ResourceType) continue;
                    if (cache == previous) { fallback = cache; continue; }
                    float candidate = WaspDistanceSquared(position, cache.transform.position);
                    if (candidate >= distance) continue;
                    distance = candidate;
                    best = cache;
                }
            }
            return best ? best : fallback;
        }

        private static bool WaspCacheValid(WaspJob job) =>
            job.Cache && job.Cache.Structure && job.Cache.Structure.Team == job.Team &&
            !job.Cache.Structure.IsDestroyed && !job.Cache.Structure.IsSold;

        private static bool FlyWaspTo(WaspJob job, Vector3 goal, WaspLeg leg, float now, bool longRange = true)
        {
            ClearWaspLanding(job);
            job.State.Claws.SetMining(false);
            var unit = job.State.Unit;
            var target = new OrderTarget(goal);
            var parameters = OrderIssueParams.SelfDirected(AgentMoveSpeed.Fast);
            parameters.LongRange = longRange;
            WaspIssuingOrder = true;
            try
            {
                if (!unit.OrderAgent.IssueOrder(OrderDefinitionRegistry.Move, in target, in parameters)) return false;
                CaptureWaspOrder(job);
            }
            finally { WaspIssuingOrder = false; }
            job.Goal = goal;
            job.Leg = leg;
            job.Deadline = now + Mathf.Clamp(18f + Mathf.Sqrt(WaspDistanceSquared(unit.transform.position, goal)) /
                Mathf.Max(1f, unit.NormalSpeed * 0.5f), 18f, 150f);
            return true;
        }

        private static bool LandWasp(WaspJob job, Vector3 position, float radius)
        {
            if (!job.Landing)
            {
                if (WaspDistanceSquared(position, job.Goal) > radius * radius) return false;
                StopWaspOrder(job);
                job.PreviousForceLand = job.State.Unit.ForceLand;
                job.State.Unit.ForceLand = true;
                job.Landing = true;
                if (job.State.Unit.RigidBody) job.State.Unit.RigidBody.WakeUp();
            }
            return !job.State.Unit.IsFlying && job.State.Unit.IsGrounded;
        }

        private static void ClearWaspLanding(WaspJob job)
        {
            if (!job.Landing) return;
            if (job.State.Unit && job.State.Unit.ForceLand) job.State.Unit.ForceLand = job.PreviousForceLand;
            job.Landing = false;
        }

        private static void CaptureWaspOrder(WaspJob job)
        {
            var orders = job.State.Unit.OrderAgent.Orders;
            job.Order = orders.Count > 0 ? orders[0] : null;
            job.OrderId = job.Order?.Id ?? 0;
        }

        private static void StopWaspOrder(WaspJob job)
        {
            var unit = job.State.Unit;
            if (!unit || !unit.AIControlled || !unit.AIAgent || !unit.OrderAgent) return;
            var orders = unit.OrderAgent.Orders;
            if (orders.Count > 0 && (!ReferenceEquals(orders[0], job.Order) || orders[0].Id != job.OrderId)) return;
            WaspIssuingOrder = true;
            try
            {
                if (orders.Count != 0) WaspClearOrders(unit.OrderAgent);
                unit.AIAgent.Stop(false);
                job.Order = null;
            }
            finally { WaspIssuingOrder = false; }
        }

        private static void FailWaspPatch(WaspJob job, float now)
        {
            if (job.Area) WaspUnreachable[job.Area] = now + 15f;
            BeginWaspReturn(job);
        }

        private static void ReleaseWaspPatch(WaspJob job)
        {
            if (!ReferenceEquals(job.Area, null) && WaspReservations.TryGetValue(job.Area, out var owner) && owner == job)
                WaspReservations.Remove(job.Area);
            job.Area = null;
            job.FirstArea = null;
            if (job.State.Claws) job.State.Claws.SetMining(false);
        }

        private static void BeginWaspReturn(WaspJob job)
        {
            ReleaseWaspPatch(job);
            StopWaspOrder(job);
            ClearWaspLanding(job);
            job.Cache = null;
            job.Leg = job.State.Holder.AmountStored > 0 ? WaspLeg.ToCache : WaspLeg.Seek;
        }

        private static void CancelWaspJob(int id, bool stop)
        {
            if (!WaspJobs.TryGetValue(id, out var job)) return;
            WaspJobs.Remove(id);
            ReleaseWaspPatch(job);
            ClearWaspLanding(job);
            if (stop) StopWaspOrder(job);
        }

        private static void CancelAllWaspJobs()
        {
            if (WaspJobs.Count != 0)
                foreach (var job in new List<WaspJob>(WaspJobs.Values)) CancelWaspJob(job.State.UnitId, true);
            WaspReservations.Clear();
            WaspOccupied.Clear();
            WaspUnreachable.Clear();
        }

        private static bool WaspCanExtract(WaspState state, Vector3 position) =>
            WaspJobs.TryGetValue(state.UnitId, out var job) && job.Leg == WaspLeg.Harvest && CanRunWaspJob(state) &&
            !state.Unit.IsFlying && state.Unit.IsGrounded && job.Area && !WaspOccupied.Contains(job.Area) &&
            job.Area.GetPositionInBounds(position) && WaspNearGround(position);

        private static bool WaspCanUnload(WaspState state) =>
            state.Unit.ControlledBy || !WaspJobs.TryGetValue(state.UnitId, out var job) ||
            (job.Leg == WaspLeg.Unload && WaspCacheValid(job) && !state.Unit.IsFlying && state.Unit.IsGrounded);

        private static int ExtractWaspBiotics(Resource type, Vector3 position, int amount, float radius, MiningClaws claws)
        {
            if (WaspJobs.Count == 0 || !WaspByClaws.TryGetValue(claws.GetInstanceID(), out var state) || state.Unit.ControlledBy)
                return ResourceArea.ExtractResource(type, position, amount, radius);
            if (!WaspHarvestActive || !WaspJobs.TryGetValue(state.UnitId, out var job) || job.Leg != WaspLeg.Harvest || !job.Area)
                return 0;
            return WaspExtractFromArea(job.Area, position, amount, radius);
        }

        private static float WaspDistanceSquared(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x, z = a.z - b.z;
            return x * x + z * z;
        }
    }
}
