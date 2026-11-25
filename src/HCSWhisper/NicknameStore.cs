using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace HCSWhisper
{
    /// <summary>
    /// Stores built-in + user-customizable Place Nicknames (e.g. "ash" -> "Ashland", "babbage" -> "New Babbage",
    /// "arc l5" -> "Yellow Core Station", "charles beach" -> "Chawla's Beach") and Full Command Aliases
    /// (e.g. "punch it" -> "engage quantum jump"). Persisted in %LOCALAPPDATA%\HCSWhisper\nicknames.json.
    /// </summary>
    public sealed class NicknameStore
    {
        public Dictionary<string, string> PlaceNicknames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> CommandAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RemovedDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static string DefaultFilePath => Path.Combine(HcsConfig.DataDir, "nicknames.json");

        private static readonly Dictionary<string, string> BuiltInPlaceNicknames = new(StringComparer.OrdinalIgnoreCase)
        {
            // Stanton Major Cities & Spaceports (Note: bare "dunlow" intentionally omitted so "Downlow" & "Dunlow Ridge" stay distinct!)
            ["babbage"] = "New Babbage",
            ["nb"] = "New Babbage",
            ["nb int"] = "New Babbage",
            ["new babbage spaceport"] = "New Babbage",
            ["interstellar spaceport"] = "New Babbage",
            ["tobin"] = "New Babbage",
            ["commons"] = "New Babbage",
            ["the commons"] = "New Babbage",
            ["a18"] = "Area18",
            ["area 18"] = "Area18",
            ["riker"] = "Area18",
            ["riker memorial"] = "Area18",
            ["riker spaceport"] = "Area18",
            ["teasa"] = "Lorville",
            ["teasa spaceport"] = "Lorville",
            ["august dunlow"] = "Orison",
            ["dunlow spaceport"] = "Orison",
            ["horizon"] = "Orison",
            ["grim"] = "Grim HEX",
            ["gh"] = "Grim HEX",
            ["hex"] = "Grim HEX",

            // Stanton LEO Orbital Stations
            ["tressler"] = "Port Tressler",
            ["pt"] = "Port Tressler",
            ["everus"] = "Everus Harbor",
            ["eh"] = "Everus Harbor",
            ["baijini"] = "Baijini Point",
            ["bp"] = "Baijini Point",
            ["seraphim"] = "Seraphim Station",
            ["olisar"] = "Seraphim Station",
            ["port olisar"] = "Seraphim Station",
            ["po"] = "Seraphim Station",

            // Stanton Moons — Whisper Homophones
            ["cleo"] = "Clio",
            ["yellow"] = "Yela",
            ["aerial"] = "Arial",
            ["ariel"] = "Arial",

            // Stanton Lagrange Stations — ArcCorp (ARC-L1..L5)
            ["arc l1"] = "Wide Forest Station",
            ["arccorp l1"] = "Wide Forest Station",
            ["wide forest"] = "Wide Forest Station",
            ["arc l2"] = "Lively Pathway Station",
            ["arccorp l2"] = "Lively Pathway Station",
            ["lively pathway"] = "Lively Pathway Station",
            ["arc l3"] = "Modern Express Station",
            ["arccorp l3"] = "Modern Express Station",
            ["modern express"] = "Modern Express Station",
            ["arc l4"] = "Faint Glen Station",
            ["arccorp l4"] = "Faint Glen Station",
            ["faint glen"] = "Faint Glen Station",
            ["arc l5"] = "Yellow Core Station",
            ["arccorp l5"] = "Yellow Core Station",
            ["yellow core"] = "Yellow Core Station",

            // Stanton Lagrange Stations — Hurston (HUR-L1..L5)
            ["hur l1"] = "Green Glade Station",
            ["hurston l1"] = "Green Glade Station",
            ["green glade"] = "Green Glade Station",
            ["hur l2"] = "Faithful Dream Station",
            ["hurston l2"] = "Faithful Dream Station",
            ["faithful dream"] = "Faithful Dream Station",
            ["hur l3"] = "Thundering Express Station",
            ["hurston l3"] = "Thundering Express Station",
            ["thundering express"] = "Thundering Express Station",
            ["hur l4"] = "Melodic Fields Station",
            ["hurston l4"] = "Melodic Fields Station",
            ["melodic fields"] = "Melodic Fields Station",
            ["hur l5"] = "High Course Station",
            ["hurston l5"] = "High Course Station",
            ["high course"] = "High Course Station",

            // Stanton Lagrange Stations — Crusader (CRU-L1, L4, L5)
            ["cru l1"] = "Ambitious Dream Station",
            ["crusader l1"] = "Ambitious Dream Station",
            ["ambitious dream"] = "Ambitious Dream Station",
            ["cru l4"] = "Shallow Fields Station",
            ["crusader l4"] = "Shallow Fields Station",
            ["shallow fields"] = "Shallow Fields Station",
            ["cru l5"] = "Beautiful Glen Station",
            ["crusader l5"] = "Beautiful Glen Station",
            ["beautiful glen"] = "Beautiful Glen Station",

            // Stanton Lagrange Stations — microTech (MIC-L1..L5)
            ["mic l1"] = "Shallow Frontier Station",
            ["mike l1"] = "Shallow Frontier Station",
            ["microtech l1"] = "Shallow Frontier Station",
            ["shallow frontier"] = "Shallow Frontier Station",
            ["mic l2"] = "Long Forest Station",
            ["mike l2"] = "Long Forest Station",
            ["microtech l2"] = "Long Forest Station",
            ["long forest"] = "Long Forest Station",
            ["mic l3"] = "Endless Odyssey Station",
            ["mike l3"] = "Endless Odyssey Station",
            ["microtech l3"] = "Endless Odyssey Station",
            ["endless odyssey"] = "Endless Odyssey Station",
            ["mic l4"] = "Red Crossroads Station",
            ["mike l4"] = "Red Crossroads Station",
            ["microtech l4"] = "Red Crossroads Station",
            ["red crossroads"] = "Red Crossroads Station",
            ["mic l5"] = "Modern Icarus Station",
            ["mike l5"] = "Modern Icarus Station",
            ["microtech l5"] = "Modern Icarus Station",
            ["modern icarus"] = "Modern Icarus Station",

            // Pyro & Nyx Stations, Planets, Moons & Popular Settlements + Whisper Homophones
            ["ash"] = "Ashland",
            ["ruin"] = "Ruin Station",
            ["orb"] = "Orbituary",
            ["obituary"] = "Orbituary",
            ["patch"] = "Patch City",
            ["rats"] = "Rat's Nest",
            ["rats nest"] = "Rat's Nest",
            ["p5 l5"] = "Rat's Nest",
            ["pyro 5 l5"] = "Rat's Nest",
            ["rods"] = "Rod's Fuel 'N Supplies",
            ["rods fuel"] = "Rod's Fuel 'N Supplies",
            ["p5 l4"] = "Rod's Fuel 'N Supplies",
            ["pyro 5 l4"] = "Rod's Fuel 'N Supplies",
            ["dudley"] = "Dudley & Daughters",
            ["dudleys"] = "Dudley & Daughters",
            ["dudley and daughters"] = "Dudley & Daughters",
            ["p6 l4"] = "Dudley & Daughters",
            ["pyro 6 l4"] = "Dudley & Daughters",
            ["megumi"] = "Megumi Refueling",
            ["p6 l5"] = "Megumi Refueling",
            ["pyro 6 l5"] = "Megumi Refueling",
            ["p6 l3"] = "Endgame",
            ["pyro 6 l3"] = "Endgame",
            ["starlight"] = "Starlight Service Station",
            ["p3 l1"] = "Starlight Service Station",
            ["pyro 3 l1"] = "Starlight Service Station",
            ["p5 l2"] = "Gaslight",
            ["pyro 5 l2"] = "Gaslight",
            ["p3 l3"] = "Patch City",
            ["pyro 3 l3"] = "Patch City",
            ["p2 l4"] = "Checkmate",
            ["pyro 2 l4"] = "Checkmate",
            ["riviera"] = "The Golden Riviera",
            ["golden riviera"] = "The Golden Riviera",
            ["shepherds"] = "Shepherd's Rest",
            ["shepherds rest"] = "Shepherd's Rest",
            ["shepards rest"] = "Shepherd's Rest",
            ["chawla"] = "Chawla's Beach",
            ["chawlas"] = "Chawla's Beach",
            ["chawlas beach"] = "Chawla's Beach",
            ["charles beach"] = "Chawla's Beach",
            ["carlas beach"] = "Chawla's Beach",
            ["challenger beach"] = "Chawla's Beach",
            ["sacren"] = "Sacren's Plot",
            ["sacrens"] = "Sacren's Plot",
            ["sacrens plot"] = "Sacren's Plot",
            ["saccharin"] = "Sacren's Plot",
            ["saccharins plot"] = "Sacren's Plot",
            ["sacrans plot"] = "Sacren's Plot",
            ["sacred plot"] = "Sacren's Plot",
            ["saffrons plot"] = "Sacren's Plot",
            ["seers"] = "Seer's Canyon",
            ["seers canyon"] = "Seer's Canyon",
            ["sears canyon"] = "Seer's Canyon",
            ["ostlers"] = "Ostler's Claim",
            ["ostlers claim"] = "Ostler's Claim",
            ["hustlers claim"] = "Ostler's Claim",
            ["oslers claim"] = "Ostler's Claim",
            ["goners"] = "Goner's Deal",
            ["goners deal"] = "Goner's Deal",
            ["gunners deal"] = "Goner's Deal",
            ["conners deal"] = "Goner's Deal",
            ["gonners deal"] = "Goner's Deal",
            ["scarpers"] = "Scarper's Turn",
            ["scarpers turn"] = "Scarper's Turn",
            ["scrapers turn"] = "Scarper's Turn",
            ["dingers"] = "Dinger's Depot",
            ["dingers depot"] = "Dinger's Depot",
            ["fingers depot"] = "Dinger's Depot",
            ["singers depot"] = "Dinger's Depot",
            ["narenas"] = "Narena's Rest",
            ["narenas rest"] = "Narena's Rest",
            ["marinas rest"] = "Narena's Rest",
            ["kabirs"] = "Kabir's Post",
            ["kabirs post"] = "Kabir's Post",
            ["cabirs post"] = "Kabir's Post",
            ["prophets"] = "Prophet's Peak",
            ["prophets peak"] = "Prophet's Peak",
            ["profits peak"] = "Prophet's Peak",
            ["canard"] = "Canard View",
            ["cannard view"] = "Canard View",
            ["conrad view"] = "Canard View",
            ["feo"] = "Feo Canyon Depot",
            ["feo canyon"] = "Feo Canyon Depot",
            ["theo canyon"] = "Feo Canyon Depot",
            ["theo canyon depot"] = "Feo Canyon Depot",
            ["stags"] = "Stag's Rut",
            ["stags rut"] = "Stag's Rut",
            ["carvers"] = "Carver's Ridge",
            ["carvers ridge"] = "Carver's Ridge",
            ["bullocks"] = "Bullock's Reach",
            ["bullocks reach"] = "Bullock's Reach",
            ["jacksons"] = "Jackson's Swap",
            ["jacksons swap"] = "Jackson's Swap",
            ["watchers"] = "Watcher's Depot",
            ["watchers depot"] = "Watcher's Depot",
            ["yangs"] = "Yang's Place",
            ["yangs place"] = "Yang's Place",
            ["blackrock"] = "Blackrock Exchange",
            ["gray gardens"] = "Gray Gardens Depot",
            ["grey gardens"] = "Gray Gardens Depot",
            ["slowburn"] = "Slowburn Depot",
            ["pharaoh"] = "Fairo",
            ["faro"] = "Fairo",
            ["farro"] = "Fairo",
            ["viewer"] = "Vuur",
            ["a deer"] = "Adir",
            ["monarchs"] = "Monox",
            ["pyro 1"] = "Pyro I",
            ["pyro 2"] = "Monox",
            ["pyro 3"] = "Bloom",
            ["pyro 4"] = "Pyro IV",
            ["pyro 5"] = "Pyro V",
            ["pyro 6"] = "Terminus",

            // Stanton Landmarks, Salvage Yards, Distribution Centers & Outposts
            ["jt"] = "Jumptown",
            ["jump town"] = "Jumptown",
            ["spk"] = "Security Post Kareah",
            ["kareah"] = "Security Post Kareah",
            ["korea"] = "Security Post Kareah",
            ["security post korea"] = "Security Post Kareah",
            ["klescher"] = "Klescher Rehabilitation Facility",
            ["fletcher"] = "Klescher Rehabilitation Facility",
            ["prison"] = "Klescher Rehabilitation Facility",
            ["gundo"] = "Covalex Shipping Hub Gundo",
            ["covalex gundo"] = "Covalex Shipping Hub Gundo",
            ["brios"] = "Brio's Breaker Yard",
            ["brioche"] = "Brio's Breaker Yard",
            ["brioche breaker yard"] = "Brio's Breaker Yard",
            ["breaker yard"] = "Brio's Breaker Yard",
            ["samson"] = "Samson & Son's Salvage Center",
            ["samsons"] = "Samson & Son's Salvage Center",
            ["samson and sons"] = "Samson & Son's Salvage Center",
            ["devlin"] = "Devlin Scrap & Salvage",
            ["orinth"] = "Reclamation & Disposal Orinth",
            ["corinth"] = "Reclamation & Disposal Orinth",
            ["dupree"] = "Dupree Industrial Manufacturing Facility",
            ["farnesway"] = "HDPC-Farnesway",
            ["farms way"] = "HDPC-Farnesway",
            ["farnsworth"] = "HDPC-Farnesway",
            ["cassillo"] = "HDPC-Cassillo",
            ["castillo"] = "HDPC-Cassillo",
            ["oparei"] = "HDMS-Oparei",
            ["opera"] = "HDMS-Oparei",
            ["bezdek"] = "HDMS-Bezdek",
            ["best deck"] = "HDMS-Bezdek",
            ["lathan"] = "HDMS-Lathan",
            ["thedus"] = "HDMS-Thedus",
            ["thetis"] = "HDMS-Thedus",
            ["norgaard"] = "HDMS-Norgaard",
            ["perlman"] = "HDMS-Perlman",
            ["hahn"] = "HDMS-Hahn",
            ["ryder"] = "HDMS-Ryder",
            ["stanhope"] = "HDMS-Stanhope",
            ["pinewood"] = "HDMS-Pinewood",
            ["hadley"] = "HDMS-Hadley",
            ["edmond"] = "HDMS-Edmond",
            ["anderson"] = "HDMS-Anderson",
            ["woodruff"] = "HDMS-Woodruff",
            ["magnolia"] = "Sakura Sun Magnolia Workcenter",
            ["goldenrod"] = "Sakura Sun Goldenrod Workcenter",
            ["anvik"] = "Rayari Anvik Research Outpost",
            ["cantwell"] = "Rayari Cantwell Research Outpost",
            ["kaltag"] = "Rayari Kaltag Research Outpost",
            ["mcgrath"] = "Rayari McGrath Research Outpost",
            ["deltana"] = "Rayari Deltana Research Outpost",
            ["deakins"] = "Deakins Research Outpost",
            ["hickes"] = "Hickes Research Outpost",
            ["benson"] = "Benson Mining Outpost",
            ["tram and myers"] = "Tram & Myers Mining",
            ["kudre"] = "Kudre Ore",
            ["could ray"] = "Kudre Ore",
            ["could ray ore"] = "Kudre Ore",
            ["loveridge"] = "Loveridge Mineral Reserve",
            ["humboldt"] = "Humboldt Mines",
            ["buds"] = "Bud's Growery",
            ["buds growery"] = "Bud's Growery",
            ["bountiful harvest"] = "Bountiful Harvest Hydroponics",
            ["shady glen"] = "Shady Glen Farms",
            ["gallete"] = "Gallete Family Farms",
            ["gillette"] = "Gallete Family Farms",
            ["gillette family farms"] = "Gallete Family Farms",
            ["terra mills"] = "Terra Mills HydroFarm",
            ["pickers"] = "Picker's Field",
            ["pickers field"] = "Picker's Field",
            ["makers"] = "Maker's Point",
            ["makers point"] = "Maker's Point",
            ["cutters"] = "Cutter's Rig",
            ["cutters rig"] = "Cutter's Rig",
            ["finns"] = "Finn's Folly",
            ["finns folly"] = "Finn's Folly",
            ["astors"] = "Astor's Clearing",
            ["astors clearing"] = "Astor's Clearing",
            ["harpers"] = "Harper's Point",
            ["harpers point"] = "Harper's Point",
            ["ravens"] = "Raven's Roost",
            ["ravens roost"] = "Raven's Roost",
            ["paradise cove"] = "Paradise Cove",
            ["necropolis"] = "The Necropolis",
            ["orphanage"] = "The Orphanage",
            ["ghost"] = "Ghost Hollow",
            ["repel"] = "Rappel",
            ["new suit"] = "Nuiqsut Emergency Shelter",
            ["nuiqsut"] = "Nuiqsut Emergency Shelter",
            ["dunlow ridge"] = "Dunlow Ridge Aid Shelter",
            ["barton flats"] = "Barton Flats Aid Shelter",
            ["dasi"] = "Wikelo Emporium Dasi Station",
            ["daisy"] = "Wikelo Emporium Dasi Station",
            ["selo"] = "Wikelo Emporium Selo Station",
            ["cello"] = "Wikelo Emporium Selo Station",
            ["kinga"] = "Wikelo Emporium Kinga Station",
            ["left ski"] = "Levski",
            ["glaciem alpha"] = "Transit Point Glaciem Alpha",
            ["glaciem bravo"] = "Transit Point Glaciem Bravo",
            ["glaciem charlie"] = "Transit Point Glaciem Charlie",
            ["glacier alpha"] = "Transit Point Glaciem Alpha",
            ["glacier bravo"] = "Transit Point Glaciem Bravo",
            ["glacier charlie"] = "Transit Point Glaciem Charlie",
        };

        private static readonly Dictionary<string, string> BuiltInCommandAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["punch it"] = "engage quantum jump",
            ["hit it"] = "engage quantum jump",
            ["gear down"] = "deploy landing gear",
            ["gear up"] = "retract landing gear",
        };

        public static NicknameStore Load(string path = null)
        {
            string file = path ?? DefaultFilePath;
            NicknameStore store = null;

            try
            {
                if (File.Exists(file))
                {
                    var loaded = JsonSerializer.Deserialize<NicknameStore>(File.ReadAllText(file));
                    if (loaded != null)
                    {
                        store = new NicknameStore
                        {
                            PlaceNicknames = new Dictionary<string, string>(loaded.PlaceNicknames ?? new(), StringComparer.OrdinalIgnoreCase),
                            CommandAliases = new Dictionary<string, string>(loaded.CommandAliases ?? new(), StringComparer.OrdinalIgnoreCase),
                            RemovedDefaults = new HashSet<string>(loaded.RemovedDefaults ?? new(), StringComparer.OrdinalIgnoreCase)
                        };
                    }
                }
            }
            catch
            {
                // Rebuild with defaults if JSON is malformed
            }

            store ??= new NicknameStore();
            bool changed = false;

            // Clean up v1.1.0 ambiguous bare "dunlow" -> "Orison" default so "Downlow" & "Dunlow Ridge" don't collide
            if (store.PlaceNicknames.TryGetValue("dunlow", out string existingDunlow) &&
                string.Equals(existingDunlow, "Orison", StringComparison.OrdinalIgnoreCase))
            {
                store.PlaceNicknames.Remove("dunlow");
                changed = true;
            }

            // Merge new built-in defaults unless the user explicitly deleted or overrode them
            foreach (var kvp in BuiltInPlaceNicknames)
            {
                if (!store.PlaceNicknames.ContainsKey(kvp.Key) && !store.RemovedDefaults.Contains("P:" + kvp.Key))
                {
                    store.PlaceNicknames[kvp.Key] = kvp.Value;
                    changed = true;
                }
            }
            foreach (var kvp in BuiltInCommandAliases)
            {
                if (!store.CommandAliases.ContainsKey(kvp.Key) && !store.RemovedDefaults.Contains("C:" + kvp.Key))
                {
                    store.CommandAliases[kvp.Key] = kvp.Value;
                    changed = true;
                }
            }

            if (changed || !File.Exists(file))
                store.Save(file);

            return store;
        }

        public void SetPlaceNickname(string nickname, string targetPlace)
        {
            if (string.IsNullOrWhiteSpace(nickname) || string.IsNullOrWhiteSpace(targetPlace))
                return;
            string k = nickname.Trim();
            PlaceNicknames[k] = targetPlace.Trim();
            RemovedDefaults.Remove("P:" + k);
        }

        public void RemovePlaceNickname(string nickname)
        {
            if (string.IsNullOrWhiteSpace(nickname)) return;
            string k = nickname.Trim();
            PlaceNicknames.Remove(k);
            if (BuiltInPlaceNicknames.ContainsKey(k))
                RemovedDefaults.Add("P:" + k);
        }

        public void SetCommandAlias(string alias, string targetCommand)
        {
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(targetCommand))
                return;
            string k = alias.Trim();
            CommandAliases[k] = targetCommand.Trim();
            RemovedDefaults.Remove("C:" + k);
        }

        public void RemoveCommandAlias(string alias)
        {
            if (string.IsNullOrWhiteSpace(alias)) return;
            string k = alias.Trim();
            CommandAliases.Remove(k);
            if (BuiltInCommandAliases.ContainsKey(k))
                RemovedDefaults.Add("C:" + k);
        }

        public void Save(string path = null)
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path ?? DefaultFilePath, JsonSerializer.Serialize(this, opts));
            }
            catch { }
        }
    }
}
