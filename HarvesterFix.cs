using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using Silica.AI;
using SilicaAdminMod;
using UnityEngine;

namespace Si_Logistics
{
    public sealed class HarvesterFixConfig
    {
        public bool Enabled = true;
        public int MaximumReservationsPerRefinery = 5;
        public float NearPatchDistanceMetres = 400f;
        public float MediumPatchDistanceMetres = 800f;
        public int NearPatchReservations = 1;
        public int MediumPatchReservations = 2;
        public int FarPatchReservations = 3;
        public float TransferWaitingTimeSeconds = 120f;
        public float ReservationCheckTimeSeconds = 10f;
        public float ReservationDistanceIncrementMetres = 50f;
    }

    public sealed partial class LogisticsMod
    {
        private enum HarvestPhase { Starting, Harvesting, WaitingForRefinery, Queued, Depositing, Manual }
        private sealed class HarvestJob
        {
            internal AIHarvesterAgent Agent;
            internal Team Team;
            internal ResourceArea Area;
            internal bool PatchReserved;
            internal HarvestPhase Phase;
            internal float ReadyAt, NextCheck, PickupUntil;
            internal bool ResumeAfterPickup;
            internal RefineryLine Line;
            internal int LastSlot = -1;
            internal bool ExitRequested;
            internal Structure PreferredRefinery;
            internal string LastDiagnostic;
        }
        private sealed class RefineryLine
        {
            internal Structure Refinery;
            internal ResourceDepositPoint Point;
            internal Vector3 Approach, Direction;
            internal readonly List<HarvestJob> Jobs = new List<HarvestJob>();
        }
        private static readonly Dictionary<int, HarvestJob> HarvestJobs = new Dictionary<int, HarvestJob>();
        private static readonly Dictionary<int, RefineryLine> RefineryLines = new Dictionary<int, RefineryLine>();
        private static readonly HashSet<int> NewHarvesters = new HashSet<int>();
        private static bool HarvestFixInstalled, HarvestOwnOrder;
        private static float NextHarvestScan;
        private static bool HarvestFixActive => HarvestFixInstalled && Cfg.Enabled && Cfg.HarvesterFix.Enabled && Game.GetIsServer() && MatchAcceptsRewards;

        private static void BindHarvesterEvents()
        {
            GameEvents.OnUnitSpawned -= HarvesterSpawned;
            GameEvents.OnUnitSpawned += HarvesterSpawned;
        }

        private static float ValidHarvestSetting(float value, float fallback, float minimum, float maximum) =>
            float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp(value, minimum, maximum);

        private static void ValidateHarvesterFixConfig()
        {
            if (Cfg.HarvesterFix == null) Cfg.HarvesterFix = new HarvesterFixConfig();
            var c = Cfg.HarvesterFix;
            c.MaximumReservationsPerRefinery = Math.Max(1, Math.Min(32, c.MaximumReservationsPerRefinery));
            c.NearPatchDistanceMetres = ValidHarvestSetting(c.NearPatchDistanceMetres, 400, 0, 100000);
            c.MediumPatchDistanceMetres = ValidHarvestSetting(c.MediumPatchDistanceMetres, 800, c.NearPatchDistanceMetres, 100000);
            c.NearPatchReservations = Math.Max(1, Math.Min(32, c.NearPatchReservations));
            c.MediumPatchReservations = Math.Max(1, Math.Min(32, c.MediumPatchReservations));
            c.FarPatchReservations = Math.Max(1, Math.Min(32, c.FarPatchReservations));
            c.TransferWaitingTimeSeconds = ValidHarvestSetting(c.TransferWaitingTimeSeconds, 120, 0, 3600);
            c.ReservationCheckTimeSeconds = ValidHarvestSetting(c.ReservationCheckTimeSeconds, 10, 1, 300);
            c.ReservationDistanceIncrementMetres = ValidHarvestSetting(c.ReservationDistanceIncrementMetres, 50, 25, 2000);
            // LoadConfig resets reservations only if validated harvester settings changed.
        }

        private void InstallHarvesterFix()
        {
            HarmonyInstance.Patch(AccessTools.Method(typeof(AIHarvesterAgent), "OnOrderedUpdate"),
                prefix: new HarmonyMethod(typeof(LogisticsMod), nameof(HarvestUpdatePrefix)));
            HarmonyInstance.Patch(AccessTools.Method(typeof(AIHarvesterAgent), "HandleOrderReturnToBase"),
                prefix: new HarmonyMethod(typeof(LogisticsMod), nameof(HarvestReturnPrefix)));
            HarmonyInstance.Patch(AccessTools.Method(typeof(AIHarvesterAgent), "Harvest"),
                prefix: new HarmonyMethod(typeof(LogisticsMod), nameof(HarvestAreaPrefix)));
            HarmonyInstance.Patch(AccessTools.Method(typeof(AIHarvesterAgent), "FindDepositPoint"),
                postfix: new HarmonyMethod(typeof(LogisticsMod), nameof(HarvestFindDepositPostfix)));
            HarmonyInstance.Patch(AccessTools.Method(typeof(AIHarvesterAgent), "Deposit"),
                prefix: new HarmonyMethod(typeof(LogisticsMod), nameof(HarvestDepositPrefix)));
            HarmonyInstance.Patch(AccessTools.Method(typeof(AIOrderProcessor), "IssueOrder"),
                prefix: new HarmonyMethod(typeof(LogisticsMod), nameof(HarvestOrderPrefix)),
                postfix: new HarmonyMethod(typeof(LogisticsMod), nameof(HarvestOrderPostfix)));
            BindHarvesterEvents();
            HarvestFixInstalled = true;
        }

        private static void HarvesterSpawned(Unit unit)
        {
            if (HarvestFixActive && unit && unit.ObjectInfo && unit.ObjectInfo.UnitType == UnitType.Harvester)
                NewHarvesters.Add(unit.GetInstanceID());
        }

