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

    [SettingName("Armor patcher settings")]
    public ArmorSettings Armor { get; set; } = new();

    [SettingName("Weapons patcher settings")]
    public WeaponsSettings Weapons { get; set; } = new();

    [SettingName("Projectiles patcher settings")]
    public ProjectilesSettings Projectiles { get; set; } = new();

    [SettingName("Ingredients patcher settings")]
    public IngredientsSettings Ingredients { get; set; } = new();

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
  
    [SettingName("Master files limit")]
    [Tooltip("Defines the limit of master files at which ReProccer Evolved closes current patching session.\n\n" +
        "ReProccer Evolved will stop patching, and save the patch when the number of master files reaches the specified limit;\n" +
        "next patching session will start from the place where it stopped, allowing you to bypass the \"TooManyMasters\" error\n" +
        "(Creation Engine plugins 254 master-files limit).\n\n" +
        "Min value is 100, max value is 240.")]
    public ushort MastersLimit
    {
        get => _mastersLimit;
        set => _mastersLimit = Math.Clamp(value, (ushort)100, (ushort)240);
    }
    private ushort _mastersLimit = 240;

    [SettingName("Armor patcher")]
    [Tooltip("Toggles the armor patcher.")]
    public bool ArmorPatcher { get; set; } = true;

    [SettingName("Weapons patcher")]
    [Tooltip("Toggles the weapons patcher.")]
    public bool WeaponsPatcher { get; set; } = true;

    [SettingName("Projectiles patcher")]
    [Tooltip("Toggles the ammo and projectiles patcher.")]
    public bool ProjectilesPatcher { get; set; } = true;

    [SettingName("Ingredients patcher")]
    [Tooltip("Toggles the alchemy ingredients patcher.")]
    public bool IngredientsPatcher { get; set; } = true;

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
    public float ArmorScalingFactor
    {
        get => _armorScalingFactor;
        set => _armorScalingFactor = value <= 0f ? 0.1f : value;
    }
    private float _armorScalingFactor = 0.1f;

    [SettingName("Maximum damage reduction")]
    [Tooltip("Maximum possible physical damage reduction, in %.")]
    public ushort MaxArmorRating
    {
        get => _maxArmorRating;
        set => _maxArmorRating = value == 0 ? (ushort)95 : value;
    }
    private ushort _maxArmorRating = 95;

    [SettingName("Boots armor factor")]
    [Tooltip("For boots material protection value will be multiplied by this value.")]
    public float SlotBoots
    {
        get => _slotBoots;
        set => _slotBoots = value <= 0 ? 1f : value;
    }
    private float _slotBoots = 1.0f;

    [SettingName("Cuirass armor factor")]
    [Tooltip("For cuirasses material protection value will be multiplied by this value.")]
    public float SlotCuirass
    {
        get => _slotCuirass;
        set => _slotCuirass = value <= 0 ? 3f : value;
    }
    private float _slotCuirass = 3.0f;

    [SettingName("Gauntlets armor factor")]
    [Tooltip("For gauntlets material protection value will be multiplied by this value.")]
    public float SlotGauntlets
    {
        get => _slotGauntlets;
        set => _slotGauntlets = value <= 0 ? 1f : value;
    }
    private float _slotGauntlets = 1.0f;

    [SettingName("Helmet armor factor")]
    [Tooltip("For helmets material protection value will be multiplied by this value.")]
    public float SlotHelmet
    {
        get => _slotHelmet;
        set => _slotHelmet = value <= 0 ? 1.5f : value;
    }
    private float _slotHelmet = 1.5f;

    [SettingName("Shield armor factor")]
    [Tooltip("For shields material protection value will be multiplied by this value.")]
    public float SlotShield
    {
        get => _slotShield;
        set => _slotShield = value <= 0 ? 1.5f : value;
    }
    private float _slotShield = 1.5f;

    [SettingName("Gold value of Dreamcloth items")]
    [Tooltip("This percentage of a regular clothing gold value will be assigned to its Dreamcloth variant.\n"
        + "E.g., the value of 120 means any Dreamcloth clothing item will cost 20% more than its original.")]
    public ushort DreamclothPrice
    {
        get => _dreamclothPrice;
        set => _dreamclothPrice = value == 0 ? (ushort)120 : value;
    }
    private ushort _dreamclothPrice = 120;

    [SettingName("Amount of material to be refunded on breakdown")]
    [Tooltip("This percentage of the armor material will be refunded on breakdown (based on the crafting recipe if possible).")]
    public ushort RefundAmount
    {
        get => _refundAmount;
        set => _refundAmount = value == 0 ? (ushort)50 : value;
    }
    private ushort _refundAmount = 50;

    [SettingName("Dreamcloth gear label")]
    [Tooltip("Dreamcloth items will have this string appended to their names; leave empty to use the default \"[Dreamcloth]\" label.")]
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
    public bool AllArmorRecipes { get; set; } = false;
}

