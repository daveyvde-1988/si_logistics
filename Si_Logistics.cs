using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;
using SilicaAdminMod;
using UnityEngine;

[assembly: MelonInfo(typeof(Si_Logistics.LogisticsMod), "Harvester Logistics Prototype", "0.9.1", "Silica community prototype")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonOptionalDependencies("Admin Mod")]

namespace Si_Logistics
{
    // UserData/Logistics_cfg/Si_Logistics_Config.json, read when the mod loads and on "/logistics reload".
    // The file wins over these defaults; missing keys keep the default. The file is only rewritten
    // when an admin changes a setting by chat command (unknown keys are dropped then).
    public sealed class LogisticsConfig
    {
        public bool Enabled = true;
        // true = 0.3.x diagnostics: vehicle names, a 5 s nearest-harvester/refinery probe and one
        // LOAD/UNLOAD line per second. Off: no mod-owned server log output.
        public bool Debug = false;
        public bool LogisticsScore = true;
        public int ScoreUnload = 2;
        public int ScoreTransfer = 1;
        public CommanderScoreConfig CommanderScore = new CommanderScoreConfig();
        [JsonProperty("AItransport")]
        public AITransportConfig AITransport = new AITransportConfig();
        public List<string> TransportUnits = new List<string> { "Shuttle", "Dropship", "Squad Transport", "Platoon Hauler" };
        public int AirCargoCapacity = 9000;
        public int GroundCargoCapacity = 3000;
        [JsonProperty("airloadunitspersecond")] public int AirLoadUnitsPerSecond = 500;
        [JsonProperty("airunloadunitspersecond")] public int AirUnloadUnitsPerSecond = 500;
        public int LoadUnitsPerSecond = 300;
        public int UnloadUnitsPerSecond = 300;
        public float LoadRadius = 40f;
        public float UnloadRadius = 65f;
        public float DeliveryCooldownSeconds = 20f;
        public BonusConfig PersonalBonus = new BonusConfig();
        public ChatConfig Chat = new ChatConfig();
        public SoundConfig Sounds = new SoundConfig();
        public CommandConfig Commands = new CommandConfig();
        [JsonProperty("harvesterfix")]
        public HarvesterFixConfig HarvesterFix = new HarvesterFixConfig();

        public sealed class CommandConfig
        {
            public bool HideFromChat = true;
        }

        public sealed class ChatConfig
        {
            // Seconds between progress-bar messages while loading/unloading (0 = start/stop only).
            public float ProgressIntervalSeconds = 3f;
            public int ProgressBarLength = 10;
            public string BarFilled = "#";
            public string BarEmpty = "-";
        }

        public sealed class BonusConfig
        {
            // Driver rewards are always enabled while the master switch is on.
            public float Share = 0.10f;
            public float TransferShare = 0.08f;
            public bool AnnounceToPlayer = true;
        }

        public sealed class SoundConfig
        {
            public bool Enabled = true;
            public string OnFull = "logistics_ding.wav";
            public string OnLoad = "logistics_tick.wav";
        }
    }

    public sealed partial class LogisticsMod : MelonMod
    {
        private sealed class Cargo
        {
            public int Amount;
            public float CooldownUntil;
            public string Transfer;
            public int TransferFrom;     // loading: 0; unloading: cargo when delivery started
            public int TransferTotal;    // loading: cargo capacity; unloading: cargo when delivery started
            public float NextProgress;
            public int LastPercent = -1;
            public float LastDiagnostic;
        }

        private static readonly Dictionary<int, Cargo> CargoByTransport = new Dictionary<int, Cargo>();
        private static readonly HashSet<string> LoggedDriverVehicles = new HashSet<string>();
        private static LogisticsConfig Cfg = new LogisticsConfig();
        private static readonly string ConfigPath =
            Path.Combine(MelonEnvironment.UserDataDirectory, "Logistics_cfg", "Si_Logistics_Config.json");
        private float _elapsed;

        // All mod-owned server log output follows the runtime Debug setting.
        private static void DebugMsg(string text) { if (Cfg.Debug) MelonLogger.Msg(text); }
        private static void DebugWarning(string text) { if (Cfg.Debug) MelonLogger.Warning(text); }
        private static void DebugError(string text) { if (Cfg.Debug) MelonLogger.Error(text); }

        public override void OnInitializeMelon()
        {
            LoadConfig();
            DebugMsg("Harvester Logistics config " + ConfigPath + ": enabled=" + Cfg.Enabled +
                ", vehicles=" + string.Join(", ", Cfg.TransportUnits) +
                ", capacity air/ground=" + Cfg.AirCargoCapacity + "/" + Cfg.GroundCargoCapacity +
                ", load/unload per s=" + Cfg.LoadUnitsPerSecond + "/" + Cfg.UnloadUnitsPerSecond +
                ", radius load/unload=" + Cfg.LoadRadius + "/" + Cfg.UnloadRadius +
                ", cooldown=" + Cfg.DeliveryCooldownSeconds + "s, debug=" + Cfg.Debug + ", sounds=" + Cfg.Sounds.Enabled +
                ", personal rewards=" + RewardSettings() + " as KoH credits");
        }

