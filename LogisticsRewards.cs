using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using MelonLoader;
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
            internal decimal Pending;
            internal float Due;
        }
        private static readonly Dictionary<long, RewardAccount> Rewards = new Dictionary<long, RewardAccount>();
        private static bool MatchAcceptsRewards = true;
        private static int RewardEpoch;
        private static Player DriverOf(Unit unit) => unit == null ? null :
            (unit.ControlledBy != null ? unit.ControlledBy : unit.Driver != null ? unit.Driver.ControlledBy : null);
        private static long SteamId(Player player) => player == null ? 0 : (long)player.PlayerID.m_ID;
        private static bool Connected(Player player, long id) => player != null && id != 0 &&
            Player.Players != null && Player.Players.Contains(player) && SteamId(player) == id;
        private static decimal RewardRate => (decimal)Cfg.PersonalBonus.Share;
        private static decimal HarvesterDepositRate => RewardRate;
        private static decimal HarvesterTransferRate => (decimal)Cfg.PersonalBonus.TransferShare;

        private static void QueueReward(Player driver, int amount, decimal rate, int epoch)
        {
            if (epoch != RewardEpoch || !Game.GetIsServer() || !MatchAcceptsRewards || !Cfg.Enabled || amount <= 0 || rate <= 0) return;
            long id = SteamId(driver);
            if (!Connected(driver, id)) return;
            if (!Rewards.TryGetValue(id, out RewardAccount account) || !ReferenceEquals(account.Player, driver))
                Rewards[id] = account = new RewardAccount { Player = driver };
            // Due does not slide forward as more rewards arrive.
            if (account.Due == 0) account.Due = Math.Max(float.Epsilon, Time.unscaledTime + RewardWindowSeconds);
            account.Pending += amount * rate;
            NextRewardCheck = Math.Min(NextRewardCheck, account.Due);
        }

        private static void PayDueRewards()
        {
            if (!Game.GetIsServer() || !MatchAcceptsRewards || !Cfg.Enabled || Time.unscaledTime < NextRewardCheck) return;
            // Retry safely on exceptions; otherwise the minimum outstanding due time wins.
            NextRewardCheck = Time.unscaledTime + 1f;
            foreach (var pair in new List<KeyValuePair<long, RewardAccount>>(Rewards))
            {
                long id = pair.Key;
                var account = pair.Value;
                if (!Connected(account.Player, id)) { Rewards.Remove(id); continue; }
                if (account.Due == 0 || Time.unscaledTime < account.Due) continue;
                account.Due = 0;
                decimal whole = decimal.Floor(account.Pending);
                if (whole < 1) continue; // Keep the fraction until this player earns another window.
                int balance = KohCredits.TryGetBalance(id);
                if (balance < 0) { account.Due = Time.unscaledTime + Math.Max(1f, RewardWindowSeconds); continue; }
                long room = (long)int.MaxValue - balance;
                int payment = (int)Math.Min(whole, Math.Max(0L, room));
                if (payment == 0) { account.Due = Time.unscaledTime + Math.Max(1f, RewardWindowSeconds); continue; }
                // Consume before invoking the external API: a re-entrant callback cannot pay twice.
                account.Pending -= payment;
                KohCredits.GrantResult result = KohCredits.TryGrant(id, payment, "Logistics rewards (batched)");
                if (Rewards.TryGetValue(id, out var current) && ReferenceEquals(current, account))
                {
                    if (result == KohCredits.GrantResult.Unavailable) account.Pending += payment;
                    if (account.Pending >= 1) account.Due = Time.unscaledTime + Math.Max(1f, RewardWindowSeconds);
                }
                if (result != KohCredits.GrantResult.Granted || !Cfg.PersonalBonus.AnnounceToPlayer || !Connected(account.Player, id)) continue;
                int total = KohCredits.TryGetBalance(id);
                HelperMethods.SendChatMessageToPlayer(account.Player, HelperMethods.chatPrefix,
                    " +" + payment + " credits for logistics" + (total >= 0 ? " (balance " + total + ")." : "."));
            }
            NextRewardCheck = float.PositiveInfinity;
            foreach (var account in Rewards.Values)
                if (account.Due > 0) NextRewardCheck = Math.Min(NextRewardCheck, account.Due);
        }

        private static void BindRewardEvents()
        {
            BindHarvesterEvents();
            BindCommanderScoreEvents();
            GameEvents.OnGameStarted -= LogisticsMatchStarted; GameEvents.OnGameStarted += LogisticsMatchStarted;
            GameEvents.OnGameEnded -= LogisticsMatchEnded; GameEvents.OnGameEnded += LogisticsMatchEnded;
            GameEvents.OnPlayerLeft -= LogisticsPlayerLeft; GameEvents.OnPlayerLeft += LogisticsPlayerLeft;
        }
        private static void LogisticsPlayerLeft(Player player)
        {
            CommanderPlayerLeft(player);
            foreach (var pair in new List<KeyValuePair<long, ScoreAccount>>(ScoreRewards))
                if (ReferenceEquals(pair.Value.Player, player)) ScoreRewards.Remove(pair.Key);
            foreach (var pair in new List<KeyValuePair<long, RewardAccount>>(Rewards))
                if (ReferenceEquals(pair.Value.Player, player)) Rewards.Remove(pair.Key);
        }
        private static void LogisticsMatchStarted(GameMode mode)
        { ResetAITransports(false); ResetHarvesterFix(false); ResetCommanderScore(); RewardEpoch++; Rewards.Clear(); ScoreRewards.Clear(); NextRewardCheck = float.PositiveInfinity; TransportErrorUntil.Clear(); CargoByTransport.Clear(); MatchAcceptsRewards = true; }
        private static void LogisticsMatchEnded(GameMode mode, Team winner)
        { ResetAITransports(false); ResetHarvesterFix(false); ResetCommanderScore(); RewardEpoch++; MatchAcceptsRewards = false; Rewards.Clear(); ScoreRewards.Clear(); NextRewardCheck = float.PositiveInfinity; TransportErrorUntil.Clear(); CargoByTransport.Clear(); }
        public override void OnSceneWasInitialized(int buildIndex, string sceneName) => BindRewardEvents();
        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        { ResetAITransports(false); ResetHarvesterFix(false); ResetCommanderScore(); RewardEpoch++; MatchAcceptsRewards = false; Rewards.Clear(); ScoreRewards.Clear(); NextRewardCheck = float.PositiveInfinity; TransportErrorUntil.Clear(); CargoByTransport.Clear(); }
        public override void OnDeinitializeMelon()
        {
            UnbindCommanderScoreEvents();
            ResetAITransports(true);
            ResetHarvesterFix(true);
            GameEvents.OnUnitSpawned -= HarvesterSpawned;
            ResetCommanderScore(); RewardEpoch++; MatchAcceptsRewards = false; Rewards.Clear(); ScoreRewards.Clear(); NextRewardCheck = float.PositiveInfinity; TransportErrorUntil.Clear(); CargoByTransport.Clear();
            GameEvents.OnGameStarted -= LogisticsMatchStarted;
            GameEvents.OnGameEnded -= LogisticsMatchEnded;
            GameEvents.OnPlayerLeft -= LogisticsPlayerLeft;
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
        private static void ClawsBegin(MiningClaws __instance, out DepositReceipt __state) =>
            __state = BeginDeposit(__instance.Creature, __instance.ResourceHolder);
        private static DepositReceipt BeginDeposit(Unit unit, ResourceHolder holder)
        {
            if (DepositReceiptPool == null) DepositReceiptPool = new Stack<DepositReceipt>();
            var receipt = DepositReceiptPool.Count > 0 ? DepositReceiptPool.Pop() : new DepositReceipt();
            receipt.Parent = CurrentDeposit; receipt.Epoch = RewardEpoch;
            if (Game.GetIsServer() && MatchAcceptsRewards && Cfg.Enabled &&
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
                    QueueReward(__state.Driver, delivered, __state.Rate, __state.Epoch);
                    AwardScore(__state.Driver, delivered, __state.ScoreRate, false, __state.Epoch);
                    CommanderDeposit(__state.Team, delivered, __state.Epoch);
                }
            }
            catch (Exception error) { DebugWarning("Logistics harvester reward: " + error.Message); }
            finally
            {
                CurrentDeposit = __state.Parent;
                __state.Parent = null; __state.Unit = null; __state.Holder = null;
                __state.Driver = null; __state.Team = null; __state.Rate = 0;
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
            Player driver = receipt != null && receipt.Holder == holder ? DriverOf(receipt.Unit) : null;
            decimal rate = HarvesterDepositRate;
            int removed = holder.RetrieveResource(requested);
            if (receipt != null && receipt.Holder == holder && removed > 0)
            {
                receipt.Driver = driver; receipt.Rate = rate;
                receipt.ScoreRate = Cfg.LogisticsScore ? Cfg.ScoreUnload : 0;
                receipt.Retrieved += Math.Min(Math.Max(0, requested), removed);
            }
            return removed;
        }
        private static int ObservedStructureStore(BaseGameObject structure, Resource resource, int amount)
        {
            int remainder = structure.StoreResource(resource, amount);
            var receipt = CurrentDeposit;
            if (receipt != null && receipt.Team != null && structure.Team == receipt.Team && resource == receipt.Team.UsableResource)
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