public class WeaponsSettings
{
    [SettingName("Base damage for one-handed weapons")]
    [Tooltip("Base damage for any one-handed weapon.")]
    public ushort OneHandedBase
    {
        get => _oneHandedBase;
        set => _oneHandedBase = value == 0 ? (ushort)12 : value;
    }
    private ushort _oneHandedBase = 12;

    [SettingName("Base damage for two-handed weapons")]
    [Tooltip("Base damage for any two-handed weapon.")]
    public ushort TwoHandedBase
    {
        get => _twoHandedBase;
        set => _twoHandedBase = value == 0 ? (ushort)23 : value;
    }
    private ushort _twoHandedBase = 23;

    [SettingName("Base damage for bows")]
    [Tooltip("Base damage for two-handed weapons.")]
    public ushort BowBase
    {
        get => _bowBase;
        set => _bowBase = value == 0 ? (ushort)22 : value;
    }
    private ushort _bowBase = 22;

    [SettingName("Base damage for crossbows")]
    [Tooltip("Base damage for crossbows.")]
    public ushort CrossbowBase
    {
        get => _crossbowBase;
        set => _crossbowBase = value == 0 ? (ushort)30 : value;
    }
    private ushort _crossbowBase = 30;

    [SettingName("Amount of material to be refunded on breakdown")]
    [Tooltip("This percentage of the weapon material will be refunded on breakdown (based on the crafting recipe if possible).")]
    public ushort RefundAmount
    {
        get => _refundAmount;
        set => _refundAmount = value == 0 ? (ushort)50 : value;
    }
    private ushort _refundAmount = 50;

    [SettingName("Gold value of Refined Silver weapons")]
    [Tooltip("This percentage of a Silver weapon gold value will be assigned to its Refined Silver version.\n"
        + "E.g., the value of 125 means any Refined Silver weapon will cost 25% more than its original.")]
    public ushort RefinedSilverPrice
    {
        get => _refinedSilverPrice;
        set => _refinedSilverPrice = value == 0 ? (ushort)125 : value;
    }
    private ushort _refinedSilverPrice = 125;

    [SettingName("Gold value of SkyRe crossbows")]
    [Tooltip("This percentage of a regular crossbow gold value will be assigned to its SkyRe enhanced version.\n"
        + "E.g., the value of 140 means any enhanced SkyRe crossbow will cost 40% more than its original.\n"
        + "Regardless of this value, double-enhanced crossbows (via the Engineer perk) additionally cost 20% more.")]
    public ushort EnhancedCrossbowsPrice
    {
        get => _enhancedCrossbowsPrice;
        set => _enhancedCrossbowsPrice = value == 0 ? (ushort)140 : value;
    }
    private ushort _enhancedCrossbowsPrice = 140;

    // SkyRe enhanced crossbows
    [Ignore]
    public ushort RecurveDamage
    {
        get => _recurveDamage;
        set => _recurveDamage = value == 0 ? (ushort)105 : value;
    }
    private ushort _recurveDamage = 105;

    [Ignore]
    public ushort SiegeDamage
    {
        get => _siegeDamage;
        set => _siegeDamage = value == 0 ? (ushort)115 : value;
    }
    private ushort _siegeDamage = 115;

    [Ignore]
    public ushort LightDamage
    {
        get => _lightDamage;
        set => _lightDamage = value == 0 ? (ushort)85 : value;
    }
    private ushort _lightDamage = 85;

    [Ignore]
    public ushort MuffledDamage
    {
        get => _muffledDamage;
        set => _muffledDamage = value == 0 ? (ushort)95 : value;
    }
    private ushort _muffledDamage = 95;

    [Ignore]
    public ushort RecurveSpeed
    {
        get => _recurveSpeed;
        set => _recurveSpeed = value == 0 ? (ushort)90 : value;
    }
    private ushort _recurveSpeed = 90;

    [Ignore]
    public ushort SiegeSpeed
    {
        get => _siegeSpeed;
        set => _siegeSpeed = value == 0 ? (ushort)80 : value;
    }
    private ushort _siegeSpeed = 80;

    [Ignore]
    public ushort LightSpeed
    {
        get => _lightSpeed;
        set => _lightSpeed = value == 0 ? (ushort)125 : value;
    }
    private ushort _lightSpeed = 125;

    [Ignore]
    public ushort MuffledSpeed
    {
        get => _muffledSpeed;
        set => _muffledSpeed = value == 0 ? (ushort)110 : value;
    }
    private ushort _muffledSpeed = 110;

