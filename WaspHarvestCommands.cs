using System;
using System.Globalization;
using System.Text;
using ModUI;
using SilicaAdminMod;
using UnityEngine;

namespace Si_Logistics
{
    // Chat side of Wasp harvest. /wasp is its own command root, like the score commands.
    //
    //   /wasp                       your harvest this round, your Wasp's Bio Cache cooldown, the settings
    //   /wasp help                  how to harvest
    //   /wasp set                   (admin) list every setting with its value and range
    //   /wasp set <key> <value>     (admin) change one setting; numbers also take +N / -N steps
    //
    // A change is validated, saved to Si_Logistics_Config.json and applied to Wasps already in the field.
    public sealed partial class LogisticsMod
    {
        private sealed class WaspSetting
        {
            internal string Key, Label, Description, Unit;
            internal bool IsBool, IsInt;
            internal double Min, Max, UiMax, Step = 1;
            internal Func<WaspHarvestConfig, double> Get;
            internal Action<WaspHarvestConfig, double> Set;
        }

        // Ranges match ValidateWaspHarvestConfig. UiMax only limits the optional menu's slider.
        private static readonly WaspSetting[] WaspSettings =
        {
            new WaspSetting { Key = "enabled", Label = "Wasp harvest", Description = "Player-flown Wasps harvest Biotics.", IsBool = true,
                Get = c => c.Enabled ? 1 : 0, Set = (c, v) => c.Enabled = v != 0 },
            new WaspSetting { Key = "carry", Label = "Wasp cargo capacity", Description = "Biotics per load; also the most one Bio Cache visit unloads.", IsInt = true, Min = 1, Max = 100000, UiMax = 5000, Unit = "Biotics",
                Get = c => c.MaxCarry, Set = (c, v) => c.MaxCarry = (int)v },
            new WaspSetting { Key = "perhit", Label = "Biotics per hit", Description = "Taken from the patch per extracting hit.", IsInt = true, Min = 1, Max = 10000, UiMax = 500, Unit = "Biotics",
                Get = c => c.ExtractPerHit, Set = (c, v) => c.ExtractPerHit = (int)v },
            new WaspSetting { Key = "radius", Label = "Extraction radius", Description = "Searched around the hit point.", Min = 0.5, Max = 50, UiMax = 50, Step = 0.5, Unit = "m",
                Get = c => c.ExtractionRadius, Set = (c, v) => c.ExtractionRadius = (float)v },
            new WaspSetting { Key = "height", Label = "Maximum height above ground", Description = "A hit extracts only this close to terrain or static ground.", Min = 0, Max = 100, UiMax = 100, Step = 0.5, Unit = "m",
                Get = c => c.GroundMaxHeight, Set = (c, v) => c.GroundMaxHeight = (float)v },
            new WaspSetting { Key = "deposit", Label = "Unload per tick", Description = "Biotics moved to the team per deposit tick.", IsInt = true, Min = 1, Max = 10000, UiMax = 1000, Unit = "Biotics",
                Get = c => c.DepositAmount, Set = (c, v) => c.DepositAmount = (int)v },
            new WaspSetting { Key = "cooldown", Label = "Bio Cache cooldown", Description = "One load per cooldown, counted from the first deposit tick of a visit. Zero disables it.", Min = 0, Max = 3600, UiMax = 600, Unit = "s",
                Get = c => c.DepositCooldownSeconds, Set = (c, v) => c.DepositCooldownSeconds = (float)v },
            new WaspSetting { Key = "reward", Label = "Wasp delivery reward share", Description = "Fraction of a Wasp delivery credited to the pilot; the team still receives all of it.", Min = 0, Max = 1, UiMax = 1, Step = 0.01, Unit = "fraction",
                Get = c => c.RewardShare, Set = (c, v) => c.RewardShare = (float)v },
            new WaspSetting { Key = "notify", Label = "Cargo message interval", Description = "Minimum seconds between two cargo-bar messages per Wasp.", Min = 0, Max = 60, UiMax = 60, Unit = "s",
                Get = c => c.NotifyIntervalSeconds, Set = (c, v) => c.NotifyIntervalSeconds = (float)v },
        };

