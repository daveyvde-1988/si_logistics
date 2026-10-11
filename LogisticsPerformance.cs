using System;
using System.Collections.Generic;
using System.Diagnostics;
using MelonLoader;
using UnityEngine;

namespace Si_Logistics
{
    public sealed partial class LogisticsMod
    {
        // Only valid during the synchronous one-second transfer tick. Eligibility,
        // resource amounts and reservation usage remain live; nothing persists a frame.
        private static bool PerformanceTickActive;
        private static readonly List<Unit> TickHarvesters = new List<Unit>();
        private static readonly List<Unit> TickTransports = new List<Unit>();
        private static readonly List<Structure> TickRefineries = new List<Structure>();
        private static readonly Dictionary<int, float> TickRefineryDistances = new Dictionary<int, float>();
        private static readonly HashSet<int> TickActiveVehicles = new HashSet<int>();
        private static readonly List<int> TickStaleCargo = new List<int>();
        private static readonly List<KeyValuePair<int, AITransportJob>> TickJobSnapshot = new List<KeyValuePair<int, AITransportJob>>();
        private static readonly Dictionary<int, float> TransportErrorUntil = new Dictionary<int, float>();
        private static readonly List<int> TickStaleErrors = new List<int>();
        private static readonly float[] ServiceRings = { 0.94f, 0.80f, 0.65f, 0.50f, 0.30f };
        private static long PerfTicks, PerfSearches, PerfNavProbes, PerfServiceHits;
        private static double PerfTotalMs, PerfMaxMs;
        private static float PerfNextReport;
        private static IEnumerable<Unit> AllocationHarvesters => PerformanceTickActive ? TickHarvesters : Unit.Units;
        private static IEnumerable<Structure> AllocationRefineries => PerformanceTickActive ? TickRefineries : Structure.Structures;

        private static void Tick()
        {
            if (Unit.Units == null || Structure.Structures == null) return;
            bool measure = Cfg.Debug;
            long started = measure ? Stopwatch.GetTimestamp() : 0;
            TickHarvesters.Clear(); TickTransports.Clear(); TickRefineries.Clear(); TickRefineryDistances.Clear();
            try
            {
                foreach (var u in Unit.Units)
                {
                    if (!u) continue;
                    if (IsTransport(u)) TickTransports.Add(u);
                    if (u.ObjectInfo && u.ObjectInfo.UnitType == UnitType.Harvester) TickHarvesters.Add(u);
                }
                foreach (var s in Structure.Structures)
                    if (s && !s.IsDestroyed && s.ResourceDepositPoints != null && s.ResourceDepositPoints.Count > 0) TickRefineries.Add(s);
                PerformanceTickActive = true;
                TickCore();
            }
            finally
            {
                PerformanceTickActive = false;
                TickHarvesters.Clear(); TickTransports.Clear(); TickRefineries.Clear(); TickRefineryDistances.Clear();
                TickJobSnapshot.Clear(); TickActiveVehicles.Clear(); TickStaleCargo.Clear(); TickStaleErrors.Clear();
                if (measure)
                {
                    double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                    PerfTicks++; PerfTotalMs += ms; PerfMaxMs = Math.Max(PerfMaxMs, ms);
                    if (Time.unscaledTime >= PerfNextReport)
                    {
                        DebugMsg("[LogisticsPerf] transfer ticks=" + PerfTicks + " avgMs=" + (PerfTotalMs / PerfTicks).ToString("F3") +
                            " maxMs=" + PerfMaxMs.ToString("F3") + " serviceSearches=" + PerfSearches + " navProbes=" + PerfNavProbes +
                            " sameFrameServiceHits=" + PerfServiceHits);
                        PerfNextReport = Time.unscaledTime + 30f;
                        PerfTicks = PerfSearches = PerfNavProbes = PerfServiceHits = 0; PerfTotalMs = PerfMaxMs = 0;
                    }
                }
                else { PerfTicks = PerfSearches = PerfNavProbes = PerfServiceHits = 0; PerfTotalMs = PerfMaxMs = 0; PerfNextReport = Time.unscaledTime + 30f; }
            }
        }

        private static float AIHarvesterRefineryDistance(Unit h)
        {
            if (!PerformanceTickActive) return NearestAIRefineryDistance(h.transform.position, h.Team);
            int id = h.GetInstanceID();
            if (!TickRefineryDistances.TryGetValue(id, out float distance))
            { distance = NearestAIRefineryDistance(h.transform.position, h.Team); TickRefineryDistances[id] = distance; }
            return distance;
        }
    }
}
