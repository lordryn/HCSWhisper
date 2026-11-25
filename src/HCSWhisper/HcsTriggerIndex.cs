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
        public string DynamicDestination { get; init; }
        public double Score { get; init; }
        public bool IsExact { get; init; }
    }

    /// <summary>
    /// Parses HCS VoicePack plain-text JSON trigger files (Event Horizon, Singularity, AdAstra, etc.),
    /// dynamically ingests all 675 Star Citizen destinations from HcsMfdLayouts.dll (bridging the 490+ places
    /// omitted from HCS's .vap SAPI brackets via hcsvtSC_Plot_Custom_10), normalizes SAPI & Whisper homophones,
    /// and resolves utterances in < 2 ms using target-focused Double-Metaphone phonetic scoring.
    /// </summary>
    public sealed class HcsTriggerIndex
    {
        public const string CustomDestinationSlotCommand = "hcsvtSC_Plot_Custom_10";
        private const string NavCoursePrefix = "plot a course to ";

        private sealed class IndexedEntry
        {
            public string Canonical { get; init; }
            public string[] Tokens { get; init; }
            public string[] PhoneticTokens { get; init; }
            public string PhoneticJoined { get; init; }
            public string NavTargetCanonical { get; init; }
            public string[] NavTargetTokens { get; init; }
            public string[] NavTargetPhoneticTokens { get; init; }
            public string NavTargetPhoneticJoined { get; init; }
            public string RawPhrase { get; init; }
            public string FriendlyName { get; init; }
            public string DynamicDestination { get; init; }
            public bool IsDangerous { get; init; }
            public bool ExactOnly { get; init; }
        }

        private readonly Dictionary<string, List<IndexedEntry>> _exactMap = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<int>> _tokenInvertedIndex = new(StringComparer.Ordinal);
        private readonly List<IndexedEntry> _allEntries = new();
        private readonly List<IndexedEntry> _navPlaceEntries = new();

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

        // Generic navigation targets handled by dedicated non-place commands ("plot a course to my mission", etc.)
        private static readonly string[] NonPlaceNavKeywords =
        {
            "mission", "objective", "drop off", "delivery point", "pick up", "collection point",
            "next leg", "second leg", "nearest ", "waypoint", "nebula", "expedition"
        };

        private static readonly (Regex Pattern, string Replacement)[] MultiWordReplacements =
        {
            // SAPI acronym & phonetic unifications (applied identically to both .hcs triggers and Whisper text)
            (new Regex(@"\bem\s+eff\s+dee\b", RegexOptions.Compiled), "mfd"),
            (new Regex(@"\bm\s+f\s+d\b", RegexOptions.Compiled), "mfd"),
            (new Regex(@"\bi\s+f\s+c\s+s\b", RegexOptions.Compiled), "ifcs"),
            (new Regex(@"\be\s+s\s+p\b", RegexOptions.Compiled), "esp"),
            (new Regex(@"\bs\s+c\s+m\b", RegexOptions.Compiled), "scm"),
            (new Regex(@"\bv\s+t\s+o\s+l\b", RegexOptions.Compiled), "vtol"),
            (new Regex(@"\bv\s+toll\b", RegexOptions.Compiled), "vtol"),
            (new Regex(@"\bvee\s+a[hr]\b", RegexOptions.Compiled), "vr"),
            (new Regex(@"\bv\s+r\b", RegexOptions.Compiled), "vr"),
            (new Regex(@"\bf\s+o\s+i\s+p\b", RegexOptions.Compiled), "foip"),
            (new Regex(@"\bh\s+u\s+d\b", RegexOptions.Compiled), "hud"),
            (new Regex(@"\bg\s+u\s+i\b", RegexOptions.Compiled), "gui"),
            (new Regex(@"\be\s+m\s+p\b", RegexOptions.Compiled), "emp"),
            (new Regex(@"\be\s+c\s+m\b", RegexOptions.Compiled), "ecm"),
            (new Regex(@"\bf\s+o\s+v\b", RegexOptions.Compiled), "fov"),
            (new Regex(@"\bf\s+s\s+d\b", RegexOptions.Compiled), "fsd"),
            (new Regex(@"\bf\s+t\s+l\b", RegexOptions.Compiled), "ftl"),
            (new Regex(@"\bf\s+s\s+s\b", RegexOptions.Compiled), "fss"),
            (new Regex(@"\bd\s+s\s+s\b", RegexOptions.Compiled), "dss"),
            (new Regex(@"\be\s+v\s+a\b", RegexOptions.Compiled), "eva"),
            (new Regex(@"\ba\s+d\s+s\b", RegexOptions.Compiled), "ads"),
            (new Regex(@"\bh\s+m\s+d\b", RegexOptions.Compiled), "hmd"),
            (new Regex(@"\bh\s+t\s+c\b", RegexOptions.Compiled), "htc"),
            (new Regex(@"\bs\s+r\s+v\b", RegexOptions.Compiled), "srv"),
            (new Regex(@"\bl\s+z\b", RegexOptions.Compiled), "lz"),
            (new Regex(@"\bc\s+q\s+c\b", RegexOptions.Compiled), "cqc"),
            (new Regex(@"\bsee\s+que\s+see\b", RegexOptions.Compiled), "cqc"),
            (new Regex(@"\ba\s+f\s+m\s+u\s*s?\b", RegexOptions.Compiled), "afmu"),
            (new Regex(@"\bay\s+eye\b", RegexOptions.Compiled), "ai"),
            (new Regex(@"\bq\s+v\b", RegexOptions.Compiled), "qv"),
            (new Regex(@"\bx\s+ray\b", RegexOptions.Compiled), "xray"),
            (new Regex(@"\bhigh\s+rez\b", RegexOptions.Compiled), "high res"),
            (new Regex(@"\bre\s+entry\b", RegexOptions.Compiled), "reentry"),
            (new Regex(@"\bout\s+fit\b", RegexOptions.Compiled), "outfit"),
            (new Regex(@"\bre\s+arm\b", RegexOptions.Compiled), "rearm"),
            (new Regex(@"\binter\s+sec\b", RegexOptions.Compiled), "intersec"),
            (new Regex(@"\bowda\b", RegexOptions.Compiled), "out of"),
            (new Regex(@"\b(?:mobi|moby|mobile|movie)\s+glass?\b", RegexOptions.Compiled), "mobiglas"),
            (new Regex(@"\bmulti\s+tool\b", RegexOptions.Compiled), "multitool"),
            (new Regex(@"\bmed\s+pack\b", RegexOptions.Compiled), "medpack"),
            (new Regex(@"\bmedi\s+pack\b", RegexOptions.Compiled), "medipack"),
            (new Regex(@"\bmed\s+gun\b", RegexOptions.Compiled), "medgun"),
            (new Regex(@"\b(?:de|the)\s+coupled\b", RegexOptions.Compiled), "decoupled"),
            (new Regex(@"\bde\s+couple\b", RegexOptions.Compiled), "decouple"),
            (new Regex(@"\bthe\s+couple\s+mode\b", RegexOptions.Compiled), "decouple mode"),
            (new Regex(@"\bre\s+couple\b", RegexOptions.Compiled), "recouple"),
            (new Regex(@"\b(?:de|this|the|dis)\s+spool\b", RegexOptions.Compiled), "despool"),
            (new Regex(@"\b(?:this|dis)\s+pool\b", RegexOptions.Compiled), "despool"),
            (new Regex(@"\b(?:pool|school)\s+(the\s+)?(jump|quantum)\s+drive\b", RegexOptions.Compiled), "spool $1$2 drive"),
            (new Regex(@"\bframe\s+shift\b", RegexOptions.Compiled), "frameshift"),
            (new Regex(@"\bsuper\s+cruise\b", RegexOptions.Compiled), "supercruise"),
            (new Regex(@"\bheat\s+s(?:ink|ync)s?\b", RegexOptions.Compiled), "heatsink"),
            (new Regex(@"\bhard\s+points?\b", RegexOptions.Compiled), "hardpoints"),
            (new Regex(@"\bsub\s+targets?\b", RegexOptions.Compiled), "subtarget"),
            (new Regex(@"\bcounter\s+measures?\b", RegexOptions.Compiled), "countermeasure"),
            (new Regex(@"\bre\s+spawn\b", RegexOptions.Compiled), "respawn"),
            (new Regex(@"\bflash\s+light\b", RegexOptions.Compiled), "flashlight"),

            // Ship & FPS command contextual homophones
            (new Regex(@"\b(deploy|drop|launch|fire)\s+(?:shaft|calf|chef)\b", RegexOptions.Compiled), "$1 chaff"),
            (new Regex(@"\b(enable|disable|activate|deactivate|engage|disengage|deploy|switch\s+to|switch\s+off)\s+(?:vital|beetle|veto)(\s+mode)?\b", RegexOptions.Compiled), "$1 vtol$2"),
            (new Regex(@"\bset\s+(?:pips|tips|hips|chips|clips)\s+to\s+leg\b", RegexOptions.Compiled), "set pips to lag"),
            (new Regex(@"\bset\s+(?:tips|hips|chips|clips)\s+to\s+(lag|lead)\b", RegexOptions.Compiled), "set pips to $1"),
            (new Regex(@"\b(increase\s+)?(?:4|four|for)\s+(shields?|deflectors?)\b", RegexOptions.Compiled), "$1fore $2"),
            (new Regex(@"\b(swap|switch|cycle)\s+(?:your|youre|yall)\b", RegexOptions.Compiled), "$1 yaw"),
            (new Regex(@"\b(switch|change|activate)(\s+to)?\s+factor\s+mode\b", RegexOptions.Compiled), "$1$2 fracture mode"),
            (new Regex(@"\bsavage\s+(mode|gimbal|modifiers?)\b", RegexOptions.Compiled), "salvage $1"),
            (new Regex(@"\b(space|speed|boost)\s+break\b", RegexOptions.Compiled), "$1 brake"),
            (new Regex(@"\b(?:hell|pale|mail)\s+target\b", RegexOptions.Compiled), "hail target"),

            // Singularity astronomy / Zaonce Expedition phonetic unifications
            (new Regex(@"\bplay(?:er)?\s+deez\b", RegexOptions.Compiled), "pleiades"),
            (new Regex(@"\bor\s+air\b", RegexOptions.Compiled), "orrere"),
            (new Regex(@"\bwitch\s+hall\b", RegexOptions.Compiled), "witchhaul"),
            (new Regex(@"\b(?:39|thirty\s+nine)\s+(?:tory|tahri|towery)\b", RegexOptions.Compiled), "thirty nine tauri"),
            (new Regex(@"\bgeorge\s+panter\s+ziss\b", RegexOptions.Compiled), "george pantazis"),
            (new Regex(@"\ball?\s+(?:tare|tie(?:\s+air)?)\b", RegexOptions.Compiled), "altair"),

            // Unify all course-plotting phrasing ("set a course for", "plotting course to", "plot a route for", "navigate to")
            (new Regex(@"\b(?:plot|plotting|set|setting|chart|charting|lay\s+in)\s+(?:a\s+)?(?:course|route)\s+(?:to|for)\b", RegexOptions.Compiled), "plot a course to"),
            (new Regex(@"^(?:navigate|head)\s+to\b", RegexOptions.Compiled), "plot a course to"),

            // Star Citizen multi-word location homophones & compound spellings
            (new Regex(@"\bnew\s+(?:beverage|vavage|bavage|cabbage|baggage|babidge|babage)\b", RegexOptions.Compiled), "new babbage"),
            (new Regex(@"\b(?:everest|everis|averys)\s+harbou?r\b", RegexOptions.Compiled), "everus harbor"),
            (new Regex(@"\beverus\s+harbour\b", RegexOptions.Compiled), "everus harbor"),
            (new Regex(@"\b(?:port|fort)\s+(?:wrestler|trestler|tresler|tressler)\b", RegexOptions.Compiled), "port tressler"),
            (new Regex(@"\b(?:by\s+genie|bi\s+jini|bai\s+jini|bikini\s+point)\b", RegexOptions.Compiled), "baijini point"),
            (new Regex(@"\b(?:pharaoh|fairo|faro|farro)\s+data\s+cent(?:er|re)\b", RegexOptions.Compiled), "farro data center"),
            (new Regex(@"\bmicro\s+tech\b", RegexOptions.Compiled), "microtech"),
            (new Regex(@"\b(?:arc|ark)\s+corp\b", RegexOptions.Compiled), "arccorp"),
            (new Regex(@"\bgrim\s*hex\b", RegexOptions.Compiled), "grim hex"),
            (new Regex(@"\byou\s+terp\b", RegexOptions.Compiled), "euterpe"),
            (new Regex(@"\bday\s+mar\b", RegexOptions.Compiled), "daymar"),
            (new Regex(@"\bdel\s+mar\b", RegexOptions.Compiled), "delamar"),
            (new Regex(@"\bde\s+la\s+mar\b", RegexOptions.Compiled), "delamar"),
            (new Regex(@"\bwiki\s+low\b", RegexOptions.Compiled), "wikelo"),
        };

        private static readonly Dictionary<string, string> SingleWordNormalizations = new(StringComparer.Ordinal)
        {
            ["mobiglass"] = "mobiglas",
            ["gimballs"] = "gimbals",
            ["gimball"] = "gimbal",
            ["gimble"] = "gimbal",
            ["gimbles"] = "gimbals",
            ["kimball"] = "gimbal",
            ["cymbal"] = "gimbal",
            ["cymbals"] = "gimbals",
            ["unspool"] = "despool",
            ["dispool"] = "despool",
            ["dismebark"] = "disembark",
            ["customise"] = "customize",
            ["defence"] = "defense",
            ["favourite"] = "favorite",
            ["favourites"] = "favorites",
            ["centre"] = "center",
            ["recenter"] = "recenter",
            ["recentre"] = "recenter",
            ["heatsinks"] = "heatsink",
            ["hardpoint"] = "hardpoints",
            ["countermeasures"] = "countermeasure",
            ["hanger"] = "hangar",
            ["forrid"] = "forward",
            ["pappa"] = "papa",
            ["puppa"] = "papa",
            ["alfer"] = "alpha",
            ["beata"] = "beta",
            ["baiter"] = "beta",
            ["beater"] = "beta",
            ["elzee"] = "lz",
            ["afmus"] = "afmu",
            ["rentry"] = "reentry",
            ["sqwark"] = "squawk",
            ["sqwarking"] = "squawking",
            ["zayonts"] = "zaonce",
            ["seance"] = "zaonce",
            ["leasty"] = "leesti",
            ["deeso"] = "diso",
            ["uzzah"] = "uszaa",
            ["oozah"] = "uszaa",
            ["usar"] = "uszaa",
            ["foojin"] = "fujin",

            // Star Citizen single-word place homophones
            ["babage"] = "babbage",
            ["vavage"] = "babbage",
            ["bavage"] = "babbage",
            ["babidge"] = "babbage",
            ["laureville"] = "lorville",
            ["loreville"] = "lorville",
            ["lawville"] = "lorville",
            ["lorvil"] = "lorville",
            ["louisville"] = "lorville",
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
            ["caliope"] = "calliope",
            ["uterpe"] = "euterpe",
            ["euterpi"] = "euterpe",
            ["yella"] = "yela",
            ["celin"] = "cellin",
            ["selen"] = "cellin",
            ["celine"] = "cellin",
            ["walla"] = "wala",
            ["wallah"] = "wala",
            ["voila"] = "wala",
            ["koala"] = "wala",
            ["liria"] = "lyria",
            ["illyria"] = "lyria",
            ["syria"] = "lyria",
            ["neymar"] = "daymar",
            ["damar"] = "daymar",
            ["zefer"] = "zephyr",
            ["wicklow"] = "wikelo",
            ["nix"] = "nyx",
            ["nicks"] = "nyx",
            ["knicks"] = "nyx",
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
            ["22"] = "twenty two",
            ["25"] = "twenty five",
            ["30"] = "thirty",
            ["39"] = "thirty nine",
            ["40"] = "forty",
            ["50"] = "fifty",
            ["60"] = "sixty",
            ["70"] = "seventy",
            ["75"] = "seventy five",
            ["80"] = "eighty",
            ["90"] = "ninety",
            ["100"] = "one hundred",
        };

        private static readonly Dictionary<string, string> RomanNumeralWords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["I"] = "one",
            ["II"] = "two",
            ["III"] = "three",
            ["IV"] = "four",
            ["V"] = "five",
            ["VI"] = "six",
            ["VII"] = "seven",
            ["VIII"] = "eight",
            ["IX"] = "nine",
            ["X"] = "ten",
            ["XX"] = "twenty",
            ["XXII"] = "twenty two"
        };

        private static readonly Dictionary<string, string> HcsFallbackVariables = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hcsvt_ShipsCatN"] = "the cat",
            ["hcsvt_ShipsCatName"] = "the cat",
            ["hcsvt_ShipsParrotName"] = "the parrot",
            ["hcsvt_ShipsDogName"] = "the dog",
            ["hcsvt_CecilCompanionName"] = "Cecil",
            ["hcsvt_MVName"] = "Mainframe",
            ["hcsvt_AstraName"] = "Astra",
            ["hcsvt_AstraTrigger"] = "Astra"
        };

        private static readonly Regex VarTokenRegex = new(@"\{TXT:([A-Za-z0-9_]+)\}", RegexOptions.Compiled);
        private static readonly Regex RangeBracketRegex = new(@"\[(\d+)\.\.(\d+)(;?)\]", RegexOptions.Compiled);
        private static readonly Regex NonWordCleanRegex = new(@"[^a-z0-9\s%]", RegexOptions.Compiled);
        private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex WhisperArtifactRegex = new(@"(\[.*?\]|\(.*?\)|\*.*?\*)", RegexOptions.Compiled);
        private static readonly Regex LagrangeGlueRegex = new(@"\b(arc|hur|cru|mic)\s*l\s*(\d)\b", RegexOptions.Compiled);
        private static readonly Regex PyroLagrangeGlueRegex = new(@"\b(?:p|pyr|pyro)\s*(\d)\s*l\s*(\d)\b", RegexOptions.Compiled);
        private static readonly Regex LagrangeStationTitleRegex = new(@"^(ARC|HUR|CRU|MIC)-L(\d)\s+(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RayariOutpostRegex = new(@"^Rayari\s+(\w+)\s+Research\s+Outpost$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RomanSuffixTokenRegex = new(@"\b(XXII|XX|VIII|VII|III|II|IV|VI|IX|X|V|I)\b", RegexOptions.Compiled);
        private static readonly Regex QvBreakerRegex = new(@"^QV\s+Breaker\s+Station\s+BRK-(\d)(\d)(\d)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
            _navPlaceEntries.Clear();
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

            var resolvedCmdCache = new Dictionary<string, (string WinningCmd, string DynamicDest)>(StringComparer.OrdinalIgnoreCase);

            if (loadedPackNames.Any(p => p.Contains("Event Horizon", StringComparison.OrdinalIgnoreCase)))
            {
                IndexStarCitizenNavigationPlaces(vaAppsDir, liveVaCommandExists, nicknames, resolvedCmdCache);
            }
            else
            {
                ApplyCustomPlaceNicknames(nicknames, liveVaCommandExists, resolvedCmdCache);
            }

            ApplyCustomCommandAliases(nicknames, liveVaCommandExists);

            ActivePackSummary = $"{string.Join(" + ", loadedPackNames)} ({TotalCommandsCount:N0} commands / {_allEntries.Count:N0} phrases)";
            WhisperPrompt = BuildPromptForPacks(loadedPackNames);
        }

        private void IndexStarCitizenNavigationPlaces(
            string vaAppsDir,
            Func<string, bool> liveVaCommandExists,
            NicknameStore nicknames,
            Dictionary<string, (string WinningCmd, string DynamicDest)> resolvedCmdCache)
        {
            // Map cleaned display name -> exact NavPlace.Name in HcsMfdLayouts.dll (preserves quotes on e.g. "\"The Pit\"")
            var rawPlaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
                                string exactName = (placeObj?.GetType().GetProperty("Name")?.GetValue(placeObj) as string)?.Trim();
                                if (!string.IsNullOrWhiteSpace(exactName) &&
                                    !exactName.Contains('#') &&
                                    !exactName.StartsWith("Comm Array", StringComparison.OrdinalIgnoreCase))
                                {
                                    string cleanKey = exactName.Trim('"');
                                    if (!rawPlaces.ContainsKey(cleanKey))
                                        rawPlaces[cleanKey] = exactName;
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

            // 2. Ensure core Stanton / Pyro / Nyx hubs + settlements are always present even in offline tests
            string[] corePlaces =
            {
                "New Babbage", "Lorville", "Orison", "Area18",
                "Port Tressler", "Everus Harbor", "Baijini Point", "Seraphim Station",
                "Grim HEX", "Ruin Station", "Orbituary", "Checkmate", "Patch City",
                "Levski", "Delamar", "Stanton Gateway", "Pyro Gateway", "Nyx Gateway", "Terra Gateway",
                "Hurston", "Crusader", "ArcCorp", "microTech", "Pyro I", "Pyro IV", "Pyro V", "Nyx I", "Nyx II", "Nyx III",
                "Bloom", "Monox", "Terminus", "Ignis", "Vatra", "Adir", "Fairo", "Fuego", "Vuur",
                "Daymar", "Yela", "Cellin", "Aberdeen", "Arial", "Magda", "Ita",
                "Lyria", "Wala", "Calliope", "Clio", "Euterpe",
                "Brio's Breaker Yard", "Ashland", "Chawla's Beach", "Sacren's Plot", "Shepherd's Rest",
                "Seer's Canyon", "Ostler's Claim", "Goner's Deal", "Scarper's Turn", "Dinger's Depot",
                "Narena's Rest", "Kabir's Post", "Prophet's Peak", "Canard View", "Feo Canyon Depot",
                "The Golden Riviera", "Gaslight", "Starlight Service Station", "Rat's Nest", "Rod's Fuel 'N Supplies",
                "Dudley & Daughters", "Megumi Refueling", "Endgame", "Downlow", "Dunlow Ridge Aid Shelter",
                "Security Post Kareah", "Jumptown", "Klescher Rehabilitation Facility", "Ghost Hollow", "Rappel", "Zephyr",
                "Covalex Shipping Hub Gundo", "Dupree Industrial Manufacturing Facility",
                "Rayari Anvik Research Outpost", "Rayari Cantwell Research Outpost",
                "Rayari Kaltag Research Outpost", "Rayari McGrath Research Outpost", "Rayari Deltana Research Outpost",
                "ARC-L1 Wide Forest Station", "ARC-L2 Lively Pathway Station",
                "ARC-L3 Modern Express Station", "ARC-L4 Faint Glen Station", "ARC-L5 Yellow Core Station",
                "HUR-L1 Green Glade Station", "HUR-L2 Faithful Dream Station",
                "HUR-L3 Thundering Express Station", "HUR-L4 Melodic Fields Station", "HUR-L5 High Course Station",
                "CRU-L1 Ambitious Dream Station", "CRU-L4 Shallow Fields Station", "CRU-L5 Beautiful Glen Station",
                "MIC-L1 Shallow Frontier Station", "MIC-L2 Long Forest Station",
                "MIC-L3 Endless Odyssey Station", "MIC-L4 Red Crossroads Station", "MIC-L5 Modern Icarus Station"
            };
            foreach (string cp in corePlaces)
            {
                if (!rawPlaces.ContainsKey(cp))
                    rawPlaces[cp] = cp;
            }

            var displayNamesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in rawPlaces)
            {
                string place = kv.Key;
                string exactMfdName = kv.Value;

                var (winningCmd, dynamicDest) = ResolveWinningPlaceCommand(exactMfdName, liveVaCommandExists, resolvedCmdCache);
                if (winningCmd == null)
                    continue;

                TotalCommandsCount++;
                displayNamesSet.Add(place);

                var autoAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { place };

                var lagMatch = LagrangeStationTitleRegex.Match(place);
                if (lagMatch.Success)
                {
                    string bodyCode = lagMatch.Groups[1].Value.ToUpperInvariant();
                    string lNum = lagMatch.Groups[2].Value;
                    string stationTitle = lagMatch.Groups[3].Value.Trim();

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
                {
                    autoAliases.Add(rayMatch.Groups[1].Value);
                    autoAliases.Add("Rayari " + rayMatch.Groups[1].Value);
                }

                if (place.Equals("Area18", StringComparison.OrdinalIgnoreCase))
                    autoAliases.Add("Area 18");

                // Automatically add spoken number variants for Roman numeral places ("Farro Data Center IV" -> "Farro Data Center four")
                if (RomanSuffixTokenRegex.IsMatch(place))
                {
                    string spokenRoman = RomanSuffixTokenRegex.Replace(place, m =>
                        RomanNumeralWords.TryGetValue(m.Groups[1].Value, out string word) ? word : m.Value);
                    autoAliases.Add(spokenRoman);
                }

                // Automatically add digit-by-digit spoken forms for QV Breaker Stations ("BRK-204" -> "Breaker Station two zero four")
                var qvMatch = QvBreakerRegex.Match(place);
                if (qvMatch.Success)
                {
                    string d1 = SingleWordNormalizations[qvMatch.Groups[1].Value];
                    string d2 = SingleWordNormalizations[qvMatch.Groups[2].Value];
                    string d3 = SingleWordNormalizations[qvMatch.Groups[3].Value];
                    autoAliases.Add($"Breaker Station {d1} {d2} {d3}");
                    autoAliases.Add($"QV Breaker Station {d1} {d2} {d3}");
                    if (qvMatch.Groups[2].Value == "0")
                    {
                        autoAliases.Add($"Breaker Station {d1} oh {d3}");
                        autoAliases.Add($"QV Breaker Station {d1} oh {d3}");
                    }
                }

                foreach (string alias in autoAliases)
                {
                    IndexDirectPhrase(
                        NavCoursePrefix + alias,
                        winningCmd,
                        $"Plot course: {place}",
                        prioritize: false,
                        dynamicDestination: dynamicDest,
                        isNavPlaceEntry: true);
                }
            }

            KnownDestinationNames.AddRange(displayNamesSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            // 3. Index all built-in + user-customizable Place Nicknames & Homophones
            ApplyCustomPlaceNicknames(nicknames, liveVaCommandExists, resolvedCmdCache);
        }

        private void ApplyCustomPlaceNicknames(
            NicknameStore nicknames,
            Func<string, bool> liveVaCommandExists,
            Dictionary<string, (string WinningCmd, string DynamicDest)> resolvedCmdCache)
        {
            if (nicknames?.PlaceNicknames == null)
                return;

            foreach (var kvp in nicknames.PlaceNicknames)
            {
                string nick = kvp.Key?.Trim();
                string targetPlace = kvp.Value?.Trim();
                if (string.IsNullOrEmpty(nick) || string.IsNullOrEmpty(targetPlace))
                    continue;

                var (winningCmd, dynamicDest) = ResolveWinningPlaceCommand(targetPlace, liveVaCommandExists, resolvedCmdCache);
                if (winningCmd != null)
                {
                    IndexDirectPhrase(
                        NavCoursePrefix + nick,
                        winningCmd,
                        $"Nickname '{nick}' → {targetPlace}",
                        prioritize: true,
                        dynamicDestination: dynamicDest,
                        isNavPlaceEntry: true);
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
                string dynDest = null;
                var existing = Match(targetPhrase, liveVaCommandExists, 0.80);
                if (existing != null)
                {
                    resolvedCmd = existing.MatchedCommand;
                    dynDest = existing.DynamicDestination;
                }
                else if (liveVaCommandExists == null || SafeVaExists(liveVaCommandExists, targetPhrase))
                {
                    resolvedCmd = targetPhrase;
                }

                if (resolvedCmd != null)
                {
                    IndexDirectPhrase(alias, resolvedCmd, $"Alias '{alias}' → {resolvedCmd}", prioritize: true, dynamicDestination: dynDest);
                }
            }
        }

        private static (string WinningCmd, string DynamicDest) ResolveWinningPlaceCommand(
            string exactPlace,
            Func<string, bool> liveVaCommandExists,
            Dictionary<string, (string WinningCmd, string DynamicDest)> cache)
        {
            if (string.IsNullOrWhiteSpace(exactPlace))
                return (null, null);

            string p = exactPlace.Trim().Trim('"');
            if (cache.TryGetValue(p, out var cached))
                return cached;

            // Build candidate target names that Event Horizon's hardcoded .vap SAPI commands use
            var candidates = new List<string> { p };

            var lagMatch = LagrangeStationTitleRegex.Match(p);
            if (lagMatch.Success)
            {
                candidates.Add(lagMatch.Groups[3].Value.Trim()); // "Yellow Core Station"
                candidates.Add($"{lagMatch.Groups[1].Value.ToUpperInvariant()}-L{lagMatch.Groups[2].Value}");
            }
            if (p.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
                candidates.Add(p.Substring(4).Trim());
            var rayMatch = RayariOutpostRegex.Match(p);
            if (rayMatch.Success)
            {
                candidates.Add("Rayari " + rayMatch.Groups[1].Value);
                candidates.Add(rayMatch.Groups[1].Value);
            }
            if (p.StartsWith("Rod's ", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add("Rod's Fuel N Supplies");
                candidates.Add("Rod's");
            }
            if (p.Contains(" & ", StringComparison.Ordinal))
                candidates.Add(p.Replace(" & ", " and "));
            if (p.Equals("Humboldt Mines", StringComparison.OrdinalIgnoreCase))
                candidates.Add("Humboldt Mine");
            if (p.StartsWith("HDMS-", StringComparison.OrdinalIgnoreCase))
                candidates.Add(p.Substring(5));
            if (p.Equals("Area18", StringComparison.OrdinalIgnoreCase))
                candidates.Add("Area 18");
            else if (p.Equals("Area 18", StringComparison.OrdinalIgnoreCase))
                candidates.Add("Area18");

            // HCS's .vap spells Nyx Gateway and Nyx I..III with "Nix"!
            if (p.Equals("Nyx Gateway", StringComparison.OrdinalIgnoreCase))
                candidates.Add("Nix Gateway");
            else if (p.Equals("Nyx I", StringComparison.OrdinalIgnoreCase))
                candidates.Add("Nix one");
            else if (p.Equals("Nyx II", StringComparison.OrdinalIgnoreCase))
                candidates.Add("Nix two");
            else if (p.Equals("Nyx III", StringComparison.OrdinalIgnoreCase))
                candidates.Add("Nix three");

            var qvMatch = QvBreakerRegex.Match(p);
            if (qvMatch.Success)
            {
                string d1 = SingleWordNormalizations[qvMatch.Groups[1].Value];
                string d2 = SingleWordNormalizations[qvMatch.Groups[2].Value];
                string d3 = SingleWordNormalizations[qvMatch.Groups[3].Value];
                candidates.Add($"Q V Breaker Station {d1} {d2} {d3}");
                candidates.Add($"Breaker Station {d1} {d2} {d3}");
            }

            string[] plotPrefixes = { "Plot a course to ", "Set a course to ", "Plot course to " };

            if (liveVaCommandExists == null)
            {
                string preferred = lagMatch.Success ? lagMatch.Groups[3].Value.Trim() : p;
                var offlineRes = ("Plot a course to " + preferred, (string)null);
                cache[p] = offlineRes;
                return offlineRes;
            }

            foreach (string candName in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string pfx in plotPrefixes)
                {
                    string fullCmd = pfx + candName;
                    if (SafeVaExists(liveVaCommandExists, fullCmd))
                    {
                        var nativeRes = (fullCmd, (string)null);
                        cache[p] = nativeRes;
                        return nativeRes;
                    }
                }
            }

            // Fallback for the 490+ routable Star Citizen places omitted from Event Horizon's .vap SAPI commands
            // (e.g. Chawla's Beach, Sacren's Plot, Rat's Nest, Bloom, Monox, Terminus, Jumptown, Arial, etc.):
            // Bridge through Event Horizon's native hcsvtSC_Plot_Custom_10 -> ((SC Map - Plot Flow))!
            if (SafeVaExists(liveVaCommandExists, CustomDestinationSlotCommand))
            {
                string mfdTarget = lagMatch.Success ? lagMatch.Groups[3].Value.Trim() : exactPlace.Trim();
                var bridgedRes = (CustomDestinationSlotCommand, mfdTarget);
                cache[p] = bridgedRes;
                return bridgedRes;
            }

            cache[p] = (null, null);
            return (null, null);
        }

        private void IndexDirectPhrase(
            string spokenForm,
            string rawCommandToExecute,
            string friendlyName,
            bool prioritize,
            string dynamicDestination = null,
            bool isNavPlaceEntry = false,
            bool exactOnly = false)
        {
            string canonical = Canonicalize(spokenForm);
            if (canonical.Length == 0)
                return;

            string[] tokens = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string[] phonetics = tokens.Select(ToPhoneticKey).ToArray();
            string phonJoined = string.Join(" ", phonetics);

            string navTarget = null;
            string[] navToks = null;
            string[] navPhon = null;
            string navPhonJoined = null;
            if (isNavPlaceEntry && canonical.StartsWith(NavCoursePrefix, StringComparison.Ordinal) && canonical.Length > NavCoursePrefix.Length)
            {
                navTarget = canonical.Substring(NavCoursePrefix.Length).Trim();
                navToks = navTarget.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                navPhon = navToks.Select(ToPhoneticKey).ToArray();
                navPhonJoined = string.Join(" ", navPhon);
            }

            var entry = new IndexedEntry
            {
                Canonical = canonical,
                Tokens = tokens,
                PhoneticTokens = phonetics,
                PhoneticJoined = phonJoined,
                NavTargetCanonical = navTarget,
                NavTargetTokens = navToks,
                NavTargetPhoneticTokens = navPhon,
                NavTargetPhoneticJoined = navPhonJoined,
                RawPhrase = rawCommandToExecute,
                FriendlyName = friendlyName,
                DynamicDestination = dynamicDestination,
                IsDangerous = DangerousKeywords.Any(d => canonical.Contains(d, StringComparison.Ordinal)),
                ExactOnly = exactOnly
            };

            if (!_exactMap.TryGetValue(canonical, out var bucket))
            {
                bucket = new List<IndexedEntry>(1);
                _exactMap[canonical] = bucket;
            }

            int existingIdx = bucket.FindIndex(b =>
                string.Equals(b.RawPhrase, rawCommandToExecute, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(b.DynamicDestination, dynamicDestination, StringComparison.OrdinalIgnoreCase));
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

            if (exactOnly)
                return;

            int idx = _allEntries.Count;
            _allEntries.Add(entry);

            if (navTarget != null)
                _navPlaceEntries.Add(entry);

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
            var variables = new Dictionary<string, string>(HcsFallbackVariables, StringComparer.OrdinalIgnoreCase);
            var triggerEntries = new List<(string FriendlyName, string VarName, string PrefixVar, string Cat1, string Trigger, bool AddToTriggers)>();

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
                    string prefixVar = root.TryGetProperty("Prefix", out var pxElem) ? (pxElem.GetString() ?? "") : "";
                    string cat1 = (root.TryGetProperty("Categories", out var cats) && cats.TryGetProperty("1", out var c1))
                        ? (c1.GetString() ?? "")
                        : "";
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

                    triggerEntries.Add((friendly, varName, prefixVar, cat1, trigger, addToTriggers));
                }
                catch { }
            }

            if (!variables.ContainsKey("CrewNames") && variables.TryGetValue("hcsvt_CrewNames", out string cn))
                variables["CrewNames"] = cn;

            // Bridge HCS's companion naming mismatch (hcsvt_ShipsParrot -> hcsvt_ShipsParrotName, etc.)
            if (variables.TryGetValue("hcsvt_ShipsParrot", out string sp) && !string.IsNullOrWhiteSpace(sp) && !sp.Contains("{TXT:"))
                variables["hcsvt_ShipsParrotName"] = sp;
            if (variables.TryGetValue("hcsvt_ShipsDog", out string sd) && !string.IsNullOrWhiteSpace(sd) && !sd.Contains("{TXT:"))
                variables["hcsvt_ShipsDogName"] = sd;
            if (variables.TryGetValue("hcsvt_ShipsCatName", out string sc) && !string.IsNullOrWhiteSpace(sc))
                variables["hcsvt_ShipsCatN"] = sc;

            foreach (var item in triggerEntries)
            {
                if (!item.AddToTriggers || string.IsNullOrWhiteSpace(item.Trigger))
                    continue;

                TotalCommandsCount++;
                string fullPattern = !string.IsNullOrEmpty(item.PrefixVar)
                    ? $"{{TXT:{item.PrefixVar}}} {item.Trigger}"
                    : item.Trigger;

                string resolved = ResolveVariables(fullPattern, variables, 0);
                List<string> expanded = ExpandTriggerPhrases(resolved, maxPermutations: 180);

                // Single-character Game Control dictation keys ("1", "2", "B", "D", "I", "V") should match exact only, never hijack fuzzy matching
                bool isSingleKeyDictation = item.Cat1.Equals("Game Control", StringComparison.OrdinalIgnoreCase) &&
                                            item.FriendlyName != null && item.FriendlyName.Trim().Length == 1;

                foreach (string rawPhrase in expanded)
                {
                    string cleanRaw = MultiSpaceRegex.Replace(rawPhrase, " ").Trim();
                    if (cleanRaw.Length > 0)
                        IndexDirectPhrase(cleanRaw, cleanRaw, item.FriendlyName, prioritize: false, exactOnly: isSingleKeyDictation);
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
            s = s.Replace("&", " and ").Replace("%", " percent ").Replace("-", " ").Replace("'", "").Replace("\u2019", "");

            // Separate glued Lagrange codes BEFORE generic letter-digit splitting ("ArcL5" -> "arc l 5", "PYR3 L2" -> "p 3 l 2")
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
                return ToMatchResult(cleanedWhisper, canonical, exact, 1.0, isExact: true);

            string stripped = StripConversationalFillers(canonical);
            if (!string.Equals(stripped, canonical, StringComparison.Ordinal) && stripped.Length > 0)
            {
                string recanon = Canonicalize(stripped);
                var fillerExact = TryLookupExact(recanon, vaCommandExists);
                if (fillerExact != null)
                    return ToMatchResult(cleanedWhisper, recanon, fillerExact, 0.98, isExact: true);
                canonical = recanon;
            }

            // Dedicated Target-Focused Double-Metaphone Navigation Matcher:
            // Avoids boilerplate prefix dilution ("plot a course to ...") so homophones on obscure outposts score sharply
            // while preventing short dissimilar destinations ("yela" vs "ita") from false-matching on shared prefix words!
            if (_navPlaceEntries.Count > 0 &&
                canonical.StartsWith(NavCoursePrefix, StringComparison.Ordinal) &&
                canonical.Length > NavCoursePrefix.Length)
            {
                string targetQuery = canonical.Substring(NavCoursePrefix.Length).Trim();
                if (!NonPlaceNavKeywords.Any(k => targetQuery.Contains(k, StringComparison.Ordinal)))
                {
                    var navMatch = MatchNavigationTarget(cleanedWhisper, canonical, targetQuery, vaCommandExists, threshold);
                    if (navMatch != null)
                        return navMatch;
                }
            }

            if (_allEntries.Count > 0)
            {
                string[] qTokens = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string[] qPhonetic = qTokens.Select(ToPhoneticKey).ToArray();
                string qPhonJoined = string.Join(" ", qPhonetic);

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
                    // Do not let non-matching navigation destinations win via shared "plot a course to" prefix boilerplate
                    if (cand.NavTargetCanonical != null)
                        continue;

                    if (Math.Abs(cand.Canonical.Length - canonical.Length) > Math.Max(8, canonical.Length / 2))
                        continue;

                    double charSim = NormalizedLevenshteinSimilarity(canonical, cand.Canonical);
                    double phonStrSim = NormalizedLevenshteinSimilarity(qPhonJoined, cand.PhoneticJoined);
                    double tokSim = Math.Max(TokenOverlapSimilarity(qTokens, cand.Tokens), TokenOverlapSimilarity(qPhonetic, cand.PhoneticTokens));

                    double combined = 0.42 * charSim + 0.33 * phonStrSim + 0.25 * tokSim;
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
                    return ToMatchResult(cleanedWhisper, canonical, best, bestScore, isExact: false);
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

        private MatchResult MatchNavigationTarget(
            string cleanedWhisper,
            string canonical,
            string targetQuery,
            Func<string, bool> vaCommandExists,
            double threshold)
        {
            string[] qToks = targetQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string[] qPhon = qToks.Select(ToPhoneticKey).ToArray();
            string qPhonJoined = string.Join(" ", qPhon);

            IndexedEntry best = null;
            double bestScore = 0;
            // Destination-only threshold is slightly more forgiving on spelling when phonetic skeleton matches strongly
            double navMin = Math.Max(0.70, threshold - 0.04);

            foreach (var cand in _navPlaceEntries)
            {
                if (Math.Abs(cand.NavTargetTokens.Length - qToks.Length) > 1)
                    continue;
                if (Math.Abs(cand.NavTargetCanonical.Length - targetQuery.Length) > Math.Max(6, targetQuery.Length / 2))
                    continue;

                double charSim = NormalizedLevenshteinSimilarity(targetQuery, cand.NavTargetCanonical);
                double phonStrSim = NormalizedLevenshteinSimilarity(qPhonJoined, cand.NavTargetPhoneticJoined);
                double tokSim = Math.Max(
                    TokenOverlapSimilarity(qToks, cand.NavTargetTokens),
                    TokenOverlapSimilarity(qPhon, cand.NavTargetPhoneticTokens));

                double score = 0.40 * charSim + 0.40 * phonStrSim + 0.20 * tokSim;
                if (score >= navMin && score > bestScore)
                {
                    if (vaCommandExists == null || SafeVaExists(vaCommandExists, cand.RawPhrase))
                    {
                        bestScore = score;
                        best = cand;
                    }
                }
            }

            return best != null ? ToMatchResult(cleanedWhisper, canonical, best, bestScore, isExact: false) : null;
        }

        private static MatchResult ToMatchResult(string rawWhisper, string canonical, IndexedEntry entry, double score, bool isExact)
        {
            return new MatchResult
            {
                RawText = rawWhisper,
                CanonicalInput = canonical,
                MatchedCommand = entry.RawPhrase,
                FriendlyName = entry.FriendlyName,
                DynamicDestination = entry.DynamicDestination,
                Score = score,
                IsExact = isExact
            };
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

        /// <summary>
        /// Double-Metaphone-inspired English phonetic skeleton so homophones like
        /// "saccharins" / "sacrens" -> "skrns", "pharaoh" / "fairo" -> "fr", "kareah" / "korea" -> "kr"
        /// produce identical or near-identical phonetic keys.
        /// </summary>
        public static string ToPhoneticKey(string word)
        {
            if (string.IsNullOrEmpty(word)) return "";
            string w = word.ToLowerInvariant();

            switch (w)
            {
                case "zero": case "oh": return "zro";
                case "one": case "won": return "wn";
                case "two": case "to": case "too": return "tw";
                case "three": return "0r";
                case "four": case "for": case "fore": return "fr";
                case "five": return "fv";
                case "six": return "sks";
                case "seven": return "svn";
                case "eight": case "ate": return "at";
                case "nine": return "nn";
                case "ten": return "tn";
            }

            if (w.StartsWith("kn") || w.StartsWith("gn") || w.StartsWith("pn") || w.StartsWith("wr") || w.StartsWith("ps"))
                w = w.Substring(1);
            else if (w.StartsWith("wh"))
                w = "w" + w.Substring(2);
            else if (w.StartsWith("x"))
                w = "z" + w.Substring(1);

            w = w.Replace("tch", "C")
                 .Replace("sh", "X")
                 .Replace("ch", "C")
                 .Replace("ph", "f")
                 .Replace("gh", "")
                 .Replace("th", "0")
                 .Replace("dg", "j")
                 .Replace("ck", "k")
                 .Replace("qu", "kw")
                 .Replace("tion", "Xn")
                 .Replace("sion", "Xn");

            w = Regex.Replace(w, @"c([eiy])", "s$1");
            w = Regex.Replace(w, @"g([eiy])", "j$1");
            w = w.Replace("c", "k").Replace("q", "k").Replace("x", "ks").Replace("z", "s").Replace("v", "f");

            if (w.Length > 2 && w.EndsWith("e"))
                w = w.Substring(0, w.Length - 1);

            var sb = new StringBuilder(w.Length);
            for (int i = 0; i < w.Length; i++)
            {
                char c = w[i];
                bool isVowel = "aeiouy".IndexOf(c) >= 0;
                if (i == 0)
                {
                    sb.Append(isVowel ? 'a' : c);
                }
                else if (!isVowel && c != 'h' && c != 'w')
                {
                    if (sb[sb.Length - 1] != c)
                        sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static string BuildPromptForPacks(List<string> packs)
        {
            if (packs.Any(p => p.Contains("Event Horizon", StringComparison.OrdinalIgnoreCase)))
            {
                return "Star Citizen voice commands: Plot a course to New Babbage, Lorville, Orison, Area18, " +
                       "Port Tressler, Everus Harbor, Baijini Point, Seraphim, Grim HEX, Ruin Station, Orbituary, Checkmate, " +
                       "Ashland, Chawla's Beach, Sacren's Plot, Rat's Nest, Levski, ARC-L1, HUR-L1, CRU-L1, MIC-L1, " +
                       "Hurston, Crusader, ArcCorp, microTech, Pyro, Nyx, Daymar, Yela, Cellin, Calliope, Clio, Euterpe, Arial, " +
                       "MFD, IFCS, SCM, VTOL, FOIP, mobiGlas, Quantum Drive, spool, despool, gimbal, chaff.";
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
