using Mutagen.Bethesda.WPF.Reflection.Attributes;

namespace ReProccer.Config;

public enum Language
{
    English,
    French, 
    German, 
    Italian, 
    Japanese, 
    Polish, 
    Russian, 
    Spanish,
    Chinese
}

public class AllSettings
{
    [SettingName("General")]
    public GeneralSettings General { get; set; } = new();

    [SettingName("Armor Patcher")]
    public ArmorSettings Armor { get; set; } = new();

    [SettingName("Debug")]
    public DebugSettings Debug { get; set; } = new();
}

public class GeneralSettings
{
    [SettingName("Game language")]
    [Tooltip("Keep in mind, that you need a translated strings file in the \"locales\" folder for languages other than English;\n"
    + "if none, default English strings will be used.")]
    public Language GameLanguage { get; set; } = Language.English;

    [SettingName("Ignored Files")]
    [Tooltip("Plugins listed here will be fully ignored - records will be ignored, rules will not be loaded,\n"
        + "and winning overrides from these plugins will be skipped.")]
    public List<string> IgnoredFiles { get; set; } = [
        "Apocalypse - Magic of Skyrim.esp",
        "Skyrim AE Redone - Enchanted Weaponry.esp",
        "ShowRaceMenuAlternative.esp"
    ];

    [SettingName("Armor Patcher")]
    [Tooltip("Toggles the armor patcher.")]
    public bool ArmorPatcher { get; set; } = true;

    [SettingName("Weapons Patcher")]
    [Tooltip("Toggles the weapons patcher.")]
    public bool WeaponsPatcher { get; set; } = true;

    [SettingName("Alchemy Patcher")]
    [Tooltip("Toggles the ingredients patcher.")]
    public bool AlchemyPatcher { get; set; } = true;

    [SettingName("Projectiles Patcher")]
    [Tooltip("Toggles the ammo and projectiles patcher.")]
    public bool ProjectilesPatcher { get; set; } = true;

    [SettingName("Allow exclusion by EdID")]
    [Tooltip("Allows the ReProccer to check for exact match in records' editor ID first when processing the exclusion list.\n"
        + "With this option you can exclude specific records, because unlike names editor IDs are most often unique.")]
    public bool ExclByEdID { get; set; } = true;
}

public class DebugSettings
{
    [SettingName("Report excluded records")]
    [Tooltip("A message will be displayed for each record found in the excluded records list.")]
    public bool ShowExcluded { get; set; } = true;

    [SettingName("Report patching results")]
    [Tooltip("A message with patching results will be displayed for each processed record.")]
    public bool ShowVerboseData { get; set; } = false;

    [SettingName("Filter patching results")]
    [Tooltip("Patching, renaming, faction assigning, and other results for records with these values in their names will be displayed when \"Report patching results\"\n"
        + "is active. Leave empty to display patching results for all processed records (not recommended).")]
    public string VerboseDataFilter { get; set; } = "";

    [SettingName("Include non-playables in reports")]
    [Tooltip("Reports of all types will also be displayed for non-playable records.")]
    public bool ShowNonPlayable { get; set; } = false;
}

public class ArmorSettings
{
    [SettingName("Damage reduction per point of armor")]
    [Tooltip("Physical damage blocked per 1 point of armor, in %.\n"
        + "E.g. the value of 0.25 means you need 4 armor for 1% of physical damage reduction (0.25 * 4).")]
    public float ArmorScalingFactor { get; set; } = 0.1f;

    [SettingName("Maximum damage reduction")]
    [Tooltip("Maximum possible physical damage reduction, in %.")]
    public int MaxArmorRating { get; set; } = 95;

    [SettingName("Boots armor factor")]
    [Tooltip("For boots material protection value will be multiplied by this value.")]
    public float SlotBoots { get; set; } = 1.0f;

    [SettingName("Cuirass armor factor")]
    [Tooltip("For cuirasses material protection value will be multiplied by this value.")]
    public float SlotCuirass { get; set; } = 3.0f;

    [SettingName("Gauntlets armor factor")]
    [Tooltip("For gauntlets material protection value will be multiplied by this value.")]
    public float SlotGauntlets { get; set; } = 1.0f;

    [SettingName("Helmet armor factor")]
    [Tooltip("For helmets material protection value will be multiplied by this value.")]
    public float SlotHelmet { get; set; } = 1.5f;

    [SettingName("Shield armor factor")]
    [Tooltip("For shields material protection value will be multiplied by this value.")]
    public float SlotShield { get; set; } = 1.5f;

    [SettingName("Price of Dreamcloth items")]
    [Tooltip("This percentage of a regular clothing price will be assigned to its Dreamcloth variant.\n"
        + "E.g., the value of 120 means any Dreamcloth clothing item will cost 20% more than its original.")]
    public int DreamclothPrice { get; set; } = 120;

    [SettingName("Amount of material to be refunded on breakdown")]
    [Tooltip("This percentage of the armor material will be refunded on breakdown (based on the crafting recipe if possible).")]
    public int RefundAmount { get; set; } = 50;

    [SettingName("Dreamcloth gear label")]
    [Tooltip("Dreamcloth items will have this string added to their names; leave empty to use the default \" [Dreamcloth]\" label.\n")]
    public string DreamclothLabel { get; set; } = " [Dreamcloth]";

    [SettingName("Leather armors recipes require the Leathercraft perk")]
    [Tooltip("If there are no other smithing perk requirements, the patcher will add the Leathercraft perk as a requirement\n"
        + "for leather-type armors recipes (Leather Armor, Imperial Light Cuirass, etc). The option does not apply to Hide and Fur armors.")]
    public bool FixLeatherCraft { get; set; } = true;

    [SettingName("No breakdown recipes for clothing")]
    [Tooltip("If active, ReProccer will not generate breakdown recipes for clothing items. This option does not apply to Dreamcloth wear.")]
    public bool NoClothingBreak { get; set; } = false;

    [SettingName("Show all Dreamcloth recipes")]
    [Tooltip("Only the Weaving Mill perk is required for Dreamcloth recipes to become visible in the crafting menu.\n"
        + "By default to declutter the crafting menu Dreamcloth recipes are hidden for clothing pieces you don't have in the inventory.")]
    public bool ShowAllRecipes { get; set; } = false;
}