        private static string WaspShow(WaspSetting setting)
        {
            double value = setting.Get(Cfg.WaspHarvest);
            if (setting.IsBool) return value != 0 ? "on" : "off";
            return setting.IsInt ? ((long)value).ToString(CultureInfo.InvariantCulture) : value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string WaspRange(WaspSetting setting) => setting.IsBool ? "on|off" :
            setting.Min.ToString("0.##", CultureInfo.InvariantCulture) + ".." + setting.Max.ToString("0.##", CultureInfo.InvariantCulture);

        private static void OnWaspCommand(Player caller, string args)
        {
            try
            {
                string[] parts = (args ?? string.Empty).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;
                if (sub == "help")
                {
                    var c = Cfg.WaspHarvest;
                    Reply(caller, "Fly a Wasp low over a Biotics patch and hit the ground with the primary attack. Carry up to " + c.MaxCarry +
                        ", then hover still inside your team's Bio Cache to unload. One load per " + c.DepositCooldownSeconds.ToString("0", CultureInfo.InvariantCulture) +
                        " s; you earn " + c.RewardShare.ToString("P0", CultureInfo.InvariantCulture) + " of each delivery as credits.");
                    Reply(caller, "/wasp: your harvest and cooldown. Admin: /wasp set (list); /wasp set <key> <value> or +N / -N.");
                    return;
                }
                if (sub == "set")
                {
                    if (caller != null && (!caller.IsAdmin() || !AdminMethods.PowerInPowers(Power.Generic, caller.GetAdminPowers())))
                    { Reply(caller, "Admin only. Use /wasp for your harvest."); return; }
                    WaspSetCommand(caller, parts);
                    return;
                }
                if (sub != string.Empty && sub != "status") { Reply(caller, "Usage: /wasp [help]. Admin: /wasp set <key> <value>."); return; }
                ShowWaspHarvestStatus(caller);
                if (caller == null)
                {
                    foreach (var tally in WaspTallies.Values)
                        Reply(caller, "  " + tally.Name + ": harvested " + tally.Harvested + ", delivered " + tally.Delivered +
                            ", credits " + tally.Reward.ToString("0.#", CultureInfo.InvariantCulture));
                    return;
                }
                if (WaspTallies.TryGetValue(SteamId(caller), out var mine))
                    Reply(caller, "This round: harvested " + mine.Harvested + ", delivered " + mine.Delivered + " Biotics, earned " +
                        mine.Reward.ToString("0.#", CultureInfo.InvariantCulture) + " credits.");
                else Reply(caller, "No Wasp harvest this round. /wasp help");
                Unit unit = caller.ControlledUnit;
                if (unit == null || !WaspByUnit.TryGetValue(unit.GetInstanceID(), out var state) || !state.Alive) return;
                float left = WaspCooldownLeft(state, Time.time);
                Reply(caller, "Your " + unit.ObjectInfo.DisplayName + ": " + state.Holder.AmountStored + "/" + state.Holder.MaxAmount + " Biotics aboard. " +
                    (state.VisitOpen ? "Unloading: " + state.Pending + " delivered this visit."
                        : left > 0 ? "Bio Cache cooldown: " + Math.Ceiling(left).ToString("0", CultureInfo.InvariantCulture) + " s left."
                        : "Ready to unload."));
            }
            catch (Exception error) { DebugWarning("Wasp harvest command: " + error); }
        }

        private static void ShowWaspHarvestStatus(Player caller)
        {
            var c = Cfg.WaspHarvest;
            Reply(caller, "Wasp harvest " + (WaspStandaloneLoaded ? "OFF (standalone Si_WaspHarvest.dll loaded; remove one of the two)" :
                    !WaspHarvestInstalled ? "UNAVAILABLE (install failed; see server log with debug on)" : c.Enabled ? "ON" : "OFF") +
                " | carry " + c.MaxCarry + ", " + c.ExtractPerHit + " per hit within " + c.ExtractionRadius.ToString("0.#", CultureInfo.InvariantCulture) + " m" +
                ", at most " + c.GroundMaxHeight.ToString("0.#", CultureInfo.InvariantCulture) + " m above ground" +
                " | unload " + c.DepositAmount + " per " + c.TickSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s, one load per " +
                c.DepositCooldownSeconds.ToString("0", CultureInfo.InvariantCulture) + " s" +
                " | reward " + c.RewardShare.ToString("P0", CultureInfo.InvariantCulture) +
                " | equipped (server): " + WaspByClaws.Count + (Cfg.Enabled || !c.Enabled ? "." : " (master Logistics is OFF)."));
        }

        // parts: [/wasp, set, key, value]
        private static void WaspSetCommand(Player caller, string[] parts)
        {
            if (parts.Length < 3)
            {
                var list = new StringBuilder("Wasp harvest settings: ");
                for (int i = 0; i < WaspSettings.Length; i++)
                    list.Append(i > 0 ? " | " : string.Empty).Append(WaspSettings[i].Key).Append('=').Append(WaspShow(WaspSettings[i]))
                        .Append(" (").Append(WaspRange(WaspSettings[i])).Append(')');
                Reply(caller, list.ToString());
                Reply(caller, "Usage: /wasp set <key> <value>, or +N / -N to step a number.");
                return;
            }
            WaspSetting setting = null;
            foreach (var candidate in WaspSettings)
                if (string.Equals(candidate.Key, parts[2], StringComparison.OrdinalIgnoreCase)) setting = candidate;
            if (setting == null) { Reply(caller, "Unknown setting '" + parts[2] + "'. /wasp set lists them."); return; }
            if (parts.Length != 4) { Reply(caller, setting.Key + " = " + WaspShow(setting) + " (" + WaspRange(setting) + "). Usage: /wasp set " + setting.Key + " <value>"); return; }

            string raw = parts[3].Trim().ToLowerInvariant();
            double value;
            if (setting.IsBool)
            {
                if (raw != "on" && raw != "off") { Reply(caller, "Usage: /wasp set " + setting.Key + " on|off"); return; }
                value = raw == "on" ? 1 : 0;
            }
            else
            {
                bool relative = raw.StartsWith("+", StringComparison.Ordinal) || raw.StartsWith("-", StringComparison.Ordinal);
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || double.IsNaN(parsed) || double.IsInfinity(parsed))
                { Reply(caller, "'" + parts[3] + "' is not a number. " + setting.Key + ": " + WaspRange(setting)); return; }
                value = relative ? setting.Get(Cfg.WaspHarvest) + parsed : parsed;
                if (setting.IsInt) value = Math.Round(value, MidpointRounding.AwayFromZero);
                if (value < setting.Min || value > setting.Max)
                { Reply(caller, setting.Key + " must be within " + WaspRange(setting) + ". Unchanged: " + WaspShow(setting) + "."); return; }
            }
            string before = WaspShow(setting);
            setting.Set(Cfg.WaspHarvest, value);
            ValidateWaspHarvestConfig();
            SaveConfig();
            ApplyWaspConfigToLive();
            Reply(caller, setting.Label + ": " + before + " -> " + WaspShow(setting) + "." +
                (Cfg.Enabled || !Cfg.WaspHarvest.Enabled ? string.Empty : " (master Logistics is OFF)"));
        }

