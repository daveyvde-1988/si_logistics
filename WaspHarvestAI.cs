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
            internal Vector3 FlightVelocity;
            internal Vector3? MiningAim;
            internal float NextStep, Deadline, LastGain;
            internal float NextLandingTrace;
            internal float NextUnload;
            internal int LastCargo;
            internal bool Landing;
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
        private static Func<Player> WaspGetRequestSender;
        private static Action<AIAiming, Vector3> WaspSetGroundAim;
        private static Action<AIAiming> WaspClearGroundAim;
        private static Func<AIAiming, Vector3> WaspGetGroundAim;
        private static bool WaspOrdersInstalled;
        private static bool WaspAIHarvestActive => WaspHarvestActive && WaspOrdersInstalled && Cfg.WaspHarvest.AIHarvest;
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
                // The project reference predates ground aiming. Cache the current server's native methods.
                var setAim = AccessTools.DeclaredMethod(typeof(AIAiming), "SetGroundAim", new[] { typeof(Vector3) });
                var clearAim = AccessTools.DeclaredMethod(typeof(AIAiming), "ClearGroundAim", Type.EmptyTypes);
                var getAim = AccessTools.DeclaredPropertyGetter(typeof(AIAiming), "GroundAimPosition");
                if (setAim == null || clearAim == null || getAim == null)
                    throw new MissingMethodException("AIAiming ground aim methods");
                WaspSetGroundAim = (Action<AIAiming, Vector3>)setAim.CreateDelegate(typeof(Action<AIAiming, Vector3>));
                WaspClearGroundAim = (Action<AIAiming>)clearAim.CreateDelegate(typeof(Action<AIAiming>));
                WaspGetGroundAim = (Func<AIAiming, Vector3>)getAim.CreateDelegate(typeof(Func<AIAiming, Vector3>));
                var requestSender = AccessTools.DeclaredMethod(typeof(NetworkLayer), "GetRequestSender", Type.EmptyTypes);
                if (requestSender != null && requestSender.IsStatic && requestSender.ReturnType == typeof(Player))
                    WaspGetRequestSender = (Func<Player>)requestSender.CreateDelegate(typeof(Func<Player>));
                WaspOrderHarmony.Patch(AccessTools.DeclaredMethod(typeof(MiningClaws), nameof(MiningClaws.OnExtractAttackHit)),
                    transpiler: new HarmonyMethod(typeof(LogisticsMod), nameof(ObserveWaspExtraction)));
                var fixedUpdate = AccessTools.DeclaredMethod(typeof(CreatureDecapod), nameof(CreatureDecapod.OrderedFixedUpdate),
                    new[] { typeof(float) }) ?? throw new MissingMethodException("CreatureDecapod.OrderedFixedUpdate(float)");
                if (AccessTools.DeclaredField(typeof(CreatureDecapod), "m_InputMovement")?.FieldType != typeof(Vector3) ||
                    AccessTools.DeclaredField(typeof(CreatureDecapod), "m_fFlyPitch")?.FieldType != typeof(float))
                    throw new MissingFieldException("CreatureDecapod flight input fields");
                WaspOrderHarmony.Patch(fixedUpdate,
                    prefix: new HarmonyMethod(typeof(LogisticsMod), nameof(BeginWaspFlightInput)),
                    finalizer: new HarmonyMethod(typeof(LogisticsMod), nameof(EndWaspFlightInput)));
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
            if (!__result && WaspAIHarvestActive && WaspJobs.Count != 0 && __0)
                __result = WaspJobs.ContainsKey(__0.GetInstanceID());
        }

        private static void WaspCommanderMove(Vector3 __1, bool __4, bool __6, bool __7,
            OrderDefinition __8, out Vector3? __state)
        {
            __state = WaspCommanderClick;
            WaspCommanderClick = WaspAIHarvestActive && !__4 && !__6 && !__7 &&
                (!__8 || __8 is MoveOrderDefinition) ? __1 : (Vector3?)null;
        }

        private static void WaspCommandCardMove(OrderDefinition __0, IReadOnlyList<Vector3> __2, bool __5, out Vector3? __state)
        {
            __state = WaspCommanderClick;
            WaspCommanderClick = WaspAIHarvestActive && __0 is MoveOrderDefinition && !__5 && __2 != null && __2.Count > 0
                ? __2[0] : (Vector3?)null;
        }

        private static Exception WaspOrderScopeEnded(Vector3? __state, Exception __exception)
        {
            WaspCommanderClick = __state;
            return __exception;
        }

        private static bool WaspOrderBefore(AIOrderProcessor __instance, OrderIssueParams __2) =>
            WaspIssuingOrder || !__2.CalledByAI || !WaspAIHarvestActive || WaspJobs.Count == 0 ||
            !__instance.Owner || !WaspJobs.ContainsKey(__instance.Owner.GetInstanceID());

        private static void WaspOrderAfter(AIOrderProcessor __instance, OrderIssueParams __2, bool __result)
        {
            if (WaspIssuingOrder || !__result || __2.CalledByAI || !WaspAIHarvestActive ||
                !MatchAcceptsRewards || WaspByUnit.Count == 0 || !(__instance.Owner is CreatureDecapod unit) ||
                !WaspByUnit.TryGetValue(unit.GetInstanceID(), out var state)) return;
            try
            {
                // Preserve accepted replacement and queued orders. They relinquish the harvest job.
                CancelWaspJob(state.UnitId, false);
                if (__2.QueueOrder ||
                    !(__instance.Orders.Count > 0 && __instance.Orders[0] is AIMoveOrder move)) return;
                var click = WaspCommanderClick;
                if (!click.HasValue && __2.BroadcastEvent)
                {
                    var sender = WaspGetRequestSender?.Invoke();
                    if (sender && sender.IsCommander && sender.Team == unit.Team) click = move.WorldPosition;
                }
                if (!click.HasValue) return;
                var area = ResourceArea.GetResourceAreaAtPosition(unit.Team, state.Holder.ResourceType, click.Value);
                if (!area || !CanRunWaspJob(state))
                {
                    if (Cfg.Debug) DebugMsg($"Wasp harvest AI {state.UnitId}: no job for Move at {click.Value}. " +
                        $"Discovered patch={!!area}, ground={unit.GroundEnabled}, melee={unit.AttackPrimary?.IsMelee}, AI={unit.AIControlled}.");
                    return;
                }
                var job = new WaspJob { State = state, Team = unit.Team, FirstArea = area };
                CaptureWaspOrder(job);
                WaspJobs.Add(state.UnitId, job);
                WaspNextOccupancy = 0f;
                if (Cfg.Debug) DebugMsg($"Wasp harvest AI {state.UnitId}: started a job at {click.Value}.");
            }
            catch (Exception error) { WaspWarn("ai-order", "Wasp harvest order: " + error.Message); }
        }

        private static void WaspOrdersCleared(AIOrderProcessor __instance)
        {
            if (!WaspIssuingOrder && WaspJobs.Count != 0 && __instance.Orders.Count > 0 && __instance.Owner)
                CancelWaspJob(__instance.Owner.GetInstanceID(), false);
        }

        private static bool CanRunWaspJob(WaspState state) =>
            state.Alive && !state.Unit.IsDestroyed && state.Unit.IsFlyingType && state.Unit.GroundEnabled &&
            state.Unit.AttackPrimary?.IsMelee == true && state.Unit.Team &&
            state.Unit.AIControlled && !state.Unit.ControlledBy && !state.Unit.PlayerControlled &&
            !state.Unit.GetRemotelyControlled() && !state.Unit.InCompartment && state.Unit.AIAgent && state.Unit.OrderAgent;

        private static void UpdateWaspJobs(float now)
        {
            if (WaspJobs.Count == 0 || !WaspAIHarvestActive || now < WaspNextJobTick) return;
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
                if (!MoveWaspTo(job, cell, WaspLeg.ToPatch, now)) FailWaspPatch(job, now);
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
                    if (!LandWasp(job, position, radius, now)) return;
                    job.Leg = WaspLeg.Harvest;
                    job.LastCargo = holder.AmountStored;
                    job.LastGain = now;
                    job.Deadline = float.PositiveInfinity;
                    AimWaspAtCell(job, job.Area.GetNearestResourceCell(state.Claws.GetExtractionPoint()));
                }
                bool grounded = WaspHasLanded(unit);
                state.Claws.SetMining(grounded);
                if (holder.AmountStored > job.LastCargo)
                {
                    if (Cfg.Debug && job.LastCargo == 0)
                        DebugMsg($"Wasp harvest AI {state.UnitId}: first melee harvest, {holder.AmountStored} Biotics aboard.");
                    job.LastCargo = holder.AmountStored;
                    job.LastGain = now;
                }
                if (now - job.LastGain < 5f) return;

                // Turn to new cells as nearby ones run out. If turning did not help, walk
                // closer on a short route instead of taking off for another landing.
                var hit = state.Claws.GetExtractionPoint();
                var cell = job.Area.GetNearestResourceCell(hit);
                if (grounded && AimWaspAtCell(job, cell))
                {
                    job.LastGain = now;
                    return;
                }
                var offset = hit - position;
                offset.y = 0;
                var goal = cell - offset;
                if (WaspDistanceSquared(position, goal) <= 1f ||
                    !MoveWaspTo(job, goal, WaspLeg.ToPatch, now, longRange: false, walk: grounded))
                    FailWaspPatch(job, now);
                return;
            }

            if (holder.AmountStored <= 0)
            {
                ClearWaspLanding(job);
                job.Cache = null;
                job.Leg = WaspLeg.Seek;
                return;
            }
            if (!WaspCacheValid(job) || now >= job.Deadline ||
                !InTransferRange(job.Cache.transform.position, job.Goal, Cfg.WaspHarvest.AIUnloadRange))
            {
                var previous = job.Cache;
                StopWaspOrder(job);
                ClearWaspLanding(job);
                job.Cache = FindWaspCache(job, position, previous, out var hover);
                if (job.Cache)
                {
                    // Use the cache's ground-level height for the native route. Flight
                    // control takes over for the final approach to the reserved altitude.
                    var routePoint = new Vector3(hover.x, job.Cache.transform.position.y, hover.z);
                    if (MoveWaspTo(job, routePoint, WaspLeg.ToCache, now))
                    {
                        job.Goal = hover;
                        return;
                    }
                }
                job.Cache = null;
                job.Leg = WaspLeg.ToCache;
                job.NextStep = now + 3f;
                return;
            }
            if (job.Leg == WaspLeg.ToCache)
            {
                float approach = WaspFlightApproachRange(unit);
                if (WaspDistanceSquared(position, job.Goal) > approach * approach) return;
                StopWaspOrder(job);
                job.Leg = WaspLeg.Unload;
                job.NextUnload = now;
                job.Deadline = now + 20f;
            }

            if (!unit.IsFlying) unit.Fly(true);
            job.FlightVelocity = Vector3.ClampMagnitude((job.Goal - position) * 1.5f, 8f);
            if (WaspCanHoverUnload(job))
                job.Deadline = now + 10f;
        }

        private static ResourceDepositPoint FindWaspCache(WaspJob job, Vector3 position, ResourceDepositPoint previous,
            out Vector3 hover)
        {
            ResourceDepositPoint best = null, fallback = null;
            hover = Vector3.zero;
            Vector3 fallbackHover = Vector3.zero;
            float distance = float.PositiveInfinity;
            foreach (var structure in job.Team.Structures)
            {
                if (!structure || structure.Team != job.Team || structure.IsDestroyed || structure.IsSold) continue;
                foreach (var cache in structure.ResourceDepositPoints)
                {
                    if (!cache || cache.ResourceType != job.State.Holder.ResourceType) continue;
                    if (!TryGetWaspHoverPoint(job, cache.transform.position, position, out var point)) continue;
                    if (cache == previous)
                    {
                        fallback = cache;
                        fallbackHover = point;
                        continue;
                    }
                    float score = (point - position).sqrMagnitude;
                    if (score >= distance) continue;
                    distance = score;
                    best = cache;
                    hover = point;
                }
            }
            if (best) return best;
            hover = fallbackHover;
            return fallback;
        }

        private static bool TryGetWaspHoverPoint(WaspJob job, Vector3 center, Vector3 position, out Vector3 hover)
        {
            hover = Vector3.zero;
            float best = float.PositiveInfinity;
            float bodyRadius = Mathf.Max(1f, job.State.Unit.PhysicalRadius);
            int mask = GamePhysics.TRACELAYERMASK_IGNOREDEBRISANDUNITS.value;
            float range = Cfg.WaspHarvest.AIUnloadRange;
            // Stagger the upper ring so arrivals at the same cache can use both heights.
            for (int ring = 0; ring < 2; ring++)
            {
                float radius = range * (ring == 0 ? 10f / 15f : 8f / 15f);
                float height = Mathf.Min(1f, range / 15f) * (ring == 0 ? 7f : 11f);
                for (int slot = 0; slot < 8; slot++)
                {
                    float angle = (slot + ring * 0.5f) * Mathf.PI / 4f;
                    var point = center + new Vector3(radius * Mathf.Cos(angle), height, radius * Mathf.Sin(angle));
                    float distance = (point - position).sqrMagnitude;
                    if (distance >= best) continue;
                    bool occupied = false;
                    foreach (var other in WaspJobs.Values)
                    {
                        if (other == job || !other.Cache || !other.State.Unit) continue;
                        float separation = bodyRadius + Mathf.Max(1f, other.State.Unit.PhysicalRadius) + 1f;
                        if ((other.Goal - point).sqrMagnitude >= separation * separation) continue;
                        occupied = true;
                        break;
                    }
                    if (occupied || Physics.CheckSphere(point, bodyRadius, mask, QueryTriggerInteraction.Ignore)) continue;
                    best = distance;
                    hover = point;
                }
            }
            return best < float.PositiveInfinity;
        }

        private static bool WaspCacheValid(WaspJob job) =>
            job.Cache && job.Cache.ResourceType == job.State.Holder.ResourceType &&
            job.Cache.Structure && job.Cache.Structure.Team == job.Team &&
            !job.Cache.Structure.IsDestroyed && !job.Cache.Structure.IsSold;

        private static bool WaspCanHoverUnload(WaspJob job)
        {
            var unit = job.State.Unit;
            if (!unit.IsFlying || unit.IsAttacking || !unit.RigidBody || !WaspCacheValid(job)) return false;
            Vector3 position = unit.transform.position;
            return InTransferRange(job.Cache.transform.position, position, Cfg.WaspHarvest.AIUnloadRange) &&
                unit.RigidBody.linearVelocity.sqrMagnitude <= 16f;
        }

        private static float WaspFlightApproachRange(CreatureDecapod unit) => Mathf.Clamp(unit.FlyMoveSpeed * 0.6f, 12f, 40f);

        private static bool MoveWaspTo(WaspJob job, Vector3 goal, WaspLeg leg, float now, bool longRange = true, bool walk = false)
        {
            ClearWaspLanding(job);
            ClearWaspMiningAim(job);
            job.State.Claws.SetMining(false);
            var unit = job.State.Unit;
            var target = new OrderTarget(goal);
            var parameters = OrderIssueParams.SelfDirected(walk ? AgentMoveSpeed.Stealth : AgentMoveSpeed.Fast);
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

        private static bool LandWasp(WaspJob job, Vector3 position, float radius, float now)
        {
            var unit = job.State.Unit;
            var offset = job.Goal - position;
            offset.y = 0f;
            float distance = offset.magnitude;
            if (!job.Landing)
            {
                // A fast Wasp can cross the touchdown radius entirely between job ticks.
                // Take over the final approach early enough to brake and line up.
                float entry = unit.IsFlying ? Mathf.Max(radius, WaspFlightApproachRange(unit)) : radius;
                if (distance > entry) return false;
                StopWaspOrder(job);
                job.Landing = true;
                job.Deadline = now + 25f;
                job.NextLandingTrace = now;
                if (Cfg.Debug) DebugMsg($"Wasp harvest AI {job.State.UnitId}: approaching {job.Leg} from {distance:0.0} m.");
            }
            if (WaspHasLanded(unit))
            {
                if (distance > radius)
                {
                    if (!MoveWaspTo(job, job.Goal, job.Leg, now, longRange: false, walk: true)) job.Deadline = now;
                    return false;
                }
                ClearWaspLanding(job);
                if (Cfg.Debug) DebugMsg($"Wasp harvest AI {job.State.UnitId}: touched down on {job.Leg}.");
                return true;
            }
            job.FlightVelocity = Vector3.zero;
            if (!unit.IsFlying || !unit.SphereCollider) return false;
            float latchSpeed = unit.FlyLatchSpeed;
            float maxApproachSpeed = Mathf.Clamp(latchSpeed * 0.45f, 1f, 10f);
            job.FlightVelocity = Vector3.ClampMagnitude(offset * 1.5f, maxApproachSpeed);
            if (distance > radius)
            {
                if (Cfg.Debug && now >= job.NextLandingTrace)
                    TraceWaspLanding(job, now, $"aligning, {distance:0.0} m from goal");
                return false;
            }
            var body = unit.SphereCollider.bounds;
            int mask = GamePhysics.TRACELAYERMASK_IGNOREDEBRISANDUNITS.value;
            if (!Physics.Raycast(body.center, Vector3.down, out var surface, 1500f, mask, QueryTriggerInteraction.Ignore) ||
                surface.collider.CompareTag("NotWalkable") ||
                surface.normal.y < Mathf.Cos(unit.MaxSlopeAngle * Mathf.Deg2Rad))
            {
                TraceWaspLanding(job, now, "no walkable surface below");
                return false;
            }

            // Keep the contact speed within the creature's native latch limit.
            float clearance = Mathf.Max(0f, surface.distance - body.extents.y);
            job.FlightVelocity.y = -Mathf.Min(Mathf.Max(0.6f, clearance * 0.6f), Mathf.Min(4f, latchSpeed * 0.45f));
            var velocity = unit.RigidBody.linearVelocity;
            if (clearance <= 0.3f && velocity.sqrMagnitude <= 9f && unit.GetLocallyOwned())
            {
                // At the surface the native flight toggle can finish the last part of touchdown.
                // Mining still waits until native physics records actual ground contact.
                unit.Fly(false);
                job.FlightVelocity = Vector3.zero;
            }
            if (Cfg.Debug && now >= job.NextLandingTrace)
                TraceWaspLanding(job, now, $"clearance={clearance:0.0} m, vertical speed={velocity.y:0.0} m/s, flying={unit.IsFlying}");
            return false;
        }

        private static void TraceWaspLanding(WaspJob job, float now, string message)
        {
            if (!Cfg.Debug || now < job.NextLandingTrace) return;
            job.NextLandingTrace = now + 4f;
            DebugMsg($"Wasp harvest AI {job.State.UnitId}: {job.Leg} landing, {message}.");
        }

        private static void ClearWaspLanding(WaspJob job)
        {
            job.Landing = false;
            job.FlightVelocity = Vector3.zero;
        }

        private static bool WaspHasLanded(CreatureDecapod unit) =>
            !unit.IsFlying && !unit.IsPhysicsFalling && unit.GetGroundPoint() != Vector3.zero;

        private static void BeginWaspFlightInput(CreatureDecapod __instance, float ___m_fFlyPitch,
            ref Vector3 ___m_InputMovement, out Vector3? __state)
        {
            __state = null;
            if (WaspJobs.Count == 0 || !WaspAIHarvestActive || !__instance.IsFlying ||
                !WaspJobs.TryGetValue(__instance.GetInstanceID(), out var job) || (!job.Landing && job.Leg != WaspLeg.Unload) ||
                !CanRunWaspJob(job.State)) return;

            // Convert the approach from world coordinates into the same pitched frame
            // that the creature's own flight physics uses.
            var rotation = __instance.GetBodyRotation(excludeFlyPitch: true);
            if (___m_fFlyPitch != 0f)
                rotation = GameMath.GetRotatedQuaternion(rotation, Vector3.right * ___m_fFlyPitch);
            var desired = Quaternion.Inverse(rotation) * job.FlightVelocity;
            float speed = Mathf.Max(0.01f, __instance.FlyMoveSpeed);
            float side = Mathf.Max(0.01f, speed * __instance.FlyMoveScaleSide);
            float forward = desired.z < 0f ? Mathf.Max(0.01f, speed * __instance.FlyMoveScaleBack) : speed;
            __state = ___m_InputMovement;
            ___m_InputMovement = Vector3.ClampMagnitude(new Vector3(desired.x / side, desired.z / forward, desired.y / side), 1f);
        }

        private static Exception EndWaspFlightInput(ref Vector3 ___m_InputMovement, Vector3? __state, Exception __exception)
        {
            if (__state.HasValue) ___m_InputMovement = __state.Value;
            return __exception;
        }

        private static bool AimWaspAtCell(WaspJob job, Vector3 cell)
        {
            var aiming = job.State.Unit.AIAgent.AgentAiming;
            if (!aiming) return false;
            if (Game.MainTerrain) cell.y = Game.MainTerrain.SampleHeight(cell) + Game.MainTerrain.transform.position.y;
            if (cell == Vector3.zero || (job.MiningAim.HasValue && job.MiningAim.Value == cell &&
                WaspGetGroundAim(aiming) == cell)) return false;
            WaspSetGroundAim(aiming, cell);
            job.MiningAim = cell;
            return true;
        }

        private static void ClearWaspMiningAim(WaspJob job)
        {
            if (!job.MiningAim.HasValue) return;
            var unit = job.State.Unit;
            if (unit && unit.AIAgent && unit.AIAgent.AgentAiming)
            {
                var aiming = unit.AIAgent.AgentAiming;
                if (WaspGetGroundAim(aiming) == job.MiningAim.Value) WaspClearGroundAim(aiming);
            }
            job.MiningAim = null;
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
            ClearWaspMiningAim(job);
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
            WaspAIHarvestActive && WaspJobs.TryGetValue(state.UnitId, out var job) &&
            job.Leg == WaspLeg.Harvest && CanRunWaspJob(state) &&
            WaspHasLanded(state.Unit) && job.Area && !WaspOccupied.Contains(job.Area) &&
            job.Area.GetPositionInBounds(position, state.Claws.ExtractionRadius) && WaspNearGround(position);

        private static bool WaspCanUnload(WaspState state)
        {
            if (state.Unit.ControlledBy) return true;
            if (!WaspAIHarvestActive) return false;
            return !WaspJobs.TryGetValue(state.UnitId, out var job) ||
                (job.Leg == WaspLeg.Unload && WaspCanHoverUnload(job));
        }

        private static int ExtractWaspBiotics(Resource type, Vector3 position, int amount, float radius, MiningClaws claws)
        {
            if (WaspByClaws.Count == 0 || !WaspByClaws.TryGetValue(claws.GetInstanceID(), out var state) || state.Unit.ControlledBy)
                return ResourceArea.ExtractResource(type, position, amount, radius);
            if (!WaspAIHarvestActive || !WaspJobs.TryGetValue(state.UnitId, out var job) || job.Leg != WaspLeg.Harvest || !job.Area)
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
