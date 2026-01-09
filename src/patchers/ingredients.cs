using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using ReProccer.Utils;
using System.Text.Json.Nodes;

namespace ReProccer.Patchers;

public static class IngredientsPatcher
{
    private static readonly Settings.AllSettings Settings = Executor.Settings!;
    private static readonly JsonObject Rules = Executor.Rules!["alchemy"]!.AsObject();

    private static Logger Logger;

    public static void Run()
    {
        List<IIngredientGetter> records = GetRecords();
        List<string> blacklist = [.. Rules["excludedEffects"]!.AsArray().Select(value => value!.GetValue<string>())];
    }

    /// <summary>
    /// Records loader.
    /// </summary>
    /// <returns>The list of records eligible for patching.</returns>
    private static List<IIngredientGetter> GetRecords()
    {
        IEnumerable<IIngredientGetter> conflictWinners = Executor.State!.LoadOrder.PriorityOrder
            .Where(plugin => !Settings.General.IgnoredFiles.Any(name => name == plugin.ModKey.FileName))
            .Where(plugin => plugin.Enabled)
            .WinningOverrides<IIngredientGetter>();

        List<IIngredientGetter> validRecords = [];
        List<string> excludedNames = [.. Rules["excludedIngredients"]!.AsArray().Select(value => value!.GetValue<string>())];

        foreach (var record in conflictWinners)
        {
            if (IsValid(record, excludedNames)) validRecords.Add(record);
        }

        Console.WriteLine($"\n~~~ {validRecords.Count} of {conflictWinners.Count()} ingredient records are eligible for patching ~~~\n\n"
            + "====================");
        return validRecords;
    }

    /// <summary>
    /// Checks if record matches necessary conditions to be patched.
    /// </summary>
    /// <param name="record">Processed record.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    /// <returns>Check result from a filter the record triggered as bool.</returns>
    private static bool IsValid(IIngredientGetter record, List<string> excludedNames)
    {
        Logger = new Logger();

        // invalid if found in the excluded records list by edid
        if (Settings.General.ExclByEdID && record.EditorID!.IsExcluded(excludedNames, true))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Logger.Info($"Found in the \"No patching\" list by EditorID");
                ShowReport(record);
            }
            return false;
        }

        // invalid if has no name
        if (record.Name is null) return false;

        // invalid if found in the excluded records list by name
        if (record.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Logger.Info($"Found in the \"No patching\" list by name");
                ShowReport(record);
            }
            return false;
        }

        return true;
    }

    /// <summary>
    /// Show patching info.<br/>
    /// Displays all messages collected with Logger() for the current record.
    /// </summary>
    /// <param name="record">Record to show mwssages for.</param>
    private static void ShowReport(this IIngredientGetter record) =>
        Logger.ShowReport($"{record.Name}", $"{record.FormKey}", $"{record.EditorID}", false, false);
}
