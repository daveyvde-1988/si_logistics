using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SilicaAdminMod;
using UnityEngine;

namespace Si_Logistics
{
    public sealed class CommanderScoreRule
    {
        public string Event;
        public bool Enabled = true;
        public string ValueSource = "Cost"; // Cost, Score, Fixed, or Amount (deposits only).
        public int Divisor = 100;
        public int Minimum = 1;
        public int Multiplier = 1; // Negative values subtract score.
        public int FixedPoints;
        // Empty matches all objects; names match ObjectInfo.name or DisplayName.
        public List<string> Objects = new List<string>();
        // Exact final signed points, bypassing the formula, for named objects.
        public Dictionary<string, int> Overrides = new Dictionary<string, int>();
    }

    public sealed class CommanderScoreConfig
    {
        public bool Enabled = true;
        public List<CommanderScoreRule> Rules = new List<CommanderScoreRule>
        {
            new CommanderScoreRule { Event = "UnitProduced" },
            new CommanderScoreRule { Event = "UnitLost", Multiplier = -1 },
            new CommanderScoreRule { Event = "BuildingCompleted", Divisor = 50 },
            new CommanderScoreRule { Event = "BuildingLost", Divisor = 50, Multiplier = -1 },
            new CommanderScoreRule { Event = "TechnologyReached", ValueSource = "Fixed", FixedPoints = 500 },
            new CommanderScoreRule { Event = "EnemyUnitDestroyed", Divisor = 100 },
            new CommanderScoreRule { Event = "EnemyBuildingDestroyed", Divisor = 50 },
            new CommanderScoreRule { Event = "BalteriumDeposited", ValueSource = "Amount", Divisor = 1000, Minimum = 0 }
        };
    }

    public sealed partial class LogisticsMod
    {
        private static readonly HashSet<BaseGameObject> CommanderBought = new HashSet<BaseGameObject>();
        private static readonly HashSet<BaseGameObject> CommanderProduced = new HashSet<BaseGameObject>();
        private static readonly HashSet<BaseGameObject> CommanderDestroyed = new HashSet<BaseGameObject>();
        private static readonly Dictionary<Team, int> CommanderHighestTier = new Dictionary<Team, int>();
        private static readonly Dictionary<CommanderScoreRule, Dictionary<Tuple<Player, Team>, decimal>> CommanderFractions =
            new Dictionary<CommanderScoreRule, Dictionary<Tuple<Player, Team>, decimal>>();
        [ThreadStatic] private static int CommanderPurchaseDepth;
        private static bool CommanderPurchaseHookReady;

        private static bool CommanderMatchActive => Game.GetIsServer() && MatchAcceptsRewards &&
            GameMode.CurrentGameMode != null && GameMode.CurrentGameMode.GameOngoing;
        private static bool CommanderScoringActive => CommanderMatchActive && Cfg.Enabled &&
            Cfg.CommanderScore.Enabled && CommanderPurchaseHookReady;

        private static void ValidateCommanderScoreConfig()
        {
            if (Cfg.CommanderScore == null) Cfg.CommanderScore = new CommanderScoreConfig();
            if (Cfg.CommanderScore.Rules == null) Cfg.CommanderScore.Rules = new List<CommanderScoreRule>();
            Cfg.CommanderScore.Rules.RemoveAll(rule => rule == null);
            foreach (var rule in Cfg.CommanderScore.Rules)
            {
                rule.Divisor = Math.Max(1, rule.Divisor);
                rule.Minimum = Math.Max(0, rule.Minimum);
                if (rule.Objects == null) rule.Objects = new List<string>();
                if (rule.Overrides == null) rule.Overrides = new Dictionary<string, int>();
            }
            // Rates changed on reload: do not apply a new formula to old fractions.
            CommanderFractions.Clear();
        }

