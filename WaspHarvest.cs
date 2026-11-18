using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using SilicaAdminMod;
using UnityEngine;

namespace Si_Logistics
{
    // "WaspHarvest" section of Si_Logistics_Config.json. Missing keys keep these defaults.
    public sealed class WaspHarvestConfig
    {
        public bool Enabled = true;
        // ObjectInfo.DisplayName of the creatures that receive harvesting claws (case-insensitive).
        public List<string> Units = new List<string> { "Wasp" };
        public int MaxCarry = 500;                  // Biotics per load; also the most one Bio Cache visit unloads
        public int ExtractPerHit = 20;              // Biotics taken from the patch per primary-attack hit
        public float ExtractionRadius = 3f;         // metres around the hit point searched for patch cells
        public float TickSeconds = 1f;              // seconds between native deposit ticks
        public int DepositAmount = 50;              // Biotics moved to the team per deposit tick
        public float DepositCooldownSeconds = 30f;  // one load per cooldown, counted from the first deposit tick; 0 = off
        public float RewardShare = 0.4f;            // pilot's personal-credit share of a delivery, 0..1
        public float GroundMaxHeight = 4f;          // a hit extracts only this close to terrain or static ground
        public float NotifyIntervalSeconds = 2f;    // minimum seconds between two cargo-bar chat lines per Wasp
    }

    // Wasp harvest: a player-flown Wasp harvests Biotics the way the Shrimp does.
    //
    // The Shrimp's harvesting is two components on its prefab: a ResourceHolder (cargo) and MiningClaws,
    // which hook the creature's primary melee attack, call the server-only ResourceArea.ExtractResource
    // at the hit point and deposit at a team ResourceDepositPoint (the Bio Cache) while the creature
    // holds still inside it. The same two components are added to every spawned Wasp instance on the
    // server (WaspHarvestInjector.cs) and gated with Harmony (WaspHarvestPatches.cs): extraction needs
    // a controlling player and a hit point near the ground, and a Bio Cache accepts one load per cooldown.
    //
    // Network safety: NetworkComponent.Awake registers an object's networked scripts once. Components
    // added to an instance afterwards are never registered, so no extra network data is produced and
    // clients, modded or not, see a vanilla Wasp. The patch is debited by the native extraction call
    // and the team is credited by the native deposit tick; this file adds no resource of its own.
    public sealed partial class LogisticsMod
    {
        private sealed class WaspState
        {
            internal Unit Unit;
            internal MiningClaws Claws;
            internal ResourceHolder Holder;
            internal int ClawsId, HolderId, UnitId;   // cached: removal never touches a destroyed object
            internal float LastCargoNotice = float.NegativeInfinity;
            internal float LastHeightNotice = float.NegativeInfinity;
            internal float LastUnloadNotice = float.NegativeInfinity;
            internal float LastCooldownNotice = float.NegativeInfinity;
            internal bool FullNoticeSent;             // "cargo full" is told once per full load
            // One Bio Cache visit = one batch: reported and rewarded once, when the deposit ticks stop.
            internal int Pending;
            internal float LastDeposit;
            internal float VisitStart = float.NegativeInfinity;
            internal Player PendingPlayer;
            internal long PendingSteamId;
            internal int PendingEpoch;
            internal bool VisitOpen => Pending > 0;
            internal bool Alive => Claws && Holder && Unit;
        }

        private sealed class WaspTally
        {
            internal string Name = string.Empty;
            internal long Harvested, Delivered;
            internal decimal Reward;
        }

        private static readonly Dictionary<int, WaspState> WaspByClaws = new Dictionary<int, WaspState>();
        private static readonly Dictionary<int, WaspState> WaspByHolder = new Dictionary<int, WaspState>();
        private static readonly Dictionary<int, WaspState> WaspByUnit = new Dictionary<int, WaspState>();
        private static readonly List<WaspState> WaspOpenVisits = new List<WaspState>();
        private static readonly List<WaspState> WaspScratch = new List<WaspState>();
        private static readonly Dictionary<long, WaspTally> WaspTallies = new Dictionary<long, WaspTally>();   // this round
        private static readonly Dictionary<string, float> WaspWarned = new Dictionary<string, float>();
        private static bool WaspHarvestInstalled, WaspStandaloneLoaded;
        private static float WaspNextSweep;

