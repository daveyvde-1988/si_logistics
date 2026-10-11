using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json;
using Silica.AI;
using SilicaAdminMod;
using UnityEngine;

namespace Si_Logistics
{
    public sealed class AITransportConfig
    {
        public bool Enabled = false;
        [JsonProperty("airloadradius")] public float AirLoadRadius = 150f;
        [JsonProperty("airunloadradius")] public float AirUnloadRadius = 150f;
        [JsonProperty("groundloadradius")] public float GroundLoadRadius = 40f;
        [JsonProperty("groundunloadradius")] public float GroundUnloadRadius = 65f;
        public float NearHarvesterDistanceMetres = 300f;
        public float MediumHarvesterDistanceMetres = 600f;
        public int NearGroundTransports = 2, NearAirTransports = 1;
        public int MediumGroundTransports = 3, MediumAirTransports = 1;
        public int FarGroundTransports = 4, FarAirTransports = 2;
        public float ReassignmentCheckSeconds = 10f;
        public float NoProgressTimeoutSeconds = 120f;
    }

    public sealed partial class LogisticsMod
    {
        private sealed class AITransportJob
        {
            internal Unit Transport, Harvester;
            internal Team Team;
            internal Structure Refinery;
            internal bool Delivering, Holding;
            internal float NextMove, NextRefinerySearch;
            internal Vector3 Destination, TargetPosition;
            internal string Status;
            internal int Attempts;
            internal int ServiceFrame = -1;
            internal Vector3 ServiceCenter, ServiceFrom, ServicePoint;
            internal float ServiceRadius;
            internal Structure ServiceRefinery;
            internal bool ServiceFound;
            internal ResourceArea AssignedArea;
            internal Unit AvoidHarvester;
            internal float AvoidUntil, NextAllocation, LastProgress, BestDistance = float.MaxValue;
            internal bool Parking;
            internal Vector3 ParkingPoint;
        }
        private static readonly Dictionary<int, AITransportJob> AITransportJobs = new Dictionary<int, AITransportJob>();
        private static bool AITransportInstalled;
        private static int AITransportOwnOrder;
        private static bool AITransportActive => AITransportInstalled && Cfg.Enabled && Cfg.AITransport.Enabled && Game.GetIsServer() && MatchAcceptsRewards;

        private static float AILoadRadius(Unit u) => u && u.IsFlyingType ? Cfg.AITransport.AirLoadRadius : Cfg.AITransport.GroundLoadRadius;
        private static float AIUnloadRadius(Unit u) => u && u.IsFlyingType ? Cfg.AITransport.AirUnloadRadius : Cfg.AITransport.GroundUnloadRadius;

        private void InstallAITransport()
        {
            HarmonyInstance.Patch(AccessTools.Method(typeof(AIOrderProcessor), "IssueOrder"),
                prefix: new HarmonyMethod(typeof(LogisticsMod), nameof(AITransportOrderPrefix)),
                postfix: new HarmonyMethod(typeof(LogisticsMod), nameof(AITransportOrderPostfix)));
            AITransportInstalled = true;
        }

        private static bool AITransportEligible(Unit u) => IsTransport(u) && u.AIAgent && u.AIAgent.AIControlled &&
            u.OrderAgent && !u.PlayerControlled && DriverOf(u) == null && !u.InCompartment;
        private static bool AIHarvesterTarget(Unit h, Team team) => h && !h.IsDestroyed && h.Team == team &&
            h.ObjectInfo && h.ObjectInfo.UnitType == UnitType.Harvester && h.IsResourceHolder;
        private static bool AIRefineryTarget(Structure s, Team team) => s && !s.IsDestroyed && s.Team == team &&
            s.ResourceDepositPoints != null && s.ResourceDepositPoints.Count > 0;

