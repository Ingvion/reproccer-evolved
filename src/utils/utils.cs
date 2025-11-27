using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReProccer.Utils;

public struct PatchingData(bool NonPlayable = false, bool HasUniqueKeyword = true, ArmorType ArmorType = ArmorType.Clothing, bool GetOverridden = false)
{
    public bool NonPlayableFlag = NonPlayable;
    public bool UniqueFlag = HasUniqueKeyword;
    public bool OverriddenFlag = GetOverridden;
    public ArmorType ArmorTypeEnum = ArmorType;

    public readonly bool IsNonPlayable() => NonPlayableFlag;
    public readonly bool IsUnique() => UniqueFlag;
    public readonly bool IsOverridden() => OverriddenFlag;
    public readonly ArmorType GetArmorType() => ArmorTypeEnum;
    public bool SetOverridden() => OverriddenFlag = true;
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

public static class Helpers
{
    public static JsonNode LoadJson(string path, string filename, bool noSkip = false)
    {
        if (!Directory.Exists($"{path}"))
        {
            throw new Exception($"\n--> Unable to find the \"{path}\" directory.\n");
        }

        string jsonString;

        try
        {
            jsonString = File.ReadAllText($"{path}/{filename}");
        }
        catch (FileNotFoundException)
        {
            return noSkip ? throw new Exception($"\n--> Unable to find \"{filename}\" in the \"\\{path}\" directory.\n") : JsonNode.Parse("{}")!;
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
                throw new Exception($"\n--> Unable to parse \"{filename}\" in the \"\\{path}\" directory.\n");
            }
            else
            {
                Console.WriteLine($"--> WARNING: \"{filename}\" in the \"\\{path}\" directory has syntax errors and will be skipped.");
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

    public static JsonNode DeepMerge(JsonNode targetJson, JsonNode sourceJson)
    {
        // There's likely a better way to merge JSONs but I'm limited by lack of experience in C#
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

    public static FormKey ParseFormKey(string fkString, bool skipResolve = false)
    {
        // 0 - mod key, 1 - local id, 2 - record type
        string[] data = fkString.Split('|');
        FormKey formKey = new(data[0], Convert.ToUInt32(data[1], 16));

        bool isResolved = skipResolve || TryToResolve(formKey, data[2]);
        if (data[0] == "ccBGSSSE025-AdvDSGS.esm")
        {
            var advDSGS = Executor.State!.LoadOrder
            .FirstOrDefault(plugin => plugin.Key.FileName.Equals(data[0]) && plugin.Value.Enabled);

            if (advDSGS == null) formKey = new("Skyrim.esm", 0x000000);
        }
        else if (!isResolved)
        {
            throw new Exception($"\n--> Unable to resolve {formKey} (no such record?)\n");
        }

        return formKey;
    }

    // for debug purposes
    private static bool TryToResolve(FormKey formKey, string type) => type switch
    {
        "EXPL" => Executor.State!.LinkCache.TryResolve<IExplosionGetter>(formKey, out _),
        "INGR" => Executor.State!.LinkCache.TryResolve<IIngredientGetter>(formKey, out _),
        "KWDA" => Executor.State!.LinkCache.TryResolve<IKeywordGetter>(formKey, out _),
        "MISC" => Executor.State!.LinkCache.TryResolve<IMiscItemGetter>(formKey, out _),
        "PERK" => Executor.State!.LinkCache.TryResolve<IPerkGetter>(formKey, out _),
        "SLGM" => Executor.State!.LinkCache.TryResolve<ISoulGemGetter>(formKey, out _),
        _ => false,
    };
}

public static class Extensions
{
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

    public static dynamic? RuleByName(string name, JsonArray rules, string data1, string data2, bool strict = false)
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

    public static string GetT9n(this string id, string lang = "", string name = "")
    {
        if (lang == "") lang = Executor.Settings!.General.GameLanguage.ToString();

        JsonNode? node = Executor.Strings![lang.ToLower()]![id];
        if (node == null && lang != "English") node = Executor.Strings["english"]![id];
        if (node == null) throw new Exception($"\n\n--> Unable to find a string for \"{id}\"\n");

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

        throw new Exception($"\n\n--> The value for \"{id}\" should be a string or an array of strings.\n");
    }

    private static string? IsInRange(this JsonArray jsonArr, int index = 0)
    {
        return index <= jsonArr.Count - 1 && jsonArr[index]!.ToString() != "" ? jsonArr[index]!.ToString() : null;
    }
}