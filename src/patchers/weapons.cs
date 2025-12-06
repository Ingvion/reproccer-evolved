using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using ReProccer.Utils;
using System.Text.Json.Nodes;

namespace ReProccer.Patchers;

public static class WeaponsPatcher
{
    private static readonly Settings.AllSettings Settings = Executor.Settings!;
    private static readonly JsonObject Rules = Executor.Rules!["weapons"]!.AsObject();

    private static EditorIDs EditorIDs;
    private static Weapon? ThisRecord;
    private static PatchingData RecordData;
    private static Logger Logger;

    public static void Run()
    {
        EditorIDs = new EditorIDs();

        List<IWeaponGetter> records = GetRecords();
        List<List<string>> blacklists = [
            [.. Rules["excludedFromRenaming"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedFromRecipes"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedSilver"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedCrossbows"]!.AsArray().Select(value => value!.GetValue<string>())]
        ];
    }

    /// <summary>
    /// Records loader.
    /// </summary>
    /// <returns>The list of weapon records eligible for patching.</returns>
    private static List<IWeaponGetter> GetRecords()
    {
        IEnumerable<IWeaponGetter> weapWinners = Executor.State!.LoadOrder.PriorityOrder
            .Where(plugin => !Settings.General.IgnoredFiles.Any(name => name == plugin.ModKey.FileName))
            .Where(plugin => plugin.Enabled)
            .WinningOverrides<IWeaponGetter>();

        Console.WriteLine($"~~~ {weapWinners.Count()} weapon records found, filtering... ~~~\n\n"
            + "====================");

        List<IWeaponGetter> weapRecords = [];

        List<string> excludedNames = [.. Rules["excludedWeapons"]!.AsArray().Select(value => value!.GetValue<string>())];
        foreach (var record in weapWinners)
        {
            if (IsValid(record, excludedNames))
            {
                weapRecords.Add(record);
            }
            else
            {
                Console.WriteLine($"{record.Name} ({record.EditorID}) is invalid");
            }
                ;
        }

        Console.WriteLine($"\n~~~ {weapRecords.Count} weapon records are eligible for patching ~~~\n\n"
            + "====================");
        return weapRecords;
    }

    /// <summary>
    /// Checks if the weapon matches necessary conditions to be patched.
    /// </summary>
    /// <param name="armor">The weapon record as IWeaponGetter.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    /// <returns>Check result from a filter the record triggered as <see cref="bool"/>.</returns>
    private static bool IsValid(IWeaponGetter weapon, List<string> excludedNames)
    {
        Logger = new Logger();

        // invalid if found in the excluded records list by edid
        if (Settings.General.ExclByEdID && weapon.EditorID!.IsExcluded(excludedNames, true))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Logger.Info($"Found in the \"No patching\" list by EditorID (as {weapon.EditorID})");
                //weapon.ShowReport();
            }
            return false;
        }

        // invalid if has no name
        if (weapon.Name == null) return false;

        // invalid if found in the excluded records list by name
        if (weapon.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Logger.Info($"Found in the \"No patching\" list by name");
                //weapon.ShowReport();
            }
            return false;
        }

        // invalid if is a staff
        if (weapon.Data!.AnimationType == WeaponAnimationType.Staff) return false;

        // valid if has a template (to skip keyword checks below)
        if (!weapon.Template.IsNull) return true;

        // invalid if has no keywords or have empty kw array (rare)
        if (weapon.Keywords == null || weapon.Keywords.Count == 0) return false;

        return true;
    }
}