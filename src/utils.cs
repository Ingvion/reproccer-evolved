using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReProccer.Utils;

public struct PatchingData()
{
    public bool NonPlayable{ get; set; }
    public bool Modified { get; set; }
    public bool Overridden { get; set; }
    public bool Unique { get; set; }
    public ArmorType ArmorType { get; set; }
}

public struct Logger()
{
    private List<string> InfoMsg { get; set; } = [];
    private List<string> CautionMsg { get; set; } = [];
    private List<string> ErrorMsg { get; set; } = [];
    private static readonly string[] Filter = Executor.Settings!.Debug.VerboseDataFilter
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public readonly void Info(string msg, bool verboseInfo = false)
    {
        if (Executor.Settings!.Debug.ShowVerboseData || !verboseInfo) InfoMsg.Add(msg);
    } 
    public readonly void Caution(string msg) => CautionMsg.Add(msg);
    public readonly void Error(string msg) => ErrorMsg.Add(msg);

    public readonly void ShowReport(string name, string formkey, bool nonPlayable)
    {
        if (Filter.Length > 0 && Filter.Any(value => name.Contains(value.Trim()))) return;

        List<List<string>> messages = [InfoMsg, CautionMsg, ErrorMsg];
        if (messages.All(group => group.Count == 0)) return;

        string[] groups = ["    > INFO:", "    > CAUTION:", "    > ERROR:"];
        if (Executor.Settings!.Debug.ShowNonPlayable || !nonPlayable)
        {
            string note = nonPlayable ? " | NON-PLAYABLE" : "";
            Console.WriteLine($"+ REPORT | {name} ({formkey}){note}");
            foreach (var msgGroup in messages)
            {
                if (msgGroup.Count == 0) continue;
                Console.WriteLine($"{groups[messages.IndexOf(msgGroup)]}");

                foreach (var msg in msgGroup)
                {
                    Console.WriteLine($"           {msg}");
                }
            }
            Console.WriteLine("====================");
        }
    }
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

public record StaticsMap(
    string Id,
    FormKey Formkey
);

public record DataMap(
    string Id,
    FormKey? Kwda = null,
    FormKey? Item = null,
    List<FormKey>? Perk = null
);

public record IngredientsMap(
    FormKey Ingr,
    int Qty,
    string Type
);

public static class Helpers
{
    /// <summary>
    /// Returns the JSON file as JsonNode.<br/>
    /// Regular and JSONC style (with single- and multiline comments) files are allowed, but not JSON5 ones.
    /// </summary>
    /// <param name="dir">The directory in which to look for the file (use AppContext.BaseDirectory + folder name)</param>
    /// <param name="file">The file to look for (any .json*)</param>
    /// <param name="noSkip">Throw exception if the file is not unparseable or does not exist</param>
    /// <returns>A specified JSON file as <see cref="JsonNode"/>.</returns>
    /// <exception cref="FileNotFoundException"><paramref name="dir" /> contains no specified file.</exception>
    /// <exception cref="JsonException"><paramref name="file" /> cannot be parsed due to sytax errors.</exception>
    public static JsonNode LoadJson(string dir, string file, bool noSkip = false)
    {
        if (!Directory.Exists($"{dir}"))
        {
            throw new Exception($"--> Unable to find the \"{dir}\" directory.\n");
        }

        string jsonString;

        try
        {
            jsonString = File.ReadAllText($"{dir}/{file}");
        }
        catch (FileNotFoundException)
        {
            return noSkip ? throw new FileNotFoundException($"--> Unable to find \"{file}\" in the \"\\{dir}\" directory.\n") : JsonNode.Parse("{}")!;
        }

        JsonNode? jsonFile;

        try
        {
            jsonString = SanitizeString(jsonString);
            jsonFile = JsonNode.Parse(jsonString);
        }
        catch (JsonException)
        {
            if (noSkip)
            {
                throw new JsonException($"--> Unable to parse \"{file}\" in the \"\\{dir}\" directory.\n");
            }
            else
            {
                Console.WriteLine($"---> WARNING: \"{file}\" in the \"\\{dir}\" directory has syntax errors and will be skipped.");
                return JsonNode.Parse("{}")!;
            }
        }

        return jsonFile!;
    }

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
    /// <param name="targetJson">Target JsonNode</param>
    /// <param name="sourceJson">Source JsonNode</param>
    /// <returns>Target <see cref="JsonNode"/> with appended source JsonNode values to it.</returns>
    public static JsonNode DeepMerge(JsonNode targetJson, JsonNode sourceJson)
    {
        if (targetJson is JsonObject target0 && sourceJson is JsonObject source0)
        {
            foreach (var prop0 in source0)
            {
                if (target0[prop0.Key] is JsonObject target1 && prop0.Value is JsonObject source1)
                {
                    foreach (var prop1 in source1)
                    {
                        if (target1[prop1.Key] is JsonArray target2 && prop1.Value is JsonArray source2)
                        {
                            foreach (var element in source2)
                            {
                                target2.Add(element!.DeepClone());
                            }
                        }
                        else
                        {
                            target1[prop1.Key] = prop1.Value!.DeepClone();
                        }
                    }
                }
                else
                {
                    target0[prop0.Key] = prop0.Value!.DeepClone();
                }
            }
        }

        return targetJson;
    }

