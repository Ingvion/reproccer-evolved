using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReProccer.Utils;

public struct StaticsData
{
    public string Id { get; set; }
    public string Desc { get; set; }
    public FormKey Kwda { get; set; }
    public List<FormKey> Items { get; set; }
    public List<FormKey> Perks { get; set; }

    public FormKey FormKey
    {
        get => Kwda;
        set => Kwda = value;
    }

    public FormKey Expl
    {
        get => Kwda;
        set => Kwda = value;
    }

    public static FormKey NullRef => new("Skyrim.esm", 0x000000);
};

public struct RecordData
{
    public bool NonPlayable { get; set; }
    public bool Modified { get; set; }
    public bool Overridden { get; set; }
    public bool Unique { get; set; }
    public bool BoundWeapon { get; set; }
    public ArmorType ArmorType { get; set; }
    public WeaponAnimationType AnimType { get; set; }
    public IMajorRecordGetter? ThisRecord { get; set; }
    public Logger Log;
    public bool IsArrow
    {
        get => BoundWeapon;
        set => BoundWeapon = value;
    }
}

public struct RecipeData
{
    public List<FormKey> Items { get; set; }
    public int Qty { get; set; }
}

public struct EditorIDs()
{
    public List<string> List { get; set; } = [];
    public readonly string Unique(string editorID)
    {
        int? incr = null;
        while (List.Contains($"{editorID}{incr}"))
        {
            incr = incr == null ? 1 : ++incr;
        }

        List.Add($"{editorID}{incr}");
        return $"{editorID}{incr}";
    }
}

public struct Report()
{
    public IMajorRecordGetter? Record;
    public Logger? Entry;
}

public readonly struct Logger()
{
    private readonly List<string> InfoMsg = [];
    private readonly List<string> CautionMsg = [];
    private readonly List<string> ErrorMsg = [];
    private static readonly string[] Filter = Executor.Settings!.Debug.ReportFilter
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public readonly void Info(string msg, bool verboseInfo = false, bool onTop = false)
    {
        if (Executor.Settings!.Debug.ShowVerboseData || !verboseInfo) 
            InfoMsg.Insert(onTop ? 0 : InfoMsg.Count, msg);
    } 
    public readonly void Caution(string msg) => CautionMsg.Add(msg);
    public readonly void Error(string msg) => ErrorMsg.Add(msg);

    public readonly void Report(string name, string formkey, string editorid, bool nonPlayable, bool isTemplated)
    {
        if (Filter.Length > 0 && !Filter.Any(name.Contains)) return;

        List<List<string>> messages = [InfoMsg, CautionMsg, ErrorMsg];
        if (messages.All(group => group.Count == 0)) return;

        string[] groups = ["    > INFO:", "    > CAUTION:", "    > ERROR:"];
        if (Executor.Settings!.Debug.ShowNonPlayable || !nonPlayable)
        {
            Console.WriteLine($"+ REPORT | {name} ({formkey} | {editorid})");
            foreach (var msgGroup in messages)
            {
                if (msgGroup.Count == 0) continue;
                Console.WriteLine($"{groups[messages.IndexOf(msgGroup)]}");
                if (messages.IndexOf(msgGroup) == 0)
                {
                    if (nonPlayable) InfoMsg.Insert(0, "Is non-playable");
                    if (isTemplated) InfoMsg.Insert(0, "Is templated");
                }

                foreach (var msg in msgGroup)
                {
                    Console.WriteLine($"         - {msg}");
                }
            }
            Console.WriteLine("====================");
        }
    }
}

public class InvalidLOException(string? message) : Exception(message) {}

public static class Helpers
{
    /// <summary>
    /// Returns JSON file as JsonNode.<br/>
    /// Only JSON/JSONC files are allowed.
    /// </summary>
    /// <param name="path">User specified folder with locales and rules</param>
    /// <param name="folder">The directory in which to look for the file (use AppContext.BaseDirectory + folder name)</param>
    /// <param name="file">The file to look for</param>
    /// <param name="noSkip">True to throw exception if the file does not exist or cannot be parsed</param>
    /// <returns>Specified JSON file as JsonNode.</returns>
    /// <exception cref="FileNotFoundException"><paramref name="dir" /> contains no specified file.</exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="dir" /> does not exist.</exception>
    public static JsonNode LoadJson(string path, string folder, string file, bool noSkip = false)
    {
        if (path == "" || !Directory.Exists($"{path}{folder}") || !File.Exists($"{path}{folder}/{file}"))
            path = $"{AppContext.BaseDirectory}";

        if (!Directory.Exists($"{path}{folder}"))
            throw new DirectoryNotFoundException($"\n--> Unable to find the \"{folder}\" directory.\n\n");

        if (!File.Exists($"{path}{folder}/{file}"))
        {
            if (noSkip) return JsonNode.Parse("{}")!;
            throw new FileNotFoundException($"\n--> Unable to find the \"{file}\" file in the \"{folder}\".\n\n");
        }

        return
            file.AsJsonNode($"{path}", $"{folder}", noSkip, path == $"{AppContext.BaseDirectory}") ??
            file.AsJsonNode($"{AppContext.BaseDirectory}", $"{folder}", noSkip, true)!;
    }