        private static bool WaspHarvestActive => WaspHarvestInstalled && Cfg.Enabled && Cfg.WaspHarvest.Enabled;

        private static void ValidateWaspHarvestConfig()
        {
            if (Cfg.WaspHarvest == null) Cfg.WaspHarvest = new WaspHarvestConfig();
            var c = Cfg.WaspHarvest;
            if (c.Units == null) c.Units = new List<string> { "Wasp" };
            c.Units.RemoveAll(string.IsNullOrWhiteSpace);
            c.MaxCarry = Math.Max(1, Math.Min(100000, c.MaxCarry));
            c.ExtractPerHit = Math.Max(1, Math.Min(10000, c.ExtractPerHit));
            c.DepositAmount = Math.Max(1, Math.Min(10000, c.DepositAmount));
            c.ExtractionRadius = ValidHarvestSetting(c.ExtractionRadius, 3f, 0.5f, 50f);
            c.TickSeconds = ValidHarvestSetting(c.TickSeconds, 1f, 0.1f, 60f);
            c.DepositCooldownSeconds = ValidHarvestSetting(c.DepositCooldownSeconds, 30f, 0f, 3600f);
            c.RewardShare = ValidRewardShare(c.RewardShare, 0.4f);
            c.GroundMaxHeight = ValidHarvestSetting(c.GroundMaxHeight, 4f, 0f, 100f);
            c.NotifyIntervalSeconds = ValidHarvestSetting(c.NotifyIntervalSeconds, 2f, 0f, 60f);
        }

        // A failure here leaves only Wasp harvest inactive; the rest of Logistics is unaffected.
        private void InstallWaspHarvest()
        {
            try
            {
                // The standalone Si_WaspHarvest mod adds the same components and owns /wasp: never run both.
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (string.Equals(assembly.GetName().Name, "Si_WaspHarvest", StringComparison.OrdinalIgnoreCase)) WaspStandaloneLoaded = true;
                if (WaspStandaloneLoaded)
                {
                    DebugWarning("Wasp harvest: standalone Si_WaspHarvest.dll is loaded; the built-in Wasp harvest stays off. Remove the standalone DLL to use it.");
                    return;
                }
                var spawned = AccessTools.DeclaredMethod(typeof(Unit), "OnPostInit");
                var extract = AccessTools.DeclaredMethod(typeof(MiningClaws), "OnExtractAttackHit");
                var retrieve = AccessTools.DeclaredMethod(typeof(ResourceHolder), "RetrieveResource", new[] { typeof(int) });
                if (spawned == null || extract == null || retrieve == null)
                    throw new MissingMethodException("Unit.OnPostInit, MiningClaws.OnExtractAttackHit or ResourceHolder.RetrieveResource(int) not found");
                HarmonyInstance.Patch(spawned, postfix: new HarmonyMethod(typeof(LogisticsMod), nameof(WaspSpawnPostfix)));
                HarmonyInstance.Patch(extract,
                    prefix: new HarmonyMethod(typeof(LogisticsMod), nameof(WaspExtractPrefix)),
                    postfix: new HarmonyMethod(typeof(LogisticsMod), nameof(WaspExtractPostfix)));
                HarmonyInstance.Patch(retrieve,
                    prefix: new HarmonyMethod(typeof(LogisticsMod), nameof(WaspRetrievePrefix)),
                    postfix: new HarmonyMethod(typeof(LogisticsMod), nameof(WaspRetrievePostfix)));
                HelperMethods.RegisterPlayerCommand("wasp", OnWaspCommand, Cfg.Commands.HideFromChat);
                WaspHarvestInstalled = true;
            }
            catch (Exception error) { DebugError("Wasp harvest: install failed, feature inactive: " + error.Message); }
        }

        private static void BindWaspHarvestEvents()
        {
            UnbindWaspHarvestEvents();
            GameEvents.OnGameStarted += WaspMatchStarted;
            GameEvents.OnGameEnded += WaspMatchEnded;
            GameEvents.OnUnitDestroyed += WaspUnitDestroyed;
            GameEvents.OnUnitDeleted += WaspUnitDeleted;
        }

