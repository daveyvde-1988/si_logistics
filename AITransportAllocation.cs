using System;
using System.Collections.Generic;
using MelonLoader;
using Silica.AI;
using UnityEngine;

namespace Si_Logistics
{
    public sealed partial class LogisticsMod
    {
        private static void ValidateAITransportConfig()
        {
            var c = Cfg.AITransport;
            c.AirLoadRadius = ValidHarvestSetting(c.AirLoadRadius, 150, 1, 2000);
            c.AirUnloadRadius = ValidHarvestSetting(c.AirUnloadRadius, 150, 1, 2000);
            c.GroundLoadRadius = ValidHarvestSetting(c.GroundLoadRadius, 40, 1, 2000);
            c.GroundUnloadRadius = ValidHarvestSetting(c.GroundUnloadRadius, 65, 1, 2000);
            c.NearHarvesterDistanceMetres = ValidHarvestSetting(c.NearHarvesterDistanceMetres, 300, 0, 100000);
            c.MediumHarvesterDistanceMetres = ValidHarvestSetting(c.MediumHarvesterDistanceMetres, 600, c.NearHarvesterDistanceMetres, 100000);
            c.NearGroundTransports = Math.Max(0, Math.Min(32, c.NearGroundTransports));
            c.MediumGroundTransports = Math.Max(0, Math.Min(32, c.MediumGroundTransports));
            c.FarGroundTransports = Math.Max(0, Math.Min(32, c.FarGroundTransports));
            c.NearAirTransports = Math.Max(0, Math.Min(32, c.NearAirTransports));
            c.MediumAirTransports = Math.Max(0, Math.Min(32, c.MediumAirTransports));
            c.FarAirTransports = Math.Max(0, Math.Min(32, c.FarAirTransports));
            c.ReassignmentCheckSeconds = ValidHarvestSetting(c.ReassignmentCheckSeconds, 10, 1, 300);
            c.NoProgressTimeoutSeconds = ValidHarvestSetting(c.NoProgressTimeoutSeconds, 120, 10, 3600);
            Cfg.AirLoadUnitsPerSecond = Math.Max(1, Math.Min(100000, Cfg.AirLoadUnitsPerSecond));
            Cfg.AirUnloadUnitsPerSecond = Math.Max(1, Math.Min(100000, Cfg.AirUnloadUnitsPerSecond));
            foreach (var job in AITransportJobs.Values) job.NextAllocation = 0;
        }

        private static ResourceArea ProductiveAIArea(Unit h, Team team)
        {
            if (!AIHarvesterTarget(h, team) || h.PlayerControlled || DriverOf(h) != null) return null;
            var agent = h.AIAgent as AIHarvesterAgent;
            if (!agent || !agent.AIControlled || agent.DockState != EHarvesterAIDockState.UNDOCKED || agent.Vehicle.HasForceMove) return null;
            ResourceArea area = agent.CurrentHarvestArea;
            if (HarvestFixActive && HarvestJobs.TryGetValue(h.GetInstanceID(), out var job))
            {
                if (job.Line != null || job.Phase == HarvestPhase.Manual || job.Phase == HarvestPhase.Depositing || job.Phase == HarvestPhase.Queued) return null;
                area = job.Area;
            }
            return area && area.ResourceAmountCurrent > 0 ? area : null;
        }

        private static float NearestAIRefineryDistance(Vector3 from, Team team)
        {
            float distance = float.MaxValue;
            if (Structure.Structures == null) return distance;
            foreach (var s in AllocationRefineries)
            {
                if (!AIRefineryTarget(s, team)) continue;
                Vector3 delta = s.transform.position - from; delta.y = 0;
                distance = Math.Min(distance, delta.magnitude);
            }
            return distance;
        }

        private static int AIHarvesterLimit(Unit h, bool air)
        {
            float distance = AIHarvesterRefineryDistance(h);
            var c = Cfg.AITransport;
            if (distance <= c.NearHarvesterDistanceMetres) return air ? c.NearAirTransports : c.NearGroundTransports;
            if (distance <= c.MediumHarvesterDistanceMetres) return air ? c.MediumAirTransports : c.MediumGroundTransports;
            return air ? c.FarAirTransports : c.FarGroundTransports;
        }

        // A shared budget implements "ground OR air", rather than adding both maxima.
        // E.g. the far tier fits four ground, two air, or two ground plus one air.
        private static float AIHarvesterUse(AITransportJob self, Unit h)
        {
            float used = 0;
            var area = ProductiveAIArea(h, h.Team);
            int groundLimit = AIHarvesterLimit(h, false), airLimit = AIHarvesterLimit(h, true);
            foreach (var other in AITransportJobs.Values)
            {
                if (ReferenceEquals(other, self) || other.Delivering || other.Harvester != h ||
                    !AITransportEligible(other.Transport) || other.Team != h.Team ||
                    other.AssignedArea != area) continue;
                int limit = other.Transport.IsFlyingType ? airLimit : groundLimit;
                used += limit > 0 ? 1f / limit : 1f;
            }
            return used;
        }

        private static bool CanAssignAIHarvester(AITransportJob j, Unit h)
        {
            if (!ProductiveAIArea(h, j.Team) || (h == j.AvoidHarvester && Time.time < j.AvoidUntil)) return false;
            int limit = AIHarvesterLimit(h, j.Transport.IsFlyingType);
            return limit > 0 && AIHarvesterUse(j, h) + 1f / limit <= 1.0001f;
        }

        private static void ReleaseAIHarvester(AITransportJob j)
        {
            j.Harvester = null; j.AssignedArea = null;
            j.Holding = false; j.Parking = false; j.NextMove = 0; j.Attempts = 0;
            j.TargetPosition = Vector3.zero; j.BestDistance = float.MaxValue;
        }

        private static void AssignAIHarvester(AITransportJob j, Unit h)
        {
            ReleaseAIHarvester(j);
            j.Harvester = h; j.AssignedArea = ProductiveAIArea(h, j.Team);
            j.LastProgress = Time.time; j.NextAllocation = Time.time + Cfg.AITransport.ReassignmentCheckSeconds;
            AILog(j, "collection reserved patch=" + HarvestObjectId(j.AssignedArea) +
                " tierLimit=" + AIHarvesterLimit(h, j.Transport.IsFlyingType));
        }

        private static Unit ChooseAIHarvester(AITransportJob j)
        {
            var candidates = new List<KeyValuePair<Unit, float>>();
            foreach (var h in AllocationHarvesters)
            {
                if (!CanAssignAIHarvester(j, h)) continue;
                float refineryDistance = AIHarvesterRefineryDistance(h);
                if (refineryDistance == float.MaxValue) continue;
                float travel = Vector3.Distance(j.Transport.transform.position, h.transform.position);
                float available = Math.Min(1f, h.GetStoredResources(j.Team.UsableResource) / (float)CapacityFor(j.Transport));
                // Ground routes pay a larger distance cost; distant harvesters benefit
                // more from hauling, but never enough to ignore pickup travel entirely.
                float score = (50f + travel * (j.Transport.IsFlyingType ? 1f : 1.5f)) *
                    (1f + AIHarvesterUse(j, h)) / ((1f + Math.Min(1200f, refineryDistance) / 600f) * (0.5f + available));
                candidates.Add(new KeyValuePair<Unit, float>(h, score));
            }
            candidates.Sort((a, b) => { int c = a.Value.CompareTo(b.Value); return c != 0 ? c : a.Key.GetInstanceID().CompareTo(b.Key.GetInstanceID()); });
            foreach (var candidate in candidates)
                if (AIServicePosition(j, candidate.Key.transform.position, AILoadRadius(j.Transport), out var point)) return candidate.Key;
            return null;
        }

        private static void BeginAIDelivery(AITransportJob j, string reason)
        {
            ReleaseAIHarvester(j); j.Delivering = true; j.Refinery = null; j.NextRefinerySearch = 0;
            AILog(j, reason);
        }

        private static void AllocateAITransport(AITransportJob j, Cargo cargo)
        {
            if (j.Harvester)
            {
                float distance = Vector3.Distance(j.Transport.transform.position, j.Harvester.transform.position);
                if (distance + 10f < j.BestDistance) { j.BestDistance = distance; j.LastProgress = Time.time; }
                var productiveArea = ProductiveAIArea(j.Harvester, j.Team);
                bool invalid = !productiveArea || j.AssignedArea != productiveArea;
                bool stalled = Time.time - j.LastProgress >= Cfg.AITransport.NoProgressTimeoutSeconds;
                bool due = Time.time >= j.NextAllocation;
                if (!invalid && !stalled && (!due || CanAssignAIHarvester(j, j.Harvester)))
                { if (due) j.NextAllocation = Time.time + Cfg.AITransport.ReassignmentCheckSeconds; return; }
                if (stalled) { j.AvoidHarvester = j.Harvester; j.AvoidUntil = Time.time + Cfg.AITransport.NoProgressTimeoutSeconds; }
                AILog(j, invalid ? "collection patch lost/depleted; reallocating" : "collection limit or progress check; reallocating");
                ReleaseAIHarvester(j); j.NextAllocation = 0;
                SetTransfer(j.Transport, cargo, null);
            }
            if (Time.time < j.NextAllocation) return;
            j.NextAllocation = Time.time + Cfg.AITransport.ReassignmentCheckSeconds;
            Unit next = ChooseAIHarvester(j);
            if (cargo.Amount > 0)
            {
                var refinery = ClosestAIRefinery(j);
                if (!next || (refinery && Vector3.Distance(j.Transport.transform.position, refinery.transform.position) <=
                    Vector3.Distance(j.Transport.transform.position, next.transform.position)))
                { BeginAIDelivery(j, "delivering partial cargo before next collection"); return; }
            }
            if (next) AssignAIHarvester(j, next);
        }

        private static void ParkAITransport(AITransportJob j)
        {
            // Pick a stable holding point outside transfer/bay traffic; no order spam.
            if (!j.Parking && Time.time >= j.NextMove)
            {
                j.NextMove = Time.time + Cfg.AITransport.ReassignmentCheckSeconds;
                Structure nearest = null; float distance = float.MaxValue;
                foreach (var s in AllocationRefineries)
                    if (AIRefineryTarget(s, j.Team))
                    { float d = (s.transform.position - j.Transport.transform.position).sqrMagnitude; if (d < distance) { distance = d; nearest = s; } }
                if (nearest)
                {
                    float radius = AIUnloadRadius(j.Transport) + 150f + j.Transport.PhysicalRadius;
                    float initial = (Math.Abs((long)j.Transport.GetInstanceID()) % 32) * Mathf.PI / 16f;
                    for (int n = 0; n < 16; n++)
                    {
                        float angle = initial + n * Mathf.PI / 8f;
                        Vector3 point = GamePhysics.GetTerrainPosition(nearest.transform.position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius);
                        if (!j.Transport.GetPointValidForAI(point, out var nav) ||
                            NearestAIRefineryDistance(nav, j.Team) < AIUnloadRadius(j.Transport) + 75f) continue;
                        j.ParkingPoint = nav; j.Parking = true; j.Holding = false; j.NextMove = Time.time + 5f;
                        if (AIOrder(j, OrderDefinitionRegistry.Move, nav)) j.Destination = nav;
                        break;
                    }
                }
            }
            if (j.Parking && Vector3.Distance(j.Transport.transform.position, j.ParkingPoint) > 25f)
            {
                if (Time.time >= j.NextMove && !j.Transport.AIAgent.IsFollowingPath)
                { j.NextMove = Time.time + 5f; if (AIOrder(j, OrderDefinitionRegistry.Move, j.ParkingPoint)) j.Holding = false; }
            }
            else AIHold(j);
            AILog(j, "logistics duty idle; waiting for productive collection slot");
        }
    }
}