        // Commander right-click on a friendly unit resolves to native Defend (the moving
        // friendly-target Follow behaviour). Do not infer intent from proximity or repaths.
        // Assigned units reject competing autonomous orders; explicit commands still execute.
        private static bool AITransportOrderPrefix(AIOrderProcessor __instance, in OrderIssueParams issueParams, ref bool __result)
        {
            if (AITransportOwnOrder > 0 || !AITransportActive) return true;
            var u = __instance.Owner as Unit;
            if (u && AITransportJobs.ContainsKey(u.GetInstanceID()) && AITransportEligible(u) &&
                (issueParams.CalledByAI || !issueParams.BroadcastEvent))
            { __result = false; return false; }
            return true;
        }

        private static void AITransportOrderPostfix(AIOrderProcessor __instance, OrderDefinition definition,
            in OrderTarget target, in OrderIssueParams issueParams, bool __result)
        {
            if (!__result || AITransportOwnOrder > 0 || !Game.GetIsServer() ||
                issueParams.CalledByAI || !issueParams.BroadcastEvent) return;
            var u = __instance.Owner as Unit;
            if (!u) return;
            bool hadJob = AITransportJobs.TryGetValue(u.GetInstanceID(), out var previous);
            if (hadJob) CancelAITransport(previous, "new external order " + definition.name, false);
            if (!AITransportActive || !AITransportEligible(u)) return;
            var h = target.Object ? target.Object.OwnerUnit : null;
            if (Cfg.Debug || hadJob || AIHarvesterTarget(h, u.Team))
                DebugMsg("[AITransport] order transport=" + u.GetInstanceID() + " definition=" + definition.name +
                    " target=" + HarvestObjectId(h) + " queued=" + issueParams.QueueOrder);
            // A queued order must not jump ahead of the player's earlier commands.
            if (issueParams.QueueOrder || definition != OrderDefinitionRegistry.Defend || !AIHarvesterTarget(h, u.Team)) return;
            var job = new AITransportJob { Transport = u, Team = u.Team, LastProgress = Time.time };
            AITransportJobs.Add(u.GetInstanceID(), job);
            if (CanAssignAIHarvester(job, h)) AssignAIHarvester(job, h);
            else AILog(job, "enrolled in logistics duty; selecting available harvester");
        }

        private static void ShowAITransportStatus(Player caller)
        {
            Reply(caller, "AI transport " + (Cfg.AITransport.Enabled ? "ON" : "OFF") + " | assigned " + AITransportJobs.Count +
                " | right-click a friendly harvester to enrol in automatic logistics duty; a new order cancels. Details in server log.");
            if (!Cfg.Enabled && Cfg.AITransport.Enabled) Reply(caller, "AI transport paused by Logistics master switch.");
            if (!Cfg.Debug) return;
            DebugMsg("[AITransport] status enabled=" + Cfg.AITransport.Enabled + " active=" + AITransportActive + " duty=" + AITransportJobs.Count +
                " bands=" + Cfg.AITransport.NearHarvesterDistanceMetres + "/" + Cfg.AITransport.MediumHarvesterDistanceMetres +
                " groundLimits=" + Cfg.AITransport.NearGroundTransports + "/" + Cfg.AITransport.MediumGroundTransports + "/" + Cfg.AITransport.FarGroundTransports +
                " airLimits=" + Cfg.AITransport.NearAirTransports + "/" + Cfg.AITransport.MediumAirTransports + "/" + Cfg.AITransport.FarAirTransports);
            foreach (var j in AITransportJobs.Values)
            {
                int amount = j.Transport && CargoByTransport.TryGetValue(j.Transport.GetInstanceID(), out var cargo) ? cargo.Amount : 0;
                DebugMsg("[AITransport] transport=" + HarvestObjectId(j.Transport) + " harvester=" + HarvestObjectId(j.Harvester) +
                    " patch=" + HarvestObjectId(j.AssignedArea) + " refinery=" + HarvestObjectId(j.Refinery) + " delivering=" + j.Delivering + " cargo=" + amount +
                    " loadRadius=" + AILoadRadius(j.Transport) + " unloadRadius=" + AIUnloadRadius(j.Transport) +
                    " holding=" + j.Holding + " position=" + (j.Transport ? j.Transport.transform.position.ToString() : "destroyed") +
                    " destination=" + j.Destination + " status=" + j.Status);
            }
        }