        private static void ShowHarvesterStatus(Player caller)
        {
            int patches = 0, queued = 0, managed = 0;
            foreach (var j in HarvestJobs.Values)
            { if (j.PatchReserved) patches++; if (j.Line != null) queued++; if (j.Phase != HarvestPhase.Manual) managed++; }
            var c = Cfg.HarvesterFix;
            Reply(caller, "Harvester fix " + (c.Enabled ? "ON" : "OFF (vanilla harvester AI)") +
                " | managed " + managed + ", patch reservations " + patches + ", refinery reservations " + queued +
                " | refinery slots " + c.MaximumReservationsPerRefinery +
                " | home patch slots " + c.NearPatchReservations + "/" + c.MediumPatchReservations + "/" + c.FarPatchReservations +
                " at <=" + c.NearPatchDistanceMetres + "m / <" + c.MediumPatchDistanceMetres + "m / farther" +
                " | pickup wait " + c.TransferWaitingTimeSeconds + "s, check " + c.ReservationCheckTimeSeconds +
                "s, first wait 100m, subsequent spacing " + c.ReservationDistanceIncrementMetres + "m");
            if (!Cfg.Enabled && c.Enabled) Reply(caller, "Harvester fix paused by Logistics master switch.");
            if (Cfg.Debug) DumpHarvesterStatus();
        }

        private static int HarvestObjectId(UnityEngine.Object value) => value ? value.GetInstanceID() : 0;

        private static void HarvestDiagnostic(HarvestJob j, string reason)
        {
            if (j.LastDiagnostic == reason) return;
            j.LastDiagnostic = reason;
            DebugMsg("[HarvesterFix] unit=" + HarvestObjectId(j.Agent.Unit) + " " + reason);
        }

        private static void DumpHarvesterStatus()
        {
            DebugMsg("[HarvesterFix] status enabled=" + Cfg.HarvesterFix.Enabled + " active=" + HarvestFixActive);
            foreach (var j in HarvestJobs.Values)
            {
                var a = j.Agent;
                if (!a || !a.Unit || !a.ResourceHolder) continue;
                DebugMsg("[HarvesterFix] unit=" + HarvestObjectId(a.Unit) + " phase=" + j.Phase +
                    " dock=" + a.DockState + " cargo=" + a.ResourceHolder.AmountStored + "/" + a.ResourceHolder.MaxAmount +
                    " teamRoom=" + (a.Team ? a.Team.RemainingResourceCapacity : 0) + " refinery=" +
                    (j.Line == null ? 0 : HarvestObjectId(j.Line.Refinery)) + " slot=" + (j.LastSlot + 1) +
                    " nativePoint=" + HarvestObjectId(a.CurrentDepositPoint) + " path=" + a.IsFollowingPath +
                    " calculating=" + a.IsCalculatingPath + " forceMove=" + a.Vehicle.HasForceMove +
                    " homePatch=" + HarvestObjectId(j.Area) + " patchReserved=" + j.PatchReserved + " patchLimit=" + PatchReservationLimit(j, j.Area) +
                    " pickupWait=" + Math.Max(0f, j.PickupUntil - Time.time).ToString("F1") +
                    " mining=" + a.MiningArms.IsMining + " extracting=" + a.MiningArms.IsExtracting +
                    " position=" + a.Unit.transform.position + " reason=" + j.LastDiagnostic);
            }
            foreach (var s in Structure.Structures)
                if (s && s.ResourceDepositPoints != null)
                    foreach (var p in s.ResourceDepositPoints)
                        if (p) DebugMsg("[HarvesterFix] refinery=" + HarvestObjectId(s) + " point=" + HarvestObjectId(p) +
                            " reserved=" + HarvestObjectId(p.ReservedByUnit) + " occupied=" + HarvestObjectId(p.OccupiedByUnit));
        }

        private static bool HarvestEligible(AIHarvesterAgent a) => a && a.Unit && !a.Unit.IsDestroyed && a.Team &&
            a.AIControlled && !a.Unit.PlayerControlled && DriverOf(a.Unit) == null && !a.Unit.InCompartment &&
            a.ResourceHolder && a.MiningArms && a.Unit.OrderAgent && a.AgentPathfinding;

        private static HarvestJob GetHarvestJob(AIHarvesterAgent a)
        {
            int id = a.Unit.GetInstanceID();
            if (!HarvestJobs.TryGetValue(id, out var j))
            {
                j = new HarvestJob { Agent = a, Team = a.Team, Area = a.CurrentHarvestArea,
                    ReadyAt = Time.time + 10f, Phase = HarvestPhase.Starting };
                HarvestJobs.Add(id, j);
                // Existing player orders are left alone when the feature is enabled.
                var orders = a.Unit.OrderAgent.Orders;
                bool newlyBuilt = NewHarvesters.Remove(id);
                bool fullFallback = a.ResourceHolder.RemainingSpace == 0 && !a.IsFollowingPath && !a.IsCalculatingPath &&
                    orders.Count > 0 && orders[0] is AIMoveOrder move &&
                    Vector3.Distance(move.WorldPosition, a.transform.position) <= Math.Max(15f, a.Unit.PhysicalRadius);
                if (!newlyBuilt && !fullFallback && orders.Count > 0 && !(orders[0] is AIHarvestOrder) && !(orders[0] is AIDepositOrder))
                    j.Phase = HarvestPhase.Manual;
                if (fullFallback) HarvestDiagnostic(j, "adopted full harvester in completed vanilla fallback move");
            }
            return j;
        }

