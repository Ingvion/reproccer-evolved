using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReProccer.Utils;

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
}