        // Rows of the optional menu: every row is one of the chat commands above.
        private static void AddWaspHarvestModUI(ModUIExport export)
        {
            if (!WaspHarvestInstalled) return;   // /wasp is not registered: offer no rows that cannot work
            export.Status("waspharvest", "Wasp harvest", Cfg.WaspHarvest.Enabled ? "ON" : "OFF")
                .Status("wasps-equipped", "Wasps equipped (server)", WaspByClaws.Count.ToString(CultureInfo.InvariantCulture))
                .Action("/wasp", "My Wasp harvest", "Your harvest this round, your Wasp's Bio Cache cooldown and the settings.").Presentation("action", "Wasp harvest")
                .Action("/wasp help", "Wasp harvest help", "How Wasp harvesting works.").Presentation("action", "Wasp harvest");
            foreach (var setting in WaspSettings)
            {
                string command = "/wasp set " + setting.Key;
                if (setting.IsBool)
                    export.OnOff(command, setting.Label, setting.Description, true, setting.Get(Cfg.WaspHarvest) != 0).Presentation("checkbox", "Wasp harvest");
                else
                    export.Value(command, setting.Label, setting.Description, true, WaspShow(setting))
                        .Presentation("slider", "Wasp harvest", setting.Min, Math.Max(setting.UiMax, setting.Get(Cfg.WaspHarvest)), setting.Step, setting.Unit);
            }
        }
    }
}