        private static void UpdateHarvesterFix()
        {
            if (!HarvestFixActive)
            { if (HarvestJobs.Count > 0) ResetHarvesterFix(true); return; }
            if (Time.time < NextHarvestScan) return;
            NextHarvestScan = Time.time + 1f;
            foreach (var pair in new List<KeyValuePair<int, HarvestJob>>(HarvestJobs))
                if (!HarvestEligible(pair.Value.Agent) || pair.Value.Team != pair.Value.Agent.Team)
                { ReleaseHarvestReservations(pair.Value); HarvestJobs.Remove(pair.Key); }
            foreach (var job in HarvestJobs.Values)
                if (job.PatchReserved && (!job.Area || job.Area.ResourceAmountCurrent <= 0))
                {
                    job.PatchReserved = false;
                    HarvestDiagnostic(job, "home patch depleted: assignment released");
                }
            if (Unit.Units == null) return;
            foreach (Unit unit in Unit.Units)
            {
                if (!unit) continue;
                var a = unit.AIAgent as AIHarvesterAgent;
                if (HarvestEligible(a)) GetHarvestJob(a);
            }
        }

        private static void ReleaseHarvestReservations(HarvestJob j)
        {
            j.PatchReserved = false;
            ReleaseRefineryReservation(j);
        }

        private static void ReleaseRefineryReservation(HarvestJob j)
        {
            if (j.Line == null) return;
            var line = j.Line;
            line.Jobs.Remove(j);
            if (line.Point && j.Agent && line.Point.ReservedByUnit == j.Agent.Unit)
                line.Point.ReservedByUnit = null;
            j.Line = null;
            j.LastSlot = -1;
            if (line.Jobs.Count == 0 && line.Refinery) RefineryLines.Remove(line.Refinery.GetInstanceID());
        }

        private static void ResetHarvesterFix(bool resumeVanilla)
        {
            var jobs = new List<HarvestJob>(HarvestJobs.Values);
            foreach (var j in jobs) ReleaseHarvestReservations(j);
            HarvestJobs.Clear(); RefineryLines.Clear(); NewHarvesters.Clear(); NextHarvestScan = 0;
            if (!resumeVanilla || !Game.GetIsServer()) return;
            // Only replace orders that were owned by our controller. Docking remains native.
            foreach (var j in jobs)
            {
                if (!HarvestEligible(j.Agent) || j.Phase == HarvestPhase.Manual || j.Phase == HarvestPhase.Starting ||
                    j.Agent.DockState != EHarvesterAIDockState.UNDOCKED || j.Phase == HarvestPhase.Depositing) continue;
                try
                {
                    HarvestOwnOrder = true;
                    var a = j.Agent;
                    var point = a.Team.GetNearestResourceDepositPoint(a.ResourceHolder.ResourceType, a.transform.position, a.Unit);
                    if (a.ResourceHolder.AmountStored > 0 && (a.ResourceHolder.RemainingSpace == 0 || !j.Area || j.Area.ResourceAmountCurrent <= 0) && point)
                        a.Unit.OrderAgent.IssueOrder(OrderDefinitionRegistry.Deposit, new OrderTarget(point.transform.position, null, point), OrderIssueParams.SelfDirected(AgentMoveSpeed.Fast));
                    else if (j.Area && j.Area.ResourceAmountCurrent > 0)
                        a.Unit.OrderAgent.IssueOrder(OrderDefinitionRegistry.Harvest, new OrderTarget(j.Area.transform.position, null, j.Area), OrderIssueParams.SelfDirected(AgentMoveSpeed.Fast));
                }
                catch (Exception e) { DebugWarning("Harvester fix release: " + e.Message); }
                finally { HarvestOwnOrder = false; }
            }
        }

        private static bool IssueHarvestOrder(HarvestJob j, OrderDefinition definition, OrderTarget target)
        {
            bool previous = HarvestOwnOrder;
            try { HarvestOwnOrder = true; return j.Agent.Unit.OrderAgent.IssueOrder(definition, target, OrderIssueParams.SelfDirected(AgentMoveSpeed.Fast)); }
            finally { HarvestOwnOrder = previous; }
        }

        private static bool HarvestOrderPrefix(AIOrderProcessor __instance, in OrderIssueParams issueParams, ref bool __result)
        {
            if (HarvestOwnOrder || !HarvestFixActive || !issueParams.CalledByAI) return true;
            var unit = __instance.Owner as Unit;
            if (unit && HarvestJobs.TryGetValue(unit.GetInstanceID(), out var j) &&
                HarvestEligible(j.Agent) && j.Phase != HarvestPhase.Manual)
            { __result = false; return false; }
            return true;
        }

        private static void HarvestOrderPostfix(AIOrderProcessor __instance, OrderDefinition definition, in OrderTarget target, in OrderIssueParams issueParams, bool __result)
        {
            if (!__result || HarvestOwnOrder || !HarvestFixActive || !issueParams.BroadcastEvent || issueParams.QueueOrder) return;
            var unit = __instance.Owner as Unit;
            var a = unit ? unit.AIAgent as AIHarvesterAgent : null;
            if (!HarvestEligible(a)) return;
            var j = GetHarvestJob(a);
            if (definition == OrderDefinitionRegistry.Deposit) return; // Deposit execution joins the managed line.
            if (definition == OrderDefinitionRegistry.Harvest)
            {
                // Explicit orders can replace a managed queue or a previous manual order.
                var area = j.Phase == HarvestPhase.Harvesting ? j.Area : target.Payload as ResourceArea;
                ReleaseRefineryReservation(j);
                if (j.Area != area) j.PatchReserved = false;
                j.Area = area; j.Phase = HarvestPhase.Starting; j.NextCheck = 0; j.ReadyAt = Time.time;
                j.PatchReserved = PatchAvailable(j, area);
                return;
            }
            ReleaseHarvestReservations(j);
            j.PickupUntil = 0;
            j.ResumeAfterPickup = false;
            j.Phase = HarvestPhase.Manual;
            HarvestDiagnostic(j, "manual order: queue control released");
        }

