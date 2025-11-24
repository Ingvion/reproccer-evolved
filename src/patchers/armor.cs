using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using ReProccer.Config;
using ReProccer.Utils;
using System.Text.Json.Nodes;

namespace ReProccer.Patchers;

public static class Armor
{
    private static readonly IPatcherState<ISkyrimMod, ISkyrimModGetter> State = Executor.State!;
    private static readonly AllSettings Settings = Executor.Settings!;
    private static readonly JsonObject Rules = Executor.Rules!["armor"]!.AsObject();

    private static PatchingData RecordData;

    public static void Run()
    {
        UpdateGMST();
        List<IArmorGetter> records = GetRecords();
	}

    private static List<IArmorGetter> GetRecords()
    {
        List<IArmorGetter> records = [];
        List<string> excludedArmor = [.. Rules["excludedArmor"]!.AsArray().Select(value => value!.GetValue<string>())];
        FormKey[] mustHave = [
            Executor.Statics!.First(elem => elem.Id == "ArmorHeavy").Formkey,
            Executor.Statics!.First(elem => elem.Id == "ArmorLight").Formkey,
            Executor.Statics!.First(elem => elem.Id == "ArmorShield").Formkey,
            Executor.Statics!.First(elem => elem.Id == "ArmorClothing").Formkey,
            Executor.Statics!.First(elem => elem.Id == "ArmorJewelry").Formkey
        ];

        var conflictWinners = State.LoadOrder.PriorityOrder
        .Where(plugin => !Settings.General.IgnoredFiles.Any(name => name == plugin.ModKey.FileName))
        .Where(plugin => plugin.Enabled)
        .WinningOverrides<IArmorGetter>();

        foreach (var armor in conflictWinners)
        {
            if (IsValid(armor, excludedArmor, mustHave)) records.Add(armor);
        }

        return records;
    }

    private static bool IsValid(IArmorGetter armor, List<string> excludedArmor, FormKey[] mustHave)
    {
        // invalid if found in the excluded records list by edid
        if (Settings.General.ExclByEdID && excludedArmor.Any(value => value.Equals(armor.EditorID)))
        {
            if (Settings.Debug.ShowExcluded) Console.WriteLine($"--> {armor.EditorID} found in the exclusion list.");
            return false;
        }

        // invalid if has no name
        if (armor.Name == null) return false;

        // invalid if found in the excluded records list by name
        if (excludedArmor.Any(value => armor.Name!.ToString()!.Contains(value)))
        {
            if (Settings.Debug.ShowExcluded) Console.WriteLine($"--> {armor.Name} found in the exclusion list.");
            return false;
        }

        // invalid if has no body template)
        if (armor.BodyTemplate == null) return false;

        // valid if has a template (to skip keyword checks below)
        if (!armor.TemplateArmor.IsNull) return true;

        // invalid if has no keywords or have empty kw array (rare)
        if (armor.Keywords == null || armor.Keywords.ToArray().Length == 0) return false;

        // invalid if it does not have any required keywords
        if (!mustHave.Any(keyword => armor.Keywords.Contains(keyword))) return false;

        return true;
    }

    private static void UpdateGMST()
    {
        FormKey armorScalingFactor = Executor.Statics!
        .First(elem => elem.Id == "ArmorScalingFactor")
        .Formkey;

        IGameSettingGetter conflictWinner = State.LinkCache.Resolve<IGameSettingGetter>(armorScalingFactor);
        GameSetting record = State.PatchMod.GameSettings.GetOrAddAsOverride(conflictWinner);

        if (record is GameSettingFloat gmstArmorScalingFactor)
        {
            gmstArmorScalingFactor.Data = Settings.Armor.ArmorScalingFactor;
        }

        FormKey maxArmorRating = Executor.Statics!
        .First(elem => elem.Id == "MaxArmorRating")
        .Formkey;

        conflictWinner = State.LinkCache.Resolve<IGameSettingGetter>(maxArmorRating);
        record = State.PatchMod.GameSettings.GetOrAddAsOverride(conflictWinner);

        if (record is GameSettingFloat gmstMaxArmorRating)
        {
            gmstMaxArmorRating.Data = Settings.Armor.MaxArmorRating;
        }
    }
}