        private static void UnbindWaspHarvestEvents()
        {
            GameEvents.OnGameStarted -= WaspMatchStarted;
            GameEvents.OnGameEnded -= WaspMatchEnded;
            GameEvents.OnUnitDestroyed -= WaspUnitDestroyed;
            GameEvents.OnUnitDeleted -= WaspUnitDeleted;
        }

        private static void WaspMatchStarted(GameMode mode)
        {
            try
            {
                // Wasps that spawn with the round may already carry claws: keep them, drop only dead entries.
                PruneWasps();
                foreach (var state in WaspByClaws.Values) state.VisitStart = float.NegativeInfinity;
                WaspTallies.Clear();
            }
            catch (Exception error) { WaspWarn("start", "Wasp harvest match start: " + error.Message); }
        }

        private static void WaspMatchEnded(GameMode mode, Team winner) => ResetWaspHarvest();

        // Closes open visits and removes the added components from surviving units, so no ungated
        // MiningClaws stay behind. Logistics discards unpaid rewards when a match ends, so a visit
        // closed here is reported but queues no reward.
        private static void ResetWaspHarvest()
        {
            try
            {
                WaspScratch.Clear(); WaspScratch.AddRange(WaspOpenVisits); WaspOpenVisits.Clear();
                foreach (var state in WaspScratch) FlushWaspVisit(state, true);
                WaspScratch.Clear();
                StripWaspClaws();
            }
            catch (Exception error) { WaspWarn("reset", "Wasp harvest reset: " + error.Message); }
            WaspByClaws.Clear(); WaspByHolder.Clear(); WaspByUnit.Clear(); WaspOpenVisits.Clear();
        }

        private static void WaspUnitDestroyed(Unit unit, GameObject instigator) => WaspUnitGone(unit);
        private static void WaspUnitDeleted(Unit unit) => WaspUnitGone(unit);

        // Every unit death on the map lands here: one count check, then one dictionary lookup.
        private static void WaspUnitGone(Unit unit)
        {
            if (WaspByUnit.Count == 0 || unit == null) return;
            try { if (WaspByUnit.TryGetValue(unit.GetInstanceID(), out var state)) RemoveWasp(state); }
            catch (Exception error) { WaspWarn("gone", "Wasp harvest unit cleanup: " + error.Message); }
        }

        private static void RegisterWasp(WaspState state)
        {
            WaspByClaws[state.ClawsId] = state;
            WaspByHolder[state.HolderId] = state;
            WaspByUnit[state.UnitId] = state;
        }

        // Forget a unit; a deposit visit still open is closed first.
        private static void RemoveWasp(WaspState state)
        {
            WaspByClaws.Remove(state.ClawsId);
            WaspByHolder.Remove(state.HolderId);
            WaspByUnit.Remove(state.UnitId);
            if (state.Pending > 0) { WaspOpenVisits.Remove(state); FlushWaspVisit(state); }
        }

        private static void PruneWasps()
        {
            if (WaspByClaws.Count == 0) return;
            WaspScratch.Clear();
            foreach (var state in WaspByClaws.Values)
            {
                bool gone = !state.Alive;
                if (!gone) { try { gone = state.Unit.IsDestroyed; } catch { gone = true; } }
                if (gone) WaspScratch.Add(state);
            }
            foreach (var state in WaspScratch) RemoveWasp(state);
            WaspScratch.Clear();
        }

        // Called every server frame: one comparison unless a visit is open or the 10 s sweep is due.
        private static void UpdateWaspHarvest()
        {
            float now = Time.time;
            if (WaspOpenVisits.Count == 0 && now < WaspNextSweep) return;
            try
            {
                if (WaspOpenVisits.Count > 0)
                {
                    // Three silent ticks close a visit, which absorbs jitter of the native deposit tick.
                    float quiet = Math.Max(3f * Cfg.WaspHarvest.TickSeconds, 2.5f);
                    for (int i = WaspOpenVisits.Count - 1; i >= 0; i--)
                    {
                        var state = WaspOpenVisits[i];
                        bool empty = !state.Alive || state.Holder.AmountStored <= 0;
                        bool loadUsed = state.Alive && Cfg.WaspHarvest.DepositCooldownSeconds > 0 && state.Pending >= state.Holder.MaxAmount;
                        if (!empty && !loadUsed && now - state.LastDeposit <= quiet) continue;
                        WaspOpenVisits.RemoveAt(i);
                        FlushWaspVisit(state);
                    }
                }
                if (now >= WaspNextSweep)
                {
                    WaspNextSweep = now + 10f;
                    PruneWasps();   // fallback for units that vanished without a destroy/delete event
                }
            }
            catch (Exception error) { WaspWarn("update", "Wasp harvest update: " + error.Message); }
        }