        private void InstallCommanderScore()
        {
            // Observe the purchase scope without referencing or changing KoH's DLL/config.
            var purchaseOwners = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly=>new[]{assembly.GetType("Si_Buy.Buy",false),assembly.GetType("Si_KingOfTheHill.KingOfTheHill",false)})
                .Where(type=>type!=null).Distinct().ToArray();
            CommanderPurchaseHookReady = purchaseOwners.Length==0;
            try
            {
                foreach(var owner in purchaseOwners)
                {
                    var methods=AccessTools.GetDeclaredMethods(owner).Where(method=>method.Name=="TryPurchase"&&method.IsStatic&&method.ReturnType==typeof(void)&&
                        method.GetParameters().Length==2&&method.GetParameters()[0].ParameterType==typeof(Player)).ToArray();
                    // New KoH has no purchase handler; Si_Buy owns it. An unknown Buy signature fails closed.
                    if(methods.Length==0&&owner.FullName=="Si_KingOfTheHill.KingOfTheHill"&&purchaseOwners.Any(t=>t.FullName=="Si_Buy.Buy"))continue;
                    if(methods.Length!=1)throw new InvalidOperationException("Unknown purchase handler in "+owner.FullName);
                    HarmonyInstance.Patch(methods[0],prefix:new HarmonyMethod(typeof(LogisticsMod),nameof(CommanderPurchaseBegin)),
                        finalizer:new HarmonyMethod(typeof(LogisticsMod),nameof(CommanderPurchaseEnd)));
                }
                var spawn = AccessTools.Method(typeof(Game), "SpawnPrefab", new[] { typeof(GameObject), typeof(Player),
                    typeof(Team), typeof(Vector3), typeof(Quaternion), typeof(bool), typeof(bool) });
                HarmonyInstance.Patch(spawn, postfix: new HarmonyMethod(typeof(LogisticsMod), nameof(CommanderSpawnedPrefab)));
                var instant = AccessTools.Method(typeof(Structure), "ConstructNonPlaceableInstant",
                    new[] { typeof(ConstructionData), typeof(Vector3), typeof(Quaternion) });
                HarmonyInstance.Patch(instant,
                    transpiler: new HarmonyMethod(typeof(LogisticsMod), nameof(CommanderObserveInstantSpawn)));
                CommanderPurchaseHookReady = true;
                DebugMsg("Commander score hooks ready; compatible purchase exclusions installed.");
            }
            catch (Exception error)
            {
                CommanderPurchaseHookReady = false;
                DebugWarning("Commander score disabled: production/purchase hooks unavailable: " + error.Message);
            }
        }

