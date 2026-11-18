using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Emit;
using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json;
using SilicaAdminMod;
using UnityEngine;

namespace Si_Logistics
{
    public sealed partial class LogisticsMod
    {
        private static float NextRewardCheck = float.PositiveInfinity;
        private static float RewardWindowSeconds => Cfg.DeliveryCooldownSeconds;
        private sealed class RewardAccount
        {
            internal Player Player;
            internal float Due;
            [JsonProperty] internal decimal Pending;
            [JsonProperty] internal decimal WaspPending;
            [JsonProperty] internal long Unconfirmed;
            [JsonProperty] internal int InFlight;
            [JsonProperty] internal int BalanceBefore;
        }
        private static readonly Dictionary<long, RewardAccount> Rewards = new Dictionary<long, RewardAccount>();
        private static string RewardLedgerPath => Path.Combine(Path.GetDirectoryName(ConfigPath), "PendingCredits.json");
        private static bool RewardLedgerDirty, RewardLedgerReadFailed;
        private static float NextRewardLedgerWrite;
        private static float NextRewardLedgerWarning;
        private static bool MatchAcceptsRewards = true;
        private static int RewardEpoch;
        private static Player DriverOf(Unit unit) => unit == null ? null :
            (unit.ControlledBy != null ? unit.ControlledBy : unit.Driver != null ? unit.Driver.ControlledBy : null);
        private static long SteamId(Player player) => player == null ? 0 : (long)player.PlayerID.m_ID;
        private static bool Connected(Player player, long id) => player != null && id != 0 &&
            Player.Players != null && Player.Players.Contains(player) && SteamId(player) == id;
        private static decimal RewardRate => (decimal)Cfg.PersonalBonus.TransportShare;
        private static decimal HarvesterDepositRate => (decimal)Cfg.PersonalBonus.HarvesterShare;
        private static decimal HarvesterTransferRate => (decimal)Cfg.PersonalBonus.TransferShare;

        private static void QueueReward(Player driver, int amount, decimal rate, int epoch)
        {
            if (epoch != RewardEpoch || !Game.GetIsServer() || !MatchAcceptsRewards || !Cfg.Enabled || amount <= 0 || rate <= 0) return;
            long id = SteamId(driver);
            if (Connected(driver, id)) QueueEarnedReward(id, driver, amount * rate, false);
        }

        private static void QueueEarnedReward(long id, Player driver, decimal credits, bool wasp)
        {
            if (!Rewards.TryGetValue(id, out var account)) Rewards[id] = account = new RewardAccount();
            if (Connected(driver, id)) account.Player = driver;
            // Due does not slide forward as more rewards arrive.
            if (account.Due == 0) account.Due = Math.Max(float.Epsilon, Time.unscaledTime + RewardWindowSeconds);
            if (wasp) account.WaspPending += credits;
            else account.Pending += credits;
            RewardLedgerDirty = true;
            NextRewardCheck = Math.Min(NextRewardCheck, account.Due);
        }

        private static void LoadRewardLedger()
        {
            string path = RewardLedgerPath;
            if (!File.Exists(path)) return;
            try
            {
                var saved = JsonConvert.DeserializeObject<Dictionary<long, RewardAccount>>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("Expected a map of Steam IDs and pending credits.");
                foreach (var pair in saved)
                {
                    var account = pair.Value;
                    if (pair.Key == 0 || account == null || account.Pending < 0 || account.WaspPending < 0 ||
                        account.InFlight < 0 || account.Unconfirmed < 0)
                        throw new InvalidDataException("Invalid pending credit entry.");
                }
                foreach (var pair in saved)
                {
                    var account = pair.Value;
                    Rewards.Add(pair.Key, account);
                    if (account.InFlight > 0)
                    {
                        account.Unconfirmed += account.InFlight;
                        MelonLogger.Warning($"Logistics: reconcile interrupted Si_Buy grant of {account.InFlight} credits for {pair.Key} " +
                            $"(balance before grant {account.BalanceBefore}).");
                        account.InFlight = 0;
                        RewardLedgerDirty = true;
                    }
                    if (account.Pending + account.WaspPending > 0) account.Due = NextRewardCheck = float.Epsilon;
                }
            }
            catch (Exception error)
            {
                RewardLedgerReadFailed = true;
                MelonLogger.Error("Logistics could not read PendingCredits.json. Payouts are held to protect the existing ledger: " + error.Message);
            }
        }