        private static void LoadConfig()
        {
            string previousHarvesterSettings = JsonConvert.SerializeObject(Cfg.HarvesterFix);
            try
            {
                if (File.Exists(ConfigPath))
                {
                    // Replace: the default (Auto) appends file lists to the initializer defaults,
                    // which doubled TransportUnits in the 0.4.0 config.
                    var settings = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
                    var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(ConfigPath));
                    var transport = json.GetValue("AItransport", StringComparison.OrdinalIgnoreCase) as Newtonsoft.Json.Linq.JObject;
                    if (transport != null)
                    {
                        if (transport.GetValue("airloadradius", StringComparison.OrdinalIgnoreCase) == null)
                            transport["airloadradius"] = transport.GetValue("DropshipLoadRadius", StringComparison.OrdinalIgnoreCase) ?? 150f;
                        if (transport.GetValue("airunloadradius", StringComparison.OrdinalIgnoreCase) == null)
                            transport["airunloadradius"] = transport.GetValue("DropshipUnloadRadius", StringComparison.OrdinalIgnoreCase) ?? 150f;
                        if (transport.GetValue("groundloadradius", StringComparison.OrdinalIgnoreCase) == null)
                            transport["groundloadradius"] = json.GetValue("LoadRadius", StringComparison.OrdinalIgnoreCase) ?? 40f;
                        if (transport.GetValue("groundunloadradius", StringComparison.OrdinalIgnoreCase) == null)
                            transport["groundunloadradius"] = json.GetValue("UnloadRadius", StringComparison.OrdinalIgnoreCase) ?? 65f;
                    }
                    var bonus = json.GetValue("PersonalBonus", StringComparison.OrdinalIgnoreCase) as Newtonsoft.Json.Linq.JObject;
                    if (bonus != null && bonus.GetValue("TransferShare", StringComparison.OrdinalIgnoreCase) == null)
                        bonus["TransferShare"] = bonus.GetValue("HarvesterTransferShare", StringComparison.OrdinalIgnoreCase) ?? 0.08f;
                    Cfg = JsonConvert.DeserializeObject<LogisticsConfig>(json.ToString(), settings) ?? new LogisticsConfig();
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                    File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Cfg, Formatting.Indented));
                    DebugMsg("Logistics: wrote default config " + ConfigPath);
                }
            }
            catch (Exception exception)
            {
                DebugError("Logistics: config " + ConfigPath + " unreadable, using defaults: " + exception.Message);
                Cfg = new LogisticsConfig();
            }
            if (Cfg.TransportUnits == null) Cfg.TransportUnits = new List<string>();
            if (Cfg.Sounds == null) Cfg.Sounds = new LogisticsConfig.SoundConfig();
            if (Cfg.PersonalBonus == null) Cfg.PersonalBonus = new LogisticsConfig.BonusConfig();
            if (Cfg.Chat == null) Cfg.Chat = new LogisticsConfig.ChatConfig();
            if (Cfg.Commands == null) Cfg.Commands = new LogisticsConfig.CommandConfig();
            Cfg.ScoreUnload = Math.Max(0, Math.Min(100, Cfg.ScoreUnload));
            Cfg.ScoreTransfer = Math.Max(0, Math.Min(100, Cfg.ScoreTransfer));
            if (!Cfg.LogisticsScore) ScoreRewards.Clear();
            ValidateCommanderScoreConfig();
            Cfg.PersonalBonus.Share = ValidRewardShare(Cfg.PersonalBonus.Share, 0.10f);
            Cfg.PersonalBonus.TransferShare = ValidRewardShare(Cfg.PersonalBonus.TransferShare, 0.08f);
            if (float.IsNaN(Cfg.DeliveryCooldownSeconds) || float.IsInfinity(Cfg.DeliveryCooldownSeconds) || Cfg.DeliveryCooldownSeconds < 0)
                Cfg.DeliveryCooldownSeconds = 20f;
            var unique = new List<string>();
            foreach (string name in Cfg.TransportUnits)
                if (!string.IsNullOrWhiteSpace(name) && !unique.Exists(n => n.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    unique.Add(name.Trim());
            Cfg.TransportUnits = unique;
            ValidateHarvesterFixConfig();
            if (previousHarvesterSettings != JsonConvert.SerializeObject(Cfg.HarvesterFix)) ResetHarvesterFix(true);
            if (Cfg.AITransport == null) Cfg.AITransport = new AITransportConfig();
            ValidateAITransportConfig();
            if (!Cfg.Enabled) ReleaseLogisticsControl();
            else if (!Cfg.AITransport.Enabled) ResetAITransports(true);
        }

        private static float ValidRewardShare(float? value, float fallback)
        {
            float share = value ?? fallback;
            return float.IsNaN(share) || float.IsInfinity(share)
                ? Mathf.Clamp01(fallback) : Mathf.Clamp01(share);
        }

        private static string RewardSettings() =>
            "transport " + Cfg.PersonalBonus.Share.ToString("P1", CultureInfo.InvariantCulture) +
            ", harvester refinery " + Cfg.PersonalBonus.Share.ToString("P1", CultureInfo.InvariantCulture) +
            ", harvester transfer " + Cfg.PersonalBonus.TransferShare.ToString("P1", CultureInfo.InvariantCulture) +
            ", payout " + Cfg.DeliveryCooldownSeconds.ToString("0.##", CultureInfo.InvariantCulture) + "s";

        private static void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Cfg, Formatting.Indented));
            }
            catch (Exception exception) { DebugWarning("Logistics: could not save config: " + exception.Message); }
        }

        public override void OnLateInitializeMelon()
        {
            InstallHarvesterRewards();
            BindRewardEvents();
            InstallHarvesterFix();
            InstallAITransport();
            InstallCommanderScore();
            // Fixed public surface: both names always expose exactly the same commands.
            HelperMethods.RegisterPlayerCommand("logistics", OnLogisticsCommand, Cfg.Commands.HideFromChat);
            HelperMethods.RegisterPlayerCommand("logi", OnLogisticsCommand, Cfg.Commands.HideFromChat);
            HelperMethods.RegisterPlayerCommand("logisticsscore", OnScoreCommand, Cfg.Commands.HideFromChat);
            HelperMethods.RegisterPlayerCommand("scoreunload", OnScoreCommand, Cfg.Commands.HideFromChat);
            HelperMethods.RegisterPlayerCommand("scoretransfer", OnScoreCommand, Cfg.Commands.HideFromChat);
            HelperMethods.RegisterPlayerCommand("logisticsscorecomander", OnCommanderScoreCommand, Cfg.Commands.HideFromChat);
            HelperMethods.RegisterPlayerCommand("logisticsscorecommander", OnCommanderScoreCommand, Cfg.Commands.HideFromChat);
        }

        private static void ReleaseLogisticsControl()
        {
            ResetAITransports(true);
            ResetHarvesterFix(true);
            if (Unit.Units == null) return;
            foreach (var unit in Unit.Units)
                if (IsTransport(unit) && CargoByTransport.TryGetValue(unit.GetInstanceID(), out var cargo))
                    SetTransfer(unit, cargo, null);
        }

        private static void OnLogisticsCommand(Player caller, string args)
        {
            try
            {
                string[] parts = (args ?? string.Empty).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;
                string a1 = parts.Length > 2 ? parts[2].ToLowerInvariant() : string.Empty;
                string a2 = parts.Length > 3 ? parts[3] : string.Empty;

                if (sub == string.Empty || sub == "status") { ShowStatus(caller); return; }
                if (sub == "help") { ShowHelp(caller); return; }
                if (caller != null && (!caller.IsAdmin() || !AdminMethods.PowerInPowers(Power.Generic, caller.GetAdminPowers())))
                {
                    Reply(caller, "Admin only. Use /logistics for status.");
                    return;
                }

                bool enabledAlias = sub == "enabled";
                if(enabledAlias)
                {
                    if(parts.Length!=3||(a1!="on"&&a1!="off")){Reply(caller,"Usage: /logistics enabled on|off");return;}
                    sub=a1;
                }
                int expected = enabledAlias ? 3 : (sub == "capacity" || sub == "rate" || sub == "radius") ? 4 :
                    (sub == "on" || sub == "off" || sub == "reload") ? 2 : 3;
                if (parts.Length != expected) { ShowHelp(caller); return; }

                switch (sub)
                {
                    case "on":
                    case "off":
                        Cfg.Enabled = sub == "on";
                        if (!Cfg.Enabled) ReleaseLogisticsControl();
                        SaveConfig();
                        Reply(caller, "Logistics " + (Cfg.Enabled ? "enabled." : "disabled.")); break;
                    case "share":
                    case "transfershare":
                        if (!TryParseFloat(a1, out float share) || share < 0f || share > 1f)
                        { Reply(caller, "Usage: " + sub + " <0..1>"); return; }
                        if (sub == "share") Cfg.PersonalBonus.Share = share;
                        else Cfg.PersonalBonus.TransferShare = share;
                        SaveConfig();
                        Reply(caller, (sub == "share" ? "Transport and harvester refinery reward" : "Harvester-to-transport pickup reward") +
                            " = " + share.ToString("P1", CultureInfo.InvariantCulture) + "."); break;
                    case "harvesterfix":
                    case "aitransport":
                        if (a1 != "on" && a1 != "off") { Reply(caller, "Usage: /logistics " + sub + " on|off"); return; }
                        if (sub == "harvesterfix")
                        {
                            Cfg.HarvesterFix.Enabled = a1 == "on";
                            if (!Cfg.HarvesterFix.Enabled) ResetHarvesterFix(true);
                        }
                        else
                        {
                            Cfg.AITransport.Enabled = a1 == "on";
                            if (!Cfg.AITransport.Enabled) ResetAITransports(true);
                        }
                        SaveConfig();
                        Reply(caller, sub + " " + a1.ToUpperInvariant() + (Cfg.Enabled ? "." : " (master Logistics is OFF).")); break;
                    case "capacity":
                        if (!int.TryParse(a2, out int capacity) || capacity <= 0 || (a1 != "air" && a1 != "ground")) { Reply(caller, "Usage: capacity air|ground <n>"); return; }
                        if (a1 == "air") Cfg.AirCargoCapacity = capacity; else Cfg.GroundCargoCapacity = capacity;
                        SaveConfig(); Reply(caller, a1 + " capacity = " + capacity + "."); break;
                    case "rate":
                        if (!int.TryParse(a2, out int rate) || rate <= 0 || (a1 != "load" && a1 != "unload")) { Reply(caller, "Usage: rate load|unload <units per second>"); return; }
                        if (a1 == "load") Cfg.LoadUnitsPerSecond = rate; else Cfg.UnloadUnitsPerSecond = rate;
                        SaveConfig(); Reply(caller, a1 + " rate = " + rate + "/s."); break;
                    case "radius":
                        if (!TryParseFloat(a2, out float radius) || radius <= 0f || (a1 != "load" && a1 != "unload")) { Reply(caller, "Usage: radius load|unload <metres>"); return; }
                        if (a1 == "load") Cfg.LoadRadius = radius; else Cfg.UnloadRadius = radius;
                        SaveConfig(); Reply(caller, a1 + " radius = " + radius.ToString("F0", CultureInfo.InvariantCulture) + " m."); break;
                    case "cooldown":
                        if (!TryParseFloat(a1, out float cooldown) || cooldown < 0f) { Reply(caller, "Usage: cooldown <seconds>"); return; }
                        Cfg.DeliveryCooldownSeconds = cooldown; SaveConfig();
                        Reply(caller, "Delivery cooldown and new reward payout delay = " + cooldown.ToString("F0", CultureInfo.InvariantCulture) + " s."); break;
                    case "debug":
                        if (a1 != "on" && a1 != "off") { Reply(caller, "Usage: debug on|off"); return; }
                        Cfg.Debug = a1 == "on"; SaveConfig();
                        Reply(caller, "Debug " + a1 + "."); break;
                    case "reload":
                        LoadConfig(); Reply(caller, "Config reloaded from " + ConfigPath + "."); break;
                    default: ShowHelp(caller); break;
                }
            }
            catch (Exception exception) { DebugWarning("Logistics command: " + exception); }
        }

        private static void ShowStatus(Player caller)
        {
            Reply(caller, "Logistics " + (Cfg.Enabled ? "ON" : "OFF") +
                " | load " + Cfg.LoadUnitsPerSecond + "/s within " + Cfg.LoadRadius.ToString("F0", CultureInfo.InvariantCulture) + " m" +
                " | unload " + Cfg.UnloadUnitsPerSecond + "/s within " + Cfg.UnloadRadius.ToString("F0", CultureInfo.InvariantCulture) + " m" +
                " | air rates " + Cfg.AirLoadUnitsPerSecond + "/" + Cfg.AirUnloadUnitsPerSecond + "/s" +
                " | air " + Cfg.AirCargoCapacity + ", ground " + Cfg.GroundCargoCapacity +
                " | cooldown " + Cfg.DeliveryCooldownSeconds.ToString("F0", CultureInfo.InvariantCulture) + " s" +
                " | rewards " + RewardSettings() + (KohCredits.Available ? " (KoH linked)" : " (KoH not found)"));
            ShowHarvesterStatus(caller);
            ShowAITransportStatus(caller);
            Reply(caller, "Logistics score " + (Cfg.LogisticsScore ? "ON" : "OFF") +
                " | unload " + Cfg.ScoreUnload + " | transfer " + Cfg.ScoreTransfer + " points per 100 Balterium" +
                (Cfg.Enabled ? "." : " (master Logistics is OFF)."));
            Reply(caller, "Commander score " + (Cfg.CommanderScore.Enabled ? "ON" : "OFF") +
                " | active " + (CommanderScoringActive ? "YES" : "NO") +
                " | KoH purchase exclusion " + (CommanderPurchaseHookReady ? "ready" : "unavailable; scoring blocked") + ".");
            Unit unit = caller != null ? caller.ControlledUnit : null;
            if (unit == null || !IsTransport(unit)) return;
            int amount = CargoByTransport.TryGetValue(unit.GetInstanceID(), out Cargo cargo) ? cargo.Amount : 0;
            Reply(caller, "Your " + unit.ObjectInfo.DisplayName + ": " + amount + "/" + CapacityFor(unit) + " Balterium aboard.");
        }

        private static void ShowHelp(Player caller)
        {
            Reply(caller, "/logistics or /logi: [status|help]. Status is available to everyone.");
            Reply(caller, "Admin: on|off; share <0..1>; transfershare <0..1>; capacity air|ground <positive integer>.");
            Reply(caller, "Admin: rate load|unload <positive integer> (ground); radius load|unload <metres> (player transfers).");
            Reply(caller, "Admin: cooldown <seconds>; debug on|off; reload; harvesterfix on|off; aitransport on|off.");
            Reply(caller, "Admin: /logisticsscore on|off; /scoreunload <0..100>; /scoretransfer <0..100> (points per 100 Balterium).");
            Reply(caller, "Admin: /logisticsscorecomander on|off; edit CommanderScore.Rules in config, then /logistics reload.");
        }

        private static bool TryParseFloat(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);

        // caller == null: server console.
        private static void Reply(Player caller, string text)
        {
            if (caller != null) HelperMethods.SendChatMessageToPlayer(caller, HelperMethods.chatPrefix, " " + text);
            else DebugMsg(text);
        }

        public override void OnUpdate()
        {
            if (!Game.GetIsServer()) return;
            UpdateHarvesterFix();
            // Earned rewards are retained while the master switch pauses payouts.
            try { PayDueRewards(); }
            catch (Exception exception) { DebugWarning("Logistics payout: " + exception.Message); }
            if (!AITransportActive && AITransportJobs.Count > 0) ResetAITransports(true);
            if (!Cfg.Enabled || !MatchAcceptsRewards) return;
            _elapsed += Time.deltaTime;
            if (_elapsed < 1f) return;
            _elapsed = 0f;
            try { Tick(); }
            catch (Exception exception) { DebugError("Logistics tick: " + exception); }
        }

        private static bool IsTransport(Unit unit)
        {
            if (unit == null || unit.DriverCompartment == null ||
                unit.DamageManager == null || unit.DamageManager.IsDestroyed || unit.Team == null ||
                unit.ObjectInfo == null) return false;
            string name = unit.ObjectInfo.DisplayName.ToString();
            // Confirm actual display names against server logs before enabling additional vehicles.
            return Cfg.TransportUnits.Exists(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private static void TickCore()
        {
            if (Unit.Units == null || Structure.Structures == null) return;
            var active = TickActiveVehicles; active.Clear();
            foreach (Unit transport in TickTransports)
            {
                if (Cfg.Debug && transport != null && transport.ControlledBy != null && transport.DriverCompartment != null && transport.ObjectInfo != null)
                {
                    string candidate = transport.ObjectInfo.DisplayName.ToString();
                    if (LoggedDriverVehicles.Add(candidate)) DebugMsg("Logistics diagnostic vehicle name: " + candidate);
                }
                if (!IsTransport(transport)) continue;
                int vehicleId = transport.GetInstanceID();
                active.Add(vehicleId);
                if (TransportErrorUntil.TryGetValue(vehicleId, out float retryAt) && Time.unscaledTime < retryAt) continue;
                try
                {
                    // Assigned AI jobs must run before the player-only transfer gate.
                    if (AITransportJobs.TryGetValue(vehicleId, out var aiJob))
                    {
                        if (TickAITransport(aiJob)) continue;
                    }
                    if (!Cfg.Enabled || transport.ControlledBy == null)
                    {
                        if (CargoByTransport.TryGetValue(vehicleId, out Cargo unattended)) SetTransfer(transport, unattended, null);
                        continue;
                    }
                    if (!CargoByTransport.TryGetValue(vehicleId, out Cargo cargo))
                    {
                        cargo = new Cargo();
                        CargoByTransport.Add(vehicleId, cargo);
                    }
                    if (Cfg.Debug) DiagnoseNearby(transport, cargo);
                    if (Time.unscaledTime < cargo.CooldownUntil) { SetTransfer(transport, cargo, null); continue; }
                    if (cargo.Amount > 0 && TryUnload(transport, cargo)) continue;
                    if (cargo.Amount < CapacityFor(transport) && TryLoad(transport, cargo)) continue;
                    SetTransfer(transport, cargo, null);
                }
                catch (Exception error)
                {
                    TransportErrorUntil[vehicleId] = Time.unscaledTime + 10f;
                    DebugError("Logistics transport=" + vehicleId + " update failed; retry in 10s: " + error);
                }
            }
            // Cargo on a destroyed vehicle is lost, like a destroyed harvester's cargo.
            var stale = TickStaleCargo; stale.Clear();
            foreach (var pair in CargoByTransport)
                if (!active.Contains(pair.Key)) stale.Add(pair.Key);
            foreach (int vehicleId in stale) CargoByTransport.Remove(vehicleId);
            TickStaleErrors.Clear();
            foreach (var pair in TransportErrorUntil) if (!active.Contains(pair.Key)) TickStaleErrors.Add(pair.Key);
            foreach (int id in TickStaleErrors) TransportErrorUntil.Remove(id);
            TickJobSnapshot.Clear();
            foreach (var pair in AITransportJobs) TickJobSnapshot.Add(pair);
            foreach (var pair in TickJobSnapshot)
                if (!active.Contains(pair.Key)) CancelAITransport(pair.Value, "transport destroyed or no longer eligible", false);
        }

        private static void DiagnoseNearby(Unit transport, Cargo cargo)
        {
            if (Time.time - cargo.LastDiagnostic < 5f) return;
            cargo.LastDiagnostic = Time.time;
            Unit nearestHolder = null;
            float holderDistance = float.MaxValue;
            int friendlyHarvesterCount = 0;
            foreach (Unit unit in Unit.Units)
            {
                if (unit == null || unit.Team != transport.Team || unit.ObjectInfo == null) continue;
                if (unit.ObjectInfo.UnitType == UnitType.Harvester) friendlyHarvesterCount++;
                if (!unit.IsResourceHolder) continue;
                float distance = Vector3.Distance(unit.transform.position, transport.transform.position);
                if (distance < holderDistance) { nearestHolder = unit; holderDistance = distance; }
            }
            Structure nearestRefinery = null;
            float refineryDistance = float.MaxValue;
            foreach (Structure structure in Structure.Structures)
            {
                if (structure == null || structure.Team != transport.Team || structure.ObjectInfo == null ||
                    structure.ObjectInfo.DisplayName.ToString().IndexOf("Refinery", StringComparison.OrdinalIgnoreCase) < 0) continue;
                float distance = Vector3.Distance(structure.transform.position, transport.transform.position);
                if (distance < refineryDistance) { nearestRefinery = structure; refineryDistance = distance; }
            }
            string holderInfo = nearestHolder == null ? "none" :
                nearestHolder.ObjectInfo.DisplayName + " distance=" + holderDistance.ToString("F1") +
                " stored=" + nearestHolder.GetStoredResources(transport.Team.UsableResource) +
                " holderCount=" + (nearestHolder.ResourceHolders == null ? 0 : nearestHolder.ResourceHolders.Count) +
                " type=" + nearestHolder.ObjectInfo.UnitType;
            string refineryInfo = nearestRefinery == null ? "none" :
                nearestRefinery.ObjectInfo.DisplayName + " distance=" + refineryDistance.ToString("F1") +
                " depositPoints=" + (nearestRefinery.ResourceDepositPoints == null ? 0 : nearestRefinery.ResourceDepositPoints.Count);
            DebugMsg("Logistics probe " + transport.ObjectInfo.DisplayName +
                " cargo=" + cargo.Amount + " friendlyHarvesters=" + friendlyHarvesterCount +
                " nearestHolder=[" + holderInfo + "] nearestRefinery=[" + refineryInfo + "]");
        }

        private static int CapacityFor(Unit transport)
        {
            return Math.Max(1, transport.IsFlyingType ? Cfg.AirCargoCapacity : Cfg.GroundCargoCapacity);
        }

        // True while loading continues: something was loaded, or an active loading session still has a
        // friendly harvester in range (an empty harvester pauses loading instead of ending it).
        private static bool TryLoad(Unit transport, Cargo cargo, Unit assignedHarvester = null, float? loadRadius = null)
        {
            bool harvesterInRange = false;
            int count = assignedHarvester ? 1 : TickHarvesters.Count;
            for (int i = 0; i < count; i++)
            {
                Unit harvester = assignedHarvester ? assignedHarvester : TickHarvesters[i];
                if ((assignedHarvester != null && harvester != assignedHarvester) || harvester == null || harvester.ObjectInfo == null || harvester.ObjectInfo.UnitType != UnitType.Harvester ||
                    harvester.Team != transport.Team || harvester.DamageManager == null ||
                    harvester.DamageManager.IsDestroyed || !harvester.IsResourceHolder ||
                    Vector3.Distance(harvester.transform.position, transport.transform.position) > (loadRadius ?? Cfg.LoadRadius)) continue;

                harvesterInRange = true;
                var resource = transport.Team.UsableResource;
                int before = harvester.GetStoredResources(resource);
                if (before == 0) continue;
                int requested = Math.Min(Math.Min(before, Math.Max(1, transport.IsFlyingType ? Cfg.AirLoadUnitsPerSecond : Cfg.LoadUnitsPerSecond)),
                                         Math.Max(0, CapacityFor(transport) - cargo.Amount));
                if (requested <= 0) continue;
                Player harvesterDriver = DriverOf(harvester);
                Player transportDriver = DriverOf(transport);
                decimal rewardRate = HarvesterTransferRate;
                int rewardEpoch = RewardEpoch;
                harvester.RetrieveResource(resource, requested);
                int after = harvester.GetStoredResources(resource);
                int removed = Math.Max(0, before - after);
                if (removed > requested)
                {
                    harvester.StoreResource(resource, removed - requested);
                    removed = requested;
                }
                cargo.Amount += removed;
                if (removed > 0)
                {
                    HarvesterTransportPickup(harvester);
                    if (transportDriver != null)
                    {
                        QueueReward(harvesterDriver, removed, rewardRate, rewardEpoch);
                        if (Connected(transportDriver, SteamId(transportDriver)))
                            AwardScore(harvesterDriver, removed, Cfg.ScoreTransfer, true, rewardEpoch);
                    }
                    SetTransfer(transport, cargo, "Loading", 0, CapacityFor(transport));
                    Progress(transport, cargo);
                    if (Cfg.Debug) DebugMsg("Logistics LOAD " + removed + " from " + harvester.ObjectInfo.DisplayName + "; cargo=" + cargo.Amount);
                    bool loaded = cargo.Amount >= CapacityFor(transport);
                    PlaySound(transport, loaded ? Cfg.Sounds.OnFull : Cfg.Sounds.OnLoad);
                    if (loaded) SetTransfer(transport, cargo, null);
                }
                if (removed > 0) return true;
            }
            return harvesterInRange && cargo.Transfer == "Loading";
        }

        private static bool TryUnload(Unit transport, Cargo cargo, Structure assignedRefinery = null, float? unloadRadius = null)
        {
            int count = assignedRefinery ? 1 : TickRefineries.Count;
            for (int i = 0; i < count; i++)
            {
                Structure refinery = assignedRefinery ? assignedRefinery : TickRefineries[i];
                if ((assignedRefinery != null && refinery != assignedRefinery) || refinery == null || refinery.Team != transport.Team ||
                    refinery.ResourceDepositPoints == null || refinery.ResourceDepositPoints.Count == 0 ||
                    refinery.DamageManager == null || refinery.DamageManager.IsDestroyed ||
                    Vector3.Distance(refinery.transform.position, transport.transform.position) > (unloadRadius ?? Cfg.UnloadRadius)) continue;
                int attempted = Math.Min(Math.Min(cargo.Amount, Math.Max(1, transport.IsFlyingType ? Cfg.AirUnloadUnitsPerSecond : Cfg.UnloadUnitsPerSecond)),
                                         Math.Max(0, transport.Team.RemainingResourceCapacity));
                if (attempted <= 0) return false;
                Player driver = DriverOf(transport);
                decimal rewardRate = RewardRate;
                int rewardEpoch = RewardEpoch;
                SetTransfer(transport, cargo, "Unloading", cargo.Amount, cargo.Amount);
                // StoreResource returns the rejected remainder, not the amount deposited.
                int rejected = transport.Team.StoreResource(attempted);
                int deposited = Math.Max(0, attempted - Math.Max(0, rejected));
                cargo.Amount -= deposited;
                if (deposited > 0)
                {
                    QueueReward(driver, deposited, rewardRate, rewardEpoch);
                    AwardScore(driver, deposited, Cfg.ScoreUnload, false, rewardEpoch);
                    CommanderDeposit(transport.Team, deposited, rewardEpoch);
                    if (Cfg.Debug) DebugMsg("Logistics UNLOAD " + deposited + "; cargo=" + cargo.Amount);
                    Progress(transport, cargo);
                    if (cargo.Amount == 0)
                    {
                        cargo.CooldownUntil = Time.unscaledTime + Math.Max(0f, Cfg.DeliveryCooldownSeconds);
                        SetTransfer(transport, cargo, null);
                    }
                    else if (transport.Team.RemainingResourceCapacity <= 0) SetTransfer(transport, cargo, null);
                }
                return deposited > 0;
            }
            return false;
        }

        // Reflection bridge to Si_KingOfTheHill.KingOfTheHill.GrantExternalCredits(long,int,string) and
        // GetCreditsBySteamId(long); no compile-time dependency. KoH returns false while its /buy
        // economy is inactive, so the player batch is retried after another 10 seconds.
        private static class KohCredits
        {
            private static MethodInfo _grant, _balance;
            private static bool _missingLogged;

            public static bool Available => Resolve();

            private static bool Resolve()
            {
                if (_grant != null) return true;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type type;
                    try { type = assembly.GetType("Si_KingOfTheHill.KingOfTheHill"); } catch { continue; }
                    if (type == null) continue;
                    _grant = type.GetMethod("GrantExternalCredits", BindingFlags.Public | BindingFlags.Static, null,
                                            new[] { typeof(long), typeof(int), typeof(string) }, null);
                    _balance = type.GetMethod("GetCreditsBySteamId", BindingFlags.Public | BindingFlags.Static, null,
                                              new[] { typeof(long) }, null);
                    break;
                }
                if (_grant == null && !_missingLogged)
                {
                    DebugMsg("Logistics: Si_KingOfTheHill credit API not found; personal bonus inactive.");
                    _missingLogged = true;
                }
                return _grant != null;
            }

            public enum GrantResult { Granted, Unavailable, Uncertain }
            public static GrantResult TryGrant(long steamId, int amount, string reason)
            {
                if (!Resolve()) return GrantResult.Unavailable;
                try { return (bool)_grant.Invoke(null, new object[] { steamId, amount, reason }) ? GrantResult.Granted : GrantResult.Unavailable; }
                catch (Exception exception) { DebugWarning("Logistics: KoH credit result uncertain; attempted batch will NOT be retried to avoid duplicate credits: " + exception.Message); return GrantResult.Uncertain; }
            }

            public static int TryGetBalance(long steamId)
            {
                if (!Resolve() || _balance == null) return -1;
                try { return (int)_balance.Invoke(null, new object[] { steamId }); }
                catch { return -1; }
            }
        }

        private static void PlaySound(Unit transport, string filename)
        {
            if (!Cfg.Sounds.Enabled || string.IsNullOrEmpty(filename) || transport.ControlledBy == null) return;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", "sounds", filename);
            if (File.Exists(path)) _ = AudioHelper.PlaySoundFile(path, transport.ControlledBy);
        }

        // Chat: the total is announced once when a transfer starts; afterwards only a progress bar
        // with percent, every Chat.ProgressIntervalSeconds, and a final bar when it stops.
        private static void SetTransfer(Unit transport, Cargo cargo, string transfer, int from = 0, int total = 0)
        {
            if (cargo.Transfer == transfer) return;
            string previous = cargo.Transfer;
            Player driver = transport.ControlledBy;
            if (previous != null)
                DebugMsg("Logistics: " + (driver != null ? driver.PlayerName : "no driver") + " " +
                    transport.ObjectInfo.DisplayName + " " + previous.ToLowerInvariant() + " ended at " + TransferPercent(cargo) +
                    "%, cargo " + cargo.Amount + "/" + CapacityFor(transport) +
                    (previous == "Unloading" ? ", delivered " + (cargo.TransferFrom - cargo.Amount) : ""));
            if (previous != null && driver != null)
            {
                int percent = TransferPercent(cargo);
                HelperMethods.SendChatMessageToPlayer(driver, HelperMethods.chatPrefix,
                    " " + previous + (TransferComplete(cargo) ? " complete " : " stopped ") + Bar(percent) +
                    (Time.unscaledTime < cargo.CooldownUntil ? " Transfer cooldown: " + Cfg.DeliveryCooldownSeconds + " seconds." : ""));
            }
            cargo.Transfer = transfer;
            if (transfer == null) return;
            cargo.TransferFrom = from;
            cargo.TransferTotal = Math.Max(1, total);
            cargo.NextProgress = Time.unscaledTime + Cfg.Chat.ProgressIntervalSeconds;
            cargo.LastPercent = -1;
            if (driver != null)
                HelperMethods.SendChatMessageToPlayer(driver, HelperMethods.chatPrefix,
                    transfer == "Loading" ? " Loading started: cargo " + cargo.Amount + "/" + total + " Balterium."
                                          : " Unloading started: " + total + " Balterium to deliver. " + Bar(100) + " remaining.");
        }

        private static void Progress(Unit transport, Cargo cargo)
        {
            if (cargo.Transfer == null || transport.ControlledBy == null || Cfg.Chat.ProgressIntervalSeconds <= 0f ||
                Time.unscaledTime < cargo.NextProgress) return;
            int percent = TransferPercent(cargo);
            if (TransferComplete(cargo) || percent == cargo.LastPercent) return; // stop message shows completion
            cargo.LastPercent = percent;
            cargo.NextProgress = Time.unscaledTime + Cfg.Chat.ProgressIntervalSeconds;
            HelperMethods.SendChatMessageToPlayer(transport.ControlledBy, HelperMethods.chatPrefix,
                " " + cargo.Transfer + " " + Bar(percent));
        }

        private static int TransferPercent(Cargo cargo)
        {
            if (cargo.Transfer == "Unloading")
                return Math.Min(100, (int)((100L * Math.Max(0, cargo.Amount) + Math.Max(1, cargo.TransferTotal) - 1) / Math.Max(1, cargo.TransferTotal)));
            int done = Math.Abs(cargo.Amount - cargo.TransferFrom);
            return Math.Min(100, (int)(100L * done / Math.Max(1, cargo.TransferTotal)));
        }

        private static bool TransferComplete(Cargo cargo) => cargo.Transfer == "Unloading"
            ? cargo.Amount == 0 : cargo.Amount >= cargo.TransferTotal;

        private static string Bar(int percent)
        {
            int length = Math.Max(1, Math.Min(40, Cfg.Chat.ProgressBarLength));
            int filled = percent * length / 100;
            var text = new System.Text.StringBuilder("[");
            for (int i = 0; i < length; i++) text.Append(i < filled ? Cfg.Chat.BarFilled : Cfg.Chat.BarEmpty);
            return text.Append("] ").Append(percent).Append('%').ToString();
        }
    }
}
