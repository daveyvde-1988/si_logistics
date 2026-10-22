using System.Globalization;
using ModUI;
using System;
using System.Collections.Generic;
namespace Si_Logistics
{
    public sealed partial class LogisticsMod
    {
        // Shared ModUI data is server-wide. Count enrolled duty, including travelling
        // and waiting transports, while excluding stale jobs awaiting the next tick.
        private static string LogisticsDutySummary()
        {
            int total = 0;
            var types = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (AITransportActive)
                foreach (var job in AITransportJobs.Values)
                {
                    var transport = job.Transport;
                    if (!AITransportEligible(transport) || transport.Team != job.Team) continue;
                    string name = transport.ObjectInfo.DisplayName.ToString();
                    types.TryGetValue(name, out int count);
                    types[name] = count + 1;
                    total++;
                }
            if (total == 0) return "0";
            var details = new List<string>();
            foreach (var type in types)
                details.Add(type.Key + ": " + type.Value.ToString(CultureInfo.InvariantCulture));
            return total.ToString(CultureInfo.InvariantCulture) + " (" + string.Join(", ", details) + ")";
        }

        private static int LogisticsRefineryCount()
        {
            int count = 0;
            if (Structure.Structures != null)
                foreach (var refinery in Structure.Structures)
                    if (refinery && refinery.Team && AIRefineryTarget(refinery, refinery.Team)) count++;
            return count;
        }

        public static string ModUIData()
        {
            string F(double v)=>v.ToString("0.######",CultureInfo.InvariantCulture);
            return ModUIExport.New()
                .Info("Carry Balterium from friendly harvesters to refineries using ground and air transports. Automate transport deliveries and harvester routing, and earn credit and score rewards for resource transfers and deliveries. Includes configurable commander scoring.")
                .Status("master","Logistics",Cfg.Enabled?"ON":"OFF")
                .Status("harvesterfix","Harvester recovery",Cfg.HarvesterFix.Enabled?"ON":"OFF")
                .Status("aitransport","AI transport",Cfg.AITransport.Enabled?"ON":"OFF")
                .Status("transport-duty","Active AI duty (server)",LogisticsDutySummary())
                .Status("refineries","Refineries (server)",LogisticsRefineryCount().ToString(CultureInfo.InvariantCulture))
                .Action("/logistics status","Status / my cargo","Request current status and your transport cargo.")
                .Action("/logistics help","Help","Show the current command syntax.")
                .OnOff("/logistics enabled","Logistics","Enable or disable through the existing master handler.",true,Cfg.Enabled).Presentation("switch","General")
                .OnOff("/logistics harvesterfix","Harvester recovery","Enable automatic harvester recovery.",true,Cfg.HarvesterFix.Enabled).Presentation("checkbox","Automation")
                .OnOff("/logistics aitransport","AI transport","Enable automated transport work.",true,Cfg.AITransport.Enabled).Presentation("checkbox","Automation")
                .Value("/logistics share","Delivery reward share","Fraction of delivered resources credited as a reward.",true,F(Cfg.PersonalBonus.Share)).Presentation("slider","Rewards",0,1,.01,"fraction")
                .Value("/logistics transfershare","Pickup reward share","Harvester-to-transport reward share.",true,F(Cfg.PersonalBonus.TransferShare)).Presentation("slider","Rewards",0,1,.01,"fraction")
                .OnOff("/logisticsscore","Player scoring","Enable logistics player score rewards.",true,Cfg.LogisticsScore).Presentation("checkbox","Score")
                .Value("/scoreunload","Unload score","Points per 100 Balterium.",true,Cfg.ScoreUnload.ToString(CultureInfo.InvariantCulture)).Presentation("slider","Score",0,100,1)
                .Value("/scoretransfer","Transfer score","Points per 100 Balterium.",true,Cfg.ScoreTransfer.ToString(CultureInfo.InvariantCulture)).Presentation("slider","Score",0,100,1)
                .OnOff("/logisticsscorecommander","Commander scoring","Enable configured commander score rules.",true,Cfg.CommanderScore.Enabled).Presentation("checkbox","Score")
                .Value("/logistics capacity air","Air cargo capacity","Positive integer Balterium capacity.",true,Cfg.AirCargoCapacity.ToString(CultureInfo.InvariantCulture)).Presentation("value","Capacity",min:1,step:1)
                .Value("/logistics capacity ground","Ground cargo capacity","Positive integer Balterium capacity.",true,Cfg.GroundCargoCapacity.ToString(CultureInfo.InvariantCulture)).Presentation("value","Capacity",min:1,step:1)
                .Value("/logistics rate load","Ground load rate (units/s)","Positive integer ground transfer rate.",true,Cfg.LoadUnitsPerSecond.ToString(CultureInfo.InvariantCulture)).Presentation("value","Rates",min:1,step:1)
                .Value("/logistics rate unload","Ground unload rate (units/s)","Positive integer ground transfer rate.",true,Cfg.UnloadUnitsPerSecond.ToString(CultureInfo.InvariantCulture)).Presentation("value","Rates",min:1,step:1)
                .Value("/logistics radius load","Load radius (m)","Player transfer radius; must be positive.",true,F(Cfg.LoadRadius)).Presentation("value","Distances",min:.001)
                .Value("/logistics radius unload","Unload radius (m)","Player transfer radius; must be positive.",true,F(Cfg.UnloadRadius)).Presentation("value","Distances",min:.001)
                .Value("/logistics cooldown","Cooldown (s)","Delivery cooldown and reward payout delay.",true,F(Cfg.DeliveryCooldownSeconds)).Presentation("value","Rates",min:0)
                .OnOff("/logistics debug","Debug","Diagnostic server logging.",true,Cfg.Debug).Presentation("checkbox","Diagnostics")
                .Action("/logistics reload","Reload config","Read existing configuration; does not change its location.",true).Presentation("action","Diagnostics")
                .Build();
        }
    }
}
