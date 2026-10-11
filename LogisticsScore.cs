using System;
using System.Collections.Generic;
using System.Globalization;
using SilicaAdminMod;

namespace Si_Logistics
{
    public sealed partial class LogisticsMod
    {
        private sealed class ScoreAccount
        {
            internal Player Player;
            // Hundredths of a point; separate streams cannot round each other up.
            internal int UnloadRemainder, TransferRemainder;
        }
        private static readonly Dictionary<long, ScoreAccount> ScoreRewards = new Dictionary<long, ScoreAccount>();

        private static void AwardScore(Player player, int amount, int rate, bool transfer, int epoch)
        {
            if (epoch != RewardEpoch || !Game.GetIsServer() || !MatchAcceptsRewards ||
                !Cfg.Enabled || !Cfg.LogisticsScore || amount <= 0 || rate <= 0) return;
            long id = SteamId(player);
            if (!Connected(player, id)) return;
            try
            {
                if (!ScoreRewards.TryGetValue(id, out var account) || !ReferenceEquals(account.Player, player))
                    ScoreRewards[id] = account = new ScoreAccount { Player = player };
                long earned = (long)amount * rate + (transfer ? account.TransferRemainder : account.UnloadRemainder);
                if (transfer) account.TransferRemainder = (int)(earned % 100);
                else account.UnloadRemainder = (int)(earned % 100);
                long points = earned / 100;
                if (points == 0) return;
                int previous = player.Score;
                player.Score = (int)Math.Min(int.MaxValue, (long)previous + points);
                if (Cfg.Debug) DebugMsg("Logistics score player=" + id + " +" + ((long)player.Score - previous) +
                    " for " + amount + " Balterium " + (transfer ? "transferred" : "unloaded"));
            }
            catch (Exception error) { DebugWarning("Logistics score: " + error.Message); }
        }

        private static void OnScoreCommand(Player caller, string args)
        {
            try
            {
                if (caller != null && (!caller.IsAdmin() || !AdminMethods.PowerInPowers(Power.Generic, caller.GetAdminPowers())))
                { Reply(caller, "Admin only."); return; }
                string[] parts = (args ?? string.Empty).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string command = parts.Length > 0 ? parts[0].TrimStart('/').ToLowerInvariant() : string.Empty;
                if (command == "logisticsscore")
                {
                    string value = parts.Length == 2 ? parts[1].ToLowerInvariant() : string.Empty;
                    if (value != "on" && value != "off")
                    { Reply(caller, "Usage: /logisticsscore on|off"); return; }
                    Cfg.LogisticsScore = value == "on";
                    if (!Cfg.LogisticsScore) ScoreRewards.Clear();
                    SaveConfig();
                    Reply(caller, "Logistics score " + (Cfg.LogisticsScore ? "ON." : "OFF."));
                    return;
                }
                if (command != "scoreunload" && command != "scoretransfer") return;
                if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rate) || rate < 0 || rate > 100)
                { Reply(caller, "Usage: /" + command + " <0..100> points per 100 Balterium"); return; }
                if (command == "scoreunload") Cfg.ScoreUnload = rate;
                else Cfg.ScoreTransfer = rate;
                SaveConfig();
                Reply(caller, command + " = " + rate + " points per 100 Balterium.");
            }
            catch (Exception error) { DebugWarning("Logistics score command: " + error.Message); }
        }
    }
}