    /// <summary>
    /// Parses file at path/folder as JSON.<br/>
    /// The method will use the fallback path if initial path is user-defined and the file results in JsonException.
    /// </summary>
    /// <param name="file">The file to look for</param>
    /// <param name="path">User specified or fallback directory</param>
    /// <param name="folder">The directory in which to look for the file ("locales" or "rules")</param>
    /// <param name="noSkip">True to throw exception if the file cannot be parsed</param>
    /// <param name="isFallback">True if path is the fallback directory</param>
    /// <returns>Specified JSON file as JsonNode, or null if the file cannot be parsed but there's another one in the fallback dir.</returns>
    /// <exception cref="JsonException"><paramref name="file" /> cannot be parsed as JSON.</exception>
    private static JsonNode? AsJsonNode(this string file, string path, string folder, bool noSkip = false, bool isFallback = false)
    {
        string jsonString = File.ReadAllText($"{path}{folder}/{file}");
        jsonString = SanitizeString(jsonString);

        try
        {
            return JsonNode.Parse(jsonString);
        }
        catch (JsonException)
        {
            if (noSkip && isFallback)
                throw new JsonException($"\n--> Unable to parse \"{file}\" in the \"{path}{folder}\" due to syntax errors.\n");

            if (!isFallback && File.Exists($"{AppContext.BaseDirectory}{folder}/{file}"))
            {
                Console.Write("====================\n\n");
                Console.WriteLine($"---> WARNING: \"{file}\" in the \"{path}{folder}\" has syntax errors and cannot be parsed;"
                    + $" default version of the file will be used instead.\n");

                return null;
            }

            Console.WriteLine($"====================\n\n" +
                $"---> WARNING: \"{file}\" in the \"{path}{folder}\" has syntax errors and cannot be parsed.\n");

            return JsonNode.Parse("{}")!;
        }
    }

    /// <summary>
    /// Removes all single- and multiline comments from the string.<br/>
    /// </summary>
    /// <param name="jsonString">A string to sanitize.</param>
    /// <returns>The string with no single- and multiline comments.</returns>
    private static string SanitizeString(string jsonString)
    {
        // single line comments up to the end of the line
        string patternA = @"//.*$";

        // multi line comments
        string patternB = @"/\*.*?\*/";

        jsonString = Regex.Replace(jsonString, patternA, "", RegexOptions.Multiline);
        jsonString = Regex.Replace(jsonString, patternB, "", RegexOptions.Singleline);

        return jsonString;
    }

    /// <summary>
    /// Merges 2 JsonNodes with appending non existing properties.<br/>
    /// </summary>
    /// <param name="targetJson">Target JsonNode.</param>
    /// <param name="sourceJson">Source JsonNode.</param>
    /// <returns>Target JsonNode with appended source JsonNode values to it.</returns>
    public static JsonNode DeepMerge(JsonNode targetJson, JsonNode sourceJson, string msg)
    {
        // return targetJson as is if sourceJson is empty/has unexpected type
        if (sourceJson is not JsonObject source0) return targetJson;

        // 1st level keys (patchers (armor, weapons, etc.), or languages (english, french, etc.))
        JsonObject patchers = targetJson.AsObject();
        foreach (var prop0 in source0)
        {
            // 2nd level keys (procedures (materialOverride, types, etc.), or string keys)
            if (patchers[prop0.Key] is JsonObject procedures && prop0.Value is JsonObject source1)
            {
                foreach (var prop1 in source1)
                {
                    // merging arrays with rules
                    if (procedures[prop1.Key] is JsonArray rules && prop1.Value is JsonArray source2)
                    {
                        foreach (var element in source2)
                        {
                            rules.Add(element!.DeepClone());
                        }
                    }
                    // prop1.Value is not JsonArray (for locales where it's a JsonValue in most cases)
                    else
                    {
                        procedures[prop1.Key] = prop1.Value!.DeepClone();
                    }
                }
            }
            else
            {
                patchers[prop0.Key] = prop0.Value!.DeepClone();
            }
        }

        return targetJson;
    }