        private static void AILog(AITransportJob j, string status)
        {
            if (j.Status == status) return;
            j.Status = status;
            DebugMsg("[AITransport] transport=" + HarvestObjectId(j.Transport) + " harvester=" + HarvestObjectId(j.Harvester) + " " + status);
        }

        private static bool AIOrder(AITransportJob j, OrderDefinition definition, Vector3 position)
        {
            AITransportOwnOrder++;
            try { return j.Transport.OrderAgent.IssueOrder(definition, new OrderTarget(position), OrderIssueParams.SelfDirected(AgentMoveSpeed.Fast)); }
            finally { AITransportOwnOrder--; }
        }

        private static void AIHold(AITransportJob j)
        {
            if (!j.Holding) j.Holding = AIOrder(j, OrderDefinitionRegistry.Stop, j.Transport.transform.position);
        }

        private static void CancelAITransport(AITransportJob j, string reason, bool stop)
        {
            if (j.Transport)
            {
                AITransportJobs.Remove(j.Transport.GetInstanceID());
                if (CargoByTransport.TryGetValue(j.Transport.GetInstanceID(), out var cargo))
                {
                    SetTransfer(j.Transport, cargo, null);
                    if (j.Transport.Team != j.Team) CargoByTransport.Remove(j.Transport.GetInstanceID());
                }
                if (stop && AITransportEligible(j.Transport)) AIHold(j);
            }
            else
                foreach (var p in new List<KeyValuePair<int, AITransportJob>>(AITransportJobs))
                    if (ReferenceEquals(p.Value, j)) AITransportJobs.Remove(p.Key);
            AILog(j, "released: " + reason);
        }

        private static void ResetAITransports(bool stop)
        {
            foreach (var j in new List<AITransportJob>(AITransportJobs.Values)) CancelAITransport(j, "disabled or match reset", stop);
            AITransportJobs.Clear();
        }

        // Uses exactly the same cargo ledger, transfer rates, radii, capacity, cooldown
        // and harvester pickup timer as player logistics. Only the assigned target loads.
        private static bool TickAITransport(AITransportJob j)
        {
            var u = j.Transport;
            if (!AITransportActive || !AITransportEligible(u) || u.Team != j.Team)
            {
                CancelAITransport(j, "disabled, team changed or player takeover", u && !u.PlayerControlled && DriverOf(u) == null);
                return false;
            }
            if (!CargoByTransport.TryGetValue(u.GetInstanceID(), out var cargo))
            { cargo = new Cargo(); CargoByTransport.Add(u.GetInstanceID(), cargo); }
            if (!j.Delivering && cargo.Amount >= CapacityFor(u)) BeginAIDelivery(j, "full; heading to refinery");
            if (j.Delivering && cargo.Amount == 0)
            {
                j.Delivering = false; j.Refinery = null; j.NextAllocation = 0;
                ReleaseAIHarvester(j);
                AILog(j, "empty; selecting next collection assignment");
            }
            if (!j.Delivering) AllocateAITransport(j, cargo);
            if (j.Delivering)
            {
                if (!AIRefineryTarget(j.Refinery, j.Team))
                {
                    j.Refinery = null;
                    if (Time.time >= j.NextRefinerySearch)
                    {
                        j.NextRefinerySearch = Time.time + 5f;
                        j.Refinery = ClosestAIRefinery(j);
                        j.Holding = false; j.NextMove = 0;
                        if (j.Refinery) AILog(j, "delivery refinery=" + j.Refinery.GetInstanceID());
                    }
                }
                if (!j.Refinery)
                { AIHold(j); SetTransfer(u, cargo, null); AILog(j, "waiting for a reachable friendly refinery"); return true; }
                if (Vector3.Distance(u.transform.position, j.Refinery.transform.position) <= AIUnloadRadius(u))
                {
                    AIHold(j);
                    if (Time.unscaledTime >= cargo.CooldownUntil && TryUnload(u, cargo, j.Refinery, AIUnloadRadius(u)))
                        AILog(j, cargo.Amount == 0 ? "delivery complete" : "unloading");
                    else { SetTransfer(u, cargo, null); AILog(j, "waiting for team storage or transfer cooldown"); }
                }
                else { SetTransfer(u, cargo, null); AIDrive(j, j.Refinery.transform.position, AIUnloadRadius(u), j.Refinery); }
            }
            else if (j.Harvester)
            {
                if (Vector3.Distance(u.transform.position, j.Harvester.transform.position) <= AILoadRadius(u))
                {
                    AIHold(j);
                    if (Time.unscaledTime >= cargo.CooldownUntil)
                    {
                        int before = cargo.Amount;
                        TryLoad(u, cargo, j.Harvester, AILoadRadius(u));
                        if (cargo.Amount > before) j.LastProgress = Time.time;
                        AILog(j, cargo.Amount > before ? "loading from assigned harvester" : "waiting for assigned harvester to mine");
                    }
                    else { j.LastProgress = Time.time; SetTransfer(u, cargo, null); AILog(j, "waiting for transfer cooldown"); }
                }
                else { SetTransfer(u, cargo, null); AIDrive(j, j.Harvester.transform.position, AILoadRadius(u)); }
            }
            else { SetTransfer(u, cargo, null); ParkAITransport(j); }
            return true;
        }

