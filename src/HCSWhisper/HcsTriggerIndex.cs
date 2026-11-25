using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HCSWhisper
{
    public sealed class MatchResult
    {
        public string RawText { get; init; }
        public string CanonicalInput { get; init; }
        public string MatchedCommand { get; init; }
        public string FriendlyName { get; init; }
        public double Score { get; init; }
        public bool IsExact { get; init; }
    }

    /// <summary>
    /// Parses HCS VoicePack plain-text JSON trigger files (Event Horizon, Singularity, AdAstra, etc.),
    /// dynamically ingests Star Citizen destinations + alternate Lagrange/spaceport names from HcsMfdLayouts.dll,
    /// applies user-customizable Place Nicknames & Command Aliases, and resolves Whisper transcriptions in < 2 ms.
    /// </summary>
    public sealed class HcsTriggerIndex
    {
        private sealed class IndexedEntry
        {
            public string Canonical { get; init; }
            public string[] Tokens { get; init; }
            public string[] PhoneticTokens { get; init; }
            public string RawPhrase { get; init; }
            public string FriendlyName { get; init; }
            public bool IsDangerous { get; init; }
        }

        private readonly Dictionary<string, List<IndexedEntry>> _exactMap = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<int>> _tokenInvertedIndex = new(StringComparer.Ordinal);
        private readonly List<IndexedEntry> _allEntries = new();

        public string ActivePackSummary { get; private set; } = "No HCS pack indexed";
        public int TotalCommandsCount { get; private set; }
        public int TotalPhrasesCount => _allEntries.Count;
        public string WhisperPrompt { get; private set; } = DefaultSpaceSimPrompt;
        public List<string> KnownDestinationNames { get; } = new();

        private const string DefaultSpaceSimPrompt =
            "Voice commands for Star Citizen and Elite Dangerous ship crew. " +
            "MFD, IFCS, SCM, NAV mode, mobiGlas, Quantum Drive, spool, despool, gimbal, gimbals, decoupled, " +
            "recouple, chaff, flare, shields, capacitor, turret ESP, turret gyro, salvage, fracture, disintegrate, " +
            "mining laser, extraction, tractor beam, landing gear, airlock, supercruise, frameshift drive, heatsink, " +
            "cargo scoop, Astra, Vega, Orion, Vulcan, Carina, Cleo, Eli, Midnight, Vasco, Verity.";

        private static readonly string[] DangerousKeywords =
        {
            "eject", "destruct", "abandon", "jettison", "suicide", "respawn"
        };

        private static readonly HashSet<string> LeadingFillers = new(StringComparer.OrdinalIgnoreCase)
        {
            "uh", "um", "erm", "hey", "ok", "okay", "please", "so", "well"
        };

        private static readonly HashSet<string> TrailingFillers = new(StringComparer.OrdinalIgnoreCase)
        {
            "please", "thanks", "thank", "you"
        };

        private static readonly (Regex Pattern, string Replacement)[] MultiWordReplacements =
        {
            (new Regex(@"\bem\s+eff\s+dee\b", RegexOptions.Compiled), "mfd"),
            (new Regex(@"\bm\s+f\s+d\b", RegexOptions.Compiled), "mfd"),
            (new Regex(@"\bi\s+f\s+c\s+s\b", RegexOptions.Compiled), "ifcs"),
            (new Regex(@"\be\s+s\s+p\b", RegexOptions.Compiled), "esp"),
            (new Regex(@"\bs\s+c\s+m\b", RegexOptions.Compiled), "scm"),
            (new Regex(@"\bv\s+t\s+o\s+l\b", RegexOptions.Compiled), "vtol"),
            (new Regex(@"\bh\s+u\s+d\b", RegexOptions.Compiled), "hud"),
            (new Regex(@"\bg\s+u\s+i\b", RegexOptions.Compiled), "gui"),
            (new Regex(@"\be\s+m\s+p\b", RegexOptions.Compiled), "emp"),
            (new Regex(@"\bf\s+o\s+v\b", RegexOptions.Compiled), "fov"),
            (new Regex(@"\bf\s+s\s+d\b", RegexOptions.Compiled), "fsd"),
            (new Regex(@"\bf\s+s\s+s\b", RegexOptions.Compiled), "fss"),
            (new Regex(@"\bd\s+s\s+s\b", RegexOptions.Compiled), "dss"),
            (new Regex(@"\be\s+v\s+a\b", RegexOptions.Compiled), "eva"),
            (new Regex(@"\ba\s+d\s+s\b", RegexOptions.Compiled), "ads"),
            (new Regex(@"\bmobi\s+glass?\b", RegexOptions.Compiled), "mobiglas"),
            (new Regex(@"\bmulti\s+tool\b", RegexOptions.Compiled), "multitool"),
            (new Regex(@"\bmed\s+pack\b", RegexOptions.Compiled), "medpack"),
            (new Regex(@"\bmedi\s+pack\b", RegexOptions.Compiled), "medipack"),
            (new Regex(@"\bmed\s+gun\b", RegexOptions.Compiled), "medgun"),
            (new Regex(@"\bde\s+coupled\b", RegexOptions.Compiled), "decoupled"),
            (new Regex(@"\bde\s+couple\b", RegexOptions.Compiled), "decouple"),
            (new Regex(@"\bre\s+couple\b", RegexOptions.Compiled), "recouple"),
            (new Regex(@"\bde\s+spool\b", RegexOptions.Compiled), "despool"),
            (new Regex(@"\bframe\s+shift\b", RegexOptions.Compiled), "frameshift"),
            (new Regex(@"\bsuper\s+cruise\b", RegexOptions.Compiled), "supercruise"),
            (new Regex(@"\bheat\s+sinks?\b", RegexOptions.Compiled), "heatsink"),
            (new Regex(@"\bsub\s+target\b", RegexOptions.Compiled), "subtarget"),
            (new Regex(@"\bcounter\s+measures?\b", RegexOptions.Compiled), "countermeasure"),
            (new Regex(@"\bre\s+spawn\b", RegexOptions.Compiled), "respawn"),
            (new Regex(@"\bflash\s+light\b", RegexOptions.Compiled), "flashlight"),
            // Unify all course-plotting phrasing ("set a course for", "plotting course to", "plot a route for", "navigate to")
            (new Regex(@"\b(?:plot|plotting|set|setting|chart|charting|lay\s+in)\s+(?:a\s+)?(?:course|route)\s+(?:to|for)\b", RegexOptions.Compiled), "plot a course to"),
            (new Regex(@"^(?:navigate|head)\s+to\b", RegexOptions.Compiled), "plot a course to"),
            // Star Citizen location homophones & compound spellings
            (new Regex(@"\bnew\s+(?:beverage|vavage|bavage|cabbage|baggage|babidge|babage)\b", RegexOptions.Compiled), "new babbage"),
            (new Regex(@"\b(?:everest|everis)\s+harbou?r\b", RegexOptions.Compiled), "everus harbor"),
            (new Regex(@"\beverus\s+harbour\b", RegexOptions.Compiled), "everus harbor"),
            (new Regex(@"\b(?:by\s+genie|bi\s+jini|bai\s+jini)\b", RegexOptions.Compiled), "baijini"),
            (new Regex(@"\bmicro\s+tech\b", RegexOptions.Compiled), "microtech"),
            (new Regex(@"\b(?:arc|ark)\s+corp\b", RegexOptions.Compiled), "arccorp"),
            (new Regex(@"\bgrim\s*hex\b", RegexOptions.Compiled), "grim hex"),
        };

        private static readonly Dictionary<string, string> SingleWordNormalizations = new(StringComparer.Ordinal)
        {
            ["mobiglass"] = "mobiglas",
            ["gimballs"] = "gimbals",
            ["gimball"] = "gimbal",
            ["unspool"] = "despool",
            ["dismebark"] = "disembark",
            ["customise"] = "customize",
            ["defence"] = "defense",
            ["favourite"] = "favorite",
            ["favourites"] = "favorites",
            ["centre"] = "center",
            ["recenter"] = "recenter",
            ["recentre"] = "recenter",
            ["heatsinks"] = "heatsink",
            ["countermeasures"] = "countermeasure",
            // Star Citizen single-word place homophones
            ["babage"] = "babbage",
            ["vavage"] = "babbage",
            ["bavage"] = "babbage",
            ["babidge"] = "babbage",
            ["laureville"] = "lorville",
            ["loreville"] = "lorville",
            ["lawville"] = "lorville",
            ["lorvil"] = "lorville",
            ["orizon"] = "orison",
            ["orizen"] = "orison",
            ["tresler"] = "tressler",
            ["trestler"] = "tressler",
            ["bijini"] = "baijini",
            ["baigini"] = "baijini",
            ["serafim"] = "seraphim",
            ["levsky"] = "levski",
            ["hursten"] = "hurston",
            ["arcorp"] = "arccorp",
            ["arkcorp"] = "arccorp",
            ["calliopi"] = "calliope",
            ["yella"] = "yela",
            ["celin"] = "cellin",
            ["selen"] = "cellin",
            ["ashlyn"] = "ashland",
            ["twenny"] = "twenty",
            ["sevenny"] = "seventy",
            ["0"] = "zero",
            ["1"] = "one",
            ["2"] = "two",
            ["3"] = "three",
            ["4"] = "four",
            ["5"] = "five",
            ["6"] = "six",
            ["7"] = "seven",
            ["8"] = "eight",
            ["9"] = "nine",
            ["10"] = "ten",
            ["11"] = "eleven",
            ["12"] = "twelve",
            ["13"] = "thirteen",
            ["14"] = "fourteen",
            ["15"] = "fifteen",
            ["16"] = "sixteen",
            ["17"] = "seventeen",
            ["18"] = "eighteen",
            ["19"] = "nineteen",
            ["20"] = "twenty",
            ["25"] = "twenty five",
            ["30"] = "thirty",
            ["40"] = "forty",
            ["50"] = "fifty",
            ["60"] = "sixty",
            ["70"] = "seventy",
            ["75"] = "seventy five",
            ["80"] = "eighty",
            ["90"] = "ninety",
            ["100"] = "one hundred",
        };

        private static readonly Regex VarTokenRegex = new(@"\{TXT:([A-Za-z0-9_]+)\}", RegexOptions.Compiled);
        private static readonly Regex RangeBracketRegex = new(@"\[(\d+)\.\.(\d+)(;?)\]", RegexOptions.Compiled);
        private static readonly Regex NonWordCleanRegex = new(@"[^a-z0-9\s%]", RegexOptions.Compiled);
        private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex WhisperArtifactRegex = new(@"(\[.*?\]|\(.*?\)|\*.*?\*)", RegexOptions.Compiled);
        private static readonly Regex LagrangeGlueRegex = new(@"\b(arc|hur|cru|mic)\s*l\s*(\d)\b", RegexOptions.Compiled);
        private static readonly Regex PyroLagrangeGlueRegex = new(@"\b(?:p|pyro)\s*(\d)\s*l\s*(\d)\b", RegexOptions.Compiled);
        private static readonly Regex LagrangeStationTitleRegex = new(@"^(ARC|HUR|CRU|MIC)-L(\d)\s+(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RayariOutpostRegex = new(@"^Rayari\s+(\w+)\s+Research\s+Outpost$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public void LoadFromVoiceAttack(
            string vaAppsDir,
            string activeProfileName,
            Func<string, string> liveVaGetText,
            Func<string, bool> liveVaCommandExists,
            NicknameStore nicknames = null)
        {
            _exactMap.Clear();
            _tokenInvertedIndex.Clear();
            _allEntries.Clear();
            KnownDestinationNames.Clear();
            TotalCommandsCount = 0;

            nicknames ??= NicknameStore.Load();

            string triggersDir = Path.Combine(vaAppsDir ?? "", "HCS VoicePack Plugins", "VoiceTriggers");
            if (!Directory.Exists(triggersDir))
            {
                ActivePackSummary = "HCS VoiceTriggers folder not found (raw VA matching active)";
                WhisperPrompt = DefaultSpaceSimPrompt;
                return;
            }

            string[] allEnglishFiles = Directory.GetFiles(triggersDir, "English *.hcs");
            if (allEnglishFiles.Length == 0)
            {
                ActivePackSummary = "No English *.hcs trigger files found";
                return;
            }

            var selectedFiles = SelectMatchingHcsFiles(allEnglishFiles, activeProfileName, liveVaCommandExists);
            var loadedPackNames = new List<string>();

            foreach (string file in selectedFiles)
            {
                string packName = Path.GetFileNameWithoutExtension(file).Replace("English ", "");
                loadedPackNames.Add(packName);
                IndexSingleHcsFile(file, liveVaGetText);
            }

            if (loadedPackNames.Any(p => p.Contains("Event Horizon", StringComparison.OrdinalIgnoreCase)))
            {
                IndexStarCitizenNavigationPlaces(vaAppsDir, liveVaCommandExists, nicknames);
            }
            else
            {
                // Still apply user PlaceNicknames on non-SC profiles (e.g. Elite Dangerous system nicknames)
                ApplyCustomPlaceNicknames(nicknames, liveVaCommandExists, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            }

            ApplyCustomCommandAliases(nicknames, liveVaCommandExists);

            ActivePackSummary = $"{string.Join(" + ", loadedPackNames)} ({TotalCommandsCount:N0} commands / {_allEntries.Count:N0} phrases)";
            WhisperPrompt = BuildPromptForPacks(loadedPackNames);
        }

        private void IndexStarCitizenNavigationPlaces(string vaAppsDir, Func<string, bool> liveVaCommandExists, NicknameStore nicknames)
        {
            var rawPlaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Dynamically extract all routable Star Citizen places from HCS's HcsMfdLayouts.dll
            try
            {
                string mfdDll = Path.Combine(vaAppsDir ?? "", "HCS VoicePack Plugins", "HcsMfdLayouts.dll");
                if (File.Exists(mfdDll))
                {
                    string hcsPluginDir = Path.GetDirectoryName(mfdDll);
                    ResolveEventHandler handler = (_, e) =>
                    {
                        string dep = Path.Combine(hcsPluginDir, new AssemblyName(e.Name).Name + ".dll");
                        return File.Exists(dep) ? Assembly.LoadFrom(dep) : null;
                    };
                    AppDomain.CurrentDomain.AssemblyResolve += handler;
                    try
                    {
                        Assembly asm = Assembly.LoadFrom(mfdDll);
                        Type npType = asm.GetType("Hcs.EventHorizon.Mfd.NavigationPlaces");
                        object shared = npType?.GetProperty("Shared", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                        FieldInfo routableField = npType?.GetField("routable", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (shared != null && routableField?.GetValue(shared) is IEnumerable routableList)
                        {
                            foreach (object placeObj in routableList)
                            {
                                string name = placeObj?.GetType().GetProperty("Name")?.GetValue(placeObj) as string;
                                if (!string.IsNullOrWhiteSpace(name) && !name.Contains('#') && !name.StartsWith("Comm Array", StringComparison.OrdinalIgnoreCase))
                                {
                                    rawPlaces.Add(name.Trim().Trim('"'));
                                }
                            }
                        }
                    }
                    finally
                    {
                        AppDomain.CurrentDomain.AssemblyResolve -= handler;
                    }
                }
            }
            catch { }

            // 2. Ensure core Stanton / Pyro / Nyx hubs are always present even in offline tests
            string[] corePlaces =
            {
                "New Babbage", "Lorville", "Orison", "Area18",
                "Port Tressler", "Everus Harbor", "Baijini Point", "Seraphim Station",
                "Grim HEX", "Ruin Station", "Orbituary", "Checkmate", "Patch City",
                "Levski", "Stanton Gateway", "Pyro Gateway", "Nyx Gateway", "Terra Gateway",
                "Hurston", "Crusader", "ArcCorp", "microTech", "Pyro", "Nyx",
                "Bloom", "Monox", "Terminus", "Ignis", "Vatra", "Adir", "Fairo", "Fuego", "Vuur",
                "Daymar", "Yela", "Cellin", "Aberdeen", "Arial", "Magda", "Ita",
                "Lyria", "Wala", "Calliope", "Clio", "Euterpe",
                "Brio's Breaker Yard", "Ashland", "The Golden Riviera", "Gaslight",
                "Starlight Service Station", "Rat's Nest", "Rod's Fuel 'N Supplies",
                "Dudley & Daughters", "Megumi Refueling", "Endgame",
                "Security Post Kareah", "Jumptown", "Klescher Rehabilitation Facility",
                "Covalex Shipping Hub Gundo", "Dupree Industrial Manufacturing Facility",
                "Rayari Anvik Research Outpost", "Rayari Cantwell Research Outpost",
                "Rayari Kaltag Research Outpost", "Rayari McGrath Research Outpost",
                "ARC-L1 Wide Forest Station", "ARC-L2 Lively Pathway Station",
                "ARC-L3 Modern Express Station", "ARC-L4 Faint Glen Station", "ARC-L5 Yellow Core Station",
                "HUR-L1 Green Glade Station", "HUR-L2 Faithful Dream Station",
                "HUR-L3 Thundering Express Station", "HUR-L4 Melodic Fields Station", "HUR-L5 High Course Station",
                "CRU-L1 Ambitious Dream Station", "CRU-L4 Shallow Fields Station", "CRU-L5 Beautiful Glen Station",
                "MIC-L1 Shallow Frontier Station", "MIC-L2 Long Forest Station",
                "MIC-L3 Endless Odyssey Station", "MIC-L4 Red Crossroads Station", "MIC-L5 Modern Icarus Station"
            };
            foreach (string cp in corePlaces) rawPlaces.Add(cp);

            var resolvedCmdCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var displayNamesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string place in rawPlaces)
            {
                string winningCmd = ResolveWinningPlaceCommand(place, liveVaCommandExists, resolvedCmdCache);
                if (winningCmd == null)
                    continue;

                TotalCommandsCount++;
                displayNamesSet.Add(place);

                // Collect all automatic alternate spoken forms for this destination entity -> all fire winningCmd!
                var autoAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { place };

                var lagMatch = LagrangeStationTitleRegex.Match(place);
                if (lagMatch.Success)
                {
                    string bodyCode = lagMatch.Groups[1].Value.ToUpperInvariant(); // ARC, HUR, CRU, MIC
                    string lNum = lagMatch.Groups[2].Value;
                    string stationTitle = lagMatch.Groups[3].Value.Trim(); // e.g. "Yellow Core Station"

                    displayNamesSet.Add(stationTitle);
                    autoAliases.Add($"{bodyCode}-L{lNum}");
                    autoAliases.Add($"{bodyCode} L{lNum}");
                    autoAliases.Add(stationTitle);
                    if (stationTitle.EndsWith(" Station", StringComparison.OrdinalIgnoreCase))
                        autoAliases.Add(stationTitle.Substring(0, stationTitle.Length - 8).Trim());

                    string fullPlanet = bodyCode switch
                    {
                        "ARC" => "ArcCorp",
                        "HUR" => "Hurston",
                        "CRU" => "Crusader",
                        "MIC" => "microTech",
                        _ => null
                    };
                    if (fullPlanet != null)
                        autoAliases.Add($"{fullPlanet} L{lNum}");
                }

                if (place.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && place.Length > 6)
                    autoAliases.Add(place.Substring(4).Trim());

                var rayMatch = RayariOutpostRegex.Match(place);
                if (rayMatch.Success)
                    autoAliases.Add(rayMatch.Groups[1].Value);

                if (place.Equals("Area18", StringComparison.OrdinalIgnoreCase))
                    autoAliases.Add("Area 18");

                // Note: Canonicalize() already unifies all "set/plot a course/route to/for" prefixes into "plot a course to",
                // so indexing "plot a course to <alias>" covers EVERY phrasing permutation automatically!
                foreach (string alias in autoAliases)
                {
                    IndexDirectPhrase("plot a course to " + alias, winningCmd, $"Plot course: {place}", prioritize: false);
                }
            }

            KnownDestinationNames.AddRange(displayNamesSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            // 3. Index all built-in + user-customizable Place Nicknames (e.g. "ash" -> "Ashland", "babbage" -> "New Babbage")
            ApplyCustomPlaceNicknames(nicknames, liveVaCommandExists, resolvedCmdCache);
        }

        private void ApplyCustomPlaceNicknames(
            NicknameStore nicknames,
            Func<string, bool> liveVaCommandExists,
            Dictionary<string, string> resolvedCmdCache)
        {
            if (nicknames?.PlaceNicknames == null)
                return;

            foreach (var kvp in nicknames.PlaceNicknames)
            {
                string nick = kvp.Key?.Trim();
                string targetPlace = kvp.Value?.Trim();
                if (string.IsNullOrEmpty(nick) || string.IsNullOrEmpty(targetPlace))
                    continue;

                string winningCmd = ResolveWinningPlaceCommand(targetPlace, liveVaCommandExists, resolvedCmdCache);
                if (winningCmd != null)
                {
                    IndexDirectPhrase("plot a course to " + nick, winningCmd, $"Nickname '{nick}' → {targetPlace}", prioritize: true);
                }
            }
        }

        private void ApplyCustomCommandAliases(NicknameStore nicknames, Func<string, bool> liveVaCommandExists)
        {
            if (nicknames?.CommandAliases == null)
                return;

            foreach (var kvp in nicknames.CommandAliases)
            {
                string alias = kvp.Key?.Trim();
                string targetPhrase = kvp.Value?.Trim();
                if (string.IsNullOrEmpty(alias) || string.IsNullOrEmpty(targetPhrase))
                    continue;

                string resolvedCmd = null;
                var existing = Match(targetPhrase, liveVaCommandExists, 0.80);
                if (existing != null)
                {
                    resolvedCmd = existing.MatchedCommand;
                }
                else if (liveVaCommandExists == null || SafeVaExists(liveVaCommandExists, targetPhrase))
                {
                    resolvedCmd = targetPhrase;
                }

                if (resolvedCmd != null)
                {
                    IndexDirectPhrase(alias, resolvedCmd, $"Alias '{alias}' → {resolvedCmd}", prioritize: true);
                }
            }
        }

        private static string ResolveWinningPlaceCommand(
            string rawPlace,
            Func<string, bool> liveVaCommandExists,
            Dictionary<string, string> cache)
        {
            if (string.IsNullOrWhiteSpace(rawPlace))
                return null;

            string p = rawPlace.Trim().Trim('"');
            if (cache.TryGetValue(p, out string cached))
                return cached;

            // Build candidate target names that Event Horizon's VoiceAttack command may use
            var candidates = new List<string> { p };

            var lagMatch = LagrangeStationTitleRegex.Match(p);
            if (lagMatch.Success)
            {
                candidates.Add(lagMatch.Groups[3].Value.Trim()); // "Yellow Core Station" (Event Horizon's actual VA phrase!)
                candidates.Add($"{lagMatch.Groups[1].Value.ToUpperInvariant()}-L{lagMatch.Groups[2].Value}");
            }
            if (p.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
                candidates.Add(p.Substring(4).Trim());
            var rayMatch = RayariOutpostRegex.Match(p);
            if (rayMatch.Success)
                candidates.Add(rayMatch.Groups[1].Value);
            if (p.StartsWith("Rod's ", StringComparison.OrdinalIgnoreCase))
                candidates.Add("Rod's");
            if (p.Equals("Area18", StringComparison.OrdinalIgnoreCase))
                candidates.Add("Area 18");
            else if (p.Equals("Area 18", StringComparison.OrdinalIgnoreCase))
                candidates.Add("Area18");

            string[] plotPrefixes = { "Plot a course to ", "Set a course to ", "Plot course to " };

            if (liveVaCommandExists == null)
            {
                // Offline test mode: prefer stripped Lagrange station name ("Yellow Core Station") just like live Event Horizon
                string preferred = lagMatch.Success ? lagMatch.Groups[3].Value.Trim() : p;
                string offlineCmd = "Plot a course to " + preferred;
                cache[p] = offlineCmd;
                return offlineCmd;
            }

            foreach (string candName in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string pfx in plotPrefixes)
                {
                    string fullCmd = pfx + candName;
                    if (SafeVaExists(liveVaCommandExists, fullCmd))
                    {
                        cache[p] = fullCmd;
                        return fullCmd;
                    }
                }
            }

            cache[p] = null;
            return null;
        }

        private void IndexDirectPhrase(string spokenForm, string rawCommandToExecute, string friendlyName, bool prioritize)
        {
            string canonical = Canonicalize(spokenForm);
            if (canonical.Length == 0)
                return;

            string[] tokens = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string[] phonetics = tokens.Select(ToSoundexKey).ToArray();

            var entry = new IndexedEntry
            {
                Canonical = canonical,
                Tokens = tokens,
                PhoneticTokens = phonetics,
                RawPhrase = rawCommandToExecute,
                FriendlyName = friendlyName,
                IsDangerous = DangerousKeywords.Any(d => canonical.Contains(d, StringComparison.Ordinal))
            };

            if (!_exactMap.TryGetValue(canonical, out var bucket))
            {
                bucket = new List<IndexedEntry>(1);
                _exactMap[canonical] = bucket;
            }

            int existingIdx = bucket.FindIndex(b => string.Equals(b.RawPhrase, rawCommandToExecute, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0)
            {
                if (prioritize && existingIdx > 0)
                {
                    var item = bucket[existingIdx];
                    bucket.RemoveAt(existingIdx);
                    bucket.Insert(0, item);
                }
                return;
            }

            if (prioritize)
                bucket.Insert(0, entry);
            else
                bucket.Add(entry);

            int idx = _allEntries.Count;
            _allEntries.Add(entry);

            foreach (string tok in tokens.Distinct())
            {
                if (tok.Length < 2) continue;
                if (!_tokenInvertedIndex.TryGetValue(tok, out var postList))
                {
                    postList = new List<int>();
                    _tokenInvertedIndex[tok] = postList;
                }
                postList.Add(idx);
            }
        }

        private static List<string> SelectMatchingHcsFiles(string[] files, string profileName, Func<string, bool> commandExists)
        {
            var result = new List<string>();
            string prof = profileName ?? "";

            foreach (string f in files)
            {
                string pack = Path.GetFileNameWithoutExtension(f).Replace("English ", "");
                if (!string.IsNullOrEmpty(prof) &&
                    (prof.Contains(pack, StringComparison.OrdinalIgnoreCase) ||
                     (pack.Equals("Event Horizon", StringComparison.OrdinalIgnoreCase) && prof.Contains("Star Citizen", StringComparison.OrdinalIgnoreCase)) ||
                     (pack.Equals("Singularity", StringComparison.OrdinalIgnoreCase) && prof.Contains("Elite", StringComparison.OrdinalIgnoreCase))))
                {
                    result.Add(f);
                }
            }

            if (result.Count == 0 && commandExists != null)
            {
                foreach (string f in files)
                {
                    if (ProbeHcsFileActive(f, commandExists))
                        result.Add(f);
                }
            }

            if (result.Count == 0)
            {
                foreach (string f in files)
                {
                    string n = Path.GetFileName(f);
                    if (n.Contains("Event Horizon", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Singularity", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(f);
                    }
                }
                if (result.Count == 0)
                    result.AddRange(files);
            }

            string adAstra = files.FirstOrDefault(f => f.EndsWith("English AdAstra.hcs", StringComparison.OrdinalIgnoreCase));
            if (adAstra != null && !result.Contains(adAstra))
            {
                if (commandExists == null || commandExists("Enable Ad Astra"))
                    result.Add(adAstra);
            }

            return result;
        }

        private static bool ProbeHcsFileActive(string filePath, Func<string, bool> commandExists)
        {
            try
            {
                int checkedCount = 0;
                foreach (string line in File.ReadLines(filePath))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("{") || !trimmed.Contains("\"VoiceTrigger\""))
                        continue;
                    if (trimmed.Contains("\"AddToTriggers\":false"))
                        continue;

                    using var doc = JsonDocument.Parse(trimmed);
                    if (!doc.RootElement.TryGetProperty("VoiceTrigger", out var vtProp))
                        continue;
                    string vt = vtProp.GetString();
                    if (string.IsNullOrWhiteSpace(vt) || vt.Contains("{TXT:"))
                        continue;

                    var sample = ExpandTriggerPhrases(vt, 2).FirstOrDefault();
                    if (!string.IsNullOrEmpty(sample))
                    {
                        if (commandExists(sample))
                            return true;
                        if (++checkedCount >= 6)
                            break;
                    }
                }
            }
            catch { }
            return false;
        }

        private void IndexSingleHcsFile(string filePath, Func<string, string> liveVaGetText)
        {
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var triggerEntries = new List<(string FriendlyName, string VarName, string Trigger, bool AddToTriggers)>();

            foreach (string rawLine in File.ReadLines(filePath))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("{") || !line.EndsWith("}"))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("VoiceTrigger", out var vtElem))
                        continue;

                    string trigger = vtElem.GetString() ?? "";
                    string varName = root.TryGetProperty("VarName", out var vnElem) ? (vnElem.GetString() ?? "") : "";
                    string friendly = root.TryGetProperty("FriendlyName", out var fnElem) ? (fnElem.GetString() ?? varName) : varName;
                    bool addToTriggers = !root.TryGetProperty("AddToTriggers", out var addElem) ||
                                         (addElem.ValueKind != JsonValueKind.False);

                    if (!string.IsNullOrEmpty(varName) && liveVaGetText != null)
                    {
                        try
                        {
                            string liveVal = liveVaGetText(varName);
                            if (!string.IsNullOrWhiteSpace(liveVal))
                                trigger = liveVal;
                        }
                        catch { }
                    }

                    if (!string.IsNullOrEmpty(varName))
                        variables[varName] = trigger;

                    triggerEntries.Add((friendly, varName, trigger, addToTriggers));
                }
                catch { }
            }

            if (!variables.ContainsKey("CrewNames") && variables.TryGetValue("hcsvt_CrewNames", out string cn))
                variables["CrewNames"] = cn;

            foreach (var item in triggerEntries)
            {
                if (!item.AddToTriggers || string.IsNullOrWhiteSpace(item.Trigger))
                    continue;

                TotalCommandsCount++;
                string resolved = ResolveVariables(item.Trigger, variables, 0);
                List<string> expanded = ExpandTriggerPhrases(resolved, maxPermutations: 180);

                foreach (string rawPhrase in expanded)
                {
                    string cleanRaw = MultiSpaceRegex.Replace(rawPhrase, " ").Trim();
                    if (cleanRaw.Length > 0)
                        IndexDirectPhrase(cleanRaw, cleanRaw, item.FriendlyName, prioritize: false);
                }
            }
        }

        private static string ResolveVariables(string text, Dictionary<string, string> vars, int depth)
        {
            if (depth > 4 || !text.Contains("{TXT:", StringComparison.Ordinal))
                return text;

            return VarTokenRegex.Replace(text, m =>
            {
                string key = m.Groups[1].Value;
                if (vars.TryGetValue(key, out string val))
                    return ResolveVariables(val, vars, depth + 1);
                return "";
            });
        }

        public static List<string> ExpandTriggerPhrases(string pattern, int maxPermutations = 180)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(pattern))
                return results;

            string normalized = RangeBracketRegex.Replace(pattern, m =>
            {
                if (!int.TryParse(m.Groups[1].Value, out int start) ||
                    !int.TryParse(m.Groups[2].Value, out int end) ||
                    end < start || (end - start) > 30)
                    return m.Value;

                var nums = Enumerable.Range(start, end - start + 1).Select(n => n.ToString()).ToList();
                if (m.Groups[3].Value == ";")
                    nums.Add("");
                return "[" + string.Join(";", nums) + "]";
            });

            List<string> topSegments = SplitOutsideBrackets(normalized, ';');
            int perSegmentCap = Math.Max(12, maxPermutations / Math.Max(1, topSegments.Count));

            foreach (string seg in topSegments)
            {
                if (results.Count >= maxPermutations)
                    break;

                string trimmedSeg = seg.Trim();
                if (trimmedSeg.Length == 0)
                    continue;

                var segExpanded = new List<string> { "" };
                int pos = 0;
                while (pos < trimmedSeg.Length)
                {
                    int open = trimmedSeg.IndexOf('[', pos);
                    if (open < 0)
                    {
                        string tail = trimmedSeg.Substring(pos);
                        for (int i = 0; i < segExpanded.Count; i++)
                            segExpanded[i] += tail;
                        break;
                    }

                    int depth = 1;
                    int close = open + 1;
                    while (close < trimmedSeg.Length && depth > 0)
                    {
                        if (trimmedSeg[close] == '[') depth++;
                        else if (trimmedSeg[close] == ']') depth--;
                        close++;
                    }
                    close--;

                    string literalBefore = trimmedSeg.Substring(pos, open - pos);
                    string inside = close > open ? trimmedSeg.Substring(open + 1, close - open - 1) : "";
                    List<string> choices = SplitOutsideBrackets(inside, ';');

                    var flatChoices = new List<string>();
                    foreach (string ch in choices)
                    {
                        if (ch.Contains('['))
                        {
                            var sub = ExpandTriggerPhrases(ch, 12);
                            if (sub.Count == 0) flatChoices.Add("");
                            else flatChoices.AddRange(sub);
                        }
                        else
                        {
                            flatChoices.Add(ch);
                        }
                    }

                    flatChoices = flatChoices
                        .OrderBy(c => string.IsNullOrWhiteSpace(c) ? 0 : 1)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(16)
                        .ToList();

                    var nextList = new List<string>();
                    foreach (string prefix in segExpanded)
                    {
                        foreach (string choice in flatChoices)
                        {
                            nextList.Add(prefix + literalBefore + choice);
                            if (nextList.Count >= perSegmentCap)
                                break;
                        }
                        if (nextList.Count >= perSegmentCap)
                            break;
                    }

                    segExpanded = nextList;
                    pos = close + 1;
                }

                foreach (string s in segExpanded)
                {
                    string cleaned = MultiSpaceRegex.Replace(s, " ").Trim();
                    if (cleaned.Length > 0)
                    {
                        results.Add(cleaned);
                        if (results.Count >= maxPermutations)
                            break;
                    }
                }
            }

            return results;
        }

        private static List<string> SplitOutsideBrackets(string s, char delimiter)
        {
            var parts = new List<string>();
            int depth = 0;
            int last = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '[') depth++;
                else if (s[i] == ']' && depth > 0) depth--;
                else if (s[i] == delimiter && depth == 0)
                {
                    parts.Add(s.Substring(last, i - last));
                    last = i + 1;
                }
            }
            parts.Add(s.Substring(last));
            return parts;
        }

        public static string Canonicalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            string s = WhisperArtifactRegex.Replace(input, " ").ToLowerInvariant();
            s = s.Replace("&", " and ").Replace("%", " percent ").Replace("-", " ").Replace("'", "");

            // Separate glued Lagrange codes BEFORE generic letter-digit splitting ("ArcL5" -> "arc l 5" instead of "arcl 5"!)
            s = LagrangeGlueRegex.Replace(s, "$1 l $2");
            s = PyroLagrangeGlueRegex.Replace(s, "p $1 l $2");

            s = Regex.Replace(s, @"([a-z])(\d)", "$1 $2");
            s = Regex.Replace(s, @"(\d)([a-z])", "$1 $2");
            s = NonWordCleanRegex.Replace(s, " ");
            s = MultiSpaceRegex.Replace(s, " ").Trim();
            if (s.Length == 0)
                return "";

            foreach (var (pattern, rep) in MultiWordReplacements)
            {
                s = pattern.Replace(s, rep);
            }

            string[] rawTokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var outTokens = new List<string>(rawTokens.Length);
            foreach (string t in rawTokens)
            {
                if (SingleWordNormalizations.TryGetValue(t, out string mapped))
                    outTokens.Add(mapped);
                else
                    outTokens.Add(t);
            }

            string result = MultiSpaceRegex.Replace(string.Join(" ", outTokens), " ").Trim();
            return CollapseRepetitionLoop(result);
        }

        private static string CollapseRepetitionLoop(string canonical)
        {
            string[] words = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 4)
                return canonical;

            for (int patLen = 1; patLen <= Math.Min(5, words.Length / 2); patLen++)
            {
                int repeats = 1;
                int idx = patLen;
                while (idx + patLen <= words.Length)
                {
                    bool same = true;
                    for (int k = 0; k < patLen; k++)
                    {
                        if (!string.Equals(words[k], words[idx + k], StringComparison.Ordinal))
                        {
                            same = false;
                            break;
                        }
                    }
                    if (!same) break;
                    repeats++;
                    idx += patLen;
                }

                if (repeats >= 2 && idx >= words.Length - patLen)
                {
                    if (patLen == 1 && string.Equals(words[0], "eject", StringComparison.Ordinal) && repeats == 3)
                        return canonical;
                    if (repeats >= 3 || (repeats == 2 && patLen >= 2))
                        return string.Join(" ", words, 0, patLen);
                }
            }

            return canonical;
        }

        private static string StripConversationalFillers(string canonical)
        {
            string[] tokens = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int start = 0;
            int end = tokens.Length - 1;
            while (start <= end && LeadingFillers.Contains(tokens[start])) start++;
            while (end >= start && TrailingFillers.Contains(tokens[end])) end--;
            if (start > end) return canonical;
            return string.Join(" ", tokens, start, end - start + 1);
        }

        public MatchResult Match(string rawWhisperText, Func<string, bool> vaCommandExists, double threshold)
        {
            if (string.IsNullOrWhiteSpace(rawWhisperText))
                return null;

            string cleanedWhisper = WhisperArtifactRegex.Replace(rawWhisperText, " ").Trim();
            if (cleanedWhisper.Length == 0)
                return null;

            string canonical = Canonicalize(cleanedWhisper);
            if (canonical.Length == 0)
                return null;

            var exact = TryLookupExact(canonical, vaCommandExists);
            if (exact != null)
            {
                return new MatchResult
                {
                    RawText = cleanedWhisper,
                    CanonicalInput = canonical,
                    MatchedCommand = exact.RawPhrase,
                    FriendlyName = exact.FriendlyName,
                    Score = 1.0,
                    IsExact = true
                };
            }

            string stripped = StripConversationalFillers(canonical);
            if (!string.Equals(stripped, canonical, StringComparison.Ordinal) && stripped.Length > 0)
            {
                // Re-run MultiWordReplacements in case stripping leading "please" exposed "^navigate to..."
                string recanon = Canonicalize(stripped);
                var fillerExact = TryLookupExact(recanon, vaCommandExists);
                if (fillerExact != null)
                {
                    return new MatchResult
                    {
                        RawText = cleanedWhisper,
                        CanonicalInput = recanon,
                        MatchedCommand = fillerExact.RawPhrase,
                        FriendlyName = fillerExact.FriendlyName,
                        Score = 0.98,
                        IsExact = true
                    };
                }
                canonical = recanon;
            }

            if (_allEntries.Count > 0)
            {
                string[] qTokens = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string[] qPhonetic = qTokens.Select(ToSoundexKey).ToArray();

                var candidateIds = new HashSet<int>();
                foreach (string qt in qTokens)
                {
                    if (_tokenInvertedIndex.TryGetValue(qt, out var postings))
                    {
                        foreach (int id in postings)
                            candidateIds.Add(id);
                    }
                }

                IEnumerable<IndexedEntry> pool = candidateIds.Count > 0
                    ? candidateIds.Select(i => _allEntries[i]).Concat(_allEntries.Where(e => Math.Abs(e.Tokens.Length - qTokens.Length) <= 1).Take(1500)).Distinct()
                    : _allEntries.Where(e => Math.Abs(e.Tokens.Length - qTokens.Length) <= 1);

                IndexedEntry best = null;
                double bestScore = 0;

                foreach (var cand in pool)
                {
                    if (Math.Abs(cand.Canonical.Length - canonical.Length) > Math.Max(8, canonical.Length / 2))
                        continue;

                    double charSim = NormalizedLevenshteinSimilarity(canonical, cand.Canonical);
                    double tokSim = TokenOverlapSimilarity(qTokens, cand.Tokens);
                    double phonSim = TokenOverlapSimilarity(qPhonetic, cand.PhoneticTokens);

                    double combined = 0.50 * charSim + 0.30 * tokSim + 0.20 * phonSim;
                    double minReq = cand.IsDangerous ? 0.95 : threshold;

                    if (combined >= minReq && combined > bestScore)
                    {
                        if (vaCommandExists == null || SafeVaExists(vaCommandExists, cand.RawPhrase))
                        {
                            bestScore = combined;
                            best = cand;
                        }
                    }
                }

                if (best != null)
                {
                    return new MatchResult
                    {
                        RawText = cleanedWhisper,
                        CanonicalInput = canonical,
                        MatchedCommand = best.RawPhrase,
                        FriendlyName = best.FriendlyName,
                        Score = bestScore,
                        IsExact = false
                    };
                }
            }

            string rawNoPunct = Regex.Replace(cleanedWhisper, @"[^\w\s]", "").Trim();
            if (rawNoPunct.Length > 0 && vaCommandExists != null && SafeVaExists(vaCommandExists, rawNoPunct))
            {
                return new MatchResult
                {
                    RawText = cleanedWhisper,
                    CanonicalInput = canonical,
                    MatchedCommand = rawNoPunct,
                    FriendlyName = "Custom VA Command",
                    Score = 1.0,
                    IsExact = true
                };
            }

            return null;
        }

        private IndexedEntry TryLookupExact(string canonical, Func<string, bool> vaExists)
        {
            if (!_exactMap.TryGetValue(canonical, out var list) || list.Count == 0)
                return null;
            if (vaExists == null)
                return list[0];
            foreach (var item in list)
            {
                if (SafeVaExists(vaExists, item.RawPhrase))
                    return item;
            }
            return list[0];
        }

        private static bool SafeVaExists(Func<string, bool> fn, string phrase)
        {
            try { return fn(phrase); }
            catch { return true; }
        }

        private static double TokenOverlapSimilarity(string[] a, string[] b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            int matches = 0;
            var used = new bool[b.Length];
            for (int i = 0; i < a.Length; i++)
            {
                for (int j = 0; j < b.Length; j++)
                {
                    if (!used[j] && string.Equals(a[i], b[j], StringComparison.Ordinal))
                    {
                        used[j] = true;
                        matches++;
                        break;
                    }
                }
            }
            return (2.0 * matches) / (a.Length + b.Length);
        }

        private static double NormalizedLevenshteinSimilarity(string a, string b)
        {
            int maxLen = Math.Max(a.Length, b.Length);
            if (maxLen == 0) return 1.0;
            int dist = LevenshteinDistance(a, b);
            return 1.0 - ((double)dist / maxLen);
        }

        private static int LevenshteinDistance(string s, string t)
        {
            int n = s.Length, m = t.Length;
            int[] prev = new int[m + 1];
            int[] curr = new int[m + 1];
            for (int j = 0; j <= m; j++) prev[j] = j;
            for (int i = 1; i <= n; i++)
            {
                curr[0] = i;
                char sc = s[i - 1];
                for (int j = 1; j <= m; j++)
                {
                    int cost = sc == t[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, curr) = (curr, prev);
            }
            return prev[m];
        }

        private static string ToSoundexKey(string word)
        {
            if (string.IsNullOrEmpty(word)) return "";
            string w = word.Replace("qu", "kw").Replace("ph", "f").Replace("sh", "ch");
            var sb = new StringBuilder(4);
            sb.Append(w[0]);
            char lastCode = MapSoundexChar(w[0]);
            for (int i = 1; i < w.Length && sb.Length < 4; i++)
            {
                char code = MapSoundexChar(w[i]);
                if (code != '0' && code != lastCode)
                    sb.Append(code);
                if (code != '0')
                    lastCode = code;
            }
            while (sb.Length < 4) sb.Append('0');
            return sb.ToString();
        }

        private static char MapSoundexChar(char c) => c switch
        {
            'b' or 'f' or 'p' or 'v' => '1',
            'c' or 'g' or 'j' or 'k' or 'q' or 's' or 'x' or 'z' => '2',
            'd' or 't' => '3',
            'l' => '4',
            'm' or 'n' => '5',
            'r' => '6',
            _ => '0'
        };

        private static string BuildPromptForPacks(List<string> packs)
        {
            if (packs.Any(p => p.Contains("Event Horizon", StringComparison.OrdinalIgnoreCase)))
            {
                return "Star Citizen voice commands: Plot a course to New Babbage, Babbage, Lorville, Orison, Area 18, " +
                       "Port Tressler, Everus Harbor, Baijini Point, Seraphim, Grim HEX, Ruin Station, Orbituary, Checkmate, " +
                       "Ashland, Levski, ARC-L1, HUR-L1, CRU-L1, MIC-L1, Hurston, Crusader, ArcCorp, microTech, Pyro, Nyx, " +
                       "Daymar, Yela, Cellin, Calliope, MFD, IFCS, SCM, mobiGlas, Quantum Drive, spool, despool, gimbal, chaff.";
            }
            if (packs.Any(p => p.Contains("Singularity", StringComparison.OrdinalIgnoreCase)))
            {
                return "Elite Dangerous ship voice commands: Supercruise, Frameshift Drive, engage jump, FSS, DSS, " +
                       "flight assist off, chaff, heatsink, shield cell, cargo scoop, landing gear, hardpoints, " +
                       "galaxy map, system map, next fire group, power to engines, power to shields, power to weapons, " +
                       "request docking, Astra, Verity, Vega, Orion, Eli, Carina, Vasco.";
            }
            return DefaultSpaceSimPrompt;
        }
    }
}