        private static void HarvestFindDepositPostfix(AIHarvesterAgent __instance, Vector3 worldPosition, ref ResourceDepositPoint __result)
        {
            if (__result || !HarvestFixActive || !HarvestEligible(__instance)) return;
            // Vanilla excludes occupied/reserved bays here, causing a refinery click to
            // resolve as Move. Resolve the deposit target; the queue controls admission.
            __result = __instance.Team.GetResourceDepositPoint(__instance.ResourceHolder.ResourceType, worldPosition);
        }

        private static bool HarvestDepositPrefix(AIHarvesterAgent __instance, ResourceDepositPoint depositPoint)
        {
            if (HarvestOwnOrder || !HarvestFixActive || !HarvestEligible(__instance)) return true;
            if (!depositPoint || !depositPoint.Structure || depositPoint.Structure.Team != __instance.Team) return true;
            var j = GetHarvestJob(__instance);
            if (j.Line != null && j.Line.Refinery == depositPoint.Structure)
            {
                // Repeated native/manual deposit orders must neither overtake the
                // head nor lose/recreate an existing FIFO ticket.
                j.NextCheck = 0;
                return false;
            }
            if (__instance.DockState != EHarvesterAIDockState.UNDOCKED)
            {
                // A physical docking operation cannot be converted to an ordinary move.
                // Adopt it in ManagedHarvestUpdate, retaining the bay until it exits.
                if (j.Line == null) j.Phase = HarvestPhase.Starting;
                return false;
            }
            ReleaseRefineryReservation(j);
            __instance.CurrentDepositPoint = null;
            __instance.Stop(true);
            __instance.MiningArms.SetMining(false);
            j.PreferredRefinery = depositPoint.Structure;
            j.Phase = HarvestPhase.WaitingForRefinery;
            j.ReadyAt = Time.time; j.NextCheck = 0;
            HarvestDiagnostic(j, "deposit requested: waiting for managed refinery admission");
            // Do not issue a nested order inside AIOrderProcessor.HandleOrder/UpdateOrders.
            // The next agent update installs the queue move or admitted deposit order.
            return false;
        }

        private static bool PatchAvailable(HarvestJob j, ResourceArea area)
        {
            if (!area || area.ResourceType != j.Agent.ResourceHolder.ResourceType || area.ResourceAmountCurrent <= 0 ||
                !FogOfWar.GetPositionVisible(j.Team, area.SignalCenter, false)) return false;
            // Existing owners retain their home patch if a new refinery reduces its
            // distance-band limit. Only new assignments are subject to the lower cap.
            if (j.PatchReserved && j.Area == area) return true;
            int count = 0;
            foreach (var other in HarvestJobs.Values)
                if (other != j && other.Team == j.Team && other.PatchReserved && other.Area == area) count++;
            return count < PatchReservationLimit(j, area);
        }

        private static int PatchReservationLimit(HarvestJob j, ResourceArea area)
        {
            var c = Cfg.HarvesterFix;
            if (!area) return c.FarPatchReservations;
            float nearest = float.PositiveInfinity;
            if (Structure.Structures != null)
                foreach (var refinery in Structure.Structures)
                {
                    if (!refinery || refinery.IsDestroyed || refinery.Team != j.Team || refinery.ResourceDepositPoints == null) continue;
                    bool acceptsResource = false;
                    foreach (var point in refinery.ResourceDepositPoints)
                        if (point && point.ResourceType == area.ResourceType) { acceptsResource = true; break; }
                    if (!acceptsResource) continue;
                    Vector3 delta = refinery.transform.position - area.SignalCenter;
                    float distance = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z);
                    if (distance < nearest) nearest = distance;
                }
            if (nearest <= c.NearPatchDistanceMetres) return c.NearPatchReservations;
            if (nearest < c.MediumPatchDistanceMetres) return c.MediumPatchReservations;
            return c.FarPatchReservations; // No friendly refinery also uses the far band.
        }

        private static ResourceArea ClosestHarvestPatch(HarvestJob j)
        {
            ResourceArea best = null;
            float distance = float.MaxValue;
            foreach (var area in ResourceArea.AllResourceAreas)
            {
                if (!PatchAvailable(j, area)) continue;
                Vector3 target = area.GetNearestResourceCell(j.Agent.ExtractionPosition, j.Agent.transform.forward);
                if (!j.Agent.Unit.GetPointValidForAI(target, out var _)) continue;
                float d = Vector3.Distance(j.Agent.transform.position, target);
                if (d < distance) { best = area; distance = d; }
            }
            return best;
        }

        private static bool HarvestAreaPrefix(AIHarvesterAgent __instance, ref ResourceArea resourceArea, ref Vector3 worldPosition)
        {
            if (HarvestOwnOrder || !HarvestFixActive || !HarvestEligible(__instance)) return true;
            var j = GetHarvestJob(__instance);
            // During service, native undocking may try to select a patch. Keep the
            // refinery reservation until the vehicle has physically finished undocking.
            if (j.Line != null) return false;
            if (Time.time < j.ReadyAt) return false;
            ReleaseRefineryReservation(j);
            if (j.Area != resourceArea) j.PatchReserved = false;
            if (!PatchAvailable(j, resourceArea)) resourceArea = ClosestHarvestPatch(j);
            j.Area = resourceArea; j.Phase = HarvestPhase.Harvesting; j.NextCheck = 0;
            if (!resourceArea) { __instance.Stop(true); return false; }
            j.PatchReserved = true;
            worldPosition = resourceArea.GetNearestResourceCell(__instance.ExtractionPosition, __instance.transform.forward);
            var orders = __instance.Unit.OrderAgent.Orders;
            if (orders.Count > 0 && orders[0] is AIHarvestOrder harvest)
            { harvest.ResourceArea = resourceArea; harvest.WorldPosition = worldPosition; }
            return true;
        }

        private static bool HarvestReturnPrefix(AIHarvesterAgent __instance)
        {
            if (HarvestOwnOrder || !HarvestFixActive || !HarvestEligible(__instance)) return true;
            var j = GetHarvestJob(__instance);
            if (j.Phase == HarvestPhase.Manual) return true;
            if (j.Line == null) j.Phase = HarvestPhase.WaitingForRefinery;
            return false;
        }

