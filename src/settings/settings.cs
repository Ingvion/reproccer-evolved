using Mutagen.Bethesda.WPF.Reflection.Attributes;

namespace ReProccer.Config;

public class SettingsUI
{
    [SettingName("General")]
    public General GeneralSettings { get; set; } = new();

    [SettingName("Armor Patcher")]
    public Armor ArmorSettings { get; set; } = new();

    [SettingName("Logger")]
    public Debug DebugSettings { get; set; } = new();
}

public class General
{
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

public class Debug
{
    [SettingName("Report excluded records")]
    [Tooltip("A message will be shown when a record is found the excluded records list.")]
    public bool ShowExcluded { get; set; } = true;
}

public class Armor
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
    [Tooltip("Dreamcloth items will have these characters added to their names.\n"
        + "Leave empty to use the default [Dreamcloth] label.")]
    public string DreamclothLabel { get; set; } = "[Dreamcloth]";

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