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

        foreach (var ingredient in records)
        {
            // skip the record for now if adding its masters exceeds the masters limit
            if (Helpers.IsOverflow(ingredient.FormKey, "INGR")) continue;

            // saving this record's formkey for the next patching session
            Executor.Session.Add(ingredient.FormKey);

            Logger = new Logger();
            PatchEffects(ingredient, blacklist);

            ShowReport(ingredient);
        }
    }

    /// <summary>
    /// Records loader.
    /// </summary>
    /// <returns>The list of ingredient records eligible for patching.</returns>
    private static List<IIngredientGetter> GetRecords()
    {
        IEnumerable<IIngredientGetter> conflictWinners = Executor.State!.LoadOrder.PriorityOrder
            .Where(plugin => !Settings.General.IgnoredFiles.Any(name => name == plugin.ModKey.FileName))
            .Where(plugin => plugin.Enabled)
            .WinningOverrides<IIngredientGetter>();

        List<IIngredientGetter> validRecords = [];
        List<string> excludedNames = [.. Rules["excludedIngredients"]!.AsArray().Select(value => value!.GetValue<string>())];
        List<string> excludedEffects = [.. Rules["excludedEffects"]!.AsArray().Select(value => value!.GetValue<string>())];

        foreach (var record in conflictWinners)
        {
            if (IsValid(record, excludedNames)) validRecords.Add(record);
        }

        Console.WriteLine($"\n~~~ {validRecords.Count} of {conflictWinners.Count()} ingredient records are eligible for patching ~~~\n\n"
            + "====================");
        return validRecords;
    }

    /// <summary>
    /// Checks if the record matches necessary conditions to be patched.
    /// </summary>
    /// <param name="ingredient">Processed record.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    /// <returns>Check result as bool.</returns>
    private static bool IsValid(IIngredientGetter ingredient, List<string> excludedNames)
    {
        // found in the session file (already patched)
        if (Executor.Session.Contains(ingredient.FormKey)) return false;

        Logger = new Logger();

        // found in the excluded records list by edid
        if (Settings.General.ExclByEdID && ingredient.EditorID!.IsExcluded(excludedNames, true))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Logger.Info("Found in the \"No patching\" list by EditorID");
                ShowReport(ingredient);
            }
            return false;
        }

        // has no name
        if (ingredient.Name is null) return false;

        // found in the excluded records list by name
        if (ingredient.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Logger.Info("Found in the \"No patching\" list by name");
                ShowReport(ingredient);
            }
            return false;
        }

        return true;
    }

    private static void PatchEffects(IIngredientGetter ingr, List<string> excludedValues)
    {
        // just in case (it's possible to remove all effects and/or effects' container in xEdit/zEdit)
        if (ingr.Effects is null || ingr.Effects.Count == 0)
        {
            Logger.Error($"The ingredient must have at least 1 effect, found none");
            return;
        }

        Ingredient? newIngr = null;
        for (int i = 0; i < ingr.Effects.Count; i++)
        {
            bool newDuration = false;
            bool newMagnitude = false;

            Executor.State!.LinkCache.TryResolve(ingr.Effects[i].BaseEffect, out var mgef);
            if (mgef is not null && mgef.IsPatchable(excludedValues))
            {
                JsonNode? ruleNode;
                float? ruleData;

                if (!mgef.Flags.HasFlag(MagicEffect.Flag.NoDuration))
                {
                    ruleNode = Helpers.RuleByName(mgef.Name!.ToString()!, Rules["effects"]!.AsArray(), data1: "names", data2: "duration");
                    ruleData = ruleNode?.AsType<float>();

                    if (ruleData is not null && ingr.Effects[i].Data!.Duration != ruleData)
                    {
                        newIngr = Executor.State!.PatchMod.Ingredients.GetOrAddAsOverride(ingr);
                        newIngr.Effects[i].Data!.Duration = (int)ruleData;
                        newDuration = true;
                    }
                }

                if (!mgef.Flags.HasFlag(MagicEffect.Flag.NoMagnitude))
                {
                    ruleNode = Helpers.RuleByName(mgef.Name!.ToString()!, Rules["effects"]!.AsArray(), data1: "names", data2: "magnitudeMult");
                    ruleData = ruleNode?.AsType<float>();

                    if (ruleData is not null && ruleData > 0 && ingr.Effects[i].Data!.Magnitude != (ingr.Effects[i].Data!.Magnitude * ruleData))
                    {
                        newIngr = Executor.State!.PatchMod.Ingredients.GetOrAddAsOverride(ingr);
                        newIngr.Effects[i].Data!.Magnitude *= (float)ruleData;
                        newMagnitude = true;
                    }
                }
            }

            if (newIngr is not null)
            {
                Logger.Info($"{mgef!.Name}: " +
                    $"duration {ingr.Effects[i].Data!.Duration}{(newDuration ? " -> " + newIngr!.Effects[i].Data!.Duration : "")}, " +
                    $"magnitude {ingr.Effects[i].Data!.Magnitude}{(newMagnitude ? " -> " + (decimal)newIngr!.Effects[i].Data!.Magnitude : "")}", true); 
            }
        }

        if (Settings.Ingredients.PriceLimits)
        {
            uint newValue = (uint)Math.Clamp(ingr.Value, Settings.Ingredients.MinValue, Settings.Ingredients.MaxValue);
            if (newValue != ingr.Value)
            {
                newIngr = Executor.State!.PatchMod.Ingredients.GetOrAddAsOverride(ingr);
                newIngr.Value = newValue;
                Logger.Info($"Value: {ingr.Value} -> {newIngr.Value}", true, true);
            }
        }
    }

    private static bool IsPatchable(this IMagicEffectGetter mgef, List<string> excludedValues)
    {
        // effect has no name
        if (mgef.Name is null) return false;

        // archetypes are restricted, and mgef's archetype is not value mod or peak value mod
        if (Settings.Ingredients.RestrictArchetypes && 
            mgef.Archetype.Type != MagicEffectArchetype.TypeEnum.ValueModifier &&
            mgef.Archetype.Type != MagicEffectArchetype.TypeEnum.PeakValueModifier)
        {
            return false;
        }

        // found in the excluded effects list by edID
        if (Settings.General.ExclByEdID && mgef.EditorID!.IsExcluded(excludedValues, true))
        {
            if (Settings.Debug.ShowExcluded)
                Logger.Info($"Found in the excluded effects list by EditorID");

            return false;
        }

        // found in the excluded effects list by name
        if (mgef.Name.ToString()!.IsExcluded(excludedValues))
        {
            if (Settings.Debug.ShowExcluded)
                Logger.Info($"Found in the excluded effects list by name");

            return false;
        }

        return true;
    }

    /// <summary>
    /// Displays patching results for the current record.<br/>
    /// </summary>
    /// <param name="record">Record to show messages for.</param>
    private static void ShowReport(this IIngredientGetter record) =>
        Logger.Report($"{record.Name}", $"{record.FormKey}", $"{record.EditorID}", false, false);
}