        private static Structure ClosestAIRefinery(AITransportJob j)
        {
            Structure best = null;
            float distance = float.MaxValue;
            foreach (var s in AllocationRefineries)
            {
                if (!AIRefineryTarget(s, j.Team)) continue;
                float d = (s.transform.position - j.Transport.transform.position).sqrMagnitude;
                if (d >= distance || !AIServicePosition(j, s.transform.position, AIUnloadRadius(j.Transport), out var point, s)) continue;
                best = s; distance = d;
            }
            return best;
        }

        private static void AIDrive(AITransportJob j, Vector3 center, float radius, Structure refinery = null)
        {
            bool moved = (center - j.TargetPosition).sqrMagnitude > Mathf.Pow(Math.Max(5f, radius * 0.25f), 2);
            if (!moved && Time.time < j.NextMove) return;
            j.NextMove = Time.time + 5f;
            // Keep an existing route when the target is stationary; native stuck recovery
            // gets time to work instead of having its path replaced every tick.
            if (!j.Holding && !moved && j.Transport.AIAgent.IsFollowingPath && j.Attempts % 4 != 3)
            { j.Attempts++; return; }
            if (!AIServicePosition(j, center, radius, out var destination, refinery))
            {
                j.TargetPosition = center; // Respect NextMove after a stationary failed search.
                AIHold(j); AILog(j, "no navigable transfer position; retrying");
                return;
            }
            j.Attempts++;
            if (AIOrder(j, OrderDefinitionRegistry.Move, destination))
            {
                j.Destination = destination; j.TargetPosition = center; j.Holding = false;
                AILog(j, j.Delivering ? "driving to refinery transfer range" : "driving to assigned harvester transfer range");
            }
            else AILog(j, "movement order rejected; retrying");
        }

        private static bool AIServicePosition(AITransportJob j, Vector3 center, float radius, out Vector3 result, Structure refinery = null)
        {
            // Selection and immediate dispatch often ask the exact same question.
            // Reuse only inside the current tick/frame, never across moving-world updates.
            var from = j.Transport.transform.position;
            if (PerformanceTickActive && j.ServiceFrame == Time.frameCount && j.ServiceCenter.Equals(center) &&
                j.ServiceFrom.Equals(from) && j.ServiceRadius == radius && j.ServiceRefinery == refinery)
            {
                if (Cfg.Debug) PerfServiceHits++;
                result = j.ServicePoint; return j.ServiceFound;
            }
            if (Cfg.Debug) PerfSearches++;
            bool found = FindAIServicePosition(j, center, radius, out result, refinery);
            j.ServiceFrame = PerformanceTickActive ? Time.frameCount : -1;
            j.ServiceCenter = center; j.ServiceFrom = from; j.ServiceRadius = radius;
            j.ServiceRefinery = refinery; j.ServicePoint = result; j.ServiceFound = found;
            return found;
        }

