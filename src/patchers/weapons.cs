using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using ReProccer.Utils;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReProccer.Patchers;

public struct CrossbowMods
{
    public int Damage { get; set; }
    public int Speed { get; set; }
    public int Weight { get; set; }
    public SoundLevel SoundLevel { get; set; }
};

public record CrossbowSubtype(
    string Id,
    string EdId,
    string Desc,
    List<FormKey> Kwda,
    List<FormKey> Perk
);

public static class WeaponsPatcher
{
    private static readonly Settings.AllSettings Settings = Executor.Settings!;
    private static readonly JsonObject Rules = Executor.Rules!["weapons"]!.AsObject();
    private static readonly (List<StaticsData> AllTypes,
                             List<StaticsData> SkyReTypes,
                             List<StaticsData> CrossbowSubtypes,
                             List<CrossbowMods> CrossbowMods,
                             List<StaticsData> AllMaterials) Statics = BuildStaticsMap();

    private static EditorIDs EditorIDs;             // tracker to ensure editorIDs uniqueness for new records
    private static RecordData PatchingData;         // frequently requested data for current record
    private static readonly List<Report> Logs = []; // list of logs for current record and records created from it

    public static void Run()
    {
        EditorIDs = new EditorIDs();

        List<IWeaponGetter> records = GetRecords();
        List<List<string>> blacklists = [
            [.. Rules["excludedFromRenaming"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedCrossbows"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedSilver"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedFromRecipes"]!.AsArray().Select(value => value!.GetValue<string>())]
        ];

        foreach (var weapon in records)
        {
            PatchingData = new RecordData
            {
                Log = new Logger(),
                AnimType = weapon.Data!.AnimationType,
                BoundWeapon = weapon.Data!.Flags.HasFlag(WeaponData.Flag.BoundWeapon),
                NonPlayable = weapon.MajorFlags.HasFlag(Weapon.MajorFlag.NonPlayable) || 
                    weapon.Data!.Flags.HasFlag(WeaponData.Flag.NonPlayable) || 
                    weapon.Data!.Flags.HasFlag(WeaponData.Flag.CantDrop),
                Unique = weapon.Keywords!.Contains("skyre__NoMeltdownRecipes".GetFormKey())
            };

            Logs.Add(new Report { Record = weapon, Entry = PatchingData.Log });

            if (!weapon.Template.IsNull)
            {
                PatchRecordNames(weapon, blacklists[0]);
                ShowReport();
                continue;
            }

            SetOverriddenData(weapon);
            // from this point we're checking if the weapon is already patched,
            // to ensure we're working with its override copy in the patch

            PatchRecordNames(PatchingData.Modified ? 
                weapon.AsOverride() : weapon, blacklists[0]);
            PatchWeaponData(PatchingData.Modified ? 
                weapon.AsOverride() : weapon);

            if (!PatchingData.NonPlayable && !PatchingData.BoundWeapon)
            {
                ProcessCrossbows(PatchingData.Modified ? 
                    weapon.AsOverride() : weapon, blacklists[1]);
                ProcessSilverWeapons(PatchingData.Modified ? 
                    weapon.AsOverride() : weapon, blacklists[2]);
                ProcessRecipes(PatchingData.Modified ? 
                    weapon.AsOverride() : weapon, blacklists[3]);
            }

            Finalizer(PatchingData.Modified ? 
                weapon.AsOverride() : weapon);
            ShowReport();
        }
    }

    /// <summary>
    /// Records loader.
    /// </summary>
    /// <returns>The list of weapon records eligible for patching.</returns>
    private static List<IWeaponGetter> GetRecords()
    {
        IEnumerable<IWeaponGetter> conflictWinners = Executor.State!.LoadOrder.PriorityOrder
            .Where(plugin => !Settings.General.IgnoredFiles.Any(name => name == plugin.ModKey.FileName))
            .Where(plugin => plugin.Enabled)
            .WinningOverrides<IWeaponGetter>();

        List<IWeaponGetter> validRecords = [];
        List<string> excludedNames = [.. Rules["excludedWeapons"]!.AsArray().Select(value => value!.GetValue<string>())];

        foreach (var record in conflictWinners)
        {
            if (IsValid(record, excludedNames)) validRecords.Add(record);
        }

        Console.WriteLine($"\n~~~ {validRecords.Count} of {conflictWinners.Count()} weapon records are eligible for patching ~~~\n\n"
            + "====================");
        return validRecords;
    }

    /// <summary>
    /// Checks if the record matches necessary conditions to be patched.
    /// </summary>
    /// <param name="weapon">Processed record.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    /// <returns>Check result as bool.</returns>
    private static bool IsValid(IWeaponGetter weapon, List<string> excludedNames)
    {
        Logger log = new();
        Logs.Add(new Report { Record = weapon, Entry = log });

        // found in the excluded records list by edID
        if (Settings.General.ExclByEdID && weapon.EditorID!.IsExcluded(excludedNames, true))
        {
            if (Settings.Debug.ShowExcluded)
            {
                log.Info("Found in the \"No patching\" list by EditorID");
                ShowReport();
            }
            return false;
        }

        // has no name
        if (weapon.Name is null) return false;

        // found in the excluded records list by name
        if (weapon.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
            {
                log.Info("Found in the \"No patching\" list by name");
                ShowReport();
            }
            return false;
        }

        // is a staff
        if (weapon.Data!.AnimationType == WeaponAnimationType.Staff) return false;

        // has a template (to skip keyword checks below)
        if (!weapon.Template.IsNull) return true;

        // has no keywords or kws array is empty (rare)
        if (weapon.Keywords is null || weapon.Keywords.Count == 0) return false;

        return true;
    }

    /// <summary>
    /// Renames the record according to the rules.
    /// </summary>
    /// <param name="weapon">Processed weapon record.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    private static void PatchRecordNames(IWeaponGetter weapon, List<string> excludedNames)
    {
        if (weapon.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded) PatchingData.Log.Info("Found in the \"No renaming\" list");
            return;
        }

        string name = weapon.Name!.ToString()!;

        /* char[] flags options:
         * i - case-insensitive search
         * g - replace all matches
         * p - string as part of a word
         * c - retain capitalization
         * n - search for the next rule
         * o - allow overridden records
         */

        if (Rules["renamer"] is JsonArray renamer)
        {
            for (int i = renamer.Count - 1; i >= 0; i--)
            {
                string options = renamer[i]!["options"]?.ToString() ?? "ic";
                char[] flags = options.ToCharArray();

                // processing armor type filter
                string filter = renamer[i]!["filter"]?.ToString() ?? "";
                if (filter != "")
                {
                    string[] filterArr = filter.Split(',');
                    if (!filterArr.Any(type => PatchingData.AnimType.ToString() == type.Replace(" ", "")))
                    {
                        continue;
                    }
                }

                if (PatchingData.Overridden && !flags.Contains('o')) return;

                if (!flags.Contains('p'))
                {
                    // checking if name contains all words from replace in any order
                    string replace = renamer[i]!["replace"]?.ToString() ?? "";
                    string[] blacklist = replace.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (replace != "" && blacklist.All(name.Contains)) continue;

                    // checking if name contains any word from skipIf
                    string skipIf = renamer[i]!["skipIf"]?.ToString() ?? "";
                    blacklist = skipIf.Split(',', StringSplitOptions.TrimEntries);
                    if (skipIf != "" && blacklist.Any(word => name.Contains(word))) continue;
                }

                string newName = Helpers.FindReplace(name, renamer[i]!["find"]!.ToString(), renamer[i]!["replace"]!.ToString(), flags);
                if (newName == name) continue;

                name = newName;
                if (!flags.Contains('n')) break;
            }
        }

        if (name != weapon.Name.ToString())
        {
            PatchingData.Log.Info($"Was renamed to {name}", true);
            weapon.AsOverride().Name = name;
        }
    }

    /// <summary>
    /// Modifies weapon material/type according to the rules.
    /// </summary>
    /// <param name="weapon">Processed weapon record.</param>
    private static void SetOverriddenData(IWeaponGetter weapon)
    {
        // processing type
        JsonNode? typeNode =
            Helpers.RuleByName(weapon.Name!.ToString()!, Rules["typeOverrides"]!.AsArray(), data1: "names", data2: "type", true);
        string? typeData = typeNode?.AsNullableType<string>();

        StaticsData newType = Statics.AllTypes.FirstOrDefault(entry => entry.Id.GetT9n() == typeData);
        if (newType.Id is not null)
        {
            foreach (var entry in Statics.SkyReTypes)
            {
                if (weapon.Keywords!.Contains(entry.Kwda!) && entry.Kwda != newType.Kwda)
                    weapon.AsOverride().Keywords!.Remove(entry.Kwda);
            }

            PatchingData.Log.Info($"The subtype was forced to {typeData}", true);
            string typeTag = (Settings.Weapons.NoTypeTags ? "TYPETAG " : "") + newType!.Id.GetT9n();
            weapon.AsOverride().Name = weapon.AsOverride().Name + " [" + typeTag + "]";
            PatchingData.Overridden = true;
        }

        // processing material
        JsonNode? materialNode = Helpers.RuleByName(
            weapon.Name!.ToString()!, Rules["materialOverrides"]!.AsArray(), data1: "names", data2: "material");
        string? materialData = materialNode?.AsNullableType<string>();

        StaticsData newMaterial = Statics.AllMaterials.FirstOrDefault(entry => entry.Id.GetT9n() == materialData);
        if (newMaterial.Id is not null)
        {
            if (newMaterial.Kwda == StaticsData.NullRef)
            {
                PatchingData.Log.Caution("A relevant \"materialOverrides\" patching rule references a material from Creation Club's \"Saints and Seducers\"");
                return;
            }

            foreach (var entry in Statics.AllMaterials)
            {
                if (weapon.Keywords!.Contains(entry.Kwda!) && entry.Kwda != newMaterial.Kwda)
                    weapon.AsOverride().Keywords!.Remove(entry.Kwda);
            }

            if (!weapon.Keywords!.Contains(newMaterial.Kwda!)) weapon.AsOverride().Keywords!.Add(newMaterial.Kwda!);
            PatchingData.Log.Info($"The material was forced to {materialData}", true);
            PatchingData.Overridden = true;
        }
    }

    /// <summary>
    /// Modifies weapon data based on other methods' results.
    /// </summary>
    /// <param name="weapon">Processed weapon record.</param>
    private static void PatchWeaponData(IWeaponGetter weapon)
    {
        PatchKeywords(weapon);

        ushort baseDamage = GetDamageData();
        (int typeDamage, float typeSpeed, float typeReach) = GetTypeData(weapon);
        (int materialDamage, float materialSpeed, float critMult) = GetMaterialData(weapon);
        float mult = GetExtraDamageMod(weapon);

        // these weapon type values must be above 0
        if (typeSpeed <= 0 || typeReach <= 0)
        {
            PatchingData.Log.Error("Weapon data will not be modified.");
            return;
        }

        if ((typeSpeed + materialSpeed) <= 0)
        {
            PatchingData.Log.Error($"Weapon speed cannot be 0 and less! The fallback value of {typeSpeed} will be used instead");
            materialSpeed = 0;
        }
        weapon.AsOverride().Data!.Speed = typeSpeed + materialSpeed;
        weapon.AsOverride().Data!.Reach = typeReach;

        ushort newDamage = (ushort)Math.Floor((baseDamage + typeDamage + materialDamage) * mult);
        if (newDamage == 0)
        {
            PatchingData.Log.Error($"Weapon damage cannot be 0 and less! The fallback value of {weapon.BasicStats!.Damage} will be used instead");
            newDamage = weapon.BasicStats!.Damage;
        }
        weapon.AsOverride().BasicStats!.Damage = newDamage;
        weapon.AsOverride().Critical!.Damage = (ushort)(newDamage * critMult);

        if (Settings.Debug.ShowVerboseData)
        {
            PatchingData.Log.Info($"Speed: {Math.Round(weapon.Data!.Speed, 2)} -> {(decimal)weapon.AsOverride().Data!.Speed} " +
                $"(type: {typeSpeed}, material: {materialSpeed})");
            PatchingData.Log.Info($"Reach: {weapon.Data!.Reach} -> {(decimal)weapon.AsOverride().Data!.Reach} " +
                $"(type: {typeReach})");
            PatchingData.Log.Info($"Damage: {weapon.BasicStats!.Damage} -> {weapon.AsOverride().BasicStats!.Damage} " +
                $"(base: {baseDamage}, type: {typeDamage}, material: {materialDamage}, mult: x{mult})");
            PatchingData.Log.Info($"Crit: {weapon.Critical!.Damage} -> {weapon.AsOverride().Critical!.Damage} " +
                $"(damage: {weapon.AsOverride().BasicStats!.Damage}, mult: x{critMult})");
        }
    }

    /// <summary>
    /// Distributes SkyRe weapon types keywords.
    /// </summary>
    /// <param name="weapon">Processed weapon record.</param>
    private static void PatchKeywords(IWeaponGetter weapon)
    {
        // SkyRe type keyword
        bool isSkyReType = false;
        if (!Statics.SkyReTypes.All(type => weapon.Keywords!.Contains(type.Kwda!)))
        {
            StaticsData newType = Statics.SkyReTypes
                .FirstOrDefault(type => type.Id.GetT9n().RegexMatch(weapon.AsOverride().Name!.ToString()!, true));

            if (newType.Id is not null)
            {
                weapon.AsOverride().Keywords!.Add(newType.Kwda!);
                isSkyReType = true;
                if (!PatchingData.Overridden) PatchingData.Log.Info($"New subtype is {newType.Id.GetT9n("english")}", true);
            }
        }

        // bound weapon keyword
        if (PatchingData.BoundWeapon && !weapon.Keywords!.Contains("skyre__WeapMaterialBound".GetFormKey()))
        {
            weapon.AsOverride().Keywords!.Add("skyre__WeapMaterialBound".GetFormKey());
            PatchingData.Log.Info("Marked as bound weapon (has \"bound weapon\" flag)", true);
        }

        // broadsword keyword
        if (weapon.AsOverride().Keywords!.Contains("WeapTypeSword".GetFormKey()) && !isSkyReType)
        {
            weapon.AsOverride().Keywords!.Add("skyre__WeapTypeBroadsword".GetFormKey());
            if (!PatchingData.Overridden) PatchingData.Log.Info($"New subtype is {"type_broadsword".GetT9n("english")}", true);
        }

        // shortbow keyword
        if (weapon.AsOverride().Keywords!.Contains("WeapTypeBow".GetFormKey()) && !isSkyReType)
        {
            weapon.AsOverride().Keywords!.Add("skyre__WeapTypeShortbow".GetFormKey());
            if (!PatchingData.Overridden) PatchingData.Log.Info($"New subtype is {"type_shortbow".GetT9n("english")}", true);
        }
    }

    /// <summary>
    /// Returns archetype damage value according to the settings.
    /// </summary>
    /// <returns>Base damage value depending on weapon archetype (value for one-handed for default).</returns>
    private static ushort GetDamageData() => PatchingData.AnimType switch
    {
        WeaponAnimationType.Bow => Settings.Weapons.BowBase,
        WeaponAnimationType.Crossbow => Settings.Weapons.CrossbowBase,
        WeaponAnimationType.TwoHandAxe => Settings.Weapons.TwoHandedBase,
        WeaponAnimationType.TwoHandSword => Settings.Weapons.TwoHandedBase,
        _ => Settings.Weapons.OneHandedBase,
    };

    /// <summary>
    /// Returns damage, speed and reach of the weapon type according to the rules.<br><br>
    /// Unlike weapon material, weapon type values are mandatory; weapon type will be considered unknown 
    /// if speed or reach resulted in type default value.
    /// </summary>
    /// <param name="weapon">Processed weapon record.</param>
    /// <returns>A tuple of damage, speed and reach associated with the weapon type, 
    /// or default values if type is unknown or relevant value has incorrect type.</returns>
    private static (int, float, float) GetTypeData(IWeaponGetter weapon)
    {
        StaticsData typeEntry = Statics.AllTypes.FirstOrDefault(type => weapon.AsOverride().Keywords!.Contains(type.Kwda!));

        if (typeEntry.Id is null)
        {
            PatchingData.Log.Error("Unable to determine the weapon type");
            return (default, default, default);
        }

        // type damage
        JsonNode? damageNode = 
            Helpers.RuleByName(weapon.AsOverride().Name!.ToString()!, Rules["types"]!.AsArray(), data1: "names", data2: "damage", true) ?? 
            Helpers.RuleByName(typeEntry.Id, Rules["types"]!.AsArray(), data1: "id", data2: "damage", true);
        int? damageValue = damageNode?.AsType<int>();

        // type speed
        JsonNode? speedNode = 
            Helpers.RuleByName(weapon.AsOverride().Name!.ToString()!, Rules["types"]!.AsArray(), data1: "names", data2: "speed", true) ??
            Helpers.RuleByName(typeEntry.Id, Rules["types"]!.AsArray(), data1: "id", data2: "speed", true);
        float? speedValue = speedNode?.AsType<float>();

        // type reach
        JsonNode? reachNode = 
            Helpers.RuleByName(weapon.AsOverride().Name!.ToString()!, Rules["types"]!.AsArray(), data1: "names", data2: "reach", true) ??
            Helpers.RuleByName(typeEntry.Id, Rules["types"]!.AsArray(), data1: "id", data2: "reach", true);
        float? reachValue = reachNode?.AsType<float>();

        return (damageValue ?? default, speedValue ?? default, reachValue ?? default);
    }

    /// <summary>
    /// Returns damage, speed and reach factors of the weapon material according to the rules.<br><br>
    /// Unlike weapon type, weapon material factors are optional, and could be null (will be transformed defaults).
    /// </summary>
    /// <param name="weapon">Processed weapon record.</param>
    /// <returns>A tuple of damage, speed and reach factors associated with the weapon material, 
    /// or default values if material is unknown or relevant value has incorrect type.</returns>
    private static (int, float, float) GetMaterialData(IWeaponGetter weapon)
    {
        StaticsData materialEntry = Statics.AllMaterials.FirstOrDefault(type => weapon.AsOverride().Keywords!.Contains(type.Kwda!));

        if (materialEntry.Id is null)
        {
            // bound weapons use pseudo-material "bound", that has no factors
            if (!PatchingData.BoundWeapon) PatchingData.Log.Error("Unable to determine the weapon material");
            return (default, default, 1f);
        }

        // material damage
        JsonNode? damageNode =
            Helpers.RuleByName(weapon.AsOverride().Name!.ToString()!, Rules["materials"]!.AsArray(), data1: "names", data2: "damage") ??
            Helpers.RuleByName(materialEntry.Id, Rules["materials"]!.AsArray(), data1: "id", data2: "damage");
        int? damageData = damageNode?.AsType<int>();

        // material speed mod
        JsonNode ? speedNode = 
            Helpers.RuleByName(weapon.AsOverride().Name!.ToString()!, Rules["materials"]!.AsArray(), data1: "names", data2: "speedMod") ??
            Helpers.RuleByName(materialEntry.Id, Rules["materials"]!.AsArray(), data1: "id", data2: "speedMod");
        float? speedModData = speedNode?.AsType<float>();

        // material crit damage mult
        JsonNode? critNode = 
            Helpers.RuleByName(weapon.AsOverride().Name!.ToString()!, Rules["materials"]!.AsArray(), data1: "names", data2: "critMult") ??
            Helpers.RuleByName(materialEntry.Id, Rules["materials"]!.AsArray(), data1: "id", data2: "critMult");
        float? critMultData = critNode?.AsType<float>();

        return (damageData ?? default, speedModData ?? default, critMultData ?? 1f);
    }

    /// <summary>
    /// Returns damage value multiplier according to the rules.
    /// </summary>
    /// <param name="weapon">Processed weapon record.</param>
    /// <returns>Weapon damage multipier as float, or 1 if value is incorrect or <= 0.</returns>
    private static float GetExtraDamageMod(IWeaponGetter weapon)
    {
        JsonNode? modifierNode = Helpers.RuleByName(weapon.Name!.ToString()!, Rules["damageModifiers"]!.AsArray(), data1: "names", data2: "multiplier");
        float? modifierData = modifierNode?.AsType<float>();

        if (modifierData is not null)
            return (float)(modifierData > 0.0f ? modifierData : 1.0f);

        return 1.0f;
    }

    /// <summary>
    /// Initiates crossbows processing.
    /// </summary>
    /// <param name="weapon">Processed weapon record.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    private static void ProcessCrossbows(IWeaponGetter weapon, List<string> excludedNames)
    {
        if (PatchingData.AnimType != WeaponAnimationType.Crossbow) return;

        if (weapon.DetectionSoundLevel != SoundLevel.Normal)
            weapon.AsOverride().DetectionSoundLevel = SoundLevel.Normal;

        if ("name_enhanced".GetT9n().RegexMatch(weapon.AsOverride().Name!.ToString()!, true))
        {
            weapon.AsOverride().Keywords!.Add("MagicDisallowEnchanting".GetFormKey());
            return;
        }

        if (weapon.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded) PatchingData.Log.Info("Found in the \"No enhanced crossbows\" list");
            return;
        }

        if (PatchingData.Unique)
        {
            PatchingData.Log.Info("No enhanced crossbows were generated (has \"No breakdown\" keyword)", true);
            return;
        }

        StaticsData? materialEntry = Statics.AllMaterials.FirstOrDefault(type => weapon.AsOverride().Keywords!.Contains(type.Kwda!));
        if (materialEntry is null) return;

        int i = 0;
        foreach (var subtypeA in Statics.CrossbowSubtypes)
        {
            for (int j = i; j < 4; j++)
            {
                StaticsData subtypeB = Statics.CrossbowSubtypes[j];
                CrossbowSubtype newSubtype = new(
                    Id: subtypeA.Id.GetT9n(),
                    EdId: subtypeA.Id.GetT9n("english"),
                    Desc: "desc_enhanced".GetT9n() + " " + subtypeA.Desc!.GetT9n(),
                    Kwda: [subtypeA.Kwda!, "DLC1CrossbowIsEnhanced".GetFormKey(), "MagicDisallowEnchanting".GetFormKey()],
                    Perk: subtypeA.Perks
                );

                if (subtypeB.Id != subtypeA.Id)
                {
                    newSubtype = new(
                        Id: subtypeB.Id.GetT9n() + " " + newSubtype.Id,
                        EdId: subtypeB.Id.GetT9n("english") + newSubtype.EdId,
                        Desc: newSubtype.Desc + " " + subtypeB.Desc!.GetT9n(),
                        Kwda: [.. newSubtype.Kwda, subtypeB.Kwda!],
                        Perk: [.. newSubtype.Perk, subtypeB.Perks[0]]
                    );
                }

                CreateCrossbowVariant(weapon.AsOverride(), (StaticsData)materialEntry, newSubtype, i, j);
            }

            i++;
        }
    }