    /// <summary>
    /// Builds a FormKey from "input", and checks whether it could be resolved.<br/>
    /// The method takes into account "Saints & Seducers" CC mod, and returns nullref FormKey for related records <br/>
    /// instead of exception in case the mod is not in the load order to avoid forcing users to it.
    /// </summary>
    /// <param name="input">A string to construct a FormKey from ( Mod_name|local_id|RECORD_TYPE ).</param>
    /// <param name="skipResolve">True to skip resolving.</param>
    /// <returns>The <see cref="FormKey"/> value.</returns>
    /// <exception cref="Exception">
    /// Constructed FormKey cannot be resolved (incorrect mod name/localId/type).<br/>
    /// </exception>
    public static FormKey ParseFormKey(string input, bool skipResolve = false)
    {
        // 0 - mod key, 1 - local id, 2 - record type
        string[] data = input.Split('|');
        FormKey formKey = new(data[0], Convert.ToUInt32(data[1], 16));

        bool isResolved = skipResolve || TryResolveAs(formKey, data[2]);
        if (data[0] == "ccBGSSSE025-AdvDSGS.esm")
        {
            var advDSGS = Executor.State!.LoadOrder
            .FirstOrDefault(plugin => plugin.Key.FileName.Equals(data[0]) && plugin.Value.Enabled);

            if (advDSGS is null) formKey = StaticsData.NullRef;
        }
        else if (!isResolved)
        {
            throw new Exception($"\n\n--> Unable to resolve {formKey} (no such record?)\n");
        }

        return formKey;
    }

    /// <summary>
    /// Attempts to resolve the form key as a specified type.<br/>
    /// </summary>
    /// <param name="formKey">A FormKey to resolve.</param>
    /// <param name="type">Record type value, as string.</param>
    /// <returns>True if the FormKey is resolvable, false otherwise.</returns>
    private static bool TryResolveAs(FormKey formKey, string type) => type switch
    {
        "EXPL" => Executor.State!.LinkCache.TryResolve<IExplosionGetter>(formKey, out _),
        "INGR" => Executor.State!.LinkCache.TryResolve<IIngredientGetter>(formKey, out _),
        "KWDA" => Executor.State!.LinkCache.TryResolve<IKeywordGetter>(formKey, out _),
        "MISC" => Executor.State!.LinkCache.TryResolve<IMiscItemGetter>(formKey, out _),
        "PERK" => Executor.State!.LinkCache.TryResolve<IPerkGetter>(formKey, out _),
        "SLGM" => Executor.State!.LinkCache.TryResolve<ISoulGemGetter>(formKey, out _),
        _ => false,
    };

    /// <summary>
    /// Returns a FormKey for stringId from the statics collection.<br/>
    /// </summary>
    /// <param name="stringId">The id by which the search is performed.</param>
    /// <returns>A FormKey from the statics list.</returns>
    public static FormKey GetFormKey(this string stringId) => Executor.Statics!.First(elem => elem.Id == stringId).FormKey;

    /// <summary>
    /// Searches the string in the record name and replaces it with another string.<br/>
    /// </summary>
    /// <param name="name">A string to search in (record name).</param>
    /// <param name="findStr">A string to search for (at least 2 characters!).</param>
    /// <param name="replaceStr">A string to replace with (could be none).</param>
    /// <param name="flags">An array of chars representing regex flags.</param>
    /// <returns>Record name as a string, modified or not.</returns>
    public static string FindReplace(string name, string findStr, string replaceStr, char[] flags)
    {
        string pattern = !flags.Contains('p') ?
             $@"(?<=^|(?<=[\s\(\[\-]))" + Regex.Escape(findStr) + @"(?=$|(?=[\s\,\)\]\-]))" :
             $"{Regex.Escape(findStr)}";

        RegexOptions options = flags.Contains('i') ? RegexOptions.IgnoreCase : RegexOptions.None;
        MatchCollection matches = Regex.Matches(name, pattern, options);

        if (matches.Count == 0) return name;

        bool sameCase = flags.Contains('c');
        if (flags.Contains('g'))
        {
            // iterating from the end to avoid indeces mismatch
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                if (sameCase) replaceStr = replaceStr.MatchCase(matches[i].Value);
                name = name.Remove(matches[i].Index, matches[i].Length).Insert(matches[i].Index, replaceStr);
            }
        }
        else
        {
            if (sameCase) replaceStr = replaceStr.MatchCase(matches[0].Value);
            name = name.Remove(matches[0].Index, matches[0].Length).Insert(matches[0].Index, replaceStr);
        }