        private static void CommanderPurchaseBegin(out int __state)
        { __state = CommanderPurchaseDepth; CommanderPurchaseDepth++; }
        private static Exception CommanderPurchaseEnd(Exception __exception, int __state)
        { CommanderPurchaseDepth = __state; return __exception; }
        private static IEnumerable<CodeInstruction> CommanderObserveInstantSpawn(IEnumerable<CodeInstruction> instructions)
        {
            var spawn = AccessTools.Method(typeof(GameMode), "SpawnUnit", new[] { typeof(GameObject), typeof(Vector3),
                typeof(Quaternion), typeof(Team), typeof(bool), typeof(bool) });
            int calls = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(spawn))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(LogisticsMod), nameof(CommanderInstantSpawn));
                    calls++;
                }
                yield return instruction;
            }
            if (calls != 1) throw new InvalidOperationException("Instant production spawn path changed.");
        }
        private static Unit CommanderInstantSpawn(GameMode mode, GameObject prefab, Vector3 position, Quaternion rotation,
            Team team, bool sendNetInit, bool sendNetSpawn)
        {
            Unit unit = mode.SpawnUnit(prefab, position, rotation, team, sendNetInit, sendNetSpawn);
            try { if (unit != null) CommanderProductionCompleted(unit); }
            catch (Exception error) { DebugWarning("Commander score instant production: " + error.Message); }
            return unit;
        }
        private static void CommanderSpawnedPrefab(GameObject __result)
        {
            if (!Game.GetIsServer() || __result == null) return;
            try
            {
                BaseGameObject obj = __result.GetBaseGameObject();
                if (obj == null) return;
                if (CommanderPurchaseDepth > 0) CommanderBought.Add(obj);
            }
            catch (Exception error) { DebugWarning("Commander score spawn observation: " + error.Message); }
        }

        private static void BindCommanderScoreEvents()
        {
            UnbindCommanderScoreEvents();
            GameEvents.OnConstructionCompleted += CommanderConstructionCompleted;
            // This fires before Player.OnTargetDestroyed and game-mode destruction handlers.
            GameEvents.OnTargetDestroyed += CommanderTargetDestroyed;
            GameEvents.OnBaseGameObjectDeleted += CommanderObjectDeleted;
            GameEvents.OnTeamTechnologyTierChanged += CommanderTierChanged;
            GameEvents.OnGameRestart += CommanderGameRestarted;
            if (CommanderHighestTier.Count == 0) SeedCommanderTiers();
        }
        private static void UnbindCommanderScoreEvents()
        {
            GameEvents.OnConstructionCompleted -= CommanderConstructionCompleted;
            GameEvents.OnTargetDestroyed -= CommanderTargetDestroyed;
            GameEvents.OnBaseGameObjectDeleted -= CommanderObjectDeleted;
            GameEvents.OnTeamTechnologyTierChanged -= CommanderTierChanged;
            GameEvents.OnGameRestart -= CommanderGameRestarted;
        }
        private static void SeedCommanderTiers()
        {
            if (Team.Teams == null) return;
            foreach (var team in Team.Teams)
                if (team != null) CommanderHighestTier[team] = Math.Max(team.TechnologyTier, team.TechnologyTierHighestReached);
        }
        private static void ResetCommanderScore()
        {
            CommanderBought.Clear(); CommanderProduced.Clear(); CommanderDestroyed.Clear();
            CommanderHighestTier.Clear(); CommanderFractions.Clear();
            SeedCommanderTiers();
        }
        private static void CommanderGameRestarted(GameMode mode) => ResetCommanderScore();
        private static void CommanderObjectDeleted(BaseGameObject obj)
        { CommanderBought.Remove(obj); CommanderProduced.Remove(obj); CommanderDestroyed.Remove(obj); }
        private static void CommanderPlayerLeft(Player player)
        {
            foreach (var fractions in CommanderFractions.Values)
                foreach (var key in fractions.Keys.Where(key => ReferenceEquals(key.Item1, player)).ToList()) fractions.Remove(key);
        }

        private static void CommanderConstructionCompleted(ConstructionSite site, bool completed)
        {
            if (!Game.GetIsServer() || !completed || site == null || site.IsDeconstructing) return;
            try
            {
                // Successful object spawn is required; research and failed/cancelled sites are excluded.
                if (site.SpawnedGameObject != null) CommanderProductionCompleted(site.SpawnedGameObject);
            }
            catch (Exception error) { DebugWarning("Commander score construction: " + error.Message); }
        }
        private static void CommanderProductionCompleted(BaseGameObject obj)
        {
            if (!CommanderMatchActive || obj == null || CommanderPurchaseDepth > 0 || CommanderBought.Contains(obj) || !CommanderProduced.Add(obj)) return;
            if (obj is Unit) ApplyCommanderRules(obj.Team, "UnitProduced", obj.ObjectInfo);
            else if (obj is Structure) ApplyCommanderRules(obj.Team, "BuildingCompleted", obj.ObjectInfo);
        }

        private static bool CommanderPlayerControlled(Unit unit) => unit != null &&
            (unit.PlayerControlled || DriverOf(unit) != null);
        private static void CommanderTargetDestroyed(Target target, GameObject instigator)
        {
            if (!CommanderMatchActive || target == null) return;
            try
            {
                BaseGameObject victim = target.Owner;
                if (victim == null || (!(victim is Unit) && !(victim is Structure)) || !CommanderDestroyed.Add(victim)) return;
                bool bought = CommanderPurchaseDepth > 0 || CommanderBought.Contains(victim);
                // Charge the team's current commander, regardless of who produced the object.
                if (!bought && (!(victim is Unit lost) || !CommanderPlayerControlled(lost)))
                    ApplyCommanderRules(victim.Team, victim is Unit ? "UnitLost" : "BuildingLost", victim.ObjectInfo);

                BaseGameObject attacker = instigator != null ? instigator.GetBaseGameObject() : null;
                if (bought || attacker == null || CommanderBought.Contains(attacker) ||
                    !Team.GetTeamsAreEnemy(attacker.Team, victim.Team)) return;
                // An automated structure turret also qualifies; player-fired weapons do not.
                if (attacker is Unit attackingUnit)
                {
                    if (CommanderPlayerControlled(attackingUnit) || !attackingUnit.AIControlled) return;
                }
                else if (!(attacker is Structure)) return;
                if (attacker is Structure && attacker.NetworkComponent != null && attacker.NetworkComponent.OwnerPlayer != null) return;
                ApplyCommanderRules(attacker.Team, victim is Unit ? "EnemyUnitDestroyed" : "EnemyBuildingDestroyed", victim.ObjectInfo);
            }
            catch (Exception error) { DebugWarning("Commander score destruction: " + error.Message); }
        }

        private static void CommanderTierChanged(Team team, int previous, int current)
        {
            if (!Game.GetIsServer() || team == null) return;
            try
            {
                int highest = CommanderHighestTier.TryGetValue(team, out int seen) ? Math.Max(seen, previous) : previous;
                CommanderHighestTier[team] = Math.Max(highest, current);
                if (!CommanderMatchActive) return;
                // Still observe while switched off, so toggling cannot re-award old tiers.
                for (int tier = highest + 1; tier <= current; tier++)
                    ApplyCommanderRules(team, "TechnologyReached", null);
            }
            catch (Exception error) { DebugWarning("Commander score technology: " + error.Message); }
        }

        private static Player CurrentCommander(Team team)
        {
            var mode = GameMode.CurrentGameMode as GameModeExt;
            Player player = team != null && mode != null ? mode.GetCommanderForTeam(team) : null;
            return Connected(player, SteamId(player)) ? player : null;
        }
        private static bool CommanderNameMatches(string name, ObjectInfo info) => info != null &&
            (string.Equals(name, info.name, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(name, info.DisplayName, StringComparison.OrdinalIgnoreCase));

        private static decimal CommanderRulePoints(CommanderScoreRule rule, ObjectInfo info, int amount)
        {
            foreach (var pair in rule.Overrides)
                if (CommanderNameMatches(pair.Key, info)) return pair.Value;
            string source = (rule.ValueSource ?? "Cost").ToLowerInvariant();
            if (source == "fixed") return (decimal)rule.FixedPoints * rule.Multiplier;
            if (source == "amount") return (decimal)amount / rule.Divisor * rule.Multiplier;
            if (info == null) return 0;
            if (source != "cost" && source != "score") return 0;
            int value = source == "score" ? info.Score : info.Cost;
            return Math.Max(rule.Minimum, Math.Floor((decimal)Math.Max(0, value) / rule.Divisor)) * rule.Multiplier;
        }
        private static void ApplyCommanderRules(Team team, string eventName, ObjectInfo info, int amount = 0)
        {
            if (!CommanderScoringActive) return;
            Player player = CurrentCommander(team);
            if (player == null) return;
            foreach (var rule in Cfg.CommanderScore.Rules)
            {
                if (!rule.Enabled || !string.Equals(rule.Event, eventName, StringComparison.OrdinalIgnoreCase) ||
                    (rule.Objects.Count > 0 && !rule.Objects.Any(name => CommanderNameMatches(name, info)))) continue;
                decimal points = CommanderRulePoints(rule, info, amount);
                if (eventName == "BalteriumDeposited")
                {
                    if (!CommanderFractions.TryGetValue(rule, out var fractions))
                        CommanderFractions[rule] = fractions = new Dictionary<Tuple<Player, Team>, decimal>();
                    var key = Tuple.Create(player, team);
                    fractions.TryGetValue(key, out decimal remainder);
                    decimal total = points + remainder;
                    points = decimal.Truncate(total);
                    fractions[key] = total - points;
                }
                if (points == 0) continue;
                int before = player.Score;
                player.Score = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (decimal)before + decimal.Truncate(points)));
                if (Cfg.Debug) DebugMsg("Commander score player=" + SteamId(player) + " event=" + eventName +
                    " object=" + (info != null ? info.DisplayName : "team") + " delta=" + ((long)player.Score - before));
            }
        }
        private static void CommanderDeposit(Team team, int amount, int epoch)
        {
            if (epoch != RewardEpoch || team == null || amount <= 0) return;
            try { ApplyCommanderRules(team, "BalteriumDeposited", null, amount); }
            catch (Exception error) { DebugWarning("Commander score deposit: " + error.Message); }
        }

        private static void OnCommanderScoreCommand(Player caller, string args)
        {
            try
            {
                if (caller != null && (!caller.IsAdmin() || !AdminMethods.PowerInPowers(Power.Generic, caller.GetAdminPowers())))
                { Reply(caller, "Admin only."); return; }
                string[] parts = (args ?? string.Empty).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string value = parts.Length == 2 ? parts[1].ToLowerInvariant() : string.Empty;
                if (value != "on" && value != "off")
                { Reply(caller, "Usage: /logisticsscorecomander on|off"); return; }
                Cfg.CommanderScore.Enabled = value == "on";
                if (!Cfg.CommanderScore.Enabled) CommanderFractions.Clear();
                SaveConfig();
                Reply(caller, "Commander score " + (Cfg.CommanderScore.Enabled ? "ON." : "OFF."));
            }
            catch (Exception error) { DebugWarning("Commander score command: " + error.Message); }
        }
    }
}
