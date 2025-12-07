using Mutagen.Bethesda.WPF.Reflection.Attributes;

namespace ReProccer.Settings;

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
    [Tooltip("Determines which language strings to use first. If there's no translated strings file for specified language\n"
        + "or no translated string in the file, default English strings will be used.")]
    public Language GameLanguage { get; set; } = Language.English;

    [SettingName("Ignored files")]
    [Tooltip("Plugins listed here will be fully ignored - records will be ignored, rules will not be loaded,\n"
        + "and winning overrides from these plugins will be skipped.")]
    public List<string> IgnoredFiles { get; set; } = [
        "Apocalypse - Magic of Skyrim.esp",
        "Skyrim AE Redone - Enchanted Weaponry.esp",
        "ShowRaceMenuAlternative.esp"
    ];

    [SettingName("Armor patcher")]
    [Tooltip("Toggles the armor patcher.")]
    public bool ArmorPatcher { get; set; } = true;

    [SettingName("Weapons patcher")]
    [Tooltip("Toggles the weapons patcher.")]
    public bool WeaponsPatcher { get; set; } = true;

    [SettingName("Alchemy patcher")]
    [Tooltip("Toggles the ingredients patcher.")]
    public bool AlchemyPatcher { get; set; } = true;

    [SettingName("Projectiles patcher")]
    [Tooltip("Toggles the ammo and projectiles patcher.")]
    public bool ProjectilesPatcher { get; set; } = true;

    [SettingName("Skip existing breakdown recipes")]
    [Tooltip("If a breakdown recipe for the record already exists in some other mod, ReProccer will not generate its own.\n"
         + "Info on found recipes will be displayed if \"Report patching results\" is active.")]
    public bool SkipExisting { get; set; } = true;

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
    [Tooltip("Only info for records with these values in their names will be displayed. Separate values by commas;\n"
        + "leave the field empty to display information on all processed records.")]
    public string ReportFilter { get; set; } = "";

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
    [Tooltip("Dreamcloth items will have this string added to their names; leave empty to use the default \"[Dreamcloth]\" label.\n")]
    public string DreamclothLabel { get; set; } = " [Dreamcloth]";

    [SettingName("Leather armors recipes require the Leathercraft perk")]
    [Tooltip("If there are no other smithing perk requirements, the patcher will add the Leathercraft perk as a requirement\n"
        + "for leather-type armors recipes (Leather Armor, Imperial Light Cuirass, etc). The option does not apply to Hide and Fur armors.")]
    public bool FixCraftRecipes { get; set; } = true;

    [SettingName("No breakdown recipes for clothing")]
    [Tooltip("If active, ReProccer will not generate breakdown recipes for clothing items. This option does not apply to Dreamcloth wear.")]
    public bool NoClothingBreak { get; set; } = false;

    [SettingName("Show all Dreamcloth recipes")]
    [Tooltip("Dreamcloth recipes will always be visible in the crafting menu once the relevant perk is acquired.\n"
        + "By default, to declutter the crafting menu, Dreamcloth recipes are hidden unless you have a required base clothing item in the inventory.")]
    public bool ShowAllRecipes { get; set; } = false;
}

public class WeaponsSettings
{
    [SettingName("Base damage for one-handed weapons")]
    [Tooltip("Base damage for any one-handed weapon.")]
    public int OneHandedBase { get; set; } = 12;

    [SettingName("Base damage for two-handed weapons")]
    [Tooltip("Base damage for any two-handed weapon.")]
    public int TwoHandedBase { get; set; } = 23;

    [SettingName("Base damage for bows")]
    [Tooltip("Base damage for two-handed weapons.")]
    public int BowBase { get; set; } = 22;

    [SettingName("Base damage for crossbows")]
    [Tooltip("Base damage for crossbows.")]
    public int CrossbowBase { get; set; } = 30;

    [SettingName("Amount of material to be refunded on breakdown")]
    [Tooltip("This percentage of the weapon material will be refunded on breakdown (based on the crafting recipe if possible).")]
    public int RefundAmount { get; set; } = 50;

    [SettingName("Price of Refined Silver weapons")]
    [Tooltip("This percentage of a Silver weapon price will be assigned to its Refined Silver version.\n"
        + "E.g., the value of 125 means any Refined Silver weapon will cost 25% more than its original.")]
    public int RefinedSilverPrice { get; set; } = 125;

    [SettingName("Price of SkyRe crossbows")]
    [Tooltip("This percentage of a regular crossbow price will be assigned to its SkyRe enhanced version.\n"
        + "E.g., the value of 140 means any enhanced SkyRe crossbow will cost 40% more than its original.\n"
        + "Regardless of this value, double-enhanced crossbows (via the Engineer perk) additionally cost 20% more.")]
    public int EnhancedCrossbowsPrice { get; set; } = 140;

    [SettingName("Remove weapon type tags")]
    [Tooltip("Removes type tags ReProccer uses to mark weapons with overridden types from weapon names (weapon_name [type_tag] -> weapon_name).")]
    public bool NoTypeTags { get; set; } = true;

    [SettingName("No vanilla Enhanced Crossbows recipes")]
    [Tooltip("Removes the ability to craft vanilla Enhanced Crossbows (crafting recipes will not be available).\n"
        + "If unchecked, vanilla Enhanced Crossbows recipes additionally require the Increased Tension perk.")]
    public bool NoClothingBreak { get; set; } = true;

    [SettingName("Preserve crossbows crafting conditions")]
    [Tooltip("Preserves recipe conditions not related to the material (like quest stage) in all crossbow recipes.\n"
        + "With this box unchecked crossbow crafting recipes will be available without completing quests.")]
    public bool ShowAllRecipes { get; set; } = true;

    [SettingName("Shortspears on right hip")]
    [Tooltip("Changes shortspears animation type from swords to waraxes.\n"
        + "Requires modified shortspears models! Install these from the Extra Animations pack (look for it on the SkyRe mod page).")]
    public bool AltShortspears { get; set; } = false;

    [SettingName("Show all special recipes")]
    [Tooltip("Refined Silver weapons and SkyRe crossbows recipes will always be visible in the crafting menu once the relevant perk is acquired.\n"
        + "By default, to declutter the crafting menu, special recipes are hidden unless you have a required base weapon in the inventory.")]
    public bool ShowAllRecipes { get; set; } = false;
}