    [Ignore]
    public ushort RecurveWeight
    {
        get => _recurveWeight;
        set => _recurveWeight = value == 0 ? (ushort)110 : value;
    }
    private ushort _recurveWeight = 110;

    [Ignore]
    public ushort SiegeWeight
    {
        get => _siegeWeight;
        set => _siegeWeight = value == 0 ? (ushort)125 : value;
    }
    private ushort _siegeWeight = 125;

    [Ignore]
    public ushort LightWeight
    {
        get => _lightWeight;
        set => _lightWeight = value == 0 ? (ushort)75 : value;
    }
    private ushort _lightWeight = 75;

    [Ignore]
    public ushort MuffledWeight
    {
        get => _muffledWeight;
        set => _muffledWeight = value == 0 ? (ushort)90 : value;
    }
    private ushort _muffledWeight = 90;

    [SettingName("Remove weapon type tags")]
    [Tooltip("Removes type tags ReProccer uses to mark weapons with overridden types from weapon names (weapon_name [type_tag] -> weapon_name).")]
    public bool NoTypeTags { get; set; } = true;

    [SettingName("No vanilla Enhanced Crossbows recipes")]
    [Tooltip("Removes the ability to craft vanilla Enhanced Crossbows (crafting recipes will not be available).\n"
        + "If unchecked, vanilla Enhanced Crossbows recipes additionally require the Increased Tension perk.")]
    public bool NoVanillaEnhanced { get; set; } = true;

    [SettingName("Preserve crossbows crafting conditions")]
    [Tooltip("Preserves original conditions in all crossbow crafting recipes.\n"
        + "Unchecked this to make crossbow crafting recipes available based on smithing mastery only.")]
    public bool KeepConditions { get; set; } = true;

    [SettingName("Shortspears on right hip")]
    [Tooltip("Changes shortspears animation type from swords to waraxes.\n"
        + "Requires modified shortspears models! Install these from the Extra Animations pack (look for it on the SkyRe mod page).")]
    public bool AltShortspears { get; set; } = false;

    [SettingName("Show all special recipes")]
    [Tooltip("Refined Silver weapons and SkyRe crossbows crafting recipes will always be visible in the crafting menu once the relevant perk is acquired.\n"
        + "By default, to declutter the crafting menu, each special recipe is hidden unless you have a required base weapon in the inventory.")]
    public bool AllWeaponRecipes { get; set; } = false;

    [SettingName("Alternative naming")]
    [Tooltip("Changes the position of weapon variant names from prefixed to suffixed.\n"
        + "E.g. Siege Dwarven Crossbow -> Dwarven Crossbow, siege; Refined Silver Greatsword -> <b>Silver Greatsword, refined.")]
    public bool SuffixedNames { get; set; } = false;
}

public class ProjectilesSettings
{
    [SettingName("Ammo qty for reforge recipes")]
    [Tooltip("Ammunition reforge recipes will require (and result in) this amount of ammo.")]
    public int AmmoQty { get; set; } = 10;

    [SettingName("Ingredients qty for reforge recipes")]
    [Tooltip("Ammunition reforge recipes will require this amount of each secondary ingredient.")]
    public int IngredsQty { get; set; } = 1;

    [SettingName("Preserve ammo crafting conditions")]
    [Tooltip("Preserves original conditions in all ammo crafting recipes.\n"
    + "Unchecked this to make ammo crafting recipes available based on smithing mastery only.")]
    public bool KeepConditions { get; set; } = true;

    [SettingName("Show all special recipes")]
    [Tooltip("Special ammo crafting recipes will always be visible in the crafting menu once the relevant perk is acquired.\n"
    + "By default, to declutter the crafting menu, each special recipe is hidden unless you have required number of base ammo in the inventory.")]
    public bool AllAmmoRecipes { get; set; } = false;
}

public class IngredientsSettings
{
    [SettingName("Restrict effect archetypes")]
    [Tooltip("Forces the patcher to process effects with \"Value Modifier\" and \"Peak Value Modifier\" archetypes only, to avoid potentially unwanted changes.\n"
    + "All effects specified in the rules by default have abovementioned archetypes.")]
    public bool RestrictArchetypes { get; set; } = true;

    [SettingName("Limit prices")]
    [Tooltip("Toggle price caps for alchemy ingredients.")]
    public bool PriceLimits { get; set; } = true;

    [SettingName("Minimum gold value of an ingredient")]
    [Tooltip("Gold value lower than specified will be changed to this value. This option only affects gold value if \"Limit prices\" option is active.")]
    public int MinValue { get; set; } = 5;

    [SettingName("Maximum gold value of an ingredient")]
    [Tooltip("Gold value higher than specified will be changed to this value. This option only affects gold value if \"Limit prices\" option is active.")]
    public int MaxValue { get; set; } = 150;
}