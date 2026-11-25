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
    /// merges live VoiceAttack hcsvt_* Customiser overrides, expands [a;b] and [1..10] permutations,
    /// normalizes HCS SAPI phonetic hacks ("em eff dee", "E S P", number words vs digits, SC spellings),
    /// and resolves Whisper transcriptions in < 2 ms.
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
            // Course plotting phrasing normalizations ("plotting course" -> "plot a course")
            (new Regex(@"\bplotting\s+(?:a\s+)?(course|route)\b", RegexOptions.Compiled), "plot a $1"),
            (new Regex(@"\bplot\s+(course|route)\b", RegexOptions.Compiled), "plot a $1"),
            (new Regex(@"\bsetting\s+(?:a\s+)?(course|route)\b", RegexOptions.Compiled), "set a $1"),
            (new Regex(@"\bset\s+(course|route)\b", RegexOptions.Compiled), "set a $1"),
            // Star Citizen location homophones & compound names
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
            ["dismebark"] = "disembark", // Fixes typo in HCS Event Horizon trigger file!
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

        public void LoadFromVoiceAttack(string vaAppsDir, string activeProfileName, Func<string, string> liveVaGetText, Func<string, bool> liveVaCommandExists)
        {
            _exactMap.Clear();
            _tokenInvertedIndex.Clear();
            _allEntries.Clear();
            TotalCommandsCount = 0;

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

            // Determine which .hcs files match the active VoiceAttack profile
            var selectedFiles = SelectMatchingHcsFiles(allEnglishFiles, activeProfileName, liveVaCommandExists);
            var loadedPackNames = new List<string>();

            foreach (string file in selectedFiles)
            {
                string packName = Path.GetFileNameWithoutExtension(file).Replace("English ", "");
                loadedPackNames.Add(packName);
                IndexSingleHcsFile(file, liveVaGetText);
            }

            // If Event Horizon is loaded, dynamically ingest Star Citizen routable locations from HcsMfdLayouts.dll!
            if (loadedPackNames.Any(p => p.Contains("Event Horizon", StringComparison.OrdinalIgnoreCase)))
            {
                IndexStarCitizenNavigationPlaces(vaAppsDir, liveVaCommandExists);
            }

            ActivePackSummary = $"{string.Join(" + ", loadedPackNames)} ({TotalCommandsCount:N0} commands / {_allEntries.Count:N0} phrases)";
            WhisperPrompt = BuildPromptForPacks(loadedPackNames);
        }

        private void IndexStarCitizenNavigationPlaces(string vaAppsDir, Func<string, bool> liveVaCommandExists)
        {
            var placeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Dynamically extract all routable Star Citizen places from HCS's own HcsMfdLayouts.dll (auto-updates with SC patches!)
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
                                if (!string.IsNullOrWhiteSpace(name) && !name.Contains('#'))
                                {
                                    placeNames.Add(name.Trim());
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
            catch
            {
                // Fallback to core list below if HcsMfdLayouts internals ever change
            }

            // 2. Always ensure core Stanton / Pyro / Nyx hubs & common short names are present
            string[] corePlaces =
            {
                "New Babbage", "Lorville", "Orison", "Area18", "Area 18",
                "Port Tressler", "Everus Harbor", "Baijini Point", "Seraphim Station", "Seraphim",
                "Grim HEX", "Grim Hex", "Ruin Station", "Orbituary", "Checkmate", "Patch City",
                "Levski", "Stanton Gateway", "Pyro Gateway", "Nyx Gateway", "Terra Gateway",
                "Hurston", "Crusader", "ArcCorp", "microTech", "Pyro", "Nyx",
                "Bloom", "Monox", "Terminus", "Ignis", "Vatra", "Adir", "Fairo", "Fuego", "Vuur",
                "Daymar", "Yela", "Cellin", "Aberdeen", "Arial", "Magda", "Ita",
                "Lyria", "Wala", "Calliope", "Clio", "Euterpe",
                "Brio's Breaker Yard", "Ashland", "Golden Riviera", "Gaslight", "Starlight Service Station",
                "Anvik", "Cantwell", "Kaltag", "McGrath",
                "HUR-L1", "HUR-L2", "HUR-L3", "HUR-L4", "HUR-L5",
                "CRU-L1", "CRU-L4", "CRU-L5",
                "ARC-L1", "ARC-L2", "ARC-L3", "ARC-L4", "ARC-L5",
                "MIC-L1", "MIC-L2", "MIC-L3", "MIC-L4", "MIC-L5"
            };
            foreach (string cp in corePlaces) placeNames.Add(cp);

            // Expand station short/long aliases (e.g. "HUR-L1 Green Glade Station" -> also try "Green Glade Station" & "HUR-L1")
            var expandedPlaces = new HashSet<string>(placeNames, StringComparer.OrdinalIgnoreCase);
            var lagrangeRegex = new Regex(@"^([A-Z]{3}-L\d)\s+(.+)$", RegexOptions.Compiled);
            foreach (string p in placeNames)
            {
                var m = lagrangeRegex.Match(p);
                if (m.Success)
                {
                    expandedPlaces.Add(m.Groups[1].Value);
                    expandedPlaces.Add(m.Groups[1].Value.Replace("-", " "));
                    expandedPlaces.Add(m.Groups[2].Value);
                }
                if (p.Equals("Area18", StringComparison.OrdinalIgnoreCase))
                    expandedPlaces.Add("Area 18");
            }

            string[] plotPrefixes = { "Plot a course to ", "Plot course to ", "Set a course to ", "Set course to ", "Plot a route to ", "Set a route to " };
            int addedDestinations = 0;

            foreach (string place in expandedPlaces)
            {
                string winningCommand = null;
                if (liveVaCommandExists != null)
                {
                    foreach (string pfx in plotPrefixes)
                    {
                        string candidateCmd = pfx + place;
                        if (SafeVaExists(liveVaCommandExists, candidateCmd))
                        {
                            winningCommand = candidateCmd;
                            break;
                        }
                    }
                }
                else
                {
                    winningCommand = "Plot a course to " + place;
                }

                if (winningCommand == null)
                    continue;

                addedDestinations++;
                TotalCommandsCount++;

                // Index natural spoken variations ("plot a course to X", "set a course to X", "plot a route to X")
                // all pointing directly to the verified VoiceAttack command!
                foreach (string spokenPrefix in new[] { "plot a course to ", "set a course to ", "plot a route to ", "plot a course for " })
                {
                    IndexDirectPhrase(spokenPrefix + place, winningCommand, $"Plot course: {place}");
                }
            }
        }

        private void IndexDirectPhrase(string spokenForm, string rawCommandToExecute, string friendlyName)
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
                IsDangerous = false
            };

            if (!_exactMap.TryGetValue(canonical, out var bucket))
            {
                bucket = new List<IndexedEntry>(1);
                _exactMap[canonical] = bucket;
            }
            if (bucket.Any(b => string.Equals(b.RawPhrase, rawCommandToExecute, StringComparison.OrdinalIgnoreCase)))
                return;

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

            // 1. Direct profile name match (e.g. "Event Horizon", "Singularity", "AdAstra", "NMS", "X4")
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

            // 2. If profile was renamed by user, probe VoiceAttack Command.Exists with signature commands
            if (result.Count == 0 && commandExists != null)
            {
                foreach (string f in files)
                {
                    if (ProbeHcsFileActive(f, commandExists))
                        result.Add(f);
                }
            }

            // 3. Fallback: load Event Horizon & Singularity if present, else all English files
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

            // Always include AdAstra companion triggers if installed alongside Singularity/Event Horizon and active
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
            catch
            {
                // Ignore malformed probe lines
            }
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

                    // Prefer live VoiceAttack Customiser override if populated!
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
                catch
                {
                    // Skip occasional non-strict comment/help lines
                }
            }

            // Common alias used in some HCS files
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
                    if (cleanRaw.Length == 0)
                        continue;

                    string canonical = Canonicalize(cleanRaw);
                    if (canonical.Length == 0)
                        continue;

                    string[] tokens = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string[] phonetics = tokens.Select(ToSoundexKey).ToArray();
                    bool dangerous = DangerousKeywords.Any(d => canonical.Contains(d, StringComparison.Ordinal));

                    var entry = new IndexedEntry
                    {
                        Canonical = canonical,
                        Tokens = tokens,
                        PhoneticTokens = phonetics,
                        RawPhrase = cleanRaw,
                        FriendlyName = item.FriendlyName,
                        IsDangerous = dangerous
                    };

                    if (!_exactMap.TryGetValue(canonical, out var bucket))
                    {
                        bucket = new List<IndexedEntry>(1);
                        _exactMap[canonical] = bucket;
                    }

                    // Avoid duplicate identical raw phrases in same canonical bucket
                    if (bucket.Any(b => string.Equals(b.RawPhrase, cleanRaw, StringComparison.OrdinalIgnoreCase)))
                        continue;

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
            }
        }

        private static string ResolveVariables(string text, Dictionary<string, string> vars, int depth)
        {
            if (depth > 4 || !text.Contains("{TXT:", StringComparison.Ordinal))
                return text;

            return VarTokenRegex.Replace(text, m =>
            {
                string key = m.Groups[1].Value;
                // For StandBy / OnMyMark optional wrappers, default to empty + primary option so base command stays compact
                if (vars.TryGetValue(key, out string val))
                    return ResolveVariables(val, vars, depth + 1);
                return "";
            });
        }

        /// <summary>
        /// Expands VoiceAttack dynamic phrase syntax: top-level ';', '[a;b]', optional '[a;]', and numeric ranges '[1..10]'.
        /// </summary>
        public static List<string> ExpandTriggerPhrases(string pattern, int maxPermutations = 180)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(pattern))
                return results;

            // Convert [1..10] or [1..5;] into [1;2;...;10]
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

            // Split top-level ';' outside square brackets
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

                    // Find matching ']' handling any nested '['
                    int depth = 1;
                    int close = open + 1;
                    while (close < trimmedSeg.Length && depth > 0)
                    {
                        if (trimmedSeg[close] == '[') depth++;
                        else if (trimmedSeg[close] == ']') depth--;
                        close++;
                    }
                    close--; // index of ']'

                    string literalBefore = trimmedSeg.Substring(pos, open - pos);
                    string inside = close > open ? trimmedSeg.Substring(open + 1, close - open - 1) : "";
                    List<string> choices = SplitOutsideBrackets(inside, ';');

                    // Flatten any nested brackets inside choices
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

                    // Prioritize empty option first if optional (keeps shortest canonical command guaranteed!)
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
                            string combined = prefix + literalBefore + choice;
                            nextList.Add(combined);
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
            s = s.Replace("%", " percent ").Replace("-", " ").Replace("'", "");
            // Split attached letter+digit boundaries like "Area18" -> "area 18" so "Area 18" and "Area18" match identically
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

            // Detect n-gram phrase repetition loops (e.g., "open map open map open map" -> "open map")
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

                // If repeated >= 3 times (or >= 2 times covering almost the whole string, excluding "eject eject eject"!)
                if (repeats >= 2 && idx >= words.Length - patLen)
                {
                    if (patLen == 1 && string.Equals(words[0], "eject", StringComparison.Ordinal) && repeats == 3)
                        return canonical; // Keep intentional "eject eject eject" intact!
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

            // 1. Direct O(1) Canonical Hit
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

            // 2. Conversational Filler-Stripped Hit ("uh deploy landing gear please")
            string stripped = StripConversationalFillers(canonical);
            if (!string.Equals(stripped, canonical, StringComparison.Ordinal) && stripped.Length > 0)
            {
                var fillerExact = TryLookupExact(stripped, vaCommandExists);
                if (fillerExact != null)
                {
                    return new MatchResult
                    {
                        RawText = cleanedWhisper,
                        CanonicalInput = stripped,
                        MatchedCommand = fillerExact.RawPhrase,
                        FriendlyName = fillerExact.FriendlyName,
                        Score = 0.98,
                        IsExact = true
                    };
                }
                canonical = stripped;
            }

            // 3. Token-Indexed + Phonetic Fuzzy Snap
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

                // If single-word / phonetic mishearing (e.g. "kwantum" -> "quantum"), scan entries of similar token count
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

            // 4. Fallback: check if user has a custom non-HCS VoiceAttack command matching raw text
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
            // Normalize leading 'kw'/'qu', 'sh'/'ch', 'ph'/'f' for sci-fi homophones ("shaft"/"chaff", "kwantum"/"quantum")
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
                return "Star Citizen voice commands: Plot a course to New Babbage, Lorville, Orison, Area 18, Port Tressler, " +
                       "Everus Harbor, Baijini Point, Seraphim Station, Grim HEX, Ruin Station, Orbituary, Checkmate, Levski, " +
                       "Hurston, Crusader, ArcCorp, microTech, Pyro, Nyx, Daymar, Yela, Cellin, Calliope, Clio, Euterpe, " +
                       "Aberdeen, Lyria, Wala, MFD, IFCS, SCM, mobiGlas, Quantum Drive, spool, despool, gimbal, decoupled, chaff.";
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