    /// <summary>
    /// Creates crossbow weapon record of a certain subtype based on the original.<br/>
    /// </summary>
    /// <param name="weapon">Original crossbow record.</param>
    /// <param name="material">Material data struct.</param>
    /// <param name="subtype">Crossbow subtype data struct.</param>
    /// <param name="pIndex">Primary subtype index.</param>
    /// <param name="sIndex">Secondary subtype index (crossbow considered double-reforged if pIndex != sIndex).</param>
    private static void CreateCrossbowVariant(Weapon weapon, StaticsData material, CrossbowSubtype subtype, int pIndex, int sIndex)
    {
        Weapon newCrossbow = Executor.State!.PatchMod.Weapons.DuplicateInAsNewRecord(weapon);

        newCrossbow.Name = Settings.Weapons.SuffixedNames ?
            weapon.Name! + ", " + subtype.Id.ToLower() :
            subtype.Id + " " + weapon.Name!;

        string newEditorID = "RP_WEAP_" + subtype.EdId + "_" + weapon.EditorID;
        newCrossbow.EditorID = EditorIDs.Unique(newEditorID);
        newCrossbow.Description = subtype.Desc;

        // setting script
        newCrossbow.VirtualMachineAdapter ??= new VirtualMachineAdapter();
        var newScript = new ScriptEntry
        {
            Name = "DLC1EnhancedCrossBowAddPerkScript",
            Flags = ScriptEntry.Flag.Local
        };
        var newProperty = new ScriptObjectProperty
        {
            Name = "DLC1EnchancedCrossbowArmorPiercingPerk",
            Flags = ScriptProperty.Flag.Edited,
            Object = new FormLink<IPerkGetter>("DLC1EnchancedCrossbowArmorPiercingPerk".GetFormKey())
        };

        newScript.Properties.Add(newProperty);
        newCrossbow.VirtualMachineAdapter.Scripts.Insert(newCrossbow.VirtualMachineAdapter.Scripts.Count, newScript);

        // adding keywords
        foreach (var kwda in subtype.Kwda)
        {
            if (!kwda.IsNull && !newCrossbow.Keywords!.Contains(kwda)) newCrossbow.Keywords!.Add(kwda);
        }

        // modifying stats
        bool isDouble = pIndex != sIndex;

        // damage
        ushort oldDamage = newCrossbow.BasicStats!.Damage;
        newCrossbow.BasicStats.Damage = 
            (ushort)(oldDamage * (Statics.CrossbowMods[pIndex].Damage / 100f) * (isDouble ? (Statics.CrossbowMods[sIndex].Damage / 100f) : 1));

        // weight
        float oldWeight = newCrossbow.BasicStats.Weight;
        newCrossbow.BasicStats.Weight =
            oldWeight * (Statics.CrossbowMods[pIndex].Weight / 100f * (isDouble ? (Statics.CrossbowMods[sIndex].Weight / 100f) : 1));

        // value
        uint oldValue = newCrossbow.BasicStats.Value;
        newCrossbow.BasicStats.Value = 
            (uint)(oldValue * (Settings.Weapons.EnhancedCrossbowsPrice / 100f) * (isDouble ? 1.2f : 1f));

        // speed
        float oldSpeed = newCrossbow.Data!.Speed;
        newCrossbow.Data.Speed =
            oldSpeed * (Statics.CrossbowMods[pIndex].Speed / 100f * (isDouble ? (Statics.CrossbowMods[sIndex].Speed / 100f) : 1));

        // sound level
        newCrossbow.DetectionSoundLevel = Statics.CrossbowMods[sIndex].SoundLevel;


        // source crossbow
        if (subtype.Perk.Count > 1)
        {
            subtype.Perk.Add("skyre_MARArtificer".GetFormKey());
            weapon = (Weapon)PatchingData.ThisRecord!;
        }
        else
        {
            PatchingData.ThisRecord = newCrossbow;
        }

        // recipe ingredients
        List<RecipeData> ingredients = [
            new RecipeData{ Items = ["LeatherStrips".GetFormKey() ], Qty = 2 },
            new RecipeData{ Items = ["SprigganSap".GetFormKey()   ], Qty = 1 },
            new RecipeData{ Items = [ material.Items[0]           ], Qty = 1 }
        ];

        Logger log = new();
        Logs.Add(new Report { Record = newCrossbow, Entry = log });

        if (Settings.Debug.ShowVerboseData)
        {
            log.Info($"Speed: {Math.Round(newCrossbow.Data!.Speed, 4)} (original: {oldSpeed}, " +
                $"subtype {(isDouble ? "A " : "")}mult: x{Statics.CrossbowMods[pIndex].Speed / 100f}" +
                $"{(isDouble ? $", subtype B mult: x{Statics.CrossbowMods[sIndex].Speed / 100f}" : "")})");
            log.Info($"Damage: {newCrossbow.BasicStats!.Damage} (original: {oldDamage}, " +
                $"subtype {(isDouble ? "A " : "")}mult: x{Statics.CrossbowMods[pIndex].Damage / 100f}" +
                $"{(isDouble ? $", subtype B mult: x{Statics.CrossbowMods[sIndex].Damage / 100f}" : "")})");
            log.Info($"Weight: {Math.Round(newCrossbow.BasicStats!.Weight, 2)} (original: {oldWeight}, " +
                $"subtype {(isDouble ? "A " : "")}mult: x{Statics.CrossbowMods[pIndex].Weight / 100f}" +
                $"{(isDouble ? $", subtype B mult: x{Statics.CrossbowMods[sIndex].Weight / 100f}" : "")})");
            log.Info($"Value: {newCrossbow.BasicStats!.Value} (original: {oldValue}, " +
                $"subtype mult: x{Settings.Weapons.EnhancedCrossbowsPrice / 100f}" +
                $"{(isDouble ? $", double enhanced mult: x1,2" : "")})");
        }

        AddCraftingRecipe(newCrossbow, weapon, subtype.Perk, material, ingredients);
    }