        // ---- called from WaspHarvestPatches.cs ----

        private static void WaspExtracted(WaspState state, Player pilot, int gained)
        {
            var holder = state.Holder;
            WaspTallyFor(pilot).Harvested += gained;
            bool full = holder.RemainingSpace <= 0;
            if (full) state.FullNoticeSent = false;   // the next hit with a full cargo is told once
            float now = Time.time;
            if (!full && now - state.LastCargoNotice < Cfg.WaspHarvest.NotifyIntervalSeconds) return;
            state.LastCargoNotice = now;
            WaspSay(pilot, "Biotics " + Bar(WaspPercent(holder.AmountStored, holder.MaxAmount)) + " " + holder.AmountStored + "/" + holder.MaxAmount +
                " (+" + gained + ")" + (full ? " Cargo full: return to a Bio Cache." : ""));
        }

        private static void WaspCargoFullHit(WaspState state, Player pilot)
        {
            if (state.FullNoticeSent) return;
            state.FullNoticeSent = true;
            WaspSay(pilot, "Biotics cargo full (" + state.Holder.AmountStored + "/" + state.Holder.MaxAmount + "): hover still inside a Bio Cache to unload.");
        }

        private static void WaspTooHigh(WaspState state, Player pilot)
        {
            float now = Time.time;
            if (now - state.LastHeightNotice < Math.Max(5f, Cfg.WaspHarvest.NotifyIntervalSeconds)) return;
            state.LastHeightNotice = now;
            WaspSay(pilot, "Too high to harvest: stay within " + Cfg.WaspHarvest.GroundMaxHeight.ToString("0.#", CultureInfo.InvariantCulture) + " m of the ground.");
        }

        // Seconds until the next deposit visit may start (0 while a visit is open or the cooldown is off).
        private static float WaspCooldownLeft(WaspState state, float now)
        {
            float cooldown = Cfg.WaspHarvest.DepositCooldownSeconds;
            if (cooldown <= 0 || state.VisitOpen) return 0f;
            return Math.Max(0f, cooldown - (now - state.VisitStart));
        }

        // Gate for ResourceHolder.RetrieveResource on an added holder; its only caller is the native
        // deposit tick. Returns the amount that may leave the cargo now; 0 = blocked.
        private static int WaspAllowedDeposit(WaspState state, int amount)
        {
            if (amount <= 0 || Cfg.WaspHarvest.DepositCooldownSeconds <= 0) return amount;
            if (state.VisitOpen) return Math.Max(0, Math.Min(amount, state.Holder.MaxAmount - state.Pending));   // one load per visit
            float now = Time.time;
            float left = WaspCooldownLeft(state, now);
            if (left <= 0) return amount;   // a new visit starts with this tick
            if (now - state.LastCooldownNotice >= 10f)
            {
                state.LastCooldownNotice = now;
                Player pilot = state.Unit ? state.Unit.ControlledBy : null;
                if (pilot != null)
                    WaspSay(pilot, "Bio Cache cooldown: " + Math.Ceiling(left).ToString("0", CultureInfo.InvariantCulture) + " s left (one load per " +
                        Cfg.WaspHarvest.DepositCooldownSeconds.ToString("0", CultureInfo.InvariantCulture) + " s).");
            }
            return 0;
        }