        private static bool SaveRewardLedger()
        {
            if (!RewardLedgerDirty) return !RewardLedgerReadFailed;
            if (RewardLedgerReadFailed) return false;
            float now = Time.unscaledTime;
            NextRewardLedgerWrite = now + 5f;
            try
            {
                var snapshot = new Dictionary<long, RewardAccount>();
                foreach (var pair in Rewards)
                {
                    var account = pair.Value;
                    if (account.Pending > 0 || account.WaspPending > 0 || account.InFlight > 0 || account.Unconfirmed > 0)
                        snapshot.Add(pair.Key, account);
                }
                string path = RewardLedgerPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, 1024, true))
                        writer.Write(JsonConvert.SerializeObject(snapshot, Formatting.Indented));
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                RewardLedgerDirty = false;
                return true;
            }
            catch (Exception error)
            {
                if (now >= NextRewardLedgerWarning)
                {
                    NextRewardLedgerWarning = now + 60f;
                    MelonLogger.Warning("Logistics could not save pending credits: " + error.Message);
                }
                return false;
            }
        }

        private static void ResumeWaspRewards()
        {
            foreach (var account in Rewards.Values)
            {
                if (account.WaspPending <= 0 || account.Due != 0) continue;
                account.Due = Math.Max(float.Epsilon, Time.unscaledTime);
                NextRewardCheck = Math.Min(NextRewardCheck, account.Due);
            }
        }

        private static void PayDueRewards()
        {
            float now = Time.unscaledTime;
            if (!Game.GetIsServer()) return;
            if (!MatchAcceptsRewards || !Cfg.Enabled || now < NextRewardCheck)
            {
                if (RewardLedgerDirty && now >= NextRewardLedgerWrite) SaveRewardLedger();
                return;
            }
            NextRewardCheck = now + 1f;
            foreach (var pair in new List<KeyValuePair<long, RewardAccount>>(Rewards))
            {
                long id = pair.Key;
                var account = pair.Value;
                if (account.Due == 0 || now < account.Due) continue;
                account.Due = 0;
                bool wasp = WaspHarvestActive;
                decimal whole = decimal.Floor(account.Pending + (wasp ? account.WaspPending : 0));
                if (whole < 1) continue; // Keep the fraction until this player earns another window.
                float retry = now + Math.Max(1f, RewardWindowSeconds);
                int balance = BuyCredits.TryGetBalance(id);
                if (balance < 0) { account.Due = retry; continue; }
                int payment = (int)Math.Min(whole, (long)int.MaxValue - balance);
                if (payment == 0) { account.Due = retry; continue; }
                // Consume before invoking the external API: a re-entrant callback cannot pay twice.
                decimal regular = Math.Min(account.Pending, payment);
                decimal waspPayment = payment - regular;
                account.Pending -= regular;
                account.WaspPending -= waspPayment;
                account.InFlight = payment;
                account.BalanceBefore = balance;
                RewardLedgerDirty = true;
                if (!SaveRewardLedger())
                {
                    account.Pending += regular;
                    account.WaspPending += waspPayment;
                    account.InFlight = 0;
                    account.Due = retry;
                    continue;
                }
                var result = BuyCredits.TryGrant(id, payment, balance);
                account.InFlight = 0;
                RewardLedgerDirty = true;
                if (result == BuyCredits.GrantResult.Unavailable)
                {
                    account.Pending += regular;
                    account.WaspPending += waspPayment;
                }
                else if (result == BuyCredits.GrantResult.Uncertain)
                {
                    account.Unconfirmed += payment;
                    MelonLogger.Warning($"Logistics: reconcile Si_Buy grant of {payment} credits for {id} (balance before grant {balance}).");
                }
                if (account.Pending + (wasp ? account.WaspPending : 0) >= 1 && account.Due == 0) account.Due = retry;
                if (result != BuyCredits.GrantResult.Granted || !Cfg.PersonalBonus.AnnounceToPlayer) continue;
                if (!Connected(account.Player, id) && Player.Players != null)
                    account.Player = Player.Players.Find(player => SteamId(player) == id);
                if (Connected(account.Player, id))
                    HelperMethods.SendChatMessageToPlayer(account.Player, HelperMethods.chatPrefix,
                        " +" + payment + " credits for logistics (balance " + (balance + payment) + ").");
            }
            NextRewardCheck = float.PositiveInfinity;
            foreach (var account in Rewards.Values)
                if (account.Due > 0) NextRewardCheck = Math.Min(NextRewardCheck, account.Due);
            if (RewardLedgerDirty) SaveRewardLedger();
        }

        private static void BindRewardEvents()
        {
            BindCommanderScoreEvents();
            BindWaspHarvestEvents();
            GameEvents.OnGameStarted -= LogisticsMatchStarted; GameEvents.OnGameStarted += LogisticsMatchStarted;
            GameEvents.OnGameEnded -= LogisticsMatchEnded; GameEvents.OnGameEnded += LogisticsMatchEnded;
            GameEvents.OnPlayerLeft -= LogisticsPlayerLeft; GameEvents.OnPlayerLeft += LogisticsPlayerLeft;
        }
        private static void LogisticsPlayerLeft(Player player)
        {
            CommanderPlayerLeft(player);
            foreach (var pair in new List<KeyValuePair<long, ScoreAccount>>(ScoreRewards))
                if (ReferenceEquals(pair.Value.Player, player)) ScoreRewards.Remove(pair.Key);
            if (Rewards.TryGetValue(SteamId(player), out var account) && ReferenceEquals(account.Player, player)) account.Player = null;
        }
        private static void LogisticsMatchStarted(GameMode mode)
        { ResetAITransports(false); ResetHarvesterFix(false); ResetCommanderScore(); RewardEpoch++; ScoreRewards.Clear(); TransportErrorUntil.Clear(); CargoByTransport.Clear(); MatchAcceptsRewards = true; }
        private static void LogisticsMatchEnded(GameMode mode, Team winner)
        { ResetAITransports(false); ResetHarvesterFix(false); ResetCommanderScore(); RewardEpoch++; MatchAcceptsRewards = false; ScoreRewards.Clear(); TransportErrorUntil.Clear(); CargoByTransport.Clear(); }
        public override void OnSceneWasInitialized(int buildIndex, string sceneName) => BindRewardEvents();
        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        { ResetWaspHarvest(); LogisticsMatchEnded(null, null); }
        public override void OnDeinitializeMelon()
        {
            UnbindCommanderScoreEvents();
            UnbindWaspHarvestEvents();
            ReleaseWaspInstallation();
            SaveRewardLedger();
            ResetAITransports(true);
            ResetHarvesterFix(true);
            ResetCommanderScore(); RewardEpoch++; MatchAcceptsRewards = false; Rewards.Clear(); ScoreRewards.Clear(); NextRewardCheck = float.PositiveInfinity; TransportErrorUntil.Clear(); CargoByTransport.Clear();
            GameEvents.OnGameStarted -= LogisticsMatchStarted;
            GameEvents.OnGameEnded -= LogisticsMatchEnded;
            GameEvents.OnPlayerLeft -= LogisticsPlayerLeft;
            HelperMethods.UnregisterAdminCommand("logisticsscore");
            HelperMethods.UnregisterAdminCommand("scoreunload");
            HelperMethods.UnregisterAdminCommand("scoretransfer");
            HelperMethods.UnregisterAdminCommand("logisticsscorecomander");
            HelperMethods.UnregisterAdminCommand("logisticsscorecommander");
            HarmonyInstance.UnpatchSelf();
        }

        // Scope each native harvester update. Only its exact retrieve/store calls are observed;
        // transport pickup calls and arbitrary team resource changes cannot trigger this reward.
        private sealed class DepositReceipt
        {
            internal DepositReceipt Parent;
            internal Unit Unit;
            internal ResourceHolder Holder;
            internal Player Driver;
            internal Team Team;
            internal decimal Rate;
            internal int ScoreRate;
            internal long Retrieved, Accepted;
            internal bool Consumed;
            internal int Epoch;
            internal WaspState Wasp;
        }
        [ThreadStatic] private static DepositReceipt CurrentDeposit;
        [ThreadStatic] private static Stack<DepositReceipt> DepositReceiptPool;

        private void InstallHarvesterRewards()
        {
            foreach (Type type in new[] { typeof(MiningArmsBase), typeof(MiningClaws) })
            {
                string prefix = type == typeof(MiningArmsBase) ? nameof(ArmsBegin) : nameof(ClawsBegin);
                HarmonyInstance.Patch(AccessTools.Method(type, "OrderedUpdate"),
                    prefix: new HarmonyMethod(typeof(LogisticsMod), prefix),
                    transpiler: new HarmonyMethod(typeof(LogisticsMod), nameof(ObserveDepositCalls)),
                    finalizer: new HarmonyMethod(typeof(LogisticsMod), nameof(DepositFinished)));
            }
        }
        private static void ArmsBegin(MiningArmsBase __instance, out DepositReceipt __state) =>
            __state = BeginDeposit(__instance.Vehicle, __instance.ResourceHolder);
        private static bool ClawsBegin(MiningClaws __instance, out DepositReceipt __state)
        {
            __state = null;
            if (WaspByClaws.Count != 0 && WaspByClaws.TryGetValue(__instance.GetInstanceID(), out var wasp))
            {
                if (!WaspHarvestActive || !MatchAcceptsRewards || !wasp.Alive || !WaspCanUnload(wasp) ||
                    (wasp.Holder.AmountStored == 0 && !wasp.Unit.GetLocallyControlled())) return false;
                __state = BeginDeposit(wasp.Unit, wasp.Holder, wasp);
                return true;
            }
            __state = BeginDeposit(__instance.Creature, __instance.ResourceHolder);
            return true;
        }
        private static DepositReceipt BeginDeposit(Unit unit, ResourceHolder holder, WaspState wasp = null)
        {
            if (DepositReceiptPool == null) DepositReceiptPool = new Stack<DepositReceipt>();
            var receipt = DepositReceiptPool.Count > 0 ? DepositReceiptPool.Pop() : new DepositReceipt();
            receipt.Parent = CurrentDeposit; receipt.Epoch = RewardEpoch;
            if (wasp != null)
            {
                receipt.Wasp = wasp; receipt.Unit = unit; receipt.Holder = holder; receipt.Team = unit.Team;
            }
            else if (Game.GetIsServer() && MatchAcceptsRewards && Cfg.Enabled &&
                unit != null && unit.ObjectInfo != null && unit.ObjectInfo.UnitType == UnitType.Harvester &&
                unit.Team != null && holder != null && holder.ResourceType == unit.Team.UsableResource)
            { receipt.Unit = unit; receipt.Holder = holder; receipt.Team = unit.Team; }
            CurrentDeposit = receipt;
            return receipt;
        }
        private static Exception DepositFinished(Exception __exception, DepositReceipt __state)
        {
            if (__state == null) return __exception;
            try
            {
                if (!__state.Consumed)
                {
                    __state.Consumed = true;
                    int delivered = (int)Math.Min(int.MaxValue, Math.Min(__state.Retrieved, __state.Accepted));
                    if (__state.Wasp != null)
                    {
                        if (__exception == null && __state.Holder && __state.Retrieved > delivered)
                            __state.Holder.StoreResource((int)(__state.Retrieved - delivered));
                        WaspDeposited(__state.Wasp, delivered, __state.Driver, __state.Epoch);
                    }
                    else
                    {
                        QueueReward(__state.Driver, delivered, __state.Rate, __state.Epoch);
                        AwardScore(__state.Driver, delivered, __state.ScoreRate, false, __state.Epoch);
                        CommanderDeposit(__state.Team, delivered, __state.Epoch);
                    }
                }
            }
            catch (Exception error) { DebugWarning("Logistics harvester reward: " + error.Message); }
            finally
            {
                CurrentDeposit = __state.Parent;
                __state.Parent = null; __state.Unit = null; __state.Holder = null;
                __state.Driver = null; __state.Team = null; __state.Wasp = null; __state.Rate = 0;
                __state.ScoreRate = 0;
                __state.Retrieved = __state.Accepted = 0; __state.Consumed = false; __state.Epoch = 0;
                // Bound retained storage and never retain Unity objects between updates.
                if (DepositReceiptPool != null && DepositReceiptPool.Count < 8) DepositReceiptPool.Push(__state);
            }
            return __exception;
        }
        private static int ObservedRetrieve(ResourceHolder holder, int requested)
        {
            var receipt = CurrentDeposit;
            if (receipt == null || receipt.Holder != holder) return holder.RetrieveResource(requested);
            bool wasp = receipt.Wasp != null;
            if (wasp)
            {
                requested = WaspAllowedDeposit(receipt.Wasp, requested);
                if (requested <= 0) return 0;
            }
            Player driver = wasp ? receipt.Unit.ControlledBy : DriverOf(receipt.Unit);
            int removed = holder.RetrieveResource(requested);
            if (removed > 0)
            {
                receipt.Driver = driver;
                if (!wasp)
                {
                    receipt.Rate = HarvesterDepositRate;
                    receipt.ScoreRate = Cfg.LogisticsScore ? Cfg.ScoreUnload : 0;
                }
                receipt.Retrieved += Math.Min(Math.Max(0, requested), removed);
            }
            return removed;
        }
        private static int ObservedStructureStore(BaseGameObject structure, Resource resource, int amount)
        {
            int remainder = structure.StoreResource(resource, amount);
            var receipt = CurrentDeposit;
            if (receipt != null && receipt.Team != null && structure.Team == receipt.Team && resource == receipt.Holder.ResourceType)
                receipt.Accepted += Math.Max(0L, (long)amount - Math.Max(0, remainder));
            return remainder;
        }
        private static int ObservedTeamStore(Team team, int amount)
        {
            int remainder = team.StoreResource(amount);
            var receipt = CurrentDeposit;
            if (receipt != null && receipt.Team == team)
                receipt.Accepted += Math.Max(0L, (long)amount - Math.Max(0, remainder));
            return remainder;
        }
        private static IEnumerable<CodeInstruction> ObserveDepositCalls(IEnumerable<CodeInstruction> instructions)
        {
            var retrieve = AccessTools.Method(typeof(ResourceHolder), "RetrieveResource", new[] { typeof(int) });
            var store = AccessTools.Method(typeof(BaseGameObject), "StoreResource", new[] { typeof(Resource), typeof(int) });
            var teamStore = AccessTools.Method(typeof(Team), "StoreResource", new[] { typeof(int) });
            int retrieves = 0, stores = 0, teamStores = 0;
            foreach (var instruction in instructions)
            {
                string replacement = null;
                if (instruction.Calls(retrieve)) { replacement = nameof(ObservedRetrieve); retrieves++; }
                else if (instruction.Calls(store)) { replacement = nameof(ObservedStructureStore); stores++; }
                else if (instruction.Calls(teamStore)) { replacement = nameof(ObservedTeamStore); teamStores++; }
                if (replacement != null)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(LogisticsMod), replacement);
                }
                yield return instruction;
            }
            if (retrieves != 1 || stores != 1 || teamStores != 1)
                throw new InvalidOperationException("Native harvester deposit call sites changed; expected one retrieve and two storage paths.");
        }
    }
}