        private static void HarvesterTransportPickup(Unit unit)
        {
            if (!HarvestFixActive) return;
            var a = unit.AIAgent as AIHarvesterAgent;
            if (!HarvestEligible(a)) return;
            var j = GetHarvestJob(a);
            // A successful field pickup opts an AI harvester back into harvesting,
            // including one previously stopped/moved manually. Never take a driver's
            // controls or interrupt factory exit/docking movement.
            if (a.DockState != EHarvesterAIDockState.UNDOCKED || a.Vehicle.HasForceMove) return;
            float tolerance = unit.PhysicalRadius + a.MiningArms.ExtractionRadius;
            var area = j.Area && j.Area.GetPositionInBounds(a.ExtractionPosition, tolerance) ? j.Area :
                ResourceArea.GetResourceAreaAtPosition(a.Team, a.ResourceHolder.ResourceType, a.ExtractionPosition, true, tolerance);
            if (!area || area.ResourceAmountCurrent <= 0)
            {
                if (j.PickupUntil > 0 || j.ResumeAfterPickup)
                { j.PickupUntil = 0; j.ResumeAfterPickup = false; j.NextCheck = 0; }
                return; // A pickup at a depleted patch must never restart the hold.
            }
            bool startingWindow = Time.time >= j.PickupUntil;
            bool restoreOrder = startingWindow || j.Line != null || j.Phase != HarvestPhase.Harvesting || a.CurrentHarvestArea != area;
            bool keepReservation = j.PatchReserved && j.Area == area;
            ReleaseRefineryReservation(j);
            if (j.Area != area) j.PatchReserved = false;
            a.CurrentDepositPoint = null;
            j.Area = area;
            // Manual placement can physically exceed the limit; it must not create
            // extra automatic reservations or displace an existing reservation.
            j.PatchReserved = keepReservation || PatchAvailable(j, area);
            j.Phase = HarvestPhase.Harvesting;
            j.ReadyAt = Time.time;
            j.PreferredRefinery = null;
            j.ExitRequested = false;
            j.ResumeAfterPickup |= restoreOrder;
            j.PickupUntil = Time.time + Cfg.HarvesterFix.TransferWaitingTimeSeconds;
            j.NextCheck = 0;
            if (startingWindow) HarvestDiagnostic(j, "field pickup: resuming top-up harvesting; timer resets on each transfer");
        }

        private static bool RefineryHasRoom(HarvestJob j, Structure refinery)
        {
            // Refinery-local storage is not an admission constraint: native storage
            // distribution handles it. Native unloading still enforces team capacity.
            return refinery && !refinery.IsDestroyed && refinery.Team == j.Team;
        }

        private static void RepairBayReservation(ResourceDepositPoint p)
        {
            if (p.OccupiedByUnit && p.OccupiedByUnit.IsDestroyed) p.OccupiedByUnit = null;
            var occupant = p.OccupiedByUnit;
            if (occupant && HarvestJobs.TryGetValue(occupant.GetInstanceID(), out var occupiedJob) &&
                occupiedJob.Phase != HarvestPhase.Manual && HarvestEligible(occupiedJob.Agent) &&
                occupiedJob.Agent.DockState == EHarvesterAIDockState.UNDOCKED && occupiedJob.Agent.CurrentDepositPoint != p &&
                Vector3.Distance(occupant.transform.position, p.transform.position) > Math.Max(100f, p.DepositRadius + occupant.PhysicalRadius * 2f))
            {
                p.OccupiedByUnit = null;
                HarvestDiagnostic(occupiedJob, "released stale occupancy after leaving bay " + HarvestObjectId(p));
            }
            var owner = p.ReservedByUnit;
            if (!owner) return;
            if (owner.IsDestroyed) { p.ReservedByUnit = null; return; }
            if (!HarvestJobs.TryGetValue(owner.GetInstanceID(), out var job) ||
                job.Phase == HarvestPhase.Manual || !HarvestEligible(job.Agent)) return;
            if (job.Agent.DockState != EHarvesterAIDockState.UNDOCKED) return;
            // Only the admitted head may own a native bay reservation. Logical queue
            // entries do not reserve the physical bay, even if they inherited one.
            bool head = job.Line != null && job.Line.Point == p && job.Line.Jobs.Count > 0 &&
                job.Line.Jobs[0] == job && job.Phase == HarvestPhase.Depositing;
            if (!head || job.Agent.CurrentDepositPoint != p)
            {
                p.ReservedByUnit = null;
                HarvestDiagnostic(job, "released obsolete native bay reservation " + HarvestObjectId(p));
            }
        }

        private static bool ForeignBayBusy(RefineryLine line)
        {
            foreach (var p in line.Refinery.ResourceDepositPoints)
            {
                if (!p) continue;
                RepairBayReservation(p);
                foreach (var unit in new[] { p.OccupiedByUnit, p.ReservedByUnit })
                {
                    if (!unit || unit.IsDestroyed) continue;
                    // An actual occupant behind the head is still an obstruction until
                    // it has moved out. Never treat every queue member as the bay owner.
                    bool head = line.Jobs.Count > 0 && line.Jobs[0].Agent && line.Jobs[0].Agent.Unit == unit;
                    if (!head) return true;
                }
            }
            return false;
        }

        private static RefineryLine GetRefineryLine(Structure refinery, ResourceDepositPoint point, Vector3 from)
        {
            if (RefineryLines.TryGetValue(refinery.GetInstanceID(), out var existing)) return existing;
            Vector3 approach = point.GetClosestApproachPoint(from);
            Vector3 direction = approach - point.transform.position; direction.y = 0;
            if (direction.sqrMagnitude < 1) { direction = from - approach; direction.y = 0; }
            if (direction.sqrMagnitude < 1) direction = refinery.transform.forward;
            return new RefineryLine { Refinery = refinery, Point = point, Approach = approach, Direction = direction.normalized };
        }

        private static RefineryLine ClosestRefinery(HarvestJob j, bool requireSlot)
        {
            RefineryLine best = null;
            RefineryLine preferred = null;
            var candidates = new List<RefineryLine>();
            float distance = float.MaxValue;
            foreach (var refinery in Structure.Structures)
            {
                if (!RefineryHasRoom(j, refinery) || refinery.ResourceDepositPoints == null) continue;
                if (!RefineryLines.TryGetValue(refinery.GetInstanceID(), out var line))
                {
                    ResourceDepositPoint point = null;
                    float near = float.MaxValue;
                    foreach (var p in refinery.ResourceDepositPoints)
                    {
                        if (!p || p.ResourceType != j.Agent.ResourceHolder.ResourceType) continue;
                        float d = Vector3.Distance(j.Agent.transform.position, p.transform.position);
                        if (d < near) { near = d; point = p; }
                    }
                    if (!point) continue;
                    line = GetRefineryLine(refinery, point, j.Agent.transform.position);
                }
                candidates.Add(line);
                // A native docking approach is not necessarily on the ordinary navmesh.
                // Rejecting it with Unit.GetPointValidForAI's 2m tolerance can exclude
                // every refinery. Native Deposit owns this final approach.
                if (requireSlot && refinery == j.PreferredRefinery) preferred = line;
                float dist = Vector3.Distance(j.Agent.transform.position, line.Point.transform.position);
                if (dist < distance) { best = line; distance = dist; }
            }
            if (!requireSlot || best == null) return best;
            if (preferred != null) best = preferred;
            int count = RefineryQueueCount(best);
            // A new arrival would take fourth place or later. Search near the
            // originally selected refinery, not across the entire map.
            if (count >= 3)
            {
                RefineryLine alternative = null;
                float nearest = float.MaxValue;
                foreach (var candidate in candidates)
                {
                    if (candidate == best || Vector3.Distance(candidate.Refinery.transform.position, best.Refinery.transform.position) > 800f) continue;
                    int otherCount = RefineryQueueCount(candidate);
                    if (otherCount >= count || otherCount >= Cfg.HarvesterFix.MaximumReservationsPerRefinery) continue;
                    float d = Vector3.Distance(j.Agent.Unit.transform.position, candidate.Point.transform.position);
                    if (d < nearest) { alternative = candidate; nearest = d; }
                }
                if (alternative != null) return alternative;
            }
            return count < Cfg.HarvesterFix.MaximumReservationsPerRefinery ? best : null;
        }

        private static int RefineryQueueCount(RefineryLine line)
        {
            int count = line.Jobs.Count;
            // A physically occupied bay may be a queued member exiting it. Count
            // it only once; truly external occupants/reservations consume one slot.
            foreach (var p in line.Refinery.ResourceDepositPoints)
            {
                if (!p) continue;
                RepairBayReservation(p);
                foreach (var unit in new[] { p.ReservedByUnit, p.OccupiedByUnit })
                {
                    if (!unit || unit.IsDestroyed) continue;
                    bool member = false;
                    foreach (var job in line.Jobs) if (job.Agent && job.Agent.Unit == unit) { member = true; break; }
                    if (!member) return count + 1;
                }
            }
            return count;
        }

        private static void HoldHarvester(HarvestJob j)
        {
            if (j.Agent.IsFollowingPath) j.Agent.Stop(true);
            j.Agent.MiningArms.SetMining(j.Area && j.Area.ResourceAmountCurrent > 0 &&
                j.Agent.ResourceHolder.RemainingSpace > 0 && j.Agent.MiningArms.IsResourceAvailable(true));
        }

        private static void SendToPatch(HarvestJob j, ResourceArea area)
        {
            if (!PatchAvailable(j, area)) area = ClosestHarvestPatch(j);
            ReleaseRefineryReservation(j);
            if (j.Area != area) j.PatchReserved = false;
            j.Area = area; j.Phase = HarvestPhase.Harvesting;
            if (!area) { HoldHarvester(j); return; }
            j.PatchReserved = true;
            if (!IssueHarvestOrder(j, OrderDefinitionRegistry.Harvest, new OrderTarget(area.transform.position, null, area)))
            { j.PatchReserved = false; j.Area = null; }
        }

        private static void JoinRefinery(HarvestJob j)
        {
            if (j.Agent.DockState == EHarvesterAIDockState.UNDOCKED)
                j.Agent.CurrentDepositPoint = null;
            var line = ClosestRefinery(j, true);
            if (line == null)
            {
                j.Phase = HarvestPhase.WaitingForRefinery; HoldHarvester(j);
                HarvestDiagnostic(j, j.Team.RemainingResourceCapacity <= 0 ? "waiting: team resource storage is full" : "waiting: no eligible refinery reservation; retrying");
                return;
            }
            // The home patch remains reserved throughout the refinery trip.
            RefineryLines[line.Refinery.GetInstanceID()] = line;
            line.Jobs.Add(j); j.Line = line; j.Phase = HarvestPhase.Queued; j.LastSlot = -1;
            HarvestDiagnostic(j, "joined refinery=" + HarvestObjectId(line.Refinery) + " queue index=" + line.Jobs.Count);
            AdvanceRefinery(j);
        }