    /// <summary>
    /// Initiates silver weapons processing.
    /// </summary>
    /// <param name="weapon">Processed weapon record.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    private static void ProcessSilverWeapons(IWeaponGetter weapon, List<string> excludedNames)
    {
        if (weapon.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded) PatchingData.Log.Info("Found in the \"No Refined Silver variety\" list");
            return;
        }

        if (!weapon.Keywords!.Contains("WeapMaterialSilver".GetFormKey()) ||
            weapon.Keywords!.Contains("skyre__WeapMaterialSilverRefined".GetFormKey()) || 
            weapon.Keywords!.Contains("WeapTypeBow".GetFormKey()))
        { 
            return;
        }

        if (PatchingData.Unique)
        {
            PatchingData.Log.Info("No Refined Silver variety was generated (has \"No breakdown\" keyword)", true);
            return;
        }

        Weapon newWeapon = Executor.State!.PatchMod.Weapons.DuplicateInAsNewRecord(weapon);
        StaticsData material = Statics.AllMaterials.First(type => type.Kwda == "skyre__WeapMaterialSilverRefined".GetFormKey());

        string prefix = "name_refined".GetT9n(Settings.General.GameLanguage.ToString(), newWeapon.Name!.ToString());
        newWeapon.Name = Settings.Weapons.SuffixedNames ?
            weapon.Name! + ", " + prefix.ToLower() :
            prefix + " " + weapon.Name!;

        string newEditorID = "RP_WEAP_" + prefix + "_" + weapon.EditorID;
        newWeapon.EditorID = EditorIDs.Unique(newEditorID);
        newWeapon.Description = "desc_refined".GetT9n();

        // setting script
        newWeapon.VirtualMachineAdapter = new VirtualMachineAdapter();
        var newScript = new ScriptEntry
        {
            Name = "SilverSwordScript",
            Flags = ScriptEntry.Flag.Local
        };
        var newProperty = new ScriptObjectProperty
        {
            Name = "SilverPerk",
            Flags = ScriptProperty.Flag.Edited,
            Object = new FormLink<IPerkGetter>("SilverPerk".GetFormKey())
        };

        newScript.Properties.Add(newProperty);
        newWeapon.VirtualMachineAdapter.Scripts.Insert(newWeapon.VirtualMachineAdapter.Scripts.Count, newScript);

        // adding keywords
        newWeapon.Keywords!.Add("skyre__WeapMaterialSilverRefined".GetFormKey());

        // modifying stats
        float oldSpeed = newWeapon.Data!.Speed;
        newWeapon.Data!.Speed *= 1.1f;
        float oldWeight = newWeapon.BasicStats!.Weight;
        newWeapon.BasicStats!.Weight *= 0.8f;
        uint oldValue = newWeapon.BasicStats.Value;
        newWeapon.BasicStats.Value = (uint)(oldValue * (Settings.Weapons.RefinedSilverPrice / 100f));

        Logger log = new();
        Logs.Add(new Report { Record = newWeapon, Entry = log });

        if (Settings.Debug.ShowVerboseData)
        {
            log.Info($"Speed: {Math.Round(newWeapon.Data!.Speed, 4)} (original: {Math.Round(oldSpeed, 2)}, Refined Silver mult: x1,1)");
            log.Info($"Weight: {Math.Round(newWeapon.BasicStats!.Weight, 2)} (original: {oldWeight}, Refined Silver mult: x0,8)");
            log.Info($"Value: {newWeapon.BasicStats!.Value} (original: {oldValue}, Refined Silver mult: x{Settings.Weapons.RefinedSilverPrice / 100f})");
        }

        // shortspears anim type bypass
        if (newWeapon.Keywords!.Contains("skyre__WeapTypeShortspear".GetFormKey()) && Settings.Weapons.AltShortspears)
        {
            if (PatchingData.AnimType == WeaponAnimationType.OneHandSword)
                newWeapon.Data!.AnimationType = WeaponAnimationType.OneHandAxe;
        };

        // recipe ingredients
        List<RecipeData> ingredients = [
            new RecipeData{ Items = ["IngotGold".GetFormKey() ], Qty = 1 }
        ];

        AddCraftingRecipe(newWeapon, weapon, [], material, ingredients);
    }

    /// <summary>
    /// Generates the crafting recipe for weapon variants (enhanced crossbows and Refined Silver weapons).<br/>
    /// </summary>
    /// <param name="newWeapon">The variant of the oldWeapon as Weapon.</param>
    /// <param name="oldWeapon">The weapon record as Weapon.</param>
    /// <param name="perks">List of variant perk FormKeys.</param>
    /// <param name="material">The weapon material data as StaticsData struct.</param>
    /// <param name="ingredients">List of ingredients and their quantity.</param>
    private static void AddCraftingRecipe(IWeaponGetter newWeapon, IWeaponGetter oldWeapon, List<FormKey> perks, StaticsData material, List<RecipeData> ingredients)
    {
        string newEditorID = newWeapon.EditorID!.Replace("RP_WEAP_", "RP_WEAP_CRAFT_");
        ConstructibleObject newRecipe = Executor.State!.PatchMod.ConstructibleObjects.AddNew();

        newRecipe.EditorID = EditorIDs.Unique(newEditorID);
        newRecipe.Items = [];

        ContainerItem baseItem = new();
        baseItem.Item = oldWeapon.ToNullableLink();
        ContainerEntry baseEntry = new();
        baseEntry.Item = baseItem;
        baseEntry.Item.Count = 1;
        newRecipe.Items.Add(baseEntry);

        foreach (var entry in ingredients)
        {
            ContainerItem newItem = new();

            if (Executor.State!.LinkCache.TryResolve<IMiscItemGetter>(entry.Items[0], out var miscItem))
            {
                newItem.Item = miscItem.ToNullableLink();
            }
            else if (Executor.State!.LinkCache.TryResolve<IIngredientGetter>(entry.Items[0], out var ingrItem))
            {
                newItem.Item = ingrItem.ToNullableLink();
            }
            else
            {
                PatchingData.Log.Error($"Ingredient {entry.Items[0]} has unexpected record type!");
                Executor.State!.PatchMod.ConstructibleObjects.Remove(newRecipe);
                return;
            }

            ContainerEntry newEntry = new();
            newEntry.Item = newItem;
            newEntry.Item.Count = entry.Qty;
            newRecipe.Items.Add(newEntry);
        }

        // retaining original conditions (for crossbows)
        if (Settings.Weapons.KeepConditions && PatchingData.AnimType == WeaponAnimationType.Crossbow)
        {
            var craftingRecipe = Executor.AllRecipes!.FirstOrDefault(
                cobj => cobj.CreatedObject.FormKey == oldWeapon.FormKey &&
                (cobj.WorkbenchKeyword.FormKey == "CraftingSmithingForge".GetFormKey() || 
                cobj.WorkbenchKeyword.FormKey == "DLC1CraftingDawnguard".GetFormKey()));

            if (craftingRecipe is not null && craftingRecipe.Conditions.Count > 0)
            {
                foreach (var cond in craftingRecipe.Conditions)
                {
                    newRecipe.Conditions.Add(cond.DeepCopy());
                }

                newRecipe.WorkbenchKeyword = Executor.State!.LinkCache.Resolve(craftingRecipe.WorkbenchKeyword).ToNullableLink();
            }
        }

        // subtype perks (for crossbows)
        foreach (var perk in perks)
        {
            newRecipe.AddHasPerkCondition(perk);
        }

        // material perks
        if (material.Perks.Count > 0)
        {
            foreach (var perk in material.Perks)
            {
                if (newRecipe.Conditions.Any(cond => cond.Data is HasPerkConditionData hasPerk && perk == hasPerk.Perk.Link.FormKey))
                    continue;

                Condition.Flag flag = material.Perks.IndexOf(perk) == material.Perks.Count - 1 ||
                    PatchingData.AnimType != WeaponAnimationType.Crossbow ? 0 : Condition.Flag.OR;
                newRecipe.AddHasPerkCondition(perk, flag);
            }
        }

        if (!Settings.Weapons.AllWeaponRecipes)
            newRecipe.AddGetItemCountCondition(oldWeapon.FormKey, CompareOperator.GreaterThanOrEqualTo);

        newRecipe.AddGetEquippedCondition(oldWeapon.FormKey, CompareOperator.NotEqualTo);

        newRecipe.CreatedObject = newWeapon.ToNullableLink();
        if (newRecipe.WorkbenchKeyword.IsNull) newRecipe.WorkbenchKeyword = 
            Executor.State!.LinkCache.Resolve<IKeywordGetter>("CraftingSmithingForge".GetFormKey()).ToNullableLink();
        newRecipe.CreatedObjectCount = 1;

        AddTemperingRecipe(newWeapon, material);
    }

    /// <summary>
    /// Generates a tempering recipe for the weapon.
    /// </summary>
    /// <param name="weapon">The weapon record as IWeaponGetter.</param>
    /// <param name="material">An associated material as StaticsData struct.</param>
    private static void AddTemperingRecipe(IWeaponGetter newWeapon, StaticsData material)
    {
        string newEditorID = newWeapon.EditorID!.Replace("RP_WEAP_", "RP_WEAP_TEMP_");
        ConstructibleObject newRecipe = Executor.State!.PatchMod.ConstructibleObjects.AddNew();

        newRecipe.EditorID = EditorIDs.Unique(newEditorID);
        newRecipe.Items = [];

        ContainerItem baseItem = new();
        baseItem.Item = Executor.State!.LinkCache.Resolve<IMiscItemGetter>(material.Items[0]).ToNullableLink();
        ContainerEntry baseEntry = new();
        baseEntry.Item = baseItem;
        baseEntry.Item.Count = 1;
        newRecipe.Items.Add(baseEntry);

        newRecipe.AddIsEnchantedCondition(Condition.Flag.OR);
        newRecipe.AddHasPerkCondition("ArcaneBlacksmith".GetFormKey());

        foreach (var perk in material.Perks)
        {
            Condition.Flag flag = material.Perks.IndexOf(perk) == material.Perks.Count - 1 ? 0 : Condition.Flag.OR;
            newRecipe.AddHasPerkCondition(perk, flag);
        }

        newRecipe.CreatedObject = newWeapon.ToNullableLink();
        newRecipe.WorkbenchKeyword = Executor.State!.LinkCache.Resolve<IKeywordGetter>("CraftingSmithingSharpeningWheel".GetFormKey()).ToNullableLink();
        newRecipe.CreatedObjectCount = 1;

        AddBreakdownRecipe(newWeapon);
    }

    /// <summary>
    /// Recipes processor.
    /// </summary>
    /// <param name="weapon">The weapon record as IWeaponGetter.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    private static void ProcessRecipes(IWeaponGetter weapon, List<string> excludedNames)
    {
        if (PatchingData.Modified) weapon = weapon.AsOverride();

        if (weapon.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
                PatchingData.Log.Info($"Found in the \"No recipe modifications\" list");

            return;
        }

        foreach (var recipe in Executor.AllRecipes!)
        {
            if (recipe.CreatedObject.FormKey == weapon.FormKey)
            {
                if (PatchingData.AnimType == WeaponAnimationType.Crossbow && 
                    (recipe.WorkbenchKeyword.FormKey == "CraftingSmithingForge".GetFormKey() || 
                    recipe.WorkbenchKeyword.FormKey == "DLC1CraftingDawnguard".GetFormKey()))
                    ModCraftingRecipe(recipe, weapon);

                if (recipe.WorkbenchKeyword.FormKey == "CraftingSmithingSharpeningWheel".GetFormKey())
                    ModTemperingRecipe(recipe, weapon);
            }
        }

        AddBreakdownRecipe(weapon);
    }

    private static void ModCraftingRecipe(IConstructibleObjectGetter recipe, IWeaponGetter weapon)
    {
        bool isEnhanced = "name_enhanced".GetT9n().RegexMatch(weapon.Name!.ToString()!, true);
        ConstructibleObject craftingRecipe = Executor.State!.PatchMod.ConstructibleObjects.GetOrAddAsOverride(recipe);

        if (Settings.Weapons.NoVanillaEnhanced && isEnhanced)
        {
            craftingRecipe.WorkbenchKeyword = new FormLinkNullable<IKeywordGetter>();
            return;
        }
        else if (!Settings.Weapons.KeepConditions)
        {
            craftingRecipe.Conditions?.Clear();
            List<FormKey> weaponPerks = [.. Statics.AllMaterials
                .Where(entry => weapon.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
                .Where(entry => entry.Perks.Count > 0)
                .SelectMany(entry => entry.Perks)
                .Distinct()];

            if (weaponPerks.Count > 0)
            {
                Condition.Flag flag = Condition.Flag.OR;
                foreach (var perk in weaponPerks)
                {
                    if (weaponPerks.IndexOf(perk) == weaponPerks.Count - 1) flag = 0;
                    craftingRecipe.AddHasPerkCondition(perk, flag);
                }
            }
        }

        craftingRecipe.AddHasPerkCondition(
            isEnhanced ? "skyre_MARCrossbowRecurve".GetFormKey() : "skyre_MARBallistics".GetFormKey());

        craftingRecipe.WorkbenchKeyword = Executor.State!.LinkCache.Resolve<IKeywordGetter>(
            Settings.Weapons.KeepConditions ? "DLC1CraftingDawnguard".GetFormKey() : "CraftingSmithingForge".GetFormKey())
            .ToNullableLink();
    }

    /// <summary>
    /// Modifies tempering recipes for the weapon.<br/><br/>
    /// The method removes existing HasPerk-type conditions, where perk is a smithing perk, and adds<br/>
    /// new ones corresponding to the weapon's keywords. If more than 1 perk is associated with a keyword,<br/>
    /// all but the last HasPerk-type conditions will have the OR flag.
    /// </summary>
    /// <param name="recipe">The tempering recipe record as IConstructibleObjectGetter.</param>
    /// <param name="weapon">The weapon record as IWeaponGetter.</param>
    private static void ModTemperingRecipe(IConstructibleObjectGetter recipe, IWeaponGetter weapon)
    {
        if (recipe.Conditions.Count > 0 && recipe.Conditions.Any(condition => condition.Data is EPTemperingItemIsEnchantedConditionData))
        {
            List<FormKey> allPerks = [.. Statics.AllMaterials
                .Where(entry => entry.Perks.Count > 0)
                .SelectMany(entry => entry.Perks)
                .Distinct()];
            List<FormKey> materialPerks = [.. Statics.AllMaterials
                .Where(entry => weapon.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
                .Where(entry => entry.Perks.Count > 0)
                .SelectMany(entry => entry.Perks)
                .Distinct()];
            List<FormKey> materialItems = [.. Statics.AllMaterials
                .Where(entry => weapon.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
                .Select(entry => entry.Items[0])
                .Distinct()];

            ConstructibleObject newRecipe = Executor.State!.PatchMod.ConstructibleObjects.GetOrAddAsOverride(recipe);
            for (int i = newRecipe.Conditions.Count - 1; i >= 0; i--)
            {
                if (newRecipe.Conditions[i].Data is HasPerkConditionData hasPerk && allPerks.Any(perk => perk == hasPerk.Perk.Link.FormKey))
                {
                    newRecipe.Conditions.Remove(newRecipe.Conditions[i]);
                }
            }

            if (PatchingData.Overridden)
            {
                newRecipe.Items?.Clear();
                foreach (var item in materialItems)
                {
                    ContainerItem newItem = new();
                    newItem.Item = Executor.State!.LinkCache.Resolve<IMiscItemGetter>(item).ToNullableLink();
                    ContainerEntry newEntry = new();
                    newEntry.Item = newItem;
                    newEntry.Item.Count = 1;
                    newRecipe.Items!.Add(newEntry);
                }
            }

            foreach (var perk in materialPerks)
            {
                Condition.Flag flag = materialPerks.IndexOf(perk) == materialPerks.Count - 1 ? 0 : Condition.Flag.OR;
                newRecipe.AddHasPerkCondition(perk, flag);
            }

            // removing ITPOs
            if (recipe.Conditions.Count == newRecipe.Conditions.Count
                && !recipe.Conditions.Except(newRecipe.Conditions).Any())
            {
                Executor.State!.PatchMod.ConstructibleObjects.Remove(newRecipe);
            }
        }
    }

    /// <summary>
    /// Generates a breakdown recipe for the weapon.
    /// </summary>
    /// <param name="weapon">The weapon record as IWeaponGetter.</param>
    /// <param name="noRecipes">True for weapon-type records with no material keywords.</param>
    private static void AddBreakdownRecipe(IWeaponGetter weapon, bool noRecipes = false)
    {
        if (PatchingData.Unique)
        {
            PatchingData.Log.Info($"No breakdown recipe was generated (has \"No breakdown\" keyword)", true);
            return;
        }

        IConstructibleObjectGetter? craftingRecipe = null;
        if (!noRecipes)
        {
            foreach (var recipe in Executor.AllRecipes!)
            {
                if (Settings.General.SkipExisting
                    && recipe.Items?.FirstOrDefault()?.Item == weapon
                    && recipe.WorkbenchKeyword.FormKey == "CraftingSmelter".GetFormKey())
                {
                    PatchingData.Log.Info($"No breakdown recipe was generated (has a breakdown recipe in the {recipe.FormKey.ModKey.FileName})");
                    return;
                }

                if (craftingRecipe is null
                    && recipe.CreatedObject.FormKey == weapon.FormKey
                    && recipe.WorkbenchKeyword.FormKey == "CraftingSmithingForge".GetFormKey())
                {
                    craftingRecipe = recipe;
                }
            }
        }

        bool isBig = PatchingData.AnimType == WeaponAnimationType.TwoHandAxe ||
            PatchingData.AnimType == WeaponAnimationType.TwoHandSword || 
            weapon.Keywords!.Contains("skyre__WeapTypeLongbow".GetFormKey());

        List<FormKey> weaponPerks = [.. Statics.AllMaterials
            .Where(entry => weapon.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
            .Where(entry => entry.Perks.Count > 0)
            .SelectMany(entry => entry.Perks)
            .Distinct()];
        List<FormKey> weaponItems = [.. Statics.AllMaterials
            .Where(entry => weapon.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
            .Select(entry => entry.Items[0])
            .Distinct()];

        bool isWeak = weaponPerks.Count == 0 || weapon.Keywords!.Contains("WeapMaterialDraugrHoned".GetFormKey()) ||
            weapon.Keywords!.Contains("WAF_WeapMaterialForsworn".GetFormKey());

        bool extraOutput = weapon.Keywords!.Contains("WeapMaterialWood".GetFormKey());

        bool fromRecipe = false;
        int qty = 1;
        FormKey ingr = weaponItems[0];
        if (craftingRecipe is not null)
        {
            foreach (var entry in weaponItems)
            {
                if (craftingRecipe.Items!.Count == 0) continue;
                foreach (var elem in craftingRecipe.Items)
                {
                    if (elem.Item.Item.FormKey == entry && elem.Item.Count > qty)
                    {
                        ingr = entry;
                        qty = elem.Item.Count;
                        fromRecipe = true;
                    }
                }
            }
        }

        int mod = Math.Clamp((isBig ? 1 : 0) + (isWeak ? -1 : 0) + (extraOutput ? 1 : 0), 1, 2);
        float outputQty = (qty + mod) * (Settings.Weapons.RefundAmount / 100f);
        int inputQty = (int)(outputQty < 1 && fromRecipe ? Math.Round(1 / outputQty) : 1);

        string newEditorID = "RP_WEAP_BREAK_" + weapon.EditorID!.Replace("RP_WEAP_", "");
        ConstructibleObject cobj = Executor.State!.PatchMod.ConstructibleObjects.AddNew();

        cobj.EditorID = EditorIDs.Unique(newEditorID);
        cobj.Items = [];

        ContainerItem newItem = new();
        newItem.Item = weapon.ToNullableLink();
        ContainerEntry newEntry = new();
        newEntry.Item = newItem;
        newEntry.Item.Count = inputQty;
        cobj.Items.Add(newEntry);

        cobj.AddHasPerkCondition("skyre_SMTBreakdown".GetFormKey());
        if (weaponPerks.Count > 0)
        {
            Condition.Flag flag = Condition.Flag.OR;
            foreach (var perk in weaponPerks)
            {
                if (weaponPerks.IndexOf(perk) == weaponPerks.Count - 1) flag = 0;
                cobj.AddHasPerkCondition(perk, flag);
            }
        }
        cobj.AddGetItemCountCondition(weapon.FormKey, CompareOperator.GreaterThanOrEqualTo);
        cobj.AddGetEquippedCondition(weapon.FormKey, CompareOperator.NotEqualTo);
        cobj.CreatedObject = Executor.State!.LinkCache.Resolve<IMiscItemGetter>(ingr).ToNullableLink();
        cobj.WorkbenchKeyword = Executor.State!.LinkCache.Resolve<IKeywordGetter>("CraftingSmelter".GetFormKey()).ToNullableLink();
        cobj.CreatedObjectCount = (ushort)Math.Clamp(Math.Floor(outputQty), 1, qty + (fromRecipe ? 0 : mod));
    }

    private static void Finalizer(IWeaponGetter weapon)
    {
        // shortspears animation type 
        if (weapon.Keywords!.Contains("skyre__WeapTypeShortspear".GetFormKey()) && Settings.Weapons.AltShortspears)
        {
            if (PatchingData.AnimType == WeaponAnimationType.OneHandSword)
            {
                weapon.AsOverride().Data!.AnimationType = WeaponAnimationType.OneHandAxe;
            }
        };

        // add/remove silver weapon script
        if (!weapon.Keywords!.Contains("WeapTypeBow".GetFormKey()))
        {
            bool hasVMAD = weapon.VirtualMachineAdapter is not null;
            if (weapon.Keywords!.Contains("WeapMaterialSilver".GetFormKey()))
            {
                if (!hasVMAD) weapon.AsOverride().VirtualMachineAdapter = new VirtualMachineAdapter();
                if (!weapon.VirtualMachineAdapter!.Scripts.Any(script => script.Name == "SilverSwordScript"))
                {
                    var newScript = new ScriptEntry
                    {
                        Name = "SilverSwordScript",
                        Flags = ScriptEntry.Flag.Local
                    };
                    var newProperty = new ScriptObjectProperty
                    {
                        Name = "SilverPerk",
                        Flags = ScriptProperty.Flag.Edited,
                        Object = new FormLink<IPerkGetter>("SilverPerk".GetFormKey())
                    };

                    newScript.Properties.Add(newProperty);
                    weapon.AsOverride().VirtualMachineAdapter!.Scripts.Insert(weapon.VirtualMachineAdapter.Scripts.Count, newScript);
                }
            }
            else if (hasVMAD && weapon.VirtualMachineAdapter!.Scripts.Any(script => script.Name == "SilverSwordScript"))
            {
                weapon.AsOverride().VirtualMachineAdapter!.Scripts.RemoveAll(script => script.Name == "SilverSwordScript");
            }
        };

        // removing type tags
        if (Settings.Weapons.NoTypeTags && weapon.AsOverride().Name!.ToString().Contains("TYPETAG"))
        {
            string pattern = @"\s\[TYPETAG\s.*\]";
            weapon.AsOverride().Name = Regex.Replace(weapon.AsOverride().Name!.ToString(), pattern, "");
        }

        // removing ITPOs
        if (!PatchingData.Modified) Executor.State!.PatchMod.Weapons.Remove(weapon);
    }

    // patcher specific helpers

    /// <summary>
    /// Copies the winning override of this-parameter to the patch file, and returns it.<br/>
    /// </summary>
    /// <param name="weapon">Processed weapon record.</param>
    /// <param name="isModified">True to mark as modified in the patching data.</param>
    /// <returns>The winning override for processed armor record.</returns>
    private static Weapon AsOverride(this IWeaponGetter weapon)
    {
        if (!PatchingData.Modified) PatchingData.Modified = true;
        return Executor.State!.PatchMod.Weapons.GetOrAddAsOverride(weapon);
    }

    /// <summary>
    /// Displays patching results for the current record and records created on its basis.<br/>
    /// </summary>
    private static void ShowReport()
    {
        foreach (var report in Logs)
        {
            IWeaponGetter weapon = (IWeaponGetter)report.Record!;
            Logger log = (Logger)report.Entry!;
            log.Report($"{weapon.Name}", $"{weapon.FormKey}", $"{weapon.EditorID}", PatchingData.NonPlayable, !weapon.Template.IsNull);
        }

        Logs.Clear();
    }

    // patcher specific statics

    /// <summary>
    /// Appends patcher-specific records to the shared statics list, generates patcher-specific collections of statics.<br/>
    /// </summary>
    /// <returns>A tuple of statics collections for weapon records: all weapon types, SkyRe weapon types only, all weapon materials, <br/>
    /// SkyRe crossbow subtypes data 1, SkyRe crossbow subtypes data 2.</returns>
    private static (List<StaticsData>, List<StaticsData>, List<StaticsData>, List<CrossbowMods>, List<StaticsData>) BuildStaticsMap()
    {
        Executor.Statics!.AddRange(
        [
            new StaticsData{Id = "WeapTypeSword",                          FormKey = Helpers.ParseFormKey("Skyrim.esm|0x01e711|KWDA")                  },
            new StaticsData{Id = "WeapTypeWaraxe",                         FormKey = Helpers.ParseFormKey("Skyrim.esm|0x01e712|KWDA")                  },
            new StaticsData{Id = "WeapTypeDagger",                         FormKey = Helpers.ParseFormKey("Skyrim.esm|0x01e713|KWDA")                  },
            new StaticsData{Id = "WeapTypeMace",                           FormKey = Helpers.ParseFormKey("Skyrim.esm|0x01e714|KWDA")                  },
            new StaticsData{Id = "WeapTypeBow",                            FormKey = Helpers.ParseFormKey("Skyrim.esm|0x01e715|KWDA")                  },
            new StaticsData{Id = "WeapTypeWarhammer",                      FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06d930|KWDA")                  },
            new StaticsData{Id = "WeapTypeGreatsword",                     FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06d931|KWDA")                  },
            new StaticsData{Id = "WeapTypeBattleaxe",                      FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06d932|KWDA")                  },
            new StaticsData{Id = "CraftingSmithingSharpeningWheel",        FormKey = Helpers.ParseFormKey("Skyrim.esm|0x088108|KWDA")                  },
            new StaticsData{Id = "MagicDisallowEnchanting",                FormKey = Helpers.ParseFormKey("Skyrim.esm|0x0c27bd|KWDA")                  },
            new StaticsData{Id = "DLC1CrossbowIsEnhanced",                 FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x00399c|KWDA")               },
            new StaticsData{Id = "DLC1CraftingDawnguard",                  FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x00f806|KWDA")               },
            new StaticsData{Id = "skyre__WeapTypeBastardSword",            FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000813|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeQuarterstaff",            FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000814|KWDA") },
            new StaticsData{Id = "skyre__WeapMaterialBound",               FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000815|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeLongbow",                 FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000816|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeShortbow",                FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000817|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeBroadsword",              FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000818|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeClub",                    FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000819|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeCrossbow",                FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081a|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeGlaive",                  FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081b|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeHalberd",                 FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081c|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeHatchet",                 FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081d|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeKatana",                  FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081e|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeLongmace",                FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081f|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeLongspear",               FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000820|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeLongsword",               FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000821|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeMaul",                    FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000822|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeNodachi",                 FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000823|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeScimitar",                FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000824|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeShortspear",              FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000825|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeShortsword",              FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000826|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeTanto",                   FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000827|KWDA") },
            new StaticsData{Id = "skyre__WeapTypeWakizashi",               FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000828|KWDA") },
            new StaticsData{Id = "skyre__WeapMaterialSilverRefined",       FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000829|KWDA") },
            new StaticsData{Id = "skyre_MAREnhancedCrossbowMuffled",       FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d97|KWDA") },
            new StaticsData{Id = "skyre_MAREnhancedCrossbowSiege",         FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d98|KWDA") },
            new StaticsData{Id = "SilverPerk",                             FormKey = Helpers.ParseFormKey("Skyrim.esm|0x10d685|PERK")                  },
            new StaticsData{Id = "DLC1EnchancedCrossbowArmorPiercingPerk", FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x00399b|PERK")               },
            new StaticsData{Id = "skyre_MARCrossbowLight",                 FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d93|PERK") },
            new StaticsData{Id = "skyre_MARCrossbowRecurve",               FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d94|PERK") },
            new StaticsData{Id = "skyre_MARCrossbowMuffled",               FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d95|PERK") },
            new StaticsData{Id = "skyre_MARCrossbowSiege",                 FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d96|PERK") },
            new StaticsData{Id = "skyre_MARArtificer",                     FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000dbf|PERK") },
            new StaticsData{Id = "skyre_SMTDeepSilver",                    FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000ee4|PERK") }
        ]);

        List<StaticsData> allTypes = [
            new StaticsData{Id = "type_battleaxe",    Kwda = "WeapTypeBattleaxe".GetFormKey()  },
            new StaticsData{Id = "type_bow",          Kwda = "WeapTypeBow".GetFormKey()        },
            new StaticsData{Id = "type_broadsword",   Kwda = "WeapTypeSword".GetFormKey()      },
            new StaticsData{Id = "type_dagger",       Kwda = "WeapTypeDagger".GetFormKey()     },
            new StaticsData{Id = "type_greatsword",   Kwda = "WeapTypeGreatsword".GetFormKey() },
            new StaticsData{Id = "type_mace",         Kwda = "WeapTypeMace".GetFormKey()       },
            new StaticsData{Id = "type_waraxe",       Kwda = "WeapTypeWaraxe".GetFormKey()     },
            new StaticsData{Id = "type_warhammer",    Kwda = "WeapTypeWarhammer".GetFormKey()  }
        ];

        List<StaticsData> skyreTypes = [
            new StaticsData{Id = "type_bastard",      Kwda = "skyre__WeapTypeBastardSword".GetFormKey() },
            new StaticsData{Id = "type_broadsword",   Kwda = "skyre__WeapTypeBroadsword".GetFormKey()   },
            new StaticsData{Id = "type_club",         Kwda = "skyre__WeapTypeClub".GetFormKey()         },
            new StaticsData{Id = "type_crossbow",     Kwda = "skyre__WeapTypeCrossbow".GetFormKey()     },
            new StaticsData{Id = "type_glaive",       Kwda = "skyre__WeapTypeGlaive".GetFormKey()       },
            new StaticsData{Id = "type_halberd",      Kwda = "skyre__WeapTypeHalberd".GetFormKey()      },
            new StaticsData{Id = "type_hatchet",      Kwda = "skyre__WeapTypeHatchet".GetFormKey()      },
            new StaticsData{Id = "type_katana",       Kwda = "skyre__WeapTypeKatana".GetFormKey()       },
            new StaticsData{Id = "type_longbow",      Kwda = "skyre__WeapTypeLongbow".GetFormKey()      },
            new StaticsData{Id = "type_longmace",     Kwda = "skyre__WeapTypeLongmace".GetFormKey()     },
            new StaticsData{Id = "type_longspear",    Kwda = "skyre__WeapTypeLongspear".GetFormKey()    },
            new StaticsData{Id = "type_longsword",    Kwda = "skyre__WeapTypeLongsword".GetFormKey()    },
            new StaticsData{Id = "type_maul",         Kwda = "skyre__WeapTypeMaul".GetFormKey()         },
            new StaticsData{Id = "type_nodachi",      Kwda = "skyre__WeapTypeNodachi".GetFormKey()      },
            new StaticsData{Id = "type_quarterstaff", Kwda = "skyre__WeapTypeQuarterstaff".GetFormKey() },
            new StaticsData{Id = "type_scimitar",     Kwda = "skyre__WeapTypeScimitar".GetFormKey()     },
            new StaticsData{Id = "type_shortbow",     Kwda = "skyre__WeapTypeShortbow".GetFormKey()     },
            new StaticsData{Id = "type_shortspear",   Kwda = "skyre__WeapTypeShortspear".GetFormKey()   },
            new StaticsData{Id = "type_shortsword",   Kwda = "skyre__WeapTypeShortsword".GetFormKey()   },
            new StaticsData{Id = "type_tanto",        Kwda = "skyre__WeapTypeTanto".GetFormKey()        },
            new StaticsData{Id = "type_wakizashi",    Kwda = "skyre__WeapTypeWakizashi".GetFormKey()    }
        ];

        allTypes.InsertRange(0, skyreTypes);

        List<StaticsData> allMaterials = [
            new StaticsData{ Id = "mat_amber",      Kwda = "cc_WeapMaterialAmber".GetFormKey(),             Items = [ "cc_IngotAmber".GetFormKey() ],    Perks = [ "GlassSmithing".GetFormKey(), "EbonySmithing".GetFormKey() ]       },
            new StaticsData{ Id = "mat_blades",     Kwda = "WAF_WeapMaterialBlades".GetFormKey(),           Items = [ "IngotSteel".GetFormKey() ],       Perks = [ "SteelSmithing".GetFormKey() ]                                     },
            new StaticsData{ Id = "mat_daedric",    Kwda = "WeapMaterialDaedric".GetFormKey(),              Items = [ "IngotEbony".GetFormKey() ],       Perks = [ "DaedricSmithing".GetFormKey() ]                                   },
            new StaticsData{ Id = "mat_dawnguard",  Kwda = "WAF_DLC1WeapMaterialDawnguard".GetFormKey(),    Items = [ "IngotSteel".GetFormKey() ],       Perks = [ "SteelSmithing".GetFormKey() ]                                     },
            new StaticsData{ Id = "mat_dark",       Kwda = "cc_WeapMaterialDark".GetFormKey(),              Items = [ "IngotQuicksilver".GetFormKey() ], Perks = [ "DaedricSmithing".GetFormKey() ]                                   },
            new StaticsData{ Id = "mat_dragonbone", Kwda = "DLC1WeapMaterialDragonbone".GetFormKey(),       Items = [ "DragonBone".GetFormKey() ],       Perks = [ "DragonArmor".GetFormKey() ]                                       },
            new StaticsData{ Id = "mat_draugr",     Kwda = "WeapMaterialDraugr".GetFormKey(),               Items = [ "IngotQuicksilver".GetFormKey() ], Perks = [ "AdvancedArmors".GetFormKey() ]                                    },
            new StaticsData{ Id = "mat_draugrh",    Kwda = "WeapMaterialDraugrHoned".GetFormKey(),          Items = [ "IngotQuicksilver".GetFormKey() ], Perks = [ "AdvancedArmors".GetFormKey() ]                                    },
            new StaticsData{ Id = "mat_dwarven",    Kwda = "WeapMaterialDwarven".GetFormKey(),              Items = [ "IngotDwarven".GetFormKey() ],     Perks = [ "DwarvenSmithing".GetFormKey() ]                                   },
            new StaticsData{ Id = "mat_ebony",      Kwda = "WeapMaterialEbony".GetFormKey(),                Items = [ "IngotEbony".GetFormKey() ],       Perks = [ "EbonySmithing".GetFormKey() ]                                     },
            new StaticsData{ Id = "mat_elven",      Kwda = "WeapMaterialElven".GetFormKey(),                Items = [ "IngotMoonstone".GetFormKey() ],   Perks = [ "ElvenSmithing".GetFormKey() ]                                     },
            new StaticsData{ Id = "mat_falmer",     Kwda = "WeapMaterialFalmer".GetFormKey(),               Items = [ "ChaurusChitin".GetFormKey() ],    Perks = [ "ElvenSmithing".GetFormKey() ]                                     },
            new StaticsData{ Id = "mat_falmerh",    Kwda = "WeapMaterialFalmerHoned".GetFormKey(),          Items = [ "ChaurusChitin".GetFormKey() ],    Perks = [ "ElvenSmithing".GetFormKey() ]                                     },
            new StaticsData{ Id = "mat_forsworn",   Kwda = "WAF_WeapMaterialForsworn".GetFormKey(),         Items = [ "IngotIron".GetFormKey() ],        Perks = [ ]                                                                  },
            new StaticsData{ Id = "mat_glass",      Kwda = "WeapMaterialGlass".GetFormKey(),                Items = [ "IngotMalachite".GetFormKey() ],   Perks = [ "GlassSmithing".GetFormKey() ]                                     },
            new StaticsData{ Id = "mat_golden",     Kwda = "cc_WeapMaterialGolden".GetFormKey(),            Items = [ "IngotMoonstone".GetFormKey() ],   Perks = [ "DaedricSmithing".GetFormKey() ]                                   },
            new StaticsData{ Id = "mat_imperial",   Kwda = "WeapMaterialImperial".GetFormKey(),             Items = [ "IngotSteel".GetFormKey() ],       Perks = [ "SteelSmithing".GetFormKey() ]                                     },
            new StaticsData{ Id = "mat_iron",       Kwda = "WeapMaterialIron".GetFormKey(),                 Items = [ "IngotIron".GetFormKey() ],        Perks = [ ]                                                                  },
            new StaticsData{ Id = "mat_madness",    Kwda = "cc_WeapMaterialMadness".GetFormKey(),           Items = [ "cc_IngotMadness".GetFormKey() ],  Perks = [ "EbonySmithing".GetFormKey() ]                                     },
            new StaticsData{ Id = "mat_nordic",     Kwda = "DLC2WeaponMaterialNordic".GetFormKey(),         Items = [ "IngotQuicksilver".GetFormKey() ], Perks = [ "AdvancedArmors".GetFormKey() ]                                    },
            new StaticsData{ Id = "mat_orcish",     Kwda = "WeapMaterialOrcish".GetFormKey(),               Items = [ "IngotOrichalcum".GetFormKey() ],  Perks = [ "OrcishSmithing".GetFormKey() ]                                    },
            new StaticsData{ Id = "mat_silverr",    Kwda = "skyre__WeapMaterialSilverRefined".GetFormKey(), Items = [ "IngotSilver".GetFormKey() ],      Perks = [ "skyre_SMTDeepSilver".GetFormKey(), "SteelSmithing".GetFormKey() ] },
            new StaticsData{ Id = "mat_silver",     Kwda = "WeapMaterialSilver".GetFormKey(),               Items = [ "IngotSilver".GetFormKey() ],      Perks = [ "skyre_SMTTradecraft".GetFormKey(), "SteelSmithing".GetFormKey() ] },
            new StaticsData{ Id = "mat_stalhrim",   Kwda = "DLC2WeaponMaterialStalhrim".GetFormKey(),       Items = [ "DLC2OreStalhrim".GetFormKey() ],  Perks = [ "GlassSmithing".GetFormKey(), "EbonySmithing".GetFormKey() ]       },
            new StaticsData{ Id = "mat_steel",      Kwda = "WeapMaterialSteel".GetFormKey(),                Items = [ "IngotSteel".GetFormKey() ],       Perks = [ "SteelSmithing".GetFormKey() ]                                     },
            new StaticsData{ Id = "mat_wood",       Kwda = "WeapMaterialWood".GetFormKey(),                 Items = [ "Charcoal".GetFormKey() ],         Perks = [ ]                                                                  }
        ];

        List<StaticsData> crossbowSubtypes = [
            new StaticsData{ Id = "name_recurve",                                                         Desc = "desc_recurve", Perks = [ "skyre_MARCrossbowRecurve".GetFormKey() ] },
            new StaticsData{ Id = "name_lweight",                                                         Desc = "desc_lweight", Perks = [ "skyre_MARCrossbowLight".GetFormKey() ]   },
            new StaticsData{ Id = "name_muffled", Kwda = "skyre_MAREnhancedCrossbowMuffled".GetFormKey(), Desc = "desc_muffled", Perks = [ "skyre_MARCrossbowMuffled".GetFormKey() ] },
            new StaticsData{ Id = "name_siege",   Kwda = "skyre_MAREnhancedCrossbowSiege".GetFormKey(),   Desc = "desc_siege",   Perks = [ "skyre_MARCrossbowSiege".GetFormKey() ]   }
        ];

        List<CrossbowMods> crossbowMods = [
            new CrossbowMods{ Damage = Settings.Weapons.RecurveDamage, Speed = Settings.Weapons.RecurveSpeed, Weight = Settings.Weapons.RecurveWeight, SoundLevel = SoundLevel.Normal },
            new CrossbowMods{ Damage = Settings.Weapons.LightDamage,   Speed = Settings.Weapons.LightSpeed,   Weight = Settings.Weapons.LightWeight,   SoundLevel = SoundLevel.Normal },
            new CrossbowMods{ Damage = Settings.Weapons.MuffledDamage, Speed = Settings.Weapons.MuffledSpeed, Weight = Settings.Weapons.MuffledWeight, SoundLevel = SoundLevel.Silent },
            new CrossbowMods{ Damage = Settings.Weapons.SiegeDamage,   Speed = Settings.Weapons.SiegeSpeed,   Weight = Settings.Weapons.SiegeWeight,   SoundLevel = SoundLevel.Loud   }
        ];

        return (allTypes, skyreTypes, crossbowSubtypes, crossbowMods, allMaterials);
    }
}