        private static void WaspDeposited(WaspState state, int amount)
        {
            if (amount <= 0) return;
            float now = Time.time;
            if (state.Pending == 0)
            {
                // A new visit: the cooldown runs from here and the reward belongs to this match.
                WaspOpenVisits.Add(state);
                state.VisitStart = now;
                state.PendingEpoch = RewardEpoch;
            }
            state.Pending += amount;
            state.LastDeposit = now;
            var holder = state.Holder;
            if (holder.RemainingSpace > 0) state.FullNoticeSent = false;
            Player pilot = state.Unit ? state.Unit.ControlledBy : null;
            if (pilot == null) return;
            state.PendingPlayer = pilot;
            state.PendingSteamId = SteamId(pilot);
            // The last tick is covered by the final delivery line.
            if (holder.AmountStored <= 0 || now - state.LastUnloadNotice < Cfg.WaspHarvest.NotifyIntervalSeconds) return;
            state.LastUnloadNotice = now;
            WaspSay(pilot, "Unloading " + Bar(WaspPercent(holder.AmountStored, holder.MaxAmount)) + " " + holder.AmountStored + " Biotics left.");
        }

        // The team already received the Biotics through the native deposit tick. The pilot's share joins
        // the batched Logistics payout, so wallet handling, payout delay and announcements stay in one place.
        private static void FlushWaspVisit(WaspState state, bool matchOver = false)
        {
            int delivered = state.Pending;
            Player pilot = state.PendingPlayer;
            long id = state.PendingSteamId;
            int epoch = state.PendingEpoch;
            state.Pending = 0; state.PendingPlayer = null; state.PendingSteamId = 0;   // the next visit pays whoever pilots then
            if (delivered <= 0 || id == 0) return;   // unloaded with nobody at the controls: the team got it, nobody is paid
            bool connected = Connected(pilot, id);
            decimal share = (decimal)Cfg.WaspHarvest.RewardShare;
            bool rewarded = !matchOver && connected && share > 0 && epoch == RewardEpoch && MatchAcceptsRewards && Cfg.Enabled;
            if (rewarded) QueueReward(pilot, delivered, share, epoch);
            if (!WaspTallies.TryGetValue(id, out var tally)) WaspTallies[id] = tally = new WaspTally();
            if (connected) tally.Name = pilot.PlayerName ?? tally.Name;
            tally.Delivered += delivered;
            if (rewarded) tally.Reward += delivered * share;
            if (Cfg.Debug) DebugMsg("Wasp harvest: player=" + id + " delivered " + delivered + " Biotics" +
                (rewarded ? ", queued " + (delivered * share).ToString("0.##", CultureInfo.InvariantCulture) + " credits" : ""));
            if (!connected) return;
            string text = "Delivered " + delivered + " Biotics to the team.";
            if (rewarded)
                text += " +" + (delivered * share).ToString("0.#", CultureInfo.InvariantCulture) + " credits" +
                    (KohCredits.Available ? " with your next logistics payout." : " pending: no credit wallet found.");
            float left = state.Alive ? WaspCooldownLeft(state, Time.time) : 0f;
            if (left > 0) text += " Next unload in " + Math.Ceiling(left).ToString("0", CultureInfo.InvariantCulture) + " s.";
            WaspSay(pilot, text);
        }

        // ---- helpers ----

        private static int WaspPercent(int amount, int maximum)
        {
            if (maximum <= 0 || amount <= 0) return 0;
            // A full bar only when really full, and never an empty one while something is aboard.
            return amount >= maximum ? 100 : Math.Max(1, Math.Min(99, (int)(100L * amount / maximum)));
        }

        private static WaspTally WaspTallyFor(Player player)
        {
            long id = SteamId(player);
            if (!WaspTallies.TryGetValue(id, out var tally)) WaspTallies[id] = tally = new WaspTally();
            tally.Name = player.PlayerName ?? tally.Name;
            return tally;
        }

        // Chat from inside a game callback: must never throw into the game's update loop.
        private static void WaspSay(Player player, string text)
        {
            try { HelperMethods.SendChatMessageToPlayer(player, HelperMethods.chatPrefix, " " + text); }
            catch (Exception error) { WaspWarn("chat", "Wasp harvest chat: " + error.Message); }
        }

        // With debug on, at most one warning per minute and key: a broken patch cannot flood the log.
        private static void WaspWarn(string key, string text)
        {
            if (!Cfg.Debug) return;
            float now = Time.realtimeSinceStartup;
            if (WaspWarned.TryGetValue(key, out float last) && now - last < 60f) return;
            WaspWarned[key] = now;
            DebugWarning(text);
        }
    }
}