        private static bool FindAIServicePosition(AITransportJob j, Vector3 center, float radius, out Vector3 result, Structure refinery)
        {
            result = Vector3.zero;
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0) return false;
            var u = j.Transport;
            Vector3 approach = u.transform.position - center; approach.y = 0;
            float angle = Mathf.Atan2(approach.z, approach.x);
            float best = float.MaxValue;
            bool found = false;
            // The previous 40%/70% rings could both lie inside a refinery footprint.
            // Search the full usable range, not just the building's interior.
            foreach (float fraction in ServiceRings)
                for (int n = 0; n < 32; n++)
                {
                    float a = angle + n * Mathf.PI / 16f;
                    Vector3 candidate = center + new Vector3(Mathf.Cos(a) * radius * fraction, 0, Mathf.Sin(a) * radius * fraction);
                    if (Game.MainTerrain) candidate.y = Game.MainTerrain.SampleHeight(candidate) + Game.MainTerrain.transform.position.y;
                    if (Cfg.Debug) PerfNavProbes++;
                    if (!u.GetPointValidForAI(candidate, out var nav)) continue;
                    ConsiderAIServicePoint(j, center, radius * 0.99f, nav, ref best, ref result, ref found);
                }
            if (refinery)
            {
                // Native approach points retain their authored height (terrain sampling
                // can put a point under a refinery platform). They are valid movement
                // destinations even where the strict generic nav-point probe rejects them.
                foreach (var deposit in refinery.ResourceDepositPoints)
                {
                    if (!deposit) continue;
                    var native = deposit.GetClosestApproachPoint(u.transform.position);
                    ConsiderAIServicePoint(j, center, radius * 0.99f, native, ref best, ref result, ref found);
                    foreach (var entry in deposit.AIEntryPoints)
                        if (entry) ConsiderAIServicePoint(j, center, radius * 0.99f, entry.position, ref best, ref result, ref found);
                }
                // Last resort is the native deposit position, never the building centre.
                // Still enforce the unchanged Logistics unload radius.
                if (!found)
                    foreach (var deposit in refinery.ResourceDepositPoints)
                        if (deposit) ConsiderAIServicePoint(j, center, radius * 0.99f, deposit.transform.position,
                            ref best, ref result, ref found);
                if (!found && j.Status != "waiting for a reachable friendly refinery")
                {
                    string points = "";
                    foreach (var deposit in refinery.ResourceDepositPoints)
                        if (deposit) points += " deposit=" + deposit.transform.position + " approach=" +
                            deposit.GetClosestApproachPoint(u.transform.position);
                    DebugMsg("[AITransport] refinery approach rejected transport=" + u.GetInstanceID() +
                        " refinery=" + refinery.GetInstanceID() + " center=" + center + " unloadRadius=" + radius + points);
                }
            }
            return found;
        }

        private static void ConsiderAIServicePoint(AITransportJob j, Vector3 center, float radius, Vector3 point,
            ref float best, ref Vector3 result, ref bool found)
        {
            if (float.IsNaN(point.sqrMagnitude) || float.IsInfinity(point.sqrMagnitude) ||
                Vector3.Distance(point, center) > radius) return;
            var u = j.Transport;
            float score = (point - u.transform.position).sqrMagnitude;
            if (score >= best) return; // Occupancy penalties can only increase this score.
            foreach (var other in AITransportJobs.Values)
            {
                if (ReferenceEquals(other, j) || !other.Transport) continue;
                Vector3 occupied = other.Holding ? other.Transport.transform.position : other.Destination;
                float spacing = Math.Max(12f, u.PhysicalRadius + other.Transport.PhysicalRadius + 5f);
                if ((occupied - point).sqrMagnitude < spacing * spacing) score += 1000000f;
            }
            if (score >= best) return;
            best = score; result = point; found = true;
        }
    }
}