        private static void AdvanceRefinery(HarvestJob j)
        {
            var line = j.Line;
            if (line == null) return;
            if (!line.Point || !RefineryHasRoom(j, line.Refinery))
            { ReleaseRefineryReservation(j); j.Agent.CurrentDepositPoint = null; j.Phase = HarvestPhase.WaitingForRefinery; JoinRefinery(j); return; }
            int slot = line.Jobs.IndexOf(j) + (ForeignBayBusy(line) ? 1 : 0);
            if (slot == 0)
            {
                if (j.Phase != HarvestPhase.Depositing || j.Agent.CurrentDepositPoint != line.Point)
                {
                    j.Agent.MiningArms.SetMining(false);
                    if (IssueHarvestOrder(j, OrderDefinitionRegistry.Deposit, new OrderTarget(line.Point.transform.position, null, line.Point)))
                    {
                        j.Phase = HarvestPhase.Depositing; j.LastSlot = 0;
                        HarvestDiagnostic(j, "admitted to refinery=" + HarvestObjectId(line.Refinery) + " slot=1");
                    }
                    else HarvestDiagnostic(j, "deposit order rejected for refinery=" + HarvestObjectId(line.Refinery));
                }
                // Native occupancy entry clears ReservedByUnit. Once it is free again,
                // reacquire only for the head, never for a tail waiting 200m away.
                if (!line.Point.ReservedByUnit && !line.Point.OccupiedByUnit && j.Phase == HarvestPhase.Depositing)
                    j.Agent.CurrentDepositPoint = line.Point;
                return;
            }
            if (j.Phase == HarvestPhase.Depositing)
            { j.Phase = HarvestPhase.Queued; j.Agent.CurrentDepositPoint = null; j.LastSlot = -1; }
            if (j.LastSlot == slot && (j.Agent.IsFollowingPath || j.Agent.IsCalculatingPath)) return;
            // slot is zero based: dock=0, first waiting vehicle=1. The first
            // waiting distance is fixed; the setting changes only later increments.
            float waitingDistance = 100f + (slot - 1) * Cfg.HarvesterFix.ReservationDistanceIncrementMetres;
            Vector3 desired = line.Approach + line.Direction * waitingDistance;
            desired = GamePhysics.GetTerrainPosition(desired);
            if (!TryQueuePoint(j, desired, out var point))
            { HoldHarvester(j); HarvestDiagnostic(j, "waiting: no navigable position for slot=" + (slot + 1)); return; }
            if (j.LastSlot != slot || Vector3.Distance(j.Agent.transform.position, point) > Math.Max(15f, j.Agent.Unit.PhysicalRadius))
            {
                j.Agent.MiningArms.SetMining(false);
                if (IssueHarvestOrder(j, OrderDefinitionRegistry.Move, new OrderTarget(point)))
                {
                    j.LastSlot = slot;
                    HarvestDiagnostic(j, "moving to refinery=" + HarvestObjectId(line.Refinery) + " slot=" + (slot + 1) + " position=" + point);
                }
                else HarvestDiagnostic(j, "queue move rejected for slot=" + (slot + 1));
            }
        }

        private static bool TryQueuePoint(HarvestJob j, Vector3 desired, out Vector3 point)
        {
            if (j.Agent.Unit.GetPointValidForAI(desired, out point)) return true;
            // Keep the configured spacing, allowing a small terrain-dependent lateral
            // adjustment instead of rejecting the whole slot for a 2m navmesh gap.
            Vector3 side = Vector3.Cross(Vector3.up, j.Line.Direction).normalized;
            for (int offset = 10; offset <= 40; offset += 10)
                foreach (int sign in new[] { -1, 1 })
                {
                    Vector3 candidate = GamePhysics.GetTerrainPosition(desired + side * (offset * sign));
                    if (j.Agent.Unit.GetPointValidForAI(candidate, out point)) return true;
                }
            point = Vector3.zero;
            return false;
        }

        private static bool HarvestUpdatePrefix(AIHarvesterAgent __instance)
        {
            if (!HarvestFixActive || !HarvestEligible(__instance)) return true;
            var j = GetHarvestJob(__instance);
            if (j.Phase == HarvestPhase.Manual) return true;
            try { return ManagedHarvestUpdate(j); }
            catch (Exception e)
            {
                ReleaseHarvestReservations(j); j.Phase = HarvestPhase.Manual;
                DebugError("Harvester fix released vehicle to vanilla after error: " + e);
                return true;
            }
        }