        return name;
    }

    /// <summary>
    /// Capitalizes/uncapitalizes 1st letter in target, based on source's 1st letter case.<br/>
    /// </summary>
    /// <param name="targetStr">A string to modify case in.</param>
    /// <param name="sourceStr">A template string.</param>
    /// <returns>Target <see cref="string"/> with capitalized/uncapitalized 1st letter.</returns>
    private static string MatchCase(this string targetStr, string sourceStr)
    {
        char[] targetChars = targetStr.ToCharArray();
        targetChars[0] = char.IsUpper(sourceStr[0]) ?
            char.ToUpper(targetChars[0]) :
            char.ToLower(targetChars[0]);

        return string.Concat(targetChars);
    }

    /// <summary>
    /// Returns the value of key B from the rules array element if condition string (c.s.) contains the value of key A.<br/>
    /// </summary>
    /// <param name="name">A c.s. to search the value(s) of key A in (record name in most cases).</param>
    /// <param name="rules">JsonArray of rule entries.</param>
    /// <param name="data1">Key A; the c.s. should contain its value (if value is an array, c.s. should contain all elements).</param>
    /// <param name="data2">Key B; its value will be returned.</param>
    /// <param name="strict">True to check key A values as separate words only.</param>
    /// <returns>A JsonNode with key B value, or null if no entry matches the conditions.</returns>
    public static JsonNode? RuleByName(string name, JsonArray rules, string data1, string data2, bool strict = false)
    {
        // iterating rules from the end for LIFO entries priority
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            if (rules[i]![data1] == null || rules[i]![data2] == null) continue;

            List<string> stringsList = GetStringsList(rules[i]![data1]);
            if (stringsList.Count == 0) continue;

            if (stringsList.All(str => str.RegexMatch(name, strict)))
            {
                return rules[i]![data2]!;
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to resolve the JsonNode source as a List of strings and return it.<br/>
    /// </summary>
    /// <param name="node">A JsonNode element, should be a JsonArray of strings, or JsonValue with string.</param>
    /// <returns>A List of strings (could be empty if JsonNode does not match the conditions).</returns>
    private static List<string> GetStringsList(JsonNode? node)
    {
        List<string> result = [];

        if (node is JsonArray jsonArr)
        {
            foreach (var elem in jsonArr)
            {
                if (elem is JsonValue asVal
                    && asVal.TryGetValue<string>(out var str))
                    result.Add(str);
            }
        }
        else if (node is JsonValue jsonVal)
        {
            if (jsonVal.TryGetValue<string>(out var str)) result.Add(str);
        }

        return result;
    }

    /// <summary>
    /// Compares the string A against the regexp of string B.<br/>
    /// </summary>
    /// <param name="str">String B for the regexp pattern, at least 2 characters.</param>
    /// <param name="name">String A to compare (record name in most cases).</param>
    /// <param name="strict">True to compare as separate words only.</param>
    /// <returns>Regex.Match result as bool.</returns>
    public static bool RegexMatch(this string str, string name, bool strict = false)
    {
        if (str.Length < 2) return false;

        string pattern = strict ?
             $@"(?<=^|(?<=[\s\(\[\-]))" + Regex.Escape(str) + @"(?=$|(?=[\s\,\)\]\-]))" :
             $"{Regex.Escape(str)}";

        Match match = Regex.Match(name, pattern, RegexOptions.IgnoreCase);

        return match.Success;
    }

    /// <summary>
    /// Returns a translated string for this-parameter from the JsonNode of translated strings.<br/><br/>
    /// The method returns translated strings in specified language only if the file with strings for this language<br/>
    /// exists, otherwise English strings will be used. The name parameter only needed for languages with grammatical<br/>
    /// gender, and require gendered adjectives and gendered nouns specified (see the guide for details).
    /// </summary>
    /// <param name="id">Key name in the translated strings JsonNode.</param>
    /// <param name="lang">A language to search in first (English if not specified)</param>
    /// <param name="name">Name of the record for languages with grammatical gender.</param>
    /// <returns>A value of matching key from the translated strings JsonNode.</returns>
    /// <exception cref="Exception"><paramref name="id" /> cannot be found in the list of strings.</exception>
    public static string GetT9n(this string id, string lang = "", string name = "")
    {
        if (lang == "") lang = Executor.Settings!.General.GameLanguage.ToString();

        JsonNode? node = Executor.Strings![lang.ToLower()]![id];
        if (node == null && lang != "English") node = Executor.Strings["english"]![id];
        if (node == null) throw new Exception($"--> Unable to find a string for \"{id}\"\n");

        if (node is JsonValue jsonVal && jsonVal.TryGetValue<string>(out var str))
        {
            return str;
        }
        else if (node is JsonArray jsonArr)
        {
            string fallback = Executor.Strings![lang.ToLower()]!["name_refined"]!.ToString();
            if (jsonArr.Count == 0) return fallback;

            node = Executor.Strings![lang.ToLower()]!["genderedNouns"];
            if (node is JsonObject jsonObj)
            {
                if (jsonObj.Count == 0) return fallback;

                foreach (var gender in jsonObj)
                {
                    if (gender.Value!.AsArray().Any(word => name.Contains(word!.ToString())))
                    {
                        return gender.Key switch
                        {
                            "m" => jsonArr.IsInRange(0) ?? fallback,
                            "f" => jsonArr.IsInRange(1) ?? fallback,
                            "n" => jsonArr.IsInRange(2) ?? fallback,
                            _ => fallback,
                        };
                    }
                }
            }

            return fallback;
        }

        throw new Exception($"--> The value for \"{id}\" should be a string or an array of strings.\n");
    }

    /// <summary>
    /// Returns an element of the array as string, with the check to avoid out of range.<br/>
    /// </summary>
    /// <param name="jsonArr">An array as JsonArray.</param>
    /// <param name="index">An index of the element.</param>
    /// <returns>An element under the index as string, or null if index is out of range or element is an empty string.</returns>
    private static string? IsInRange(this JsonArray jsonArr, int index = 0)
    {
        return index <= jsonArr.Count - 1 && jsonArr[index]!.ToString() != "" ? jsonArr[index]!.ToString() : null;
    }

    /// <summary>
    /// Safe cast of JsonNode elements to int or float.<br/>
    /// </summary>
    /// <param name="jsonNode">A JsonNode element.</param>
    /// <returns>Int or float value, or default values for these types.</returns>
    public static T? AsType<T>(this JsonNode jsonNode) where T : struct
    {
        if (jsonNode is JsonValue jsonVal && jsonVal.TryGetValue<T>(out var asType)) return asType;

        Console.WriteLine($"--> Unexpected type of the value {jsonNode}, should be {typeof(T)}\n" +
            $"====================");
        return default;
    }

    /// <summary>
    /// Safe cast of JsonNode elements to string.<br/>
    /// </summary>
    /// <param name="jsonNode">A JsonNode element.</param>
    /// <returns>String value or null.</returns>
    public static T? AsNullableType<T>(this JsonNode jsonNode) where T : class
    {
        if (jsonNode is JsonValue jsonVal && jsonVal.TryGetValue<T>(out var asType)) return asType;

        Console.WriteLine($"--> Unexpected type of the value {jsonNode}, should be {typeof(T)}\n" +
            $"====================");
        return default;
    }

    /// <summary>
    /// Checks if this-parameter string exists in the excluded strings list.<br/>
    /// </summary>
    /// <param name="str">A string to check.</param>
    /// <param name="excludedStrings">A List of strings.</param>
    /// <param name="fullMatch">True to search for full match only.</param>
    /// <returns>True if receiver string exists in the excluded strings list.</returns>
    public static bool IsExcluded(this string str, List<string> excludedStrings, bool fullMatch = false)
    {
        return fullMatch ? excludedStrings.Any(value => value.Equals(str)) : excludedStrings.Count > 0 && excludedStrings.Any(str.Contains);
    }
}

public static class Conditions
{
    /// <summary>
    /// Adds a HasPerk-type condition to the array of conditions in a constructible object record.<br/>
    /// </summary>
    /// <param name="cobj">Constructible object record (recipe).</param>
    /// <param name="perk">FormKey of the perk to check.</param>
    /// <param name="flag">OR/AND flag (pass Condition.Flag.OR enum to set the OR flag, or 0 as default AND).</param>
    /// <param name="pos">Condition position in the array of conditions (is the last element by default).</param>
    public static void AddHasPerkCondition(this ConstructibleObject cobj, FormKey perk, Condition.Flag flag = 0, int pos = -1)
    {
        var hasPerk = new HasPerkConditionData()
        {
            RunOnType = Condition.RunOnType.Subject
        };
        hasPerk.Perk.Link.SetTo(perk);

        pos = pos == -1 ? cobj.Conditions.Count : pos;
        cobj.Conditions.Insert(pos, new ConditionFloat
        {
            CompareOperator = CompareOperator.EqualTo,
            Data = hasPerk,
            Flags = flag,
            ComparisonValue = 1
        });
    }

    /// <summary>
    /// Adds a GetItemCount-type condition to the array of conditions in a constructible object record.<br/>
    /// </summary>
    /// <param name="cobj">Constructible object record (recipe).</param>
    /// <param name="item">FormKey of the item to check.</param>
    /// <param name="type">Compare type enum (CompareOperator.EqualTo, CompareOperator.GreaterThen, etc.).</param>
    /// <param name="flag">OR/AND flag (pass Condition.Flag.OR enum to set the OR flag, or 0 as default AND).</param>
    /// <param name="pos">Condition position in the array of conditions (is the last element by default).</param>
    /// <param name="count">Number of items to compare against.</param>
    public static void AddGetItemCountCondition(this ConstructibleObject cobj, FormKey item, CompareOperator type, Condition.Flag flag = 0, int pos = -1, int count = 1)
    {
        var getItemCount = new GetItemCountConditionData()
        {
            RunOnType = Condition.RunOnType.Subject
        };
        getItemCount.ItemOrList.Link.SetTo(item);

        pos = pos == -1 ? cobj.Conditions.Count : pos;
        cobj.Conditions.Insert(pos, new ConditionFloat
        {
            CompareOperator = type,
            Data = getItemCount,
            Flags = flag,
            ComparisonValue = count
        });
    }

    /// <summary>
    /// Adds a GetEquipped-type condition to the array of conditions in a constructible object record.<br/>
    /// </summary>
    /// <param name="cobj">Constructible object record (recipe).</param>
    /// <param name="item">FormKey of the item to check.</param>
    /// <param name="type">Compare type enum (CompareOperator.EqualTo, CompareOperator.GreaterThen, etc.).</param>
    /// <param name="flag">OR/AND flag (pass Condition.Flag.OR enum to set the OR flag, or 0 as default AND).</param>
    /// <param name="pos">Condition position in the array of conditions (is the last element by default).</param>
    public static void AddGetEquippedCondition(this ConstructibleObject cobj, FormKey item, CompareOperator type = CompareOperator.EqualTo, Condition.Flag flag = 0, int pos = -1)
    {
        var getEquipped = new GetEquippedConditionData()
        {
            RunOnType = Condition.RunOnType.Subject
        };
        getEquipped.ItemOrList.Link.SetTo(item);

        pos = pos == -1 ? cobj.Conditions.Count : pos;
        cobj.Conditions.Insert(pos, new ConditionFloat
        {
            CompareOperator = type,
            Data = getEquipped,
            Flags = flag,
            ComparisonValue = 1
        });
    }

    /// <summary>
    /// Adds a TemperingItemIsEnchanted-type condition to the array of conditions in a constructible object record.<br/>
    /// </summary>
    /// <param name="cobj">Constructible object record (recipe).</param>
    /// <param name="flag">OR/AND flag (pass Condition.Flag.OR enum to set the OR flag, or 0 as default AND).</param>
    /// <param name="pos">Condition position in the array of conditions (is the last element by default).</param>
    public static void AddIsEnchantedCondition(this ConstructibleObject cobj, Condition.Flag flag = 0, int pos = -1)
    {
        var hasPerk = new EPTemperingItemIsEnchantedConditionData()
        {
            RunOnType = Condition.RunOnType.Subject
        };

        pos = pos == -1 ? cobj.Conditions.Count : pos;
        cobj.Conditions.Insert(pos, new ConditionFloat
        {
            CompareOperator = CompareOperator.NotEqualTo,
            Data = hasPerk,
            Flags = flag,
            ComparisonValue = 1
        });
    }
}