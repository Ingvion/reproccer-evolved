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

    private static Utils.PatchingData RecordData;

    public static void Run()
    {
        UpdateGMST();
	}
	
    private static void UpdateGMST()
    {
        FormKey armorScalingFactor = Executor.Statics!
        .First(elem => elem.Id == "ArmorScalingFactor")
        .Formkey;

        IGameSettingGetter conflictWinner = State.LinkCache.Resolve<IGameSettingGetter>(armorScalingFactor);
        GameSetting record = State.PatchMod.GameSettings.GetOrAddAsOverride(conflictWinner);

        if (record is GameSettingFloat gmstArmorScalingFactor) gmstArmorScalingFactor.Data = Settings.Armor.ArmorScalingFactor;

        FormKey maxArmorRating = Executor.Statics!
        .First(elem => elem.Id == "MaxArmorRating")
        .Formkey;

        conflictWinner = State.LinkCache.Resolve<IGameSettingGetter>(maxArmorRating);
        record = State.PatchMod.GameSettings.GetOrAddAsOverride(conflictWinner);

        if (record is GameSettingFloat gmstMaxArmorRating) gmstMaxArmorRating.Data = Settings.Armor.MaxArmorRating;
    }
}