        private static bool ManagedHarvestUpdate(HarvestJob j)
        {
            var a = j.Agent;
            float now = Time.time;
            a.IsIdle = false;
            if (j.Line == null && !j.ExitRequested && a.CurrentDepositPoint && a.CurrentDepositPoint.Structure &&
                a.CurrentDepositPoint.Structure.Team == j.Team)
            {
                var point = a.CurrentDepositPoint;
                if (a.DockState != EHarvesterAIDockState.UNDOCKED)
                {
                    var line = GetRefineryLine(point.Structure, point, a.Unit.transform.position);
                    if (line.Jobs.Count >= Cfg.HarvesterFix.MaximumReservationsPerRefinery)
                    {
                        j.Phase = HarvestPhase.WaitingForRefinery;
                        j.ExitRequested = IssueHarvestOrder(j, OrderDefinitionRegistry.Move, new OrderTarget(point.GetClosestApproachPoint(a.Unit.transform.position)));
                        HarvestDiagnostic(j, "native bay entrant exiting: queue at hard reservation limit");
                        return true;
                    }
                    RefineryLines[line.Refinery.GetInstanceID()] = line;
                    line.Jobs.Add(j); j.Line = line;
                    j.Phase = line.Jobs.Count == 1 ? HarvestPhase.Depositing : HarvestPhase.Queued;
                    j.LastSlot = -1;
                    HarvestDiagnostic(j, "adopted native docking at BACK of refinery=" + HarvestObjectId(line.Refinery) + " queue index=" + line.Jobs.Count);
                }
                else
                {
                    j.PreferredRefinery = point.Structure;
                    a.CurrentDepositPoint = null;
                    j.Phase = a.ResourceHolder.AmountStored > 0 ? HarvestPhase.WaitingForRefinery : HarvestPhase.Starting;
                    j.NextCheck = 0;
                }
            }
            // A destroyed/missing docking target cannot ever finish vanilla's DOCKED
            // state while cargo remains. Recover only after force-movement has ended.
            if (!a.CurrentDepositPoint && !a.Vehicle.HasForceMove && a.DockState != EHarvesterAIDockState.UNDOCKED)
            {
                a.DockState = EHarvesterAIDockState.UNDOCKED;
                ReleaseRefineryReservation(j); j.Phase = HarvestPhase.Starting; j.NextCheck = 0;
                HarvestDiagnostic(j, "recovered missing native dock target");
            }
            // Let the native docking/undocking controller and factory force-move finish.
            if (a.DockState != EHarvesterAIDockState.UNDOCKED)
            {
                if (j.Line != null && j.Line.Jobs.IndexOf(j) > 0 && !j.ExitRequested)
                {
                    // Let native undocking clear a late entrant already inside the
                    // bay. It keeps its tail ticket and cannot become queue head.
                    j.ExitRequested = IssueHarvestOrder(j, OrderDefinitionRegistry.Move, new OrderTarget(j.Line.Approach));
                    j.Phase = HarvestPhase.Queued;
                }
                if (j.Line != null && !j.ExitRequested && now >= j.NextCheck && !RefineryHasRoom(j, j.Line.Refinery))
                {
                    j.NextCheck = now + Cfg.HarvesterFix.ReservationCheckTimeSeconds;
                    j.ExitRequested = IssueHarvestOrder(j, OrderDefinitionRegistry.Move, new OrderTarget(j.Line.Approach));
                }
                return true;
            }
            if (a.Vehicle.HasForceMove) return false;
            if (now < j.ReadyAt) return false;
            if (j.Line == null && j.ExitRequested && !a.CurrentDepositPoint)
            { j.ExitRequested = false; j.NextCheck = 0; }
            if (j.Phase == HarvestPhase.Queued && j.ExitRequested && !a.CurrentDepositPoint)
            { j.ExitRequested = false; j.NextCheck = 0; }
            if (j.Phase == HarvestPhase.Depositing && (a.ResourceHolder.AmountStored == 0 || j.ExitRequested) && !a.CurrentDepositPoint)
            {
                ReleaseRefineryReservation(j); j.ExitRequested = false; j.PreferredRefinery = null;
                j.Phase = HarvestPhase.Starting; j.NextCheck = 0;
                HarvestDiagnostic(j, "cleared refinery; released queue head");
            }
            // Keep the home patch through extraction, top-ups, refinery travel and
            // unloading. Depletion, reassignment or relinquishing AI releases it.
            if (j.PickupUntil > 0 && now >= j.PickupUntil)
            {
                j.PickupUntil = 0; j.ResumeAfterPickup = false; j.NextCheck = 0;
                HarvestDiagnostic(j, "pickup window expired: normal harvesting/refinery routing resumed");
            }

            bool depleted = !j.Area || j.Area.ResourceAmountCurrent <= 0;
            if (depleted && (j.PickupUntil > 0 || j.ResumeAfterPickup))
            {
                j.PickupUntil = 0; j.ResumeAfterPickup = false; j.NextCheck = 0;
                HarvestDiagnostic(j, "patch depleted: pickup hold cleared; normal routing resumed");
            }
            bool full = a.ResourceHolder.RemainingSpace == 0;
            bool pickupWait = now < j.PickupUntil;
            if (pickupWait || j.ResumeAfterPickup)
            {
                if (full || depleted) { HoldHarvester(j); return false; }
                if (j.ResumeAfterPickup || a.CurrentHarvestArea != j.Area)
                {
                    if (now < j.NextCheck) return false;
                    j.NextCheck = now + Cfg.HarvesterFix.ReservationCheckTimeSeconds;
                    // Restores the native agent's CurrentHarvestArea and local mining
                    // movement, not just our own Harvesting enum. Do not reissue on
                    // every transfer tick; that would repeatedly restart its path.
                    if (!IssueHarvestOrder(j, OrderDefinitionRegistry.Harvest,
                        new OrderTarget(j.Area.transform.position, null, j.Area)))
                    { HarvestDiagnostic(j, "top-up harvest order rejected; retrying"); return false; }
                    j.ResumeAfterPickup = false;
                }
                j.Phase = HarvestPhase.Harvesting;
                return true; // Native mining/repositioning continues until full.
            }
            if (now >= j.NextCheck)
            {
                j.NextCheck = now + Cfg.HarvesterFix.ReservationCheckTimeSeconds;
                if (j.Line != null) AdvanceRefinery(j);
                else if (pickupWait && (full || depleted)) HoldHarvester(j);
                else if (j.Phase == HarvestPhase.WaitingForRefinery && !pickupWait) JoinRefinery(j);
                else if (full && !pickupWait) JoinRefinery(j);
                else if (depleted && !pickupWait)
                {
                    var next = ClosestHarvestPatch(j);
                    var refinery = a.ResourceHolder.AmountStored > 0 ? ClosestRefinery(j, false) : null;
                    float patchDistance = next ? Vector3.Distance(a.transform.position, next.GetNearestResourceCell(a.ExtractionPosition, a.transform.forward)) : float.MaxValue;
                    if (a.ResourceHolder.AmountStored > 0 && (next == null || (refinery != null &&
                        Vector3.Distance(a.transform.position, refinery.Point.transform.position) < patchDistance))) JoinRefinery(j);
                    else SendToPatch(j, next);
                }
                else if (j.Phase == HarvestPhase.Starting || (j.Area && a.CurrentHarvestArea != j.Area))
                    SendToPatch(j, PatchAvailable(j, j.Area) ? j.Area : ClosestHarvestPatch(j));
            }
            if (j.Phase == HarvestPhase.Depositing) return true;
            if (j.Phase == HarvestPhase.Queued) return false;
            if (j.Phase == HarvestPhase.WaitingForRefinery || a.ResourceHolder.RemainingSpace == 0 || !j.Area || j.Area.ResourceAmountCurrent <= 0)
            { HoldHarvester(j); return false; }
            // Native mining, local repositioning and stuck recovery stay intact.
            return j.Area != null;
        }
    }
}
