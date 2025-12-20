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
    private static readonly (List<DataMap> AllTypes,
                             List<DataMap> SkyReTypes,
                             List<DataMap> CrossbowSubtypes,
                             List<CrossbowMods> CrossbowMods,
                             List<DataMap> AllMaterials) Statics = BuildStaticsMap();

    private static EditorIDs EditorIDs;
    private static PatchingData RecordData;
    private static Logger Logger;

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
            RecordData = new PatchingData
            {
                AnimType = weapon.Data!.AnimationType,
                BoundWeapon = weapon.Data!.Flags.HasFlag(WeaponData.Flag.BoundWeapon),
                NonPlayable = weapon.MajorFlags.HasFlag(Weapon.MajorFlag.NonPlayable) || 
                    weapon.Data!.Flags.HasFlag(WeaponData.Flag.NonPlayable) || 
                    weapon.Data!.Flags.HasFlag(WeaponData.Flag.CantDrop),
                Unique = weapon.Keywords!.Contains(GetFormKey("skyre__NoMeltdownRecipes"))
            };
            Logger = new Logger();

            if (!weapon.Template.IsNull)
            {
                PatchRecordNames(weapon, blacklists[0]);
                weapon.ShowReport();
                continue;
            }

            SetOverriddenData(weapon);
            PatchRecordNames(weapon, blacklists[0]);
            PatchWeaponData(weapon);

            if (!RecordData.NonPlayable && !RecordData.BoundWeapon)
            {
                ProcessCrossbows(weapon, blacklists[1]);
                ProcessSilverWeapons(weapon, blacklists[2]);
                ProcessRecipes(weapon, blacklists[3]);
            }

            Finalizer(weapon.AsOverride());
            ShowReport(weapon);
        }
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

        Console.WriteLine($"\n~~~ {weapWinners.Count()} weapon records found, filtering... ~~~\n");

        List<IWeaponGetter> weapRecords = [];

        List<string> excludedNames = [.. Rules["excludedWeapons"]!.AsArray().Select(value => value!.GetValue<string>())];
        foreach (var record in weapWinners)
        {
            if (IsValid(record, excludedNames)) weapRecords.Add(record);
        }

        Console.WriteLine($"~~~ {weapRecords.Count} weapon records are eligible for patching ~~~\n\n"
            + "====================");
        return weapRecords;
    }

    /// <summary>
    /// Checks if the weapon matches necessary conditions to be patched.
    /// </summary>
    /// <param name="weapon">The weapon record as IWeaponGetter.</param>
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
                ShowReport(weapon);
            }
            return false;
        }

        // invalid if has no name
        if (weapon.Name is null) return false;

        // invalid if found in the excluded records list by name
        if (weapon.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Logger.Info($"Found in the \"No patching\" list by name");
                ShowReport(weapon);
            }
            return false;
        }

        // invalid if is a staff
        if (weapon.Data!.AnimationType == WeaponAnimationType.Staff) return false;

        // valid if has a template (to skip keyword checks below)
        if (!weapon.Template.IsNull) return true;

        // invalid if has no keywords or have empty kw array (rare)
        if (weapon.Keywords is null || weapon.Keywords.Count == 0) return false;

        return true;
    }

    private static void PatchRecordNames(IWeaponGetter weapon, List<string> excludedNames)
    {
        if (weapon.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded) Logger.Info($"Found in the \"No renaming\" list");
            return;
        }

        string name = weapon.Name!.ToString()!;

        /* Options:
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
                    if (!filterArr.Any(type => RecordData.AnimType.ToString() == type.Replace(" ", "")))
                    {
                        continue;
                    }
                }

                if (RecordData.Overridden && !flags.Contains('o')) return;

                if (!flags.Contains('p'))
                {
                    // check if name contains all words from replace in any order
                    string replace = renamer[i]!["replace"]?.ToString() ?? "";
                    string[] blacklist = replace.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (replace != "" && blacklist.All(name.Contains)) continue;

                    // check if name contains any word from skipIf
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
            Logger.Info($"Was renamed to {name}", true);
            weapon.AsOverride(true).Name = name;
        }
    }

    /// <summary>
    /// Modifies weapon materials/types according to the rules.
    /// </summary>
    /// <param name="weapon">The armor record as IWeaponGetter.</param>
    private static void SetOverriddenData(IWeaponGetter weapon)
    {
        // type
        JsonNode? typeOverrideNode =
            Helpers.RuleByName(weapon.Name!.ToString()!, Rules["typeOverrides"]!.AsArray(), data1: "names", data2: "type", true);
        string? typeOverrideString = typeOverrideNode?.AsNullableType<string>();

        DataMap newType = Statics.AllTypes.FirstOrDefault(entry => entry.Id.GetT9n() == typeOverrideString);
        if (newType.Id is not null)
        {
            foreach (var entry in Statics.SkyReTypes)
            {
                if (weapon.Keywords!.Contains(entry.Kwda!) && entry.Kwda != newType.Kwda)
                {
                    weapon.AsOverride().Keywords!.Remove(entry.Kwda);
                }
            }

            Logger.Info($"The subtype is forced to {typeOverrideString} in accordance with patching rules", true);
            string typeTag = (Settings.Weapons.NoTypeTags ? "TYPETAG " : "") + newType!.Id.GetT9n();
            weapon.AsOverride(true).Name = weapon.AsOverride().Name + " [" + typeTag + "]";
            RecordData.Overridden = true;
        }

        // material
        JsonNode? matOverrideNode = Helpers.RuleByName(
            weapon.Name!.ToString()!, Rules["materialOverrides"]!.AsArray(), data1: "names", data2: "material");
        string? matOverrideString = matOverrideNode?.AsNullableType<string>();

        DataMap newMaterial = Statics.AllMaterials.FirstOrDefault(entry => entry.Id.GetT9n() == matOverrideString);
        if (newMaterial.Id is not null)
        {
            if (newMaterial.Kwda == DataMap.NullRef)
            {
                Logger.Caution("A relevant \"materialOverrides\" patching rule references a material from Creation Club's \"Saints and Seducers\"");
                return;
            }

            foreach (var entry in Statics.AllMaterials)
            {
                if (weapon.Keywords!.Contains(entry.Kwda!) && entry.Kwda != newMaterial.Kwda)
                {
                    weapon.AsOverride().Keywords!.Remove(entry.Kwda);
                }
            }

            if (!weapon.Keywords!.Contains(newMaterial.Kwda!)) weapon.AsOverride(true).Keywords!.Add(newMaterial.Kwda!);
            Logger.Info($"The material is forced to {matOverrideString} in accordance with patching rules", true);
            RecordData.Overridden = true;
        }
    }

    private static void PatchWeaponData(IWeaponGetter weapon)
    {
        PatchKeywords(weapon);

        int baseData = GetBaseData();
        (int? typeDamage, float? typeSpeed, float? typeReach, float mult) = GetTypeData(weapon);
        (int materialDamage, float materialSpeed, float critMult) = GetMaterialData(weapon);

        // SpeedMod and CritMult have fallback values
        if (new object?[] { typeDamage, typeSpeed, typeReach }.Any(data => data is null))
        {
            Logger.Error("Weapon data will not be modified.");
            return;
        }

        weapon.AsOverride().Data!.Speed = (float)typeSpeed! + materialSpeed;
        weapon.AsOverride().Data!.Reach = (float)typeReach!;

        ushort newDamage = (ushort)(Math.Floor(baseData + (float)typeDamage! + materialDamage) * mult);
        weapon.AsOverride().BasicStats!.Damage = newDamage;
        weapon.AsOverride().Critical!.Damage = (ushort)(newDamage * critMult);

        if (Settings.Debug.ShowVerboseData)
        {
            // Math.Round to deal with floating point presicion issue
            Logger.Info($"Speed: {weapon.Data!.Speed} -> {Math.Round(weapon.AsOverride().Data!.Speed, 2)} " +
                $"(type: {typeSpeed}, material: {materialSpeed})");
            Logger.Info($"Reach: {weapon.Data!.Reach} -> {(decimal)weapon.AsOverride().Data!.Reach} " +
                $"(type: {typeReach})");
            Logger.Info($"Damage: {weapon.BasicStats!.Damage} -> {weapon.AsOverride().BasicStats!.Damage} " +
                $"(base: {baseData}, type: {typeDamage}, material: {materialDamage}, mult: x{mult})");
            Logger.Info($"Crit: {weapon.Critical!.Damage} -> {weapon.AsOverride().Critical!.Damage} " +
                $"(damage: {weapon.AsOverride().BasicStats!.Damage}, mult: x{critMult})");
        }
    }

    private static int GetBaseData() => RecordData.AnimType switch
    {
        WeaponAnimationType.Bow => Settings.Weapons.BowBase,
        WeaponAnimationType.Crossbow => Settings.Weapons.CrossbowBase,
        WeaponAnimationType.TwoHandAxe => Settings.Weapons.TwoHandedBase,
        WeaponAnimationType.TwoHandSword => Settings.Weapons.TwoHandedBase,
        _ => Settings.Weapons.OneHandedBase,
    };

    private static void PatchKeywords(IWeaponGetter weapon)
    {
        bool isSkyReType = false;
        // SkyRe type keyword
        if (!Statics.SkyReTypes.All(type => weapon.Keywords!.Contains(type.Kwda!)))
        {
            DataMap newType = Statics.SkyReTypes
                .FirstOrDefault(type => type.Id.GetT9n().RegexMatch(weapon.AsOverride().Name!.ToString()!, true));

            if (newType.Id is not null)
            {
                weapon.AsOverride(true).Keywords!.Add(newType.Kwda!);
                isSkyReType = true;
                if (!RecordData.Overridden) Logger.Info($"New subtype is {newType.Id.GetT9n("english")}", true);
            }
        }

        // bound weapon keyword
        if (RecordData.BoundWeapon && !weapon.Keywords!.Contains(GetFormKey("skyre__WeapMaterialBound")))
        {
            weapon.AsOverride(true).Keywords!.Add(GetFormKey("skyre__WeapMaterialBound"));
            Logger.Info("Marked as a bound weapon due to the \"bound weapon\" flag", true);
        }

        // broadsword keyword
        if (weapon.AsOverride().Keywords!.Contains(GetFormKey("WeapTypeSword")) && !isSkyReType)
        {
            weapon.AsOverride(true).Keywords!.Add(GetFormKey("skyre__WeapTypeBroadsword"));
            if (!RecordData.Overridden) Logger.Info($"New subtype is {"type_broadsword".GetT9n("english")}", true);
        }

        // shortbow keyword
        if (weapon.AsOverride().Keywords!.Contains(GetFormKey("WeapTypeBow")) && !isSkyReType)
        {
            weapon.AsOverride(true).Keywords!.Add(GetFormKey("skyre__WeapTypeShortbow"));
            if (!RecordData.Overridden) Logger.Info($"New subtype is {"type_shortbow".GetT9n("english")}", true);
        }
    }

    private static (int?, float?, float?, float) GetTypeData(IWeaponGetter weapon)
    {
        DataMap typeEntry = Statics.AllTypes.FirstOrDefault(type => weapon.AsOverride().Keywords!.Contains(type.Kwda!));

        if (typeEntry.Id is null)
        {
            Logger.Error("Unable to determine the weapon type");
            return (null, null, null, 1f);
        }

        // type damage
        JsonNode? damageNode = 
            Helpers.RuleByName(weapon.AsOverride().Name!.ToString()!, Rules["types"]!.AsArray(), data1: "names", data2: "damage", true) ?? 
            Helpers.RuleByName(typeEntry.Id, Rules["types"]!.AsArray(), data1: "id", data2: "damage", true);
        int? typeDamage = damageNode?.AsType<int>();

        // type speed
        JsonNode? speedNode = 
            Helpers.RuleByName(weapon.AsOverride().Name!.ToString()!, Rules["types"]!.AsArray(), data1: "names", data2: "speed", true) ??
            Helpers.RuleByName(typeEntry.Id, Rules["types"]!.AsArray(), data1: "id", data2: "speed", true);
        float? typeSpeed = speedNode?.AsType<float>();

        // type reach
        JsonNode? reachNode = 
            Helpers.RuleByName(weapon.AsOverride().Name!.ToString()!, Rules["types"]!.AsArray(), data1: "names", data2: "reach", true) ??
            Helpers.RuleByName(typeEntry.Id, Rules["types"]!.AsArray(), data1: "id", data2: "reach", true);
        float? typeReach = reachNode?.AsType<float>();

        // damage mult
        float? modifier = Helpers.RuleByName(weapon.AsOverride().Name!.ToString()!,
            Rules["damageModifiers"]!.AsArray(), data1: "names", data2: "multiplier")?.AsType<float>();

        return (typeDamage, typeSpeed, typeReach, modifier ?? 1f);
    }

    private static (int, float, float) GetMaterialData(IWeaponGetter weapon)
    {
        DataMap materialEntry = Statics.AllMaterials.FirstOrDefault(type => weapon.AsOverride().Keywords!.Contains(type.Kwda!));

        if (materialEntry.Id is null)
        {
            // bound weapons use pseudo-material "bound", that has no material mods
            if (!RecordData.BoundWeapon) Logger.Error("Unable to determine the weapon material");
            return (0, 0, 1f);
        }

        // material damage
        JsonNode? damageNode =
            Helpers.RuleByName(weapon.AsOverride().Name!.ToString()!, Rules["materials"]!.AsArray(), data1: "names", data2: "damage") ??
            Helpers.RuleByName(materialEntry.Id, Rules["materials"]!.AsArray(), data1: "id", data2: "damage");
        int? materialDamage = damageNode?.AsType<int>();

        // material speed mod
        JsonNode ? speedNode = 
            Helpers.RuleByName(weapon.AsOverride().Name!.ToString()!, Rules["materials"]!.AsArray(), data1: "names", data2: "speedMod") ??
            Helpers.RuleByName(materialEntry.Id, Rules["materials"]!.AsArray(), data1: "id", data2: "speedMod");
        float? materialSpeedMod = speedNode?.AsType<float>();

        // material crit damage mult
        JsonNode? critNode = 
            Helpers.RuleByName(weapon.AsOverride().Name!.ToString()!, Rules["materials"]!.AsArray(), data1: "names", data2: "critMult") ??
            Helpers.RuleByName(materialEntry.Id, Rules["materials"]!.AsArray(), data1: "id", data2: "critMult");
        float? materialCritMult = critNode?.AsType<float>();

        return (materialDamage ?? 0, materialSpeedMod ?? 0, materialCritMult ?? 1f);
    }

    private static void ProcessCrossbows(IWeaponGetter weapon, List<string> excludedNames)
    {
        if (RecordData.AnimType != WeaponAnimationType.Crossbow) return;

        // Detection sound level patching
        if (weapon.DetectionSoundLevel != SoundLevel.Normal)
        {
            weapon.AsOverride(true).DetectionSoundLevel = SoundLevel.Normal;
        }

        if ("name_enhanced".GetT9n().RegexMatch(weapon.AsOverride().Name!.ToString()!, true))
        {
            weapon.AsOverride(true).Keywords!.Add(GetFormKey("MagicDisallowEnchanting"));
            return;
        }

        if (weapon.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded) Logger.Info($"Found in the \"No Enhanced variants\" list");
            return;
        }

        if (RecordData.Unique)
        {
            Logger.Info($"No Enhanced variants were generated due to the \"No breakdown\" keyword", true);
            return;
        }

        DataMap? materialEntry = Statics.AllMaterials.FirstOrDefault(type => weapon.AsOverride().Keywords!.Contains(type.Kwda!));
        if (materialEntry is null) return;

        int i = 0;
        foreach (var subtypeA in Statics.CrossbowSubtypes)
        {
            for (int j = i; j < 4; j++)
            {
                DataMap subtypeB = Statics.CrossbowSubtypes[j];
                CrossbowSubtype newSubtype = new(
                    Id: subtypeA.Id.GetT9n(),
                    EdId: subtypeA.Id.GetT9n("english"),
                    Desc: "desc_enhanced".GetT9n() + " " + subtypeA.Desc!.GetT9n(),
                    Kwda: [subtypeA.Kwda!, GetFormKey("DLC1CrossbowIsEnhanced"), GetFormKey("MagicDisallowEnchanting")],
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

                CreateCrossbowVariant(weapon.AsOverride(), (DataMap)materialEntry, newSubtype, i, j);
            }

            i++;
        }

        Logger.Info($"Crossbow variants generated.", true);
    }

    private static void CreateCrossbowVariant(Weapon weapon, DataMap material, CrossbowSubtype subtype, int pIndex, int sIndex)
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
            Object = new FormLink<IPerkGetter>(GetFormKey("DLC1EnchancedCrossbowArmorPiercingPerk"))
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

        newCrossbow.BasicStats!.Damage = (ushort)
            (newCrossbow.BasicStats!.Damage * (Statics.CrossbowMods[pIndex].Damage / 100f) * (isDouble ? (Statics.CrossbowMods[sIndex].Damage / 100f) : 1));
        newCrossbow.BasicStats.Weight *= Statics.CrossbowMods[pIndex].Weight / 100f * (isDouble ? (Statics.CrossbowMods[sIndex].Weight / 100f) : 1);
        newCrossbow.BasicStats.Value =
            (uint)(newCrossbow.BasicStats.Value * (Settings.Weapons.EnhancedCrossbowsPrice / 100f) * (isDouble ? 1.2f : 1f));
        newCrossbow.Data!.Speed *= Statics.CrossbowMods[pIndex].Speed / 100f * (isDouble ? (Statics.CrossbowMods[sIndex].Speed / 100f) : 1);
        newCrossbow.DetectionSoundLevel = Statics.CrossbowMods[sIndex].SoundLevel;

        // source crossbow
        if (subtype.Perk.Count > 1)
        {
            subtype.Perk.Add(GetFormKey("skyre_MARArtificer"));
            weapon = (Weapon)RecordData.ThisRecord!;
        }
        else
        {
            RecordData.ThisRecord = newCrossbow;
        }

        // recipe ingredients
        List<DataMap> ingredients = [
            new DataMap{Items = [ GetFormKey("LeatherStrips") ], Qty = 2, Id = "MISC"},
            new DataMap{Items = [ GetFormKey("SprigganSap") ],   Qty = 1, Id = "INGR"},
            new DataMap{Items = [ material.Items[0] ],            Qty = 1, Id = "MISC"}
        ];

        AddCraftingRecipe(newCrossbow, weapon, subtype.Perk, material, ingredients);
    }

    private static void ProcessSilverWeapons(IWeaponGetter weapon, List<string> excludedNames)
    {
        if (weapon.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded) Logger.Info($"Found in the \"No Refined Silver variants\" list");
            return;
        }

        if (!weapon.Keywords!.Contains(GetFormKey("WeapMaterialSilver")) ||
            weapon.Keywords!.Contains(GetFormKey("skyre__WeapMaterialSilverRefined")) || 
            weapon.Keywords!.Contains(GetFormKey("WeapTypeBow")))
        { 
            return;
        }

        if (RecordData.Unique)
        {
            Logger.Info($"No Refined Silver variants were generated due to the \"No breakdown\" keyword", true);
            return;
        }

        Weapon newWeapon = Executor.State!.PatchMod.Weapons.DuplicateInAsNewRecord(weapon);
        DataMap material = Statics.AllMaterials.First(type => type.Kwda == GetFormKey("skyre__WeapMaterialSilverRefined"));

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
            Object = new FormLink<IPerkGetter>(GetFormKey("SilverPerk"))
        };

        newScript.Properties.Add(newProperty);
        newWeapon.VirtualMachineAdapter.Scripts.Insert(newWeapon.VirtualMachineAdapter.Scripts.Count, newScript);

        // adding keywords
        newWeapon.Keywords!.Add(GetFormKey("skyre__WeapMaterialSilverRefined"));

        // modifying stats
        newWeapon.Data!.Speed *= 1.1f;
        newWeapon.BasicStats!.Weight *= 0.8f;
        newWeapon.BasicStats.Value = (uint)(newWeapon.BasicStats.Value * (Settings.Weapons.RefinedSilverPrice / 100f));

        // shortspears anim type bypass
        if (newWeapon.Keywords!.Contains(GetFormKey("skyre__WeapTypeShortspear")) && Settings.Weapons.AltShortspears)
        {
            if (RecordData.AnimType == WeaponAnimationType.OneHandSword)
            {
                newWeapon.Data!.AnimationType = WeaponAnimationType.OneHandAxe;
            }
        };

        // recipe ingredients
        List<DataMap> ingredients = [
            new DataMap{Items = [ GetFormKey("IngotGold") ], Qty = 1, Id = "MISC"}
        ];

        AddCraftingRecipe(newWeapon, weapon, [], material, ingredients);
    }

    /// <summary>
    /// Generates the crafting recipe for weapon variants (enhanced crossbows and Refined Silver weapons).<br/>
    /// </summary>
    /// <param name="newWeapon">The variant of the oldWeapon as Weapon.</param>
    /// <param name="oldWeapon">The weapon record as Weapon.</param>
    /// <param name="perks">List of variant perk FormKeys.</param>
    /// <param name="material">The weapon material data as DataMap struct.</param>
    /// <param name="ingredients">List of ingredients and their quantity.</param>
    private static void AddCraftingRecipe(IWeaponGetter newWeapon, IWeaponGetter oldWeapon, List<FormKey> perks, DataMap material, List<DataMap> ingredients)
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

            switch (entry.Id)
            {
                case "MISC":
                    newItem.Item = Executor.State!.LinkCache.Resolve<IMiscItemGetter>(entry.Items[0]).ToNullableLink();
                    break;

                case "INGR":
                    newItem.Item = Executor.State!.LinkCache.Resolve<IIngredientGetter>(entry.Items[0]).ToNullableLink();
                    break;
            }

            ContainerEntry newEntry = new();
            newEntry.Item = newItem;
            newEntry.Item.Count = entry.Qty;
            newRecipe.Items.Add(newEntry);
        }

        // retaining original conditions (for crossbows)
        if (Settings.Weapons.KeepConditions && RecordData.AnimType == WeaponAnimationType.Crossbow)
        {
            var craftingRecipe = Executor.AllRecipes!.FirstOrDefault(
                cobj => cobj.CreatedObject.FormKey == oldWeapon.FormKey &&
                (cobj.WorkbenchKeyword.FormKey == GetFormKey("CraftingSmithingForge") || 
                cobj.WorkbenchKeyword.FormKey == GetFormKey("DLC1CraftingDawnguard")));

            if (craftingRecipe is not null && craftingRecipe.Conditions.Count != 0)
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
        if (material.Perks is not null)
        {
            foreach (var perk in material.Perks)
            {
                if (newRecipe.Conditions.Any(cond => cond.Data is HasPerkConditionData hasPerk && perk == hasPerk.Perk.Link.FormKey))
                    continue;

                Condition.Flag flag = material.Perks.IndexOf(perk) == material.Perks.Count - 1 || 
                RecordData.AnimType != WeaponAnimationType.Crossbow ? 0 : Condition.Flag.OR;
                newRecipe.AddHasPerkCondition(perk, flag);
            }
        }

        if (!Settings.Weapons.AllWeaponRecipes)
            newRecipe.AddGetItemCountCondition(oldWeapon.FormKey, CompareOperator.GreaterThanOrEqualTo);

        newRecipe.AddGetEquippedCondition(oldWeapon.FormKey, CompareOperator.NotEqualTo);

        newRecipe.CreatedObject = newWeapon.ToNullableLink();
        if (newRecipe.WorkbenchKeyword.IsNull) newRecipe.WorkbenchKeyword = 
            Executor.State!.LinkCache.Resolve<IKeywordGetter>(GetFormKey("CraftingSmithingForge")).ToNullableLink();
        newRecipe.CreatedObjectCount = 1;

        AddTemperingRecipe(newWeapon, material);
    }

    /// <summary>
    /// Generates a tempering recipe for the weapon.
    /// </summary>
    /// <param name="weapon">The weapon record as IWeaponGetter.</param>
    /// <param name="material">An associated material as DataMap struct.</param>
    private static void AddTemperingRecipe(IWeaponGetter newWeapon, DataMap material)
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
        newRecipe.AddHasPerkCondition(GetFormKey("ArcaneBlacksmith"));

        foreach (var perk in material.Perks)
        {
            Condition.Flag flag = material.Perks.IndexOf(perk) == material.Perks.Count - 1 ? 0 : Condition.Flag.OR;
            newRecipe.AddHasPerkCondition(perk, flag);
        }

        newRecipe.CreatedObject = newWeapon.ToNullableLink();
        newRecipe.WorkbenchKeyword = Executor.State!.LinkCache.Resolve<IKeywordGetter>(GetFormKey("CraftingSmithingSharpeningWheel")).ToNullableLink();
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
        if (RecordData.Modified) weapon = weapon.AsOverride();

        if (weapon.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
                Logger.Info($"Found in the \"No recipe modifications\" list");

            return;
        }

        foreach (var recipe in Executor.AllRecipes!)
        {
            if (recipe.CreatedObject.FormKey == weapon.FormKey)
            {
                if (RecordData.AnimType == WeaponAnimationType.Crossbow && 
                    (recipe.WorkbenchKeyword.FormKey == GetFormKey("CraftingSmithingForge") || 
                    recipe.WorkbenchKeyword.FormKey == GetFormKey("DLC1CraftingDawnguard")))
                    ModCraftingRecipe(recipe, weapon);

                if (recipe.WorkbenchKeyword.FormKey == GetFormKey("CraftingSmithingSharpeningWheel"))
                    ModTemperingRecipe(recipe, weapon);
            }
        }

        AddBreakdownRecipe(weapon);
    }

    private static void ModCraftingRecipe(IConstructibleObjectGetter recipe, IWeaponGetter weapon)
    {
        bool isEnhanced = "name_enhanced".GetT9n().RegexMatch(weapon.Name!.ToString()!, true);
        //Console.WriteLine($"Name is {weapon.Name!.ToString()}, Pattern is {"name_enhanced".GetT9n()}, res is {isEnhanced}");
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
                .Where(entry => entry.Perks is not null)
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

        craftingRecipe.AddHasPerkCondition(GetFormKey(
            isEnhanced ? "skyre_MARCrossbowRecurve" : "skyre_MARBallistics"));

        craftingRecipe.WorkbenchKeyword = Executor.State!.LinkCache.Resolve<IKeywordGetter>(
            Settings.Weapons.KeepConditions ? GetFormKey("DLC1CraftingDawnguard") : GetFormKey("CraftingSmithingForge"))
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
        if (recipe.Conditions.Count != 0 && recipe.Conditions.Any(condition => condition.Data is EPTemperingItemIsEnchantedConditionData))
        {
            List<FormKey> allPerks = [.. Statics.AllMaterials
                .Where(entry => entry.Perks is not null)
                .SelectMany(entry => entry.Perks)
                .Distinct()];
            List<FormKey> materialPerks = [.. Statics.AllMaterials
                .Where(entry => weapon.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
                .Where(entry => entry.Perks is not null)
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

            if (RecordData.Overridden)
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
        if (RecordData.Unique)
        {
            Logger.Info($"The breakdown recipe was not generated due to the \"No breakdown\" keyword", true);
            return;
        }

        IConstructibleObjectGetter? craftingRecipe = null;
        if (!noRecipes)
        {
            foreach (var recipe in Executor.AllRecipes!)
            {
                if (Settings.General.SkipExisting
                    && recipe.Items?.FirstOrDefault()?.Item == weapon
                    && recipe.WorkbenchKeyword.FormKey == GetFormKey("CraftingSmelter"))
                {
                    Logger.Info($"Already has a breakdown recipe in the {recipe.FormKey.ModKey.FileName}");
                    return;
                }

                if (craftingRecipe is null
                    && recipe.CreatedObject.FormKey == weapon.FormKey
                    && recipe.WorkbenchKeyword.FormKey == GetFormKey("CraftingSmithingForge"))
                {
                    craftingRecipe = recipe;
                }
            }
        }

        bool isBig = RecordData.AnimType == WeaponAnimationType.TwoHandAxe || 
            RecordData.AnimType == WeaponAnimationType.TwoHandSword || 
            weapon.Keywords!.Contains(GetFormKey("skyre__WeapTypeLongbow"));

        List<FormKey> weaponPerks = [.. Statics.AllMaterials
            .Where(entry => weapon.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
            .Where(entry => entry.Perks is not null)
            .SelectMany(entry => entry.Perks)
            .Distinct()];
        List<FormKey> weaponItems = [.. Statics.AllMaterials
            .Where(entry => weapon.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
            .Select(entry => entry.Items[0])
            .Distinct()];

        bool isWeak = weaponPerks.Count == 0 || weapon.Keywords!.Contains(GetFormKey("WeapMaterialDraugrHoned")) ||
            weapon.Keywords!.Contains(GetFormKey("WAF_WeapMaterialForsworn"));

        bool extraOutput = weapon.Keywords!.Contains(GetFormKey("WeapMaterialWood"));

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

        cobj.AddHasPerkCondition(GetFormKey("skyre_SMTBreakdown"));
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
        cobj.WorkbenchKeyword = Executor.State!.LinkCache.Resolve<IKeywordGetter>(GetFormKey("CraftingSmelter")).ToNullableLink();
        cobj.CreatedObjectCount = (ushort)Math.Clamp(Math.Floor(outputQty), 1, qty + (fromRecipe ? 0 : mod));
    }

    private static void Finalizer(Weapon weapon)
    {
        // shortspears animation type 
        if (weapon.Keywords!.Contains(GetFormKey("skyre__WeapTypeShortspear")) && Settings.Weapons.AltShortspears)
        {
            if (RecordData.AnimType == WeaponAnimationType.OneHandSword)
            {
                weapon.AsOverride(true).Data!.AnimationType = WeaponAnimationType.OneHandAxe;
            }
        };

        // add/remove silver weapon script
        if (!weapon.Keywords!.Contains(GetFormKey("WeapTypeBow")))
        {
            bool hasVMAD = weapon.VirtualMachineAdapter is not null;
            if (weapon.Keywords!.Contains(GetFormKey("WeapMaterialSilver")))
            {
                if (!hasVMAD) weapon.VirtualMachineAdapter = new VirtualMachineAdapter();
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
                        Object = new FormLink<IPerkGetter>(GetFormKey("SilverPerk"))
                    };

                    newScript.Properties.Add(newProperty);
                    weapon.AsOverride(true).VirtualMachineAdapter!.Scripts.Insert(weapon.VirtualMachineAdapter.Scripts.Count, newScript);
                }
            }
            else if (hasVMAD && weapon.VirtualMachineAdapter!.Scripts.Any(script => script.Name == "SilverSwordScript"))
            {
                weapon.AsOverride(true).VirtualMachineAdapter!.Scripts.RemoveAll(script => script.Name == "SilverSwordScript");
            }
        };

        // removing type tags
        if (Settings.Weapons.NoTypeTags && weapon.Name!.ToString().Contains("TYPETAG"))
        {
            string pattern = @"\s\[TYPETAG\s.*\]";
            weapon.AsOverride(true).Name = Regex.Replace(weapon.Name!.ToString(), pattern, "");
        }

        // removing ITPOs
        if (!RecordData.Modified) Executor.State!.PatchMod.Weapons.Remove(weapon);
    }

    // patcher specific helpers

    /// <summary>
    /// Returns the FormKey with id from the statics record.<br/>
    /// </summary>
    /// <param name="id">The id in the elements with the FormKey to return.</param>
    /// <returns>A FormKey from the statics list.</returns>
    private static FormKey GetFormKey(string stringId) => Executor.Statics!.First(elem => elem.Id == stringId).FormKey;

    /// <summary>
    /// Returns the winning override for this-parameter, and copies it to the patch file.<br/>
    /// </summary>
    /// <param name="weapon">The weapon record as IWeaponGetter.</param>
    /// <param name="markModified">True to mark as modified in the patching data.</param>
    /// <returns>The winning override as <see cref="Weapon"/>.</returns>
    private static Weapon AsOverride(this IWeaponGetter weapon, bool markModified = false)
    {
        if (markModified) RecordData.Modified = true;
        return Executor.State!.PatchMod.Weapons.GetOrAddAsOverride(weapon);
    }

    /// <summary>
    /// Displays info and errors.<br/>
    /// </summary>
    /// <param name="weapon">The weapon record as IWeaponGetter.</param>
    private static void ShowReport(this IWeaponGetter weapon) => 
        Logger.ShowReport($"{weapon.Name}", $"{weapon.FormKey}", $"{weapon.EditorID}", RecordData.NonPlayable, !weapon.Template.IsNull);

    // patcher specific statics
    private static (List<DataMap>, List<DataMap>, List<DataMap>, List<CrossbowMods>, List<DataMap>) BuildStaticsMap()
    {
        Executor.Statics!.AddRange(
        [
            new DataMap{Id = "WeapTypeSword",                          FormKey = Helpers.ParseFormKey("Skyrim.esm|0x01e711|KWDA")                  },
            new DataMap{Id = "WeapTypeWaraxe",                         FormKey = Helpers.ParseFormKey("Skyrim.esm|0x01e712|KWDA")                  },
            new DataMap{Id = "WeapTypeDagger",                         FormKey = Helpers.ParseFormKey("Skyrim.esm|0x01e713|KWDA")                  },
            new DataMap{Id = "WeapTypeMace",                           FormKey = Helpers.ParseFormKey("Skyrim.esm|0x01e714|KWDA")                  },
            new DataMap{Id = "WeapTypeBow",                            FormKey = Helpers.ParseFormKey("Skyrim.esm|0x01e715|KWDA")                  },
            new DataMap{Id = "WeapTypeWarhammer",                      FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06d930|KWDA")                  },
            new DataMap{Id = "WeapTypeGreatsword",                     FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06d931|KWDA")                  },
            new DataMap{Id = "WeapTypeBattleaxe",                      FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06d932|KWDA")                  },
            new DataMap{Id = "CraftingSmithingSharpeningWheel",        FormKey = Helpers.ParseFormKey("Skyrim.esm|0x088108|KWDA")                  },
            new DataMap{Id = "MagicDisallowEnchanting",                FormKey = Helpers.ParseFormKey("Skyrim.esm|0x0c27bd|KWDA")                  },
            new DataMap{Id = "DLC1CrossbowIsEnhanced",                 FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x00399c|KWDA")               },
            new DataMap{Id = "DLC1CraftingDawnguard",                  FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x00f806|KWDA")               },
            new DataMap{Id = "skyre__WeapTypeBastardSword",            FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000813|KWDA") },
            new DataMap{Id = "skyre__WeapTypeQuarterstaff",            FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000814|KWDA") },
            new DataMap{Id = "skyre__WeapMaterialBound",               FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000815|KWDA") },
            new DataMap{Id = "skyre__WeapTypeLongbow",                 FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000816|KWDA") },
            new DataMap{Id = "skyre__WeapTypeShortbow",                FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000817|KWDA") },
            new DataMap{Id = "skyre__WeapTypeBroadsword",              FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000818|KWDA") },
            new DataMap{Id = "skyre__WeapTypeClub",                    FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000819|KWDA") },
            new DataMap{Id = "skyre__WeapTypeCrossbow",                FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081a|KWDA") },
            new DataMap{Id = "skyre__WeapTypeGlaive",                  FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081b|KWDA") },
            new DataMap{Id = "skyre__WeapTypeHalberd",                 FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081c|KWDA") },
            new DataMap{Id = "skyre__WeapTypeHatchet",                 FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081d|KWDA") },
            new DataMap{Id = "skyre__WeapTypeKatana",                  FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081e|KWDA") },
            new DataMap{Id = "skyre__WeapTypeLongmace",                FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081f|KWDA") },
            new DataMap{Id = "skyre__WeapTypeLongspear",               FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000820|KWDA") },
            new DataMap{Id = "skyre__WeapTypeLongsword",               FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000821|KWDA") },
            new DataMap{Id = "skyre__WeapTypeMaul",                    FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000822|KWDA") },
            new DataMap{Id = "skyre__WeapTypeNodachi",                 FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000823|KWDA") },
            new DataMap{Id = "skyre__WeapTypeScimitar",                FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000824|KWDA") },
            new DataMap{Id = "skyre__WeapTypeShortspear",              FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000825|KWDA") },
            new DataMap{Id = "skyre__WeapTypeShortsword",              FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000826|KWDA") },
            new DataMap{Id = "skyre__WeapTypeTanto",                   FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000827|KWDA") },
            new DataMap{Id = "skyre__WeapTypeWakizashi",               FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000828|KWDA") },
            new DataMap{Id = "skyre__WeapMaterialSilverRefined",       FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000829|KWDA") },
            new DataMap{Id = "skyre_MAREnhancedCrossbowMuffled",       FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d97|KWDA") },
            new DataMap{Id = "skyre_MAREnhancedCrossbowSiege",         FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d98|KWDA") },
            new DataMap{Id = "SilverPerk",                             FormKey = Helpers.ParseFormKey("Skyrim.esm|0x10d685|PERK")                  },
            new DataMap{Id = "DLC1EnchancedCrossbowArmorPiercingPerk", FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x00399b|PERK")               },
            new DataMap{Id = "skyre_MARCrossbowLight",                 FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d93|PERK") },
            new DataMap{Id = "skyre_MARCrossbowRecurve",               FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d94|PERK") },
            new DataMap{Id = "skyre_MARCrossbowMuffled",               FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d95|PERK") },
            new DataMap{Id = "skyre_MARCrossbowSiege",                 FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d96|PERK") },
            new DataMap{Id = "skyre_MARArtificer",                     FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000dbf|PERK") },
            new DataMap{Id = "skyre_SMTTradecraft",                    FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000ee0|PERK") },
            new DataMap{Id = "skyre_SMTDeepSilver",                    FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000ee4|PERK") }
        ]);

        List<DataMap> allTypes = [
            new DataMap{Id = "type_battleaxe",    Kwda = GetFormKey("WeapTypeBattleaxe")  },
            new DataMap{Id = "type_bow",          Kwda = GetFormKey("WeapTypeBow")        },
            new DataMap{Id = "type_broadsword",   Kwda = GetFormKey("WeapTypeSword")      },
            new DataMap{Id = "type_dagger",       Kwda = GetFormKey("WeapTypeDagger")     },
            new DataMap{Id = "type_greatsword",   Kwda = GetFormKey("WeapTypeGreatsword") },
            new DataMap{Id = "type_mace",         Kwda = GetFormKey("WeapTypeMace")       },
            new DataMap{Id = "type_waraxe",       Kwda = GetFormKey("WeapTypeWaraxe")     },
            new DataMap{Id = "type_warhammer",    Kwda = GetFormKey("WeapTypeWarhammer")  }
        ];

        List<DataMap> skyreTypes = [
            new DataMap{Id = "type_bastard",      Kwda = GetFormKey("skyre__WeapTypeBastardSword") },
            new DataMap{Id = "type_broadsword",   Kwda = GetFormKey("skyre__WeapTypeBroadsword")   },
            new DataMap{Id = "type_club",         Kwda = GetFormKey("skyre__WeapTypeClub")         },
            new DataMap{Id = "type_crossbow",     Kwda = GetFormKey("skyre__WeapTypeCrossbow")     },
            new DataMap{Id = "type_glaive",       Kwda = GetFormKey("skyre__WeapTypeGlaive")       },
            new DataMap{Id = "type_halberd",      Kwda = GetFormKey("skyre__WeapTypeHalberd")      },
            new DataMap{Id = "type_hatchet",      Kwda = GetFormKey("skyre__WeapTypeHatchet")      },
            new DataMap{Id = "type_katana",       Kwda = GetFormKey("skyre__WeapTypeKatana")       },
            new DataMap{Id = "type_longbow",      Kwda = GetFormKey("skyre__WeapTypeLongbow")      },
            new DataMap{Id = "type_longmace",     Kwda = GetFormKey("skyre__WeapTypeLongmace")     },
            new DataMap{Id = "type_longspear",    Kwda = GetFormKey("skyre__WeapTypeLongspear")    },
            new DataMap{Id = "type_longsword",    Kwda = GetFormKey("skyre__WeapTypeLongsword")    },
            new DataMap{Id = "type_maul",         Kwda = GetFormKey("skyre__WeapTypeMaul")         },
            new DataMap{Id = "type_nodachi",      Kwda = GetFormKey("skyre__WeapTypeNodachi")      },
            new DataMap{Id = "type_quarterstaff", Kwda = GetFormKey("skyre__WeapTypeQuarterstaff") },
            new DataMap{Id = "type_scimitar",     Kwda = GetFormKey("skyre__WeapTypeScimitar")     },
            new DataMap{Id = "type_shortbow",     Kwda = GetFormKey("skyre__WeapTypeShortbow")     },
            new DataMap{Id = "type_shortspear",   Kwda = GetFormKey("skyre__WeapTypeShortspear")   },
            new DataMap{Id = "type_shortsword",   Kwda = GetFormKey("skyre__WeapTypeShortsword")   },
            new DataMap{Id = "type_tanto",        Kwda = GetFormKey("skyre__WeapTypeTanto")        },
            new DataMap{Id = "type_wakizashi",    Kwda = GetFormKey("skyre__WeapTypeWakizashi")    }
        ];

        allTypes.InsertRange(0, skyreTypes);

        List<DataMap> allMaterials = [
            new DataMap{Id = "mat_amber",      Kwda = GetFormKey("cc_WeapMaterialAmber"),             Items = [ GetFormKey("cc_IngotAmber") ],    Perks = [ GetFormKey("GlassSmithing"), GetFormKey("EbonySmithing") ]       },
            new DataMap{Id = "mat_blades",     Kwda = GetFormKey("WAF_WeapMaterialBlades"),           Items = [ GetFormKey("IngotSteel") ],       Perks = [ GetFormKey("SteelSmithing")]                                     },
            new DataMap{Id = "mat_daedric",    Kwda = GetFormKey("WeapMaterialDaedric"),              Items = [ GetFormKey("IngotEbony") ],       Perks = [ GetFormKey("DaedricSmithing")]                                   },
            new DataMap{Id = "mat_dawnguard",  Kwda = GetFormKey("WAF_DLC1WeapMaterialDawnguard"),    Items = [ GetFormKey("IngotSteel") ],       Perks = [ GetFormKey("SteelSmithing")]                                     },
            new DataMap{Id = "mat_dark",       Kwda = GetFormKey("cc_WeapMaterialDark"),              Items = [ GetFormKey("IngotQuicksilver") ], Perks = [ GetFormKey("DaedricSmithing")]                                   },
            new DataMap{Id = "mat_dragonbone", Kwda = GetFormKey("DLC1WeapMaterialDragonbone"),       Items = [ GetFormKey("DragonBone") ],       Perks = [ GetFormKey("DragonArmor")]                                       },
            new DataMap{Id = "mat_draugr",     Kwda = GetFormKey("WeapMaterialDraugr"),               Items = [ GetFormKey("IngotQuicksilver") ], Perks = [ GetFormKey("AdvancedArmors")]                                    },
            new DataMap{Id = "mat_draugrh",    Kwda = GetFormKey("WeapMaterialDraugrHoned"),          Items = [ GetFormKey("IngotQuicksilver") ], Perks = [ GetFormKey("AdvancedArmors") ]                                   },
            new DataMap{Id = "mat_dwarven",    Kwda = GetFormKey("WeapMaterialDwarven"),              Items = [ GetFormKey("IngotDwarven") ],     Perks = [ GetFormKey("DwarvenSmithing") ]                                  },
            new DataMap{Id = "mat_ebony",      Kwda = GetFormKey("WeapMaterialEbony"),                Items = [ GetFormKey("IngotEbony") ],       Perks = [ GetFormKey("EbonySmithing") ]                                    },
            new DataMap{Id = "mat_elven",      Kwda = GetFormKey("WeapMaterialElven"),                Items = [ GetFormKey("IngotMoonstone") ],   Perks = [ GetFormKey("ElvenSmithing") ]                                    },
            new DataMap{Id = "mat_falmer",     Kwda = GetFormKey("WeapMaterialFalmer"),               Items = [ GetFormKey("ChaurusChitin") ],    Perks = [ GetFormKey("ElvenSmithing") ]                                    },
            new DataMap{Id = "mat_falmerh",    Kwda = GetFormKey("WeapMaterialFalmerHoned"),          Items = [ GetFormKey("ChaurusChitin") ],    Perks = [ GetFormKey("ElvenSmithing") ]                                    },
            new DataMap{Id = "mat_forsworn",   Kwda = GetFormKey("WAF_WeapMaterialForsworn"),         Items = [ GetFormKey("IngotIron") ]                                                                                   },
            new DataMap{Id = "mat_glass",      Kwda = GetFormKey("WeapMaterialGlass"),                Items = [ GetFormKey("IngotMalachite") ],   Perks = [ GetFormKey("GlassSmithing") ]                                    },
            new DataMap{Id = "mat_golden",     Kwda = GetFormKey("cc_WeapMaterialGolden"),            Items = [ GetFormKey("IngotMoonstone") ],   Perks = [ GetFormKey("DaedricSmithing") ]                                  },
            new DataMap{Id = "mat_imperial",   Kwda = GetFormKey("WeapMaterialImperial"),             Items = [ GetFormKey("IngotSteel") ],       Perks = [ GetFormKey("SteelSmithing") ]                                    },
            new DataMap{Id = "mat_iron",       Kwda = GetFormKey("WeapMaterialIron"),                 Items = [ GetFormKey("IngotIron") ]                                                                                   },
            new DataMap{Id = "mat_madness",    Kwda = GetFormKey("cc_WeapMaterialMadness"),           Items = [ GetFormKey("cc_IngotMadness") ],  Perks = [ GetFormKey("EbonySmithing") ]                                    },
            new DataMap{Id = "mat_nordic",     Kwda = GetFormKey("DLC2WeaponMaterialNordic"),         Items = [ GetFormKey("IngotQuicksilver") ], Perks = [ GetFormKey("AdvancedArmors") ]                                   },
            new DataMap{Id = "mat_orcish",     Kwda = GetFormKey("WeapMaterialOrcish"),               Items = [ GetFormKey("IngotOrichalcum") ],  Perks = [ GetFormKey("OrcishSmithing") ]                                   },
            new DataMap{Id = "mat_silverr",    Kwda = GetFormKey("skyre__WeapMaterialSilverRefined"), Items = [ GetFormKey("IngotSilver") ],      Perks = [ GetFormKey("skyre_SMTDeepSilver"), GetFormKey("SteelSmithing") ] },
            new DataMap{Id = "mat_silver",     Kwda = GetFormKey("WeapMaterialSilver"),               Items = [ GetFormKey("IngotSilver") ],      Perks = [ GetFormKey("skyre_SMTTradecraft"), GetFormKey("SteelSmithing") ] },
            new DataMap{Id = "mat_stalhrim",   Kwda = GetFormKey("DLC2WeaponMaterialStalhrim"),       Items = [ GetFormKey("DLC2OreStalhrim") ],  Perks = [ GetFormKey("GlassSmithing"), GetFormKey("EbonySmithing") ]       },
            new DataMap{Id = "mat_steel",      Kwda = GetFormKey("WeapMaterialSteel"),                Items = [ GetFormKey("IngotSteel") ],       Perks = [ GetFormKey("SteelSmithing") ]                                    },
            new DataMap{Id = "mat_wood",       Kwda = GetFormKey("WeapMaterialWood"),                 Items = [ GetFormKey("Charcoal") ]                                                                                    }
        ];

        List<DataMap> crossbowSubtypes = [
            new DataMap{Id = "name_recurve",                                                        Desc = "desc_recurve", Perks = [ GetFormKey("skyre_MARCrossbowRecurve") ]},
            new DataMap{Id = "name_lweight",                                                        Desc = "desc_lweight", Perks = [ GetFormKey("skyre_MARCrossbowLight") ]  },
            new DataMap{Id = "name_muffled", Kwda = GetFormKey("skyre_MAREnhancedCrossbowMuffled"), Desc = "desc_muffled", Perks = [ GetFormKey("skyre_MARCrossbowMuffled") ]},
            new DataMap{Id = "name_siege",   Kwda = GetFormKey("skyre_MAREnhancedCrossbowSiege"),   Desc = "desc_siege",   Perks = [ GetFormKey("skyre_MARCrossbowSiege") ]  }
        ];

        List<CrossbowMods> crossbowMods = [
            new CrossbowMods{Damage = Settings.Weapons.RecurveDamage, Speed = Settings.Weapons.RecurveSpeed, Weight = Settings.Weapons.RecurveWeight, SoundLevel = SoundLevel.Normal },
            new CrossbowMods{Damage = Settings.Weapons.LightDamage,   Speed = Settings.Weapons.LightSpeed,   Weight = Settings.Weapons.LightWeight,   SoundLevel = SoundLevel.Normal },
            new CrossbowMods{Damage = Settings.Weapons.MuffledDamage, Speed = Settings.Weapons.MuffledSpeed, Weight = Settings.Weapons.MuffledWeight, SoundLevel = SoundLevel.Silent },
            new CrossbowMods{Damage = Settings.Weapons.SiegeDamage,   Speed = Settings.Weapons.SiegeSpeed,   Weight = Settings.Weapons.SiegeWeight,   SoundLevel = SoundLevel.Loud   }
        ];

        return (allTypes, skyreTypes, crossbowSubtypes, crossbowMods, allMaterials);
    }
}