    /// <summary>
    /// Constructs the FormKey value and checks whether it could be resolved.<br/>
    /// The method takes into account "Saints & Seducers" CC mod, and returns nullref FormKey for related records <br/>
    /// instead of exception in case the mod is not in the load order to avoid forcing users to use it.
    /// </summary>
    /// <param name="str">A string to conbstruct a FormKey from ("Mod_name|local_id|RECORD_TYPE")</param>
    /// <param name="skipResolve">True to skip the record resolution attempt</param>
    /// <returns>The FormKey value, or nullref FormKey if the record originates from the "Saints & Seducers".</returns>
    /// <exception cref="Exception">
    /// The constructed FormKey cannot be resolved (incorrect mod name/localId/type).<br/>
    /// </exception>
    public static FormKey ParseFormKey(string str, bool skipResolve = false)
    {
        // 0 - mod key, 1 - local id, 2 - record type
        string[] data = str.Split('|');
        FormKey formKey = new(data[0], Convert.ToUInt32(data[1], 16));

        bool isResolved = skipResolve || TryResolveAs(formKey, data[2]);
        if (data[0] == "ccBGSSSE025-AdvDSGS.esm")
        {
            var advDSGS = Executor.State!.LoadOrder
            .FirstOrDefault(plugin => plugin.Key.FileName.Equals(data[0]) && plugin.Value.Enabled);

            if (advDSGS == null) formKey = new("Skyrim.esm", 0x000000);
        }
        else if (!isResolved)
        {
            throw new Exception($"--> Unable to resolve {formKey} (no such record?)\n");
        }

        return formKey;
    }

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
    /// Search for the substring in the record name and replace it with another substring.<br/>
    /// </summary>
    /// <param name="name">The string to look in (record name)</param>
    /// <param name="findStr">A string/substring to look for (at least 2 characters)</param>
    /// <param name="replaceStr">A string/substring to replace with.</param>
    /// <param name="flags">An array of chars representing regex flags.</param>
    /// <returns>The record name as a <see cref="string"/>, modified or not.</returns>
    public static string FindReplace(string name, string findStr, string replaceStr, char[] flags)
    {
        string pattern = !flags.Contains('p') ?
             $@"(?<=^|(?<=\s))" + Regex.Escape(findStr) + @"(?=$|(?=\s))" :
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

    private static string MatchCase(this string target, string source)
    {
        char[] targetLetters = target.ToCharArray();
        targetLetters[0] = char.IsUpper(source[0]) ?
            char.ToUpper(targetLetters[0]) :
            char.ToLower(targetLetters[0]);

        return string.Concat(targetLetters);
    }

    /// <summary>
    /// Returns the data2 value from the rules entry if the data1 value from the same entry is present in the passed string.<br/>
    /// </summary>
    /// <param name="name">A string to search value A in (record name in most cases)</param>
    /// <param name="rules">JsonArray of rule entries</param>
    /// <param name="data1">The passed string should contain a value (which is either a string or array of strings) of this key.</param>
    /// <param name="data2">The value of this key will be returned.</param>
    /// <param name="strict">True to check if all strings from of the data1 value (if array) present in the passed string.</param>
    /// <returns>The <see cref="JsonNode"/> with data2 value, or null no entry matches the conditions.</returns>
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

    private static bool RegexMatch(this string str, string name, bool strict)
    {
        if (str.Length < 2) return false;

        string pattern = strict ?
             $@"(?<=^|(?<=\s))" + Regex.Escape(str) + @"(?=$|(?=\s))" :
             $"{Regex.Escape(str)}";

        Match match = Regex.Match(name, pattern, RegexOptions.IgnoreCase);

        return match.Success;
    }

    /// <summary>
    /// Returns the translated string for this-parameter from the JsonNode of translated strings.<br/><br/>
    /// The method returns translated strings in specified language only if the file with strings for this language<br/>
    /// exists, otherwise English strings will be used. The name parameter only needed for languages with grammatical<br/>
    /// gender, and require gendered adjectives and gendered nouns specified (the the guide for details).
    /// </summary>
    /// <param name="id">Key name as this-parameter</param>
    /// <param name="lang">The language to search in first (english if not specified)</param>
    /// <param name="name">Name of the record for languages with grammatical gender.</param>
    /// <returns>The <see cref="string"/> for this-parameter from the translated strings node.</returns>
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

    private static string? IsInRange(this JsonArray jsonArr, int index = 0)
    {
        return index <= jsonArr.Count - 1 && jsonArr[index]!.ToString() != "" ? jsonArr[index]!.ToString() : null;
    }

    /// <summary>
    /// Safe cast of JsonNode elements to the necessary type.<br/>
    /// </summary>
    /// <param name="cobj">A JsonNode as this-parameter</param>
    /// <param name="type">Awaited type</param>
    /// <returns><see cref="JsonValue"/> as one of the basic types (<see cref="string"/>, <see cref="float"/>, <see cref="int"/>), or null.</returns>
    public static dynamic? AsType(this JsonNode jsonNode, string type)
    {
        if (jsonNode is JsonValue jsonVal)
        {
            switch (type)
            {
                case "string":
                    if (jsonVal.TryGetValue<string>(out var asString)) return asString;
                    break;

                case "int":
                    if (jsonVal.TryGetValue<int>(out var intAsIs)) return intAsIs;
                    if (jsonVal.TryGetValue<float>(out var intAsFloat)) return (int)intAsFloat;
                    break;

                case "float":
                    if (jsonVal.TryGetValue<float>(out var floatAsIs)) return floatAsIs;
                    if (jsonVal.TryGetValue<int>(out var floatAsInt)) return floatAsInt;
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if this-parameter string exists in the excluded strings list.<br/>
    /// </summary>
    /// <param name="str">A string as this-parameter</param>
    /// <param name="excludedStrings">A list of strings</param>
    /// <param name="fullMatch">True to look for this-parameter string as a full string only</param>
    /// <returns>True if receiver string exists in the excluded strings list</returns>
    public static bool IsExcluded(this string str, List<string> excludedStrings, bool fullMatch = false)
    {
        return fullMatch ? excludedStrings.Any(value => value.Equals(str)) : excludedStrings.Count > 0 && excludedStrings.Any(str.Contains);
    }

    /*
    /// <summary>
    /// Increments editor ID index by 1 until it becomes unique to avoid duplication of existing records editor IDs.<br/>
    /// </summary>
    /// <param name="newEditorID">Editor ID as string.</param>
    /// <returns>Editor ID with a number appended to it as <see cref="string"/></returns>
    public static string ToUnique(this string newEditorID)
    {

        int? incr = null;
        while (Executor.NewEditorIDs.Contains($"{newEditorID}{incr}"))
        {
            incr = incr == null ? 1 : ++incr;
        }

        Executor.NewEditorIDs.Add($"{newEditorID}{incr}");
        return $"{newEditorID}{incr}";
    }

    /// <summary>
    /// Displays reports.<br/>
    /// </summary>
    /// <param name="name">Record name.</param>
    /// <param name="formkey">Record FormKey.</param>
    /// <param name="nonPlayable">True if the record has a Non-Playable flag.</param>
    /// <param name="msgList">List of lists of strings with messages.</param>
    public static void DisplayLog(string name, string formkey, bool nonPlayable, List<List<string>> msgList)
    {
        if (Executor.Settings!.Debug.ShowVerboseData) return;

        string[] filter = Executor.Settings!.Debug.VerboseDataFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (filter.Length > 0 && !filter.Any(value => name.Contains(value.Trim()))) return;


        string[] group = ["-- ~ INFO", "-- > CAUTION", "-- # ERROR"];
        if (Executor.Settings!.Debug.ShowNonPlayable || !nonPlayable)
        {
            string note = nonPlayable ? " | NON-PLAYABLE" : "";
            Console.WriteLine($"+ REPORT | {name} ({formkey}){note}");
            foreach (var msgGroup in msgList)
            {
                if (msgGroup.Count > 0) continue;
                Console.WriteLine($"{group[msgList.IndexOf(msgGroup)]}");

                foreach (var msg in msgGroup)
                {
                    Console.WriteLine($"----- {msg}");
                }
            }
            Console.WriteLine("====================");
        }
    }
    */
}

public static class Conditions
{
    /// <summary>
    /// Adds a HasPerk-type condition to the array of conditions in a constructible object record.<br/>
    /// </summary>
    /// <param name="cobj">A constructible object as this-parameter</param>
    /// <param name="perk">The FormKey of the perk to check</param>
    /// <param name="flag">Pass Condition.Flag.OR enum to set the OR flag or 0 as None.</param>
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
    /// <param name="cobj">A constructible object as this-parameter</param>
    /// <param name="item">The FormKey of the item to check</param>
    /// <param name="type">Compare type enum (CompareOperator.EqualTo, CompareOperator.GreaterThen, etc.).</param>
    /// <param name="flag">Pass Condition.Flag.OR enum to set the OR flag or 0 as None.</param>
    /// <param name="pos">Condition position in the array of conditions (is the last element by default).</param>
    /// <param name="count">Number of items to compare with.</param>
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
    /// <param name="cobj">A constructible object as this-parameter</param>
    /// <param name="item">The FormKey of the item to check</param>
    /// <param name="type">Compare type enum (CompareOperator.EqualTo, CompareOperator.GreaterThen, etc.).</param>
    /// <param name="flag">Pass Condition.Flag.OR enum to set the OR flag or 0 as None.</param>
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
}