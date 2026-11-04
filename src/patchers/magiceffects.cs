using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using ReProccer.Utils;
using System.Text.Json.Nodes;

namespace ReProccer.Patchers;

public static class MagicEffectsPatcher
{
    private static readonly Settings.AllSettings Settings = Executor.Settings!;
    private static readonly JsonObject Rules = Executor.Rules!["magiceffects"]!.AsObject();

    private static Logger Logger;

    public static void Run()
    {
        List<ISpellGetter> records = GetRecords();
        List<string> blacklist = [.. Rules["excludedEffects"]!.AsArray().Select(value => value!.GetValue<string>())];

        foreach (var spell in records)
        {
            // skip the record for now if adding its masters exceeds the masters limit
            if (Helpers.IsOverflow(spell.FormKey, "SPEL")) continue;

            // saving this record's formkey for the next patching session
            Executor.Session.Add(spell.FormKey);

            Logger = new Logger();
            //AddSneakAttackScript(spell);
            //AddAugmentedElementMGEF(spell);
            //AddElementalRamMGEF(spell);
            //PatchIntenseFlamesMGEF(spell);
            //PatchShokingBlastMGEF(spell);

            ShowReport(spell);
        }
    }

    /// <summary>
    /// Records loader.
    /// </summary>
    /// <returns>The list of spell records eligible for patching.</returns>
    private static List<ISpellGetter> GetRecords()
    {
        IEnumerable<ISpellGetter> conflictWinners = Executor.State!.LoadOrder.PriorityOrder
            .Where(plugin => !Settings.General.IgnoredFiles.Any(name => name == plugin.ModKey.FileName))
            .Where(plugin => plugin.Enabled)
            .Where(plugin => plugin.Mod!.ModHeader.Author != "ReProccer Evolved")
            .WinningOverrides<ISpellGetter>();

        List<ISpellGetter> validRecords = [];

        foreach (var record in conflictWinners)
        {
            if (IsValid(record)) validRecords.Add(record);
        }

        Console.WriteLine($"\n~~~ {validRecords.Count} of {conflictWinners.Count()} spell records are eligible for patching ~~~\n\n"
            + "====================");
        return validRecords;
    }

    /// <summary>
    /// Checks if the record matches necessary conditions to be patched.
    /// </summary>
    /// <param name="spell">Processed record.</param>
    /// <returns>Check result as bool.</returns>
    private static bool IsValid(ISpellGetter spell)
    {
        // found in the session file (already processed)
        if (Executor.Session.Contains(spell.FormKey)) return false;

        Logger = new Logger();

        // has no name
        if (spell.Name is null) return false;

        // not a spell (is ability, voice, power etc.)
        if (spell.Type != SpellType.Spell) return false;

        return true;
    }

    // patcher specific statics

    /// <summary>
    /// Appends patcher-specific records to the shared statics list.<br/>
    /// </summary>
    private static void BuildStaticsMap()
    {
        Executor.Statics!.AddRange(
        [
            new StaticsData{Id = "EitherHand",                             FormKey = Helpers.ParseFormKey("Skyrim.esm|0x013F44|EQUP")                  }
        ]);
    }
}
