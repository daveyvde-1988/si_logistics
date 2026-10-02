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

[assembly: MelonInfo(typeof(Si_Logistics.LogisticsMod), "Harvester Logistics Prototype", "0.4.1", "Silica community prototype")]
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
        // LOAD/UNLOAD line per second. Off: one log line per finished load or delivery.
        public bool Debug = false;
        public List<string> TransportUnits = new List<string> { "Shuttle", "Dropship", "Squad Transport", "Platoon Hauler" };
        public int AirCargoCapacity = 9000;
        public int GroundCargoCapacity = 3000;
        public int LoadUnitsPerSecond = 300;
        public int UnloadUnitsPerSecond = 300;
        public float LoadRadius = 40f;
        public float UnloadRadius = 65f;
        public float DeliveryCooldownSeconds = 20f;
        public BonusConfig PersonalBonus = new BonusConfig();
        public ChatConfig Chat = new ChatConfig();
        public SoundConfig Sounds = new SoundConfig();
        public CommandConfig Commands = new CommandConfig();

        public sealed class CommandConfig
        {
            public string PlayerCommand = "logistics";
            public List<string> Aliases = new List<string> { "logi" };
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
            public bool Enabled = true;
            // Share of every delivered Balterium unit that becomes personal King of the Hill credits
            // (spendable with /buy) for the driver who unloads. 0.05 = 5 %: 9,000 delivered pays 450.
            public float Share = 0.05f;
            // Missing shares inherit the old rates once at config load, then remain independent.
            public float? HarvesterDepositShare;
            public float? HarvesterTransferShare;
            public float PayoutDelaySeconds = 20f;
            // Legacy configuration key retained for compatibility; not a payout threshold.
            public int MinBatch = 100;
            public bool AnnounceToPlayer = true;
        }

        public sealed class SoundConfig
        {
            public bool Enabled = false;
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

        public override void OnInitializeMelon()
        {
            LoadConfig();
            MelonLogger.Msg("Harvester Logistics config " + ConfigPath + ": enabled=" + Cfg.Enabled +
                ", vehicles=" + string.Join(", ", Cfg.TransportUnits) +
                ", capacity air/ground=" + Cfg.AirCargoCapacity + "/" + Cfg.GroundCargoCapacity +
                ", load/unload per s=" + Cfg.LoadUnitsPerSecond + "/" + Cfg.UnloadUnitsPerSecond +
                ", radius load/unload=" + Cfg.LoadRadius + "/" + Cfg.UnloadRadius +
                ", cooldown=" + Cfg.DeliveryCooldownSeconds + "s, debug=" + Cfg.Debug + ", sounds=" + Cfg.Sounds.Enabled +
                ", personal bonus=" + (Cfg.PersonalBonus.Enabled ? RewardSettings() + " as KoH credits" : "off"));
        }

        private static void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    // Replace: the default (Auto) appends file lists to the initializer defaults,
                    // which doubled TransportUnits in the 0.4.0 config.
                    var settings = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
                    Cfg = JsonConvert.DeserializeObject<LogisticsConfig>(File.ReadAllText(ConfigPath), settings) ?? new LogisticsConfig();
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                    File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Cfg, Formatting.Indented));
                    MelonLogger.Msg("Logistics: wrote default config " + ConfigPath);
                }
            }
            catch (Exception exception)
            {
                MelonLogger.Error("Logistics: config " + ConfigPath + " unreadable, using defaults: " + exception.Message);
                Cfg = new LogisticsConfig();
            }
            if (Cfg.TransportUnits == null) Cfg.TransportUnits = new List<string>();
            if (Cfg.Sounds == null) Cfg.Sounds = new LogisticsConfig.SoundConfig();
            if (Cfg.PersonalBonus == null) Cfg.PersonalBonus = new LogisticsConfig.BonusConfig();
            if (Cfg.Chat == null) Cfg.Chat = new LogisticsConfig.ChatConfig();
            if (Cfg.Commands == null) Cfg.Commands = new LogisticsConfig.CommandConfig();
            if (Cfg.Commands.Aliases == null) Cfg.Commands.Aliases = new List<string>();
            Cfg.PersonalBonus.Share = float.IsNaN(Cfg.PersonalBonus.Share) || float.IsInfinity(Cfg.PersonalBonus.Share)
                ? 0.05f : Mathf.Clamp01(Cfg.PersonalBonus.Share);
            Cfg.PersonalBonus.HarvesterDepositShare = ValidRewardShare(
                Cfg.PersonalBonus.HarvesterDepositShare, Cfg.PersonalBonus.Share * 2f);
            Cfg.PersonalBonus.HarvesterTransferShare = ValidRewardShare(
                Cfg.PersonalBonus.HarvesterTransferShare, Cfg.PersonalBonus.Share);
            float delay = Cfg.PersonalBonus.PayoutDelaySeconds;
            Cfg.PersonalBonus.PayoutDelaySeconds = float.IsNaN(delay) || float.IsInfinity(delay)
                ? 20f : Mathf.Clamp(delay, 1f, 3600f);
            var unique = new List<string>();
            foreach (string name in Cfg.TransportUnits)
                if (!string.IsNullOrWhiteSpace(name) && !unique.Exists(n => n.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    unique.Add(name.Trim());
            Cfg.TransportUnits = unique;
        }

        private static float ValidRewardShare(float? value, float fallback)
        {
            float share = value ?? fallback;
            return float.IsNaN(share) || float.IsInfinity(share)
                ? Mathf.Clamp01(fallback) : Mathf.Clamp01(share);
        }

        private static string RewardSettings() =>
            "transport " + Cfg.PersonalBonus.Share.ToString("P1", CultureInfo.InvariantCulture) +
            ", harvester refinery " + Cfg.PersonalBonus.HarvesterDepositShare.Value.ToString("P1", CultureInfo.InvariantCulture) +
            ", harvester transfer " + Cfg.PersonalBonus.HarvesterTransferShare.Value.ToString("P1", CultureInfo.InvariantCulture) +
            ", payout " + Cfg.PersonalBonus.PayoutDelaySeconds.ToString("0.##", CultureInfo.InvariantCulture) + "s";

        private static void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Cfg, Formatting.Indented));
            }
            catch (Exception exception) { MelonLogger.Warning("Logistics: could not save config: " + exception.Message); }
        }

        public override void OnLateInitializeMelon()
        {
            InstallHarvesterRewards();
            BindRewardEvents();
            // AdminMod's command lists exist only after its own initialization.
            var names = new List<string> { Cfg.Commands.PlayerCommand };
            names.AddRange(Cfg.Commands.Aliases);
            var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
                if (!string.IsNullOrWhiteSpace(name) && registered.Add(name.Trim()))
                    HelperMethods.RegisterPlayerCommand(name.Trim(), OnLogisticsCommand, Cfg.Commands.HideFromChat);
        }

        // "/logistics [status|help]" for everyone; admin (SAM Generic):
        // on|off, bonus on|off, share <0..1>, capacity air|ground <n>, rate load|unload <n>,
        // radius load|unload <m>, cooldown <s>, debug on|off, reload. Changes are saved to the JSON.
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
                    Reply(caller, "Admin only. Use /" + Cfg.Commands.PlayerCommand + " for status.");
                    return;
                }

                switch (sub)
                {
                    case "on":
                    case "off":
                        Cfg.Enabled = sub == "on"; SaveConfig();
                        Reply(caller, "Logistics " + (Cfg.Enabled ? "enabled." : "disabled.")); break;
                    case "bonus":
                        if (a1 != "on" && a1 != "off") { Reply(caller, "Usage: bonus on|off"); return; }
                        Cfg.PersonalBonus.Enabled = a1 == "on"; SaveConfig();
                        Reply(caller, "Personal bonus " + a1 + "."); break;
                    case "share":
                        if (!TryParseFloat(a1, out float share) || float.IsNaN(share) || float.IsInfinity(share) || share < 0f || share > 1f) { Reply(caller, "Usage: share <0..1>"); return; }
                        Cfg.PersonalBonus.Share = share; SaveConfig();
                        Reply(caller, "Transport unload bonus share = " + share.ToString("P1", CultureInfo.InvariantCulture) + "."); break;
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
                        Reply(caller, "Delivery cooldown = " + cooldown.ToString("F0", CultureInfo.InvariantCulture) + " s."); break;
                    case "debug":
                        if (a1 != "on" && a1 != "off") { Reply(caller, "Usage: debug on|off"); return; }
                        Cfg.Debug = a1 == "on"; SaveConfig();
                        Reply(caller, "Debug " + a1 + "."); break;
                    case "reload":
                        LoadConfig(); Reply(caller, "Config reloaded from " + ConfigPath + "."); break;
                    default: ShowHelp(caller); break;
                }
            }
            catch (Exception exception) { MelonLogger.Warning("Logistics command: " + exception); }
        }

        private static void ShowStatus(Player caller)
        {
            Reply(caller, "Logistics " + (Cfg.Enabled ? "ON" : "OFF") +
                " | load " + Cfg.LoadUnitsPerSecond + "/s within " + Cfg.LoadRadius.ToString("F0", CultureInfo.InvariantCulture) + " m" +
                " | unload " + Cfg.UnloadUnitsPerSecond + "/s within " + Cfg.UnloadRadius.ToString("F0", CultureInfo.InvariantCulture) + " m" +
                " | air " + Cfg.AirCargoCapacity + ", ground " + Cfg.GroundCargoCapacity +
                " | cooldown " + Cfg.DeliveryCooldownSeconds.ToString("F0", CultureInfo.InvariantCulture) + " s" +
                " | bonus " + (Cfg.PersonalBonus.Enabled ? RewardSettings() +
                    (KohCredits.Available ? " (KoH linked)" : " (KoH not found)") : "off"));
            Unit unit = caller != null ? caller.ControlledUnit : null;
            if (unit == null || !IsTransport(unit)) return;
            int amount = CargoByTransport.TryGetValue(unit.GetInstanceID(), out Cargo cargo) ? cargo.Amount : 0;
            Reply(caller, "Your " + unit.ObjectInfo.DisplayName + ": " + amount + "/" + CapacityFor(unit) + " Balterium aboard.");
        }

        private static void ShowHelp(Player caller)
        {
            string command = "/" + Cfg.Commands.PlayerCommand;
            Reply(caller, command + " [status|help]  |  admin: " + command +
                " on|off, bonus on|off, share <0..1>, capacity air|ground <n>, rate load|unload <n>, radius load|unload <m>, cooldown <s>, debug on|off, reload");
        }

        private static bool TryParseFloat(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        // caller == null: server console.
        private static void Reply(Player caller, string text)
        {
            if (caller != null) HelperMethods.SendChatMessageToPlayer(caller, HelperMethods.chatPrefix, " " + text);
            else MelonLogger.Msg(text);
        }

        public override void OnUpdate()
        {
            if (!Game.GetIsServer()) return;
            // Earned rewards still mature if an admin disables new transfers/bonuses.
            try { PayDueRewards(); }
            catch (Exception exception) { MelonLogger.Warning("Logistics payout: " + exception.Message); }
            if (!Cfg.Enabled || !MatchAcceptsRewards) return;
            _elapsed += Time.deltaTime;
            if (_elapsed < 1f) return;
            _elapsed = 0f;
            try { Tick(); }
            catch (Exception exception) { MelonLogger.Error("Logistics tick: " + exception); }
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

        private static void Tick()
        {
            if (Unit.Units == null || Structure.Structures == null) return;
            var active = new HashSet<int>();
            foreach (Unit transport in Unit.Units)
            {
                if (Cfg.Debug && transport != null && transport.ControlledBy != null && transport.DriverCompartment != null && transport.ObjectInfo != null)
                {
                    string candidate = transport.ObjectInfo.DisplayName.ToString();
                    if (LoggedDriverVehicles.Add(candidate)) MelonLogger.Msg("Logistics diagnostic vehicle name: " + candidate);
                }
                if (!IsTransport(transport)) continue;
                int vehicleId = transport.GetInstanceID();
                active.Add(vehicleId);
                if (transport.ControlledBy == null)
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
            // Cargo on a destroyed vehicle is lost, like a destroyed harvester's cargo.
            var stale = new List<int>();
            foreach (var pair in CargoByTransport)
                if (!active.Contains(pair.Key)) stale.Add(pair.Key);
            foreach (int vehicleId in stale) CargoByTransport.Remove(vehicleId);
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
            MelonLogger.Msg("Logistics probe " + transport.ObjectInfo.DisplayName +
                " cargo=" + cargo.Amount + " friendlyHarvesters=" + friendlyHarvesterCount +
                " nearestHolder=[" + holderInfo + "] nearestRefinery=[" + refineryInfo + "]");
        }

        private static int CapacityFor(Unit transport)
        {
            return Math.Max(1, transport.IsFlyingType ? Cfg.AirCargoCapacity : Cfg.GroundCargoCapacity);
        }

        // True while loading continues: something was loaded, or an active loading session still has a
        // friendly harvester in range (an empty harvester pauses loading instead of ending it).
        private static bool TryLoad(Unit transport, Cargo cargo)
        {
            bool harvesterInRange = false;
            foreach (Unit harvester in Unit.Units)
            {
                if (harvester == null || harvester.ObjectInfo == null || harvester.ObjectInfo.UnitType != UnitType.Harvester ||
                    harvester.Team != transport.Team || harvester.DamageManager == null ||
                    harvester.DamageManager.IsDestroyed || !harvester.IsResourceHolder ||
                    Vector3.Distance(harvester.transform.position, transport.transform.position) > Cfg.LoadRadius) continue;

                harvesterInRange = true;
                var resource = transport.Team.UsableResource;
                int before = harvester.GetStoredResources(resource);
                if (before == 0) continue;
                int requested = Math.Min(Math.Min(before, Math.Max(1, Cfg.LoadUnitsPerSecond)),
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
                    if (transportDriver != null) QueueReward(harvesterDriver, removed, rewardRate, rewardEpoch);
                    SetTransfer(transport, cargo, "Loading", 0, CapacityFor(transport));
                    Progress(transport, cargo);
                    if (Cfg.Debug) MelonLogger.Msg("Logistics LOAD " + removed + " from " + harvester.ObjectInfo.DisplayName + "; cargo=" + cargo.Amount);
                    bool loaded = cargo.Amount >= CapacityFor(transport);
                    PlaySound(transport, loaded ? Cfg.Sounds.OnFull : Cfg.Sounds.OnLoad);
                    if (loaded) SetTransfer(transport, cargo, null);
                }
                if (removed > 0) return true;
            }
            return harvesterInRange && cargo.Transfer == "Loading";
        }

        private static bool TryUnload(Unit transport, Cargo cargo)
        {
            foreach (Structure refinery in Structure.Structures)
            {
                if (refinery == null || refinery.Team != transport.Team ||
                    refinery.ResourceDepositPoints == null || refinery.ResourceDepositPoints.Count == 0 ||
                    refinery.DamageManager == null || refinery.DamageManager.IsDestroyed ||
                    Vector3.Distance(refinery.transform.position, transport.transform.position) > Cfg.UnloadRadius) continue;
                int attempted = Math.Min(Math.Min(cargo.Amount, Math.Max(1, Cfg.UnloadUnitsPerSecond)),
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
                    if (Cfg.Debug) MelonLogger.Msg("Logistics UNLOAD " + deposited + "; cargo=" + cargo.Amount);
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
                    MelonLogger.Msg("Logistics: Si_KingOfTheHill credit API not found; personal bonus inactive.");
                    _missingLogged = true;
                }
                return _grant != null;
            }

            public enum GrantResult { Granted, Unavailable, Uncertain }
            public static GrantResult TryGrant(long steamId, int amount, string reason)
            {
                if (!Resolve()) return GrantResult.Unavailable;
                try { return (bool)_grant.Invoke(null, new object[] { steamId, amount, reason }) ? GrantResult.Granted : GrantResult.Unavailable; }
                catch (Exception exception) { MelonLogger.Warning("Logistics: KoH credit result uncertain; attempted batch will NOT be retried to avoid duplicate credits: " + exception.Message); return GrantResult.Uncertain; }
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
                MelonLogger.Msg("Logistics: " + (driver != null ? driver.PlayerName : "no driver") + " " +
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
