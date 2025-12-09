using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using ReProccer.Utils;
using System.Text.Json.Nodes;

namespace ReProccer.Patchers;

public static class WeaponsPatcher
{
    private static readonly Settings.AllSettings Settings = Executor.Settings!;
    private static readonly JsonObject Rules = Executor.Rules!["weapons"]!.AsObject();
    private static readonly (List<DataMap> AllTypes,
                             List<DataMap> SkyReTypes,
                             List<DataMap> CrossbowSubtypes,
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
            [.. Rules["excludedFromRecipes"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedSilver"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedCrossbows"]!.AsArray().Select(value => value!.GetValue<string>())]
        ];

        foreach (var weapon in records)
        {
            RecordData = new PatchingData
            {
                AnimType = weapon.Data!.AnimationType,
                BoundWeapon = weapon.Data!.Flags.HasFlag(WeaponData.Flag.BoundWeapon),
                NonPlayable = weapon.MajorFlags.HasFlag(Weapon.MajorFlag.NonPlayable),
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

        Console.WriteLine($"~~~ {weapWinners.Count()} weapon records found, filtering... ~~~\n\n"
            + "====================");

        List<IWeaponGetter> weapRecords = [];

        List<string> excludedNames = [.. Rules["excludedWeapons"]!.AsArray().Select(value => value!.GetValue<string>())];
        foreach (var record in weapWinners)
        {
            if (IsValid(record, excludedNames)) weapRecords.Add(record);
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
                ShowReport(weapon);
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
                ShowReport(weapon);
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

        DataMap? newType = Statics.AllTypes.FirstOrDefault(entry => entry.Id.GetT9n() == typeOverrideString);
        if (newType != null)
        {
            foreach (var entry in Statics.SkyReTypes)
            {
                if (weapon.Keywords!.Contains((FormKey)entry.Kwda!) && entry.Kwda != newType.Kwda)
                {
                    weapon.AsOverride().Keywords!.Remove((FormKey)entry.Kwda);
                }
            }

            Logger.Info($"The subtype is forced to {typeOverrideString} in accordance with patching rules", true);
            string typeTag = (Settings.Weapons.NoTypeTags ? "TYPE " : "") + newType!.Id.GetT9n();
            weapon.AsOverride(true).Name = weapon.AsOverride().Name + " [" + typeTag + "]";
            RecordData.Overridden = true;
        }

        // material
        JsonNode? matOverrideNode = Helpers.RuleByName(
            weapon.Name!.ToString()!, Rules["materialOverrides"]!.AsArray(), data1: "names", data2: "material");
        string? matOverrideString = matOverrideNode?.AsNullableType<string>();

        DataMap? newMaterial = Statics.AllMaterials.FirstOrDefault(entry => entry.Id.GetT9n() == matOverrideString);
        if (newMaterial != null)
        {
            FormKey nullRef = new("Skyrim.esm", 0x000000);
            if (newMaterial.Kwda == nullRef)
            {
                Logger.Caution("A relevant \"materialOverrides\" patching rule references a material from Creation Club's \"Saints and Seducers\"");
                return;
            }

            foreach (var entry in Statics.AllMaterials)
            {
                if (weapon.Keywords!.Contains((FormKey)entry.Kwda!) && entry.Kwda != newMaterial.Kwda)
                {
                    weapon.AsOverride().Keywords!.Remove((FormKey)entry.Kwda);
                }
            }

            if (!weapon.Keywords!.Contains((FormKey)newMaterial.Kwda!)) weapon.AsOverride(true).Keywords!.Add((FormKey)newMaterial.Kwda!);
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
        if (!Statics.SkyReTypes.All(type => weapon.Keywords!.Contains((FormKey)type.Kwda!)))
        {
            DataMap? newType = Statics.SkyReTypes
                .FirstOrDefault(type => type.Id.GetT9n().RegexMatch(weapon.AsOverride().Name!.ToString()!, true));

            if (newType is not null)
            {
                weapon.AsOverride(true).Keywords!.Add((FormKey)newType.Kwda!);
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
        DataMap? typeEntry = Statics.AllTypes.FirstOrDefault(type => weapon.AsOverride().Keywords!.Contains((FormKey)type.Kwda!));

        if (typeEntry is null)
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
        DataMap? materialEntry = Statics.AllMaterials.FirstOrDefault(type => weapon.AsOverride().Keywords!.Contains((FormKey)type.Kwda!));

        if (materialEntry is null)
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

    // patcher specific helpers

        /// <summary>
        /// Returns the FormKey with id from the statics record.<br/>
        /// </summary>
        /// <param name="id">The id in the elements with the FormKey to return.</param>
        /// <returns>A FormKey from the statics list.</returns>
    private static FormKey GetFormKey(string id) => Executor.Statics!.First(elem => elem.Id == id).Formkey;

    /// <summary>
    /// Returns the winning override for this-parameter, and copies it to the patch file.<br/>
    /// </summary>
    /// <param name="weapon">The weapon record as IWeaponGetter.</param>
    /// <param name="markModified">True to mark as modified in the local record data.</param>
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
    /// <param name="msgList">The list of list of strings with messages.</param>
    private static void ShowReport(this IWeaponGetter weapon) => Logger.ShowReport($"{weapon.Name}", $"{weapon.FormKey}", $"{weapon.EditorID}", RecordData.NonPlayable, !weapon.Template.IsNull);

    // patcher specific statics
    private static (List<DataMap>, List<DataMap>, List<DataMap>, List<DataMap>) BuildStaticsMap()
    {
        Executor.Statics!.AddRange(
        [
            new(Id: "WeapTypeSword",                          Formkey: Helpers.ParseFormKey("Skyrim.esm|0x01e711|KWDA")                  ),
            new(Id: "WeapTypeWaraxe",                         Formkey: Helpers.ParseFormKey("Skyrim.esm|0x01e712|KWDA")                  ),
            new(Id: "WeapTypeDagger",                         Formkey: Helpers.ParseFormKey("Skyrim.esm|0x01e713|KWDA")                  ),
            new(Id: "WeapTypeMace",                           Formkey: Helpers.ParseFormKey("Skyrim.esm|0x01e714|KWDA")                  ),
            new(Id: "WeapTypeBow",                            Formkey: Helpers.ParseFormKey("Skyrim.esm|0x01e715|KWDA")                  ),
            new(Id: "WeapTypeWarhammer",                      Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06d930|KWDA")                  ),
            new(Id: "WeapTypeGreatsword",                     Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06d931|KWDA")                  ),
            new(Id: "WeapTypeBattleaxe",                      Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06d932|KWDA")                  ),
            new(Id: "skyre__WeapTypeBastardSword",            Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000813|KWDA") ),
            new(Id: "skyre__WeapTypeQuarterstaff",            Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000814|KWDA") ),
            new(Id: "skyre__WeapMaterialBound",               Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000815|KWDA") ),
            new(Id: "skyre__WeapTypeShortbow",                Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000817|KWDA") ),
            new(Id: "skyre__WeapTypeBroadsword",              Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000818|KWDA") ),
            new(Id: "skyre__WeapTypeClub",                    Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000819|KWDA") ),
            new(Id: "skyre__WeapTypeCrossbow",                Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081a|KWDA") ),
            new(Id: "skyre__WeapTypeGlaive",                  Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081b|KWDA") ),
            new(Id: "skyre__WeapTypeHalberd",                 Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081c|KWDA") ),
            new(Id: "skyre__WeapTypeHatchet",                 Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081d|KWDA") ),
            new(Id: "skyre__WeapTypeKatana",                  Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081e|KWDA") ),
            new(Id: "skyre__WeapTypeLongbow",                 Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000816|KWDA") ),
            new(Id: "skyre__WeapTypeLongmace",                Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00081f|KWDA") ),
            new(Id: "skyre__WeapTypeLongspear",               Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000820|KWDA") ),
            new(Id: "skyre__WeapTypeLongsword",               Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000821|KWDA") ),
            new(Id: "skyre__WeapTypeMaul",                    Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000822|KWDA") ),
            new(Id: "skyre__WeapTypeNodachi",                 Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000823|KWDA") ),
            new(Id: "skyre__WeapTypeScimitar",                Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000824|KWDA") ),
            new(Id: "skyre__WeapTypeShortspear",              Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000825|KWDA") ),
            new(Id: "skyre__WeapTypeShortsword",              Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000826|KWDA") ),
            new(Id: "skyre__WeapTypeTanto",                   Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000827|KWDA") ),
            new(Id: "skyre__WeapTypeWakizashi",               Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000828|KWDA") ),
            new(Id: "skyre__WeapMaterialSilverRefined",       Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000829|KWDA") ),
            new(Id: "skyre_MAREnhancedCrossbowMuffled",       Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d97|KWDA") ),
            new(Id: "skyre_MAREnhancedCrossbowSiege",         Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d98|KWDA") ),
            new(Id: "SilverPerk",                             Formkey: Helpers.ParseFormKey("Skyrim.esm|0x10d685|PERK")                  ),
            new(Id: "skyre_MARCrossbowLight",                 Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d93|PERK") ),
            new(Id: "skyre_MARCrossbowRecurve",               Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d94|PERK") ),
            new(Id: "skyre_MARCrossbowMuffled",               Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d95|PERK") ),
            new(Id: "skyre_MARCrossbowSiege",                 Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d96|PERK") ),
            new(Id: "skyre_MARArtificer",                     Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000dbf|PERK") ),
            new(Id: "skyre_SMTTradecraft",                    Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000ee0|PERK") ),
            new(Id: "skyre_SMTDeepSilver",                    Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000ee4|PERK") )
        ]);

        List<DataMap> allTypes = [
            new(Id: "type_battleaxe",    Kwda: GetFormKey("WeapTypeBattleaxe")  ),
            new(Id: "type_bow",          Kwda: GetFormKey("WeapTypeBow")        ),
            new(Id: "type_broadsword",   Kwda: GetFormKey("WeapTypeSword")      ),
            new(Id: "type_dagger",       Kwda: GetFormKey("WeapTypeDagger")     ),
            new(Id: "type_greatsword",   Kwda: GetFormKey("WeapTypeGreatsword") ),
            new(Id: "type_mace",         Kwda: GetFormKey("WeapTypeMace")       ),
            new(Id: "type_waraxe",       Kwda: GetFormKey("WeapTypeWaraxe")     ),
            new(Id: "type_warhammer",    Kwda: GetFormKey("WeapTypeWarhammer")  )
        ];

        List<DataMap> skyreTypes = [
            new(Id: "type_bastard",      Kwda: GetFormKey("skyre__WeapTypeBastardSword") ),
            new(Id: "type_broadsword",   Kwda: GetFormKey("skyre__WeapTypeBroadsword")   ),
            new(Id: "type_club",         Kwda: GetFormKey("skyre__WeapTypeClub")         ),
            new(Id: "type_crossbow",     Kwda: GetFormKey("skyre__WeapTypeCrossbow")     ),
            new(Id: "type_glaive",       Kwda: GetFormKey("skyre__WeapTypeGlaive")       ),
            new(Id: "type_halberd",      Kwda: GetFormKey("skyre__WeapTypeHalberd")      ),
            new(Id: "type_hatchet",      Kwda: GetFormKey("skyre__WeapTypeHatchet")      ),
            new(Id: "type_katana",       Kwda: GetFormKey("skyre__WeapTypeKatana")       ),
            new(Id: "type_longbow",      Kwda: GetFormKey("skyre__WeapTypeLongbow")      ),
            new(Id: "type_longmace",     Kwda: GetFormKey("skyre__WeapTypeLongmace")     ),
            new(Id: "type_longspear",    Kwda: GetFormKey("skyre__WeapTypeLongspear")    ),
            new(Id: "type_longsword",    Kwda: GetFormKey("skyre__WeapTypeLongsword")    ),
            new(Id: "type_maul",         Kwda: GetFormKey("skyre__WeapTypeMaul")         ),
            new(Id: "type_nodachi",      Kwda: GetFormKey("skyre__WeapTypeNodachi")      ),
            new(Id: "type_quarterstaff", Kwda: GetFormKey("skyre__WeapTypeQuarterstaff") ),
            new(Id: "type_scimitar",     Kwda: GetFormKey("skyre__WeapTypeScimitar")     ),
            new(Id: "type_shortbow",     Kwda: GetFormKey("skyre__WeapTypeShortbow")     ),
            new(Id: "type_shortspear",   Kwda: GetFormKey("skyre__WeapTypeShortspear")   ),
            new(Id: "type_shortsword",   Kwda: GetFormKey("skyre__WeapTypeShortsword")   ),
            new(Id: "type_tanto",        Kwda: GetFormKey("skyre__WeapTypeTanto")        ),
            new(Id: "type_wakizashi",    Kwda: GetFormKey("skyre__WeapTypeWakizashi")    )
        ];

        allTypes.InsertRange(0, skyreTypes);

        List<DataMap> allMaterials = [
            new(Id: "mat_amber",      Kwda: GetFormKey("cc_WeapMaterialAmber"),             Item: GetFormKey("cc_IngotAmber"),    Perk: [ GetFormKey("GlassSmithing"), GetFormKey("EbonySmithing") ]       ),
            new(Id: "mat_blades",     Kwda: GetFormKey("WAF_WeapMaterialBlades"),           Item: GetFormKey("IngotSteel"),       Perk: [ GetFormKey("SteelSmithing") ]                                    ),
            new(Id: "mat_daedric",    Kwda: GetFormKey("WeapMaterialDaedric"),              Item: GetFormKey("IngotEbony"),       Perk: [ GetFormKey("DaedricSmithing") ]                                  ),
            new(Id: "mat_dawnguard",  Kwda: GetFormKey("WAF_DLC1WeapMaterialDawnguard"),    Item: GetFormKey("IngotSteel"),       Perk: [ GetFormKey("SteelSmithing") ]                                    ),
            new(Id: "mat_dark",       Kwda: GetFormKey("cc_WeapMaterialDark"),              Item: GetFormKey("IngotQuicksilver"), Perk: [ GetFormKey("DaedricSmithing") ]                                  ),
            new(Id: "mat_dragonbone", Kwda: GetFormKey("DLC1WeapMaterialDragonbone"),       Item: GetFormKey("DragonBone"),       Perk: [ GetFormKey("DragonArmor") ]                                      ),
            new(Id: "mat_draugr",     Kwda: GetFormKey("WeapMaterialDraugr"),               Item: GetFormKey("IngotQuicksilver"), Perk: [ GetFormKey("AdvancedArmors") ]                                   ),
            new(Id: "mat_draugrh",    Kwda: GetFormKey("WeapMaterialDraugrHoned"),          Item: GetFormKey("IngotQuicksilver"), Perk: [ GetFormKey("AdvancedArmors") ]                                   ),
            new(Id: "mat_dwarven",    Kwda: GetFormKey("WeapMaterialDwarven"),              Item: GetFormKey("IngotDwarven"),     Perk: [ GetFormKey("DwarvenSmithing") ]                                  ),
            new(Id: "mat_ebony",      Kwda: GetFormKey("WeapMaterialEbony"),                Item: GetFormKey("IngotEbony"),       Perk: [ GetFormKey("EbonySmithing") ]                                    ),
            new(Id: "mat_elven",      Kwda: GetFormKey("WeapMaterialElven"),                Item: GetFormKey("IngotMoonstone"),   Perk: [ GetFormKey("ElvenSmithing") ]                                    ),
            new(Id: "mat_falmer",     Kwda: GetFormKey("WeapMaterialFalmer"),               Item: GetFormKey("ChaurusChitin"),    Perk: [ GetFormKey("ElvenSmithing") ]                                    ),
            new(Id: "mat_falmerh",    Kwda: GetFormKey("WeapMaterialFalmerHoned"),          Item: GetFormKey("ChaurusChitin"),    Perk: [ GetFormKey("ElvenSmithing") ]                                    ),
            new(Id: "mat_forsworn",   Kwda: GetFormKey("WAF_WeapMaterialForsworn"),         Item: GetFormKey("IngotIron")                                                                                  ),
            new(Id: "mat_glass",      Kwda: GetFormKey("WeapMaterialGlass"),                Item: GetFormKey("IngotMalachite"),   Perk: [ GetFormKey("GlassSmithing") ]                                    ),
            new(Id: "mat_golden",     Kwda: GetFormKey("cc_WeapMaterialGolden"),            Item: GetFormKey("IngotMoonstone"),   Perk: [ GetFormKey("DaedricSmithing") ]                                  ),
            new(Id: "mat_imperial",   Kwda: GetFormKey("WeapMaterialImperial"),             Item: GetFormKey("IngotSteel"),       Perk: [ GetFormKey("SteelSmithing") ]                                    ),
            new(Id: "mat_iron",       Kwda: GetFormKey("WeapMaterialIron"),                 Item: GetFormKey("IngotIron")                                                                                  ),
            new(Id: "mat_madness",    Kwda: GetFormKey("cc_WeapMaterialMadness"),           Item: GetFormKey("cc_IngotMadness"),  Perk: [ GetFormKey("EbonySmithing") ]                                    ),
            new(Id: "mat_nordic",     Kwda: GetFormKey("DLC2WeaponMaterialNordic"),         Item: GetFormKey("IngotQuicksilver"), Perk: [ GetFormKey("AdvancedArmors") ]                                   ),
            new(Id: "mat_orcish",     Kwda: GetFormKey("WeapMaterialOrcish"),               Item: GetFormKey("IngotOrichalcum"),  Perk: [ GetFormKey("OrcishSmithing") ]                                   ),
            new(Id: "mat_silverr",    Kwda: GetFormKey("skyre__WeapMaterialSilverRefined"), Item: GetFormKey("IngotQuicksilver"), Perk: [ GetFormKey("skyre_SMTDeepSilver"), GetFormKey("SteelSmithing") ] ),
            new(Id: "mat_silver",     Kwda: GetFormKey("WeapMaterialSilver"),               Item: GetFormKey("IngotSilver"),      Perk: [ GetFormKey("skyre_SMTTradecraft"), GetFormKey("SteelSmithing") ] ),
            new(Id: "mat_stalhrim",   Kwda: GetFormKey("DLC2WeaponMaterialStalhrim"),       Item: GetFormKey("DLC2OreStalhrim"),  Perk: [ GetFormKey("GlassSmithing"), GetFormKey("EbonySmithing") ]       ),
            new(Id: "mat_steel",      Kwda: GetFormKey("WeapMaterialSteel"),                Item: GetFormKey("IngotSteel"),       Perk: [ GetFormKey("SteelSmithing") ]                                    ),
            new(Id: "mat_wood",       Kwda: GetFormKey("WeapMaterialWood"),                 Item: GetFormKey("Charcoal")                                                                                   )
        ];

        List<DataMap> crossbowSubtypes = [
            new(Id: "name_recurve",                                                       Desc: "desc_recurve", Perk: [ GetFormKey("skyre_MARCrossbowRecurve") ] ),
            new(Id: "name_lweight",                                                       Desc: "desc_lweight", Perk: [ GetFormKey("skyre_MARCrossbowLight") ]   ),
            new(Id: "name_muffled", Kwda: GetFormKey("skyre_MAREnhancedCrossbowMuffled"), Desc: "desc_muffled", Perk: [ GetFormKey("skyre_MARCrossbowMuffled") ] ),
            new(Id: "name_siege",   Kwda: GetFormKey("skyre_MAREnhancedCrossbowSiege"),   Desc: "desc_siege",   Perk: [ GetFormKey("skyre_MARCrossbowSiege") ]   )
        ];

        return (allTypes, skyreTypes, crossbowSubtypes, allMaterials);
    }
}