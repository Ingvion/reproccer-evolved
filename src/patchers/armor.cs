using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using ReProccer.Config;
using ReProccer.Utils;
using System.Text.Json.Nodes;

namespace ReProccer.Patchers;

public static class ArmorPatcher
{
    private static readonly IPatcherState<ISkyrimMod, ISkyrimModGetter> State = Executor.State!;
    private static readonly AllSettings Settings = Executor.Settings!;
    private static readonly JsonObject Rules = Executor.Rules!["armor"]!.AsObject();
    private static readonly List<StaticsMap>? LocalStatics = BuildStatics();

    private static readonly List<DataMap> HeavyMaterials = BuildHeavyMaterialsMap();
    private static readonly List<DataMap> LightMaterials = BuildLightMaterialsMap();
    private static readonly List<DataMap> AllMaterials = [.. HeavyMaterials.Union(LightMaterials)];
    private static readonly List<DataMap> FactionBinds = BuildFactionBindsMap();
    private static Armor? PatchedRecord;
    private static PatchingData RecordData;
    private static List<List<string>>? Report;

    public static void Run()
    {
        UpdateGMST();
        List<IArmorGetter> records = GetRecords();
        List<List<string>> blacklists = [
            [.. Rules["excludedFromRenaming"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedDreamcloth"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedFromRecipes"]!.AsArray().Select(value => value!.GetValue<string>())]
        ];

        string[] verboseFilter = Settings.Debug.VerboseDataFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var armor in records)
        {
            PatchedRecord = null;
            Report = [ [], [], [] ];

            // storing some data publicly to avoid sequential passing of arguments
            RecordData = new PatchingData(
                nonPlayable: armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable),
                hasUniqueKeyword: armor.Keywords!.Contains(GetFormKey("skyre__NoMeltdownRecipes")),
                armorType: armor.BodyTemplate!.ArmorType);

            /* armor records with templates inherit their data from the template, but have unique names;
               jewelry type clothing items require no other patching */
            if (!armor.TemplateArmor.IsNull || (RecordData.GetArmorType() == ArmorType.Clothing
                && armor.Keywords!.Contains(GetFormKey("ArmorJewelry", true))))
            {
                PatchRecordNames(armor, blacklists[0]);
                ShowReport(armor, Report);
                continue;
            }

            SetOverriddenData(armor);

            if (RecordData.GetArmorType() != ArmorType.Clothing)
            {
                PatchShieldWeight(armor, RecordData.GetArmorType());
                PatchArmorRating(armor);
            }

            if (!RecordData.IsNonPlayable())
            {
                PatchRecordNames(armor, blacklists[0]);
                PatchMasqueradeKeywords(armor);
                if (RecordData.GetArmorType() == ArmorType.Clothing)
                {
                    ProcessClothing(armor, blacklists[1]);
                    ShowReport(armor, Report);
                    continue;
                }
            }

            ShowReport(armor, Report);
        }
    }

    private static void UpdateGMST()
    {
        FormKey armorScalingFactor = new("Skyrim.esm", 0x021a72);

        IGameSettingGetter conflictWinner = State.LinkCache.Resolve<IGameSettingGetter>(armorScalingFactor);
        GameSetting record = State.PatchMod.GameSettings.GetOrAddAsOverride(conflictWinner);

        if (record is GameSettingFloat gmstArmorScalingFactor)
        {
            gmstArmorScalingFactor.Data = Settings.Armor.ArmorScalingFactor;
        }

        FormKey maxArmorRating = new("Skyrim.esm", 0x037deb);

        conflictWinner = State.LinkCache.Resolve<IGameSettingGetter>(maxArmorRating);
        record = State.PatchMod.GameSettings.GetOrAddAsOverride(conflictWinner);

        if (record is GameSettingFloat gmstMaxArmorRating)
        {
            gmstMaxArmorRating.Data = Settings.Armor.MaxArmorRating;
        }
    }

    private static List<IArmorGetter> GetRecords()
    {
        List<IArmorGetter> records = [];
        List<string> excludedArmor = [.. Rules["excludedArmor"]!.AsArray().Select(value => value!.GetValue<string>())];
        List<FormKey> mustHave = [
            GetFormKey("ArmorHeavy", true),
            GetFormKey("ArmorLight", true),
            GetFormKey("ArmorShield", true),
            GetFormKey("ArmorClothing", true),
            GetFormKey("ArmorJewelry", true)
        ];

        var conflictWinners = State.LoadOrder.PriorityOrder
        .Where(plugin => !Settings.General.IgnoredFiles.Any(name => name == plugin.ModKey.FileName))
        .Where(plugin => plugin.Enabled)
        .WinningOverrides<IArmorGetter>();

        Console.WriteLine($"~~~ {conflictWinners.Count()} armor records found, filtering... ~~~\n\n"
            + "====================");

        foreach (var armor in conflictWinners)
        {
            if (IsValid(armor, excludedArmor, mustHave)) records.Add(armor);
        }

        Console.WriteLine($"\n~~~ {records.Count} armor records are eligible for patching ~~~\n\n"
            + "====================");
        return records;
    }

    private static bool IsValid(IArmorGetter armor, List<string> excludedArmor, List<FormKey> mustHave)
    {
        // invalid if found in the excluded records list by edid
        if (Settings.General.ExclByEdID && excludedArmor.Any(value => value.Equals(armor.EditorID)))
        {
            if (Settings.Debug.ShowExcluded) Log(armor, "~ INFO", $"found in the exclusion list (as {armor.EditorID}).");
            return false;
        }

        // invalid if has no name
        if (armor.Name == null) return false;

        // invalid if found in the excluded records list by name
        if (excludedArmor.Any(value => armor.Name!.ToString()!.Contains(value)))
        {
            if (Settings.Debug.ShowExcluded) Log(armor, "~ INFO", "found in the exclusion list.");
            return false;
        }

        // invalid if has no body template)
        if (armor.BodyTemplate == null) return false;

        // valid if has a template (to skip keyword checks below)
        if (!armor.TemplateArmor.IsNull) return true;

        // invalid if has no keywords or have empty kw array (rare)
        if (armor.Keywords == null || armor.Keywords.Count == 0) return false;

        // invalid if it does not have any required keywords
        if (!mustHave.Any(keyword => armor.Keywords.Contains(keyword))) return false;

        return true;
    }

    private static void PatchRecordNames(IArmorGetter armor, List<string> renamingBlacklist)
    {
        if (renamingBlacklist.Count > 0
            && renamingBlacklist.Any(value => armor.Name!.ToString()!.Contains(value)))
        {
            Report![2].Add($"Found in the \"No renaming\" list");
            return;
        }

        string name = armor.Name!.ToString()!;

        /* Options:
         * i - case-insensitive search
         * g - replace all matches
         * p - string as part of a word
         * c - retain capitalization
         * n - search for the next rule
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
                    if (!filterArr.Any(type => RecordData.GetArmorType().ToString() == type.Replace(" ", "")))
                    {
                        continue;
                    }
                }

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

        if (name != armor.Name.ToString())
        {
            Report![2].Add($"Was renamed to {name}");
            GetAsOverride(armor).Name = name;
        }
    }

    private static void SetOverriddenData(IArmorGetter armor)
    {
        JsonNode? overrideNode = Helpers.RuleByName(
            armor.Name!.ToString()!, Rules["materialOverrides"]!.AsArray(), data1: "names", data2: "material");
        string? overrideString = overrideNode?.AsType("string");

        if (overrideNode != null && overrideString == null)
        {
            Report![1].Add("The material name returned from the relevant \"materialOverrides\" rule should be a string.");
            return;
        }

        FormKey nullRef = new("Skyrim.esm", 0x000000);
        foreach (var entry1 in AllMaterials)
        {
            string id = entry1.Id.ToString();
            id = id.GetT9n();

            if (id == overrideString)
            {
                if  (entry1.Kwda == nullRef)
                {
                    Report![0].Add("The relevant \"materialOverrides\" rule references a material from Creation Club's \"Saints and Seducers\"");
                    break;
                }

                foreach (var entry2 in AllMaterials)
                {
                    if (armor.Keywords!.Contains((FormKey)entry2.Kwda!) && entry2.Kwda != entry1.Kwda)
                    {
                        GetAsOverride(armor).Keywords!.Remove((FormKey)entry2.Kwda);
                    }
                }

                GetAsOverride(armor).Keywords!.Add((FormKey)entry1.Kwda!);
                break;
            }
        }
    }

    private static void PatchShieldWeight(IArmorGetter armor, ArmorType armorType)
    {
        if (!armor.Keywords!.Contains(GetFormKey("ArmorShield", true))) return;

        if (armorType == ArmorType.HeavyArmor && !armor.Keywords!.Contains(GetFormKey("skyre__ArmorShieldHeavy", true)))
        {
            GetAsOverride(armor).Keywords!.Add(GetFormKey("skyre__ArmorShieldHeavy", true));
            GetAsOverride(armor).BashImpactDataSet = new FormLinkNullable<IImpactDataSetGetter>(GetFormKey("WPNBashShieldHeavyImpactSet", true));
            GetAsOverride(armor).AlternateBlockMaterial = new FormLinkNullable<IMaterialTypeGetter>(GetFormKey("MaterialShieldHeavy", true));
        }
        else if (armorType == ArmorType.LightArmor && !armor.Keywords!.Contains(GetFormKey("skyre__ArmorShieldLight", true)))
        {
            GetAsOverride(armor).Keywords!.Add(GetFormKey("skyre__ArmorShieldLight", true));
            GetAsOverride(armor).BashImpactDataSet = new FormLinkNullable<IImpactDataSetGetter>(GetFormKey("WPNBashShieldLightImpactSet", true));
            GetAsOverride(armor).AlternateBlockMaterial = new FormLinkNullable<IMaterialTypeGetter>(GetFormKey("MaterialShieldLight", true));
        }
    }

    private static void PatchArmorRating(IArmorGetter armor)
    {
        float? slotFactor = GetSlotFactor(armor);
        int? materialFactor = GetMaterialFactor(armor);

        if (slotFactor == null || materialFactor == null) return;

        float extraMod = GetExtraArmorMod(armor);
        double newArmorRating = Math.Floor((float)slotFactor * (int)materialFactor * extraMod);

        if ((float)newArmorRating != armor.ArmorRating)
        {
            GetAsOverride(armor).ArmorRating = (float)newArmorRating;
            Report![2].Add($"Armor rating modified: {armor.ArmorRating} -> {GetAsOverride(armor).ArmorRating}");
        }
    }

    private static float? GetSlotFactor (IArmorGetter armor)
    {
        if (armor.Keywords!.Contains(GetFormKey("ArmorSlotBoots", true)))
        {
            return Settings.Armor.SlotBoots;
        }
        else if (armor.Keywords!.Contains(GetFormKey("ArmorSlotCuirass", true)))
        {
            return Settings.Armor.SlotCuirass;
        }
        else if (armor.Keywords!.Contains(GetFormKey("ArmorSlotGauntlets", true)))
        {
            return Settings.Armor.SlotGauntlets;
        }
        else if (armor.Keywords!.Contains(GetFormKey("ArmorSlotHelmet", true)))
        {
            return Settings.Armor.SlotHelmet;
        }
        else if (armor.Keywords!.Contains(GetFormKey("ArmorShield", true)))
        {
            return Settings.Armor.SlotShield;
        }

        Report![1].Add("Unable to determine an equip slot for the record.");
        return null;
    }

    private static int? GetMaterialFactor(IArmorGetter armor)
    {
        string? materialId = null;
        FormKey nullRef = new("Skyrim.esm", 0x000000);
        foreach (var entry in AllMaterials)
        {
            FormKey kwda = (FormKey)entry.Kwda!;
            if (kwda != nullRef && armor.Keywords!.Contains(kwda))
            {
                materialId = entry.Id;
                break;
            }
        }

        JsonNode? factorNode = Helpers.RuleByName(armor.Name!.ToString()!, Rules["materials"]!.AsArray(), data1: "names", data2: "armor");
        if (factorNode == null && materialId != null) factorNode = Helpers.RuleByName(materialId!, Rules["materials"]!.AsArray(), data1: "id", data2: "armor");
        int? factorInt = factorNode?.AsType("int");

        if (factorInt != null)
        {
            if (materialId == null) Report![0].Add("The record has a \"materials\" rule for its name but no material keyword.");
            return factorInt;
        }

        if (factorNode != null && factorInt == null)
        {
            Report![1].Add("The armor value in the relevant \"materials\" rule should be a number.");
        }

        Report![1].Add("Unable to determine the material.");
        return null;
    }

    private static float GetExtraArmorMod(IArmorGetter armor)
    {
        JsonNode? modifierNode = Helpers.RuleByName(armor.Name!.ToString()!, Rules["armorModifiers"]!.AsArray(), data1: "names", data2: "multiplier");
        float? modifierFloat = modifierNode?.AsType("float");

        if (modifierFloat != null)
        {
            return (float)(modifierFloat > 0.0f ? modifierFloat : 1.0f);
        }

        if (modifierNode != null && modifierFloat == null)
        {
            Report![1].Add("The multiplier value in the relevant \"armorModifiers\" rule should be a number.");
        }

        return 1.0f;
    }

    private static void PatchMasqueradeKeywords(IArmorGetter armor)
    {
        JsonArray rules = Rules["masquerade"]!.AsArray();
        List<string> addedFactions = [];

        foreach (var rule in rules)
        {
            string[] namesArr = rule!["names"]!.ToString().Split(',', StringSplitOptions.TrimEntries);
            if (!namesArr.Any(word => armor.Name!.ToString()!.Contains(word))) continue;

            string[] filterArr = rule!["filter"]?.ToString().Replace(" ", "").Split(',') ?? [];
            if (filterArr.Length != 0 && !filterArr.Any(type => type == RecordData.GetArmorType().ToString())) continue;

            string factions = rule!["faction"]!.ToString();
            foreach (var entry in FactionBinds)
            {
                if (factions.Contains(entry.Id.GetT9n()) && !armor.Keywords!.Contains((FormKey)entry.Kwda!))
                {
                    GetAsOverride(armor).Keywords!.Add((FormKey)entry.Kwda);
                    addedFactions.Add($"{entry.Id.GetT9n()}");
                }
            }
        }
        if (addedFactions.Count > 0) Report![2].Add($"Faction keywords added: {string.Join(", ", addedFactions)}");
    }

    // local patcher helpers
    private static FormKey GetFormKey(string id, bool local = false)
    {
        return (local ? LocalStatics! : Executor.Statics!).First(elem => elem.Id == id).Formkey;
    }

    private static Armor GetAsOverride(this IArmorGetter armor)
    {
        if (!RecordData.IsModified()) RecordData.SetModified();
        return PatchedRecord?.FormKey != armor.FormKey ? State.PatchMod.Armors.GetOrAddAsOverride(armor) : PatchedRecord;
    }

    private static void ShowReport(IArmorGetter armor, List<List<string>> msgList)
    {
        if (msgList[0].Count > 0) Log(armor, "> CAUTION", msgList[0]);
        if (msgList[1].Count > 0) Log(armor, "# ERROR", msgList[1]);

        if (!Settings.Debug.ShowVerboseData) return;

        string[] filter = Settings.Debug.VerboseDataFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (filter.Length > 0 && !filter.Any(value => armor.Name!.ToString()!.Contains(value.Trim()))) return;
        if (msgList[2].Count > 0) Log(armor, "+ REPORT", msgList[2]);
    }

    private static void Log(IArmorGetter armor, string prefix, string message)
    {
        if (Settings.Debug.ShowNonPlayable || !RecordData.IsNonPlayable())
        {
            Console.WriteLine($"{prefix}: {armor.Name} ({armor.FormKey}): {message}\n"
                + "====================");
        }
    }

    private static void Log(IArmorGetter armor, string prefix, List<string> messages)
    {
        if (Settings.Debug.ShowNonPlayable || !RecordData.IsNonPlayable())
        {
            string note = RecordData.IsNonPlayable() ? " | NON-PLAYABLE" : "";
            Console.WriteLine($"{prefix} | {armor.Name} ({armor.FormKey}){note}");
            foreach(var msg in messages)
            {
                Console.WriteLine($"--- {msg}");
            }
            Console.WriteLine("====================");
        }
    }

    // armor patcher data maps
    private static List<StaticsMap> BuildStatics() => [
        new(Id: "ArmorHeavy",                             Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06bbd2|KWDA")                  ),
        new(Id: "ArmorLight",                             Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06bbd3|KWDA")                  ),
        new(Id: "ArmorClothing",                          Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06bbe8|KWDA")                  ),
        new(Id: "ArmorJewelry",                           Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06bbe9|KWDA")                  ),
        new(Id: "ArmorSlotCuirass",                       Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06c0ec|KWDA")                  ),
        new(Id: "ArmorSlotBoots",                         Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06c0ed|KWDA")                  ),
        new(Id: "ArmorSlotHelmet",                        Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06c0ee|KWDA")                  ),
        new(Id: "ArmorSlotGauntlets",                     Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06c0ef|KWDA")                  ),
        new(Id: "VendorItemArmor",                        Formkey: Helpers.ParseFormKey("Skyrim.esm|0x08f959|KWDA")                  ),
        new(Id: "VendorItemJewelry",                      Formkey: Helpers.ParseFormKey("Skyrim.esm|0x08f95a|KWDA")                  ),
        new(Id: "VendorItemClothing",                     Formkey: Helpers.ParseFormKey("Skyrim.esm|0x08f95b|KWDA")                  ),
        new(Id: "ArmorShield",                            Formkey: Helpers.ParseFormKey("Skyrim.esm|0x0965b2|KWDA")                  ),
        new(Id: "ClothingBody",                           Formkey: Helpers.ParseFormKey("Skyrim.esm|0x0a8657|KWDA")                  ),
        new(Id: "ClothingHead",                           Formkey: Helpers.ParseFormKey("Skyrim.esm|0x10cd11|KWDA")                  ),
        new(Id: "MaterialShieldLight",                    Formkey: Helpers.ParseFormKey("Skyrim.esm|0x016978|KWDA", true)            ),
        new(Id: "MaterialShieldHeavy",                    Formkey: Helpers.ParseFormKey("Skyrim.esm|0x016979|KWDA", true)            ),
        new(Id: "WPNBashShieldLightImpactSet",            Formkey: Helpers.ParseFormKey("Skyrim.esm|0x0183fb|KWDA", true)            ),
        new(Id: "WPNBashShieldHeavyImpactSet",            Formkey: Helpers.ParseFormKey("Skyrim.esm|0x0183fe|KWDA", true)            ),
        new(Id: "skyre__ArmorShieldHeavy",                Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00080f|KWDA") ),
        new(Id: "skyre__ArmorShieldLight",                Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000810|KWDA") ),
        new(Id: "skyre__ArmorDreamcloth",                 Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000811|KWDA") ),
        new(Id: "skyre__DreamclothBody",                  Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00098d|KWDA") ),
        new(Id: "skyre_SPCMasqueradeBandit",              Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000f44|KWDA") ),
        new(Id: "skyre_SPCMasqueradeForsworn",            Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000f45|KWDA") ),
        new(Id: "skyre_SPCMasqueradeImperial",            Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000f46|KWDA") ),
        new(Id: "skyre_SPCMasqueradeStormcloak",          Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000f47|KWDA") ),
        new(Id: "skyre_SPCMasqueradeThalmor",             Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000f48|KWDA") ),
    ];

    private static List<DataMap> BuildHeavyMaterialsMap() => [
        new(Id: "mat_ancientnord", Kwda: GetFormKey("WAF_ArmorMaterialDraugr"),              Item: GetFormKey("IngotCorundum"),    Perk: [ GetFormKey("AdvancedArmors") ]                             ),
        new(Id: "mat_blades",      Kwda: GetFormKey("ArmorMaterialBlades"),                  Item: GetFormKey("IngotSteel"),       Perk: [ GetFormKey("SteelSmithing") ]                              ),
        new(Id: "mat_bonemoldh",   Kwda: GetFormKey("DLC2ArmorMaterialBonemoldHeavy"),       Item: GetFormKey("DLC2NetchLeather"), Perk: [ GetFormKey("AdvancedArmors") ]                             ),
        new(Id: "mat_chitinh",     Kwda: GetFormKey("DLC2ArmorMaterialChitinHeavy"),         Item: GetFormKey("DLC2ChitinPlate"),  Perk: [ GetFormKey("ElvenSmithing") ]                              ),
        new(Id: "mat_daedric",     Kwda: GetFormKey("ArmorMaterialDaedric"),                 Item: GetFormKey("IngotEbony"),       Perk: [ GetFormKey("DaedricSmithing") ]                            ),
        new(Id: "mat_dawnguard",   Kwda: GetFormKey("DLC1ArmorMaterialDawnguard"),           Item: GetFormKey("IngotSteel"),       Perk: [ GetFormKey("SteelSmithing") ]                              ),
        new(Id: "mat_dawnguardh",  Kwda: GetFormKey("DLC1ArmorMaterialHunter"),              Item: GetFormKey("IngotSteel"),       Perk: [ GetFormKey("SteelSmithing") ]                              ),
        new(Id: "mat_dragonplate", Kwda: GetFormKey("ArmorMaterialDragonplate"),             Item: GetFormKey("DragonBone"),       Perk: [ GetFormKey("DragonArmor") ]                                ),
        new(Id: "mat_dwarven",     Kwda: GetFormKey("ArmorMaterialDwarven"),                 Item: GetFormKey("IngotDwarven"),     Perk: [ GetFormKey("DwarvenSmithing") ]                            ),
        new(Id: "mat_ebony",       Kwda: GetFormKey("ArmorMaterialEbony"),                   Item: GetFormKey("IngotEbony"),       Perk: [ GetFormKey("EbonySmithing") ]                              ),
        new(Id: "mat_falmerhr",    Kwda: GetFormKey("DLC1ArmorMaterialFalmerHardened"),      Item: GetFormKey("ChaurusChitin"),    Perk: [ GetFormKey("ElvenSmithing") ]                              ),
        new(Id: "mat_falmerhv",    Kwda: GetFormKey("DLC1ArmorMaterialFalmerHeavy"),         Item: GetFormKey("ChaurusChitin"),    Perk: [ GetFormKey("ElvenSmithing") ]                              ),
        new(Id: "mat_falmer",      Kwda: GetFormKey("DLC1ArmorMaterialFalmerHeavyOriginal"), Item: GetFormKey("ChaurusChitin"),    Perk: [ GetFormKey("ElvenSmithing") ]                              ),
        new(Id: "mat_golden",      Kwda: GetFormKey("cc_ArmorMaterialGolden"),               Item: GetFormKey("IngotGold"),        Perk: [ GetFormKey("DaedricSmithing") ]                            ),
        new(Id: "mat_imperialh",   Kwda: GetFormKey("ArmorMaterialImperialHeavy"),           Item: GetFormKey("IngotSteel"),       Perk: [ GetFormKey("SteelSmithing") ]                              ),
        new(Id: "mat_iron",        Kwda: GetFormKey("ArmorMaterialIron"),                    Item: GetFormKey("IngotIron")                                                                            ),
        new(Id: "mat_ironb",       Kwda: GetFormKey("ArmorMaterialIronBanded"),              Item: GetFormKey("IngotIron")                                                                            ),
        new(Id: "mat_madness",     Kwda: GetFormKey("cc_ArmorMaterialMadness"),              Item: GetFormKey("cc_IngotMadness"),  Perk: [ GetFormKey("EbonySmithing") ]                              ),
        new(Id: "mat_nordic",      Kwda: GetFormKey("DLC2ArmorMaterialNordicHeavy"),         Item: GetFormKey("IngotQuicksilver"), Perk: [ GetFormKey("AdvancedArmors") ]                             ),
        new(Id: "mat_orcish",      Kwda: GetFormKey("ArmorMaterialOrcish"),                  Item: GetFormKey("IngotOrichalcum"),  Perk: [ GetFormKey("OrcishSmithing") ]                             ),
        new(Id: "mat_stalhrimh",   Kwda: GetFormKey("DLC2ArmorMaterialStalhrimHeavy"),       Item: GetFormKey("DLC2OreStalhrim"),  Perk: [ GetFormKey("GlassSmithing"), GetFormKey("EbonySmithing") ] ),
        new(Id: "mat_steel",       Kwda: GetFormKey("ArmorMaterialSteel"),                   Item: GetFormKey("IngotSteel"),       Perk: [ GetFormKey("SteelSmithing") ]                              ),
        new(Id: "mat_steelp",      Kwda: GetFormKey("ArmorMaterialSteelPlate"),              Item: GetFormKey("IngotSteel"),       Perk: [ GetFormKey("AdvancedArmors") ]                             ),
        new(Id: "mat_wolf",        Kwda: GetFormKey("WAF_ArmorWolf"),                        Item: GetFormKey("IngotSteel"),       Perk: [ GetFormKey("SteelSmithing") ]                              )
    ];

    private static List<DataMap> BuildLightMaterialsMap() => [
        new(Id: "mat_amber",       Kwda: GetFormKey("cc_ArmorMaterialAmber"),          Item: GetFormKey("cc_IngotAmber"),    Perk: [ GetFormKey("GlassSmithing"), GetFormKey("EbonySmithing") ]         ),
        new(Id: "mat_bonemold",    Kwda: GetFormKey("DLC2ArmorMaterialBonemoldLight"), Item: GetFormKey("DLC2NetchLeather"), Perk: [ GetFormKey("AdvancedArmors") ]                                     ),
        new(Id: "mat_chitin",      Kwda: GetFormKey("DLC2ArmorMaterialChitinLight"),   Item: GetFormKey("DLC2ChitinPlate"),  Perk: [ GetFormKey("ElvenSmithing") ]                                      ),
        new(Id: "mat_dark",        Kwda: GetFormKey("cc_ArmorMaterialDark"),           Item: GetFormKey("IngotQuicksilver"), Perk: [ GetFormKey("DaedricSmithing") ]                                    ),
        new(Id: "mat_dragonscale", Kwda: GetFormKey("ArmorMaterialDragonscale"),       Item: GetFormKey("DragonScales"),     Perk: [ GetFormKey("DragonArmor") ]                                        ),
        new(Id: "mat_elven",       Kwda: GetFormKey("ArmorMaterialElven"),             Item: GetFormKey("IngotMoonstone"),   Perk: [ GetFormKey("ElvenSmithing") ]                                      ),
        new(Id: "mat_elveng",      Kwda: GetFormKey("ArmorMaterialElvenGilded"),       Item: GetFormKey("IngotMoonstone"),   Perk: [ GetFormKey("ElvenSmithing") ]                                      ),
        new(Id: "mat_forsworn",    Kwda: GetFormKey("ArmorMaterialForsworn"),          Item: GetFormKey("LeatherStrips")                                                                                ),
        new(Id: "mat_glass",       Kwda: GetFormKey("ArmorMaterialGlass"),             Item: GetFormKey("IngotMalachite"),   Perk: [ GetFormKey("GlassSmithing") ]                                      ),
        new(Id: "mat_guard",       Kwda: GetFormKey("WAF_ArmorMaterialGuard"),         Item: GetFormKey("IngotIron"),        Perk: [ GetFormKey("skyre_SMTLeathercraft"), GetFormKey("SteelSmithing") ] ),
        new(Id: "mat_hide",        Kwda: GetFormKey("ArmorMaterialHide"),              Item: GetFormKey("LeatherStrips")                                                                                ),
        new(Id: "mat_imperial",    Kwda: GetFormKey("ArmorMaterialImperialLight"),     Item: GetFormKey("IngotSteel"),       Perk: [ GetFormKey("skyre_SMTLeathercraft"), GetFormKey("SteelSmithing") ] ),
        new(Id: "mat_imperials",   Kwda: GetFormKey("ArmorMaterialImperialStudded"),   Item: GetFormKey("LeatherStrips"),    Perk: [ GetFormKey("skyre_SMTLeathercraft") ]                              ),
        new(Id: "mat_leather",     Kwda: GetFormKey("ArmorMaterialLeather"),           Item: GetFormKey("LeatherStrips"),    Perk: [ GetFormKey("skyre_SMTLeathercraft") ]                              ),
        new(Id: "mat_nightingale", Kwda: GetFormKey("ArmorNightingale"),               Item: GetFormKey("LeatherStrips"),    Perk: [ GetFormKey("skyre_SMTLeathercraft") ]                              ),
        new(Id: "mat_scaled",      Kwda: GetFormKey("ArmorMaterialScaled"),            Item: GetFormKey("IngotCorundum"),    Perk: [ GetFormKey("AdvancedArmors") ]                                     ),
        new(Id: "mat_shrouded",    Kwda: GetFormKey("ArmorDarkBrotherhood"),           Item: GetFormKey("LeatherStrips"),    Perk: [ GetFormKey("skyre_SMTLeathercraft") ]                              ),
        new(Id: "mat_stalhrim",    Kwda: GetFormKey("DLC2ArmorMaterialStalhrimLight"), Item: GetFormKey("DLC2OreStalhrim"),  Perk: [ GetFormKey("GlassSmithing"), GetFormKey("EbonySmithing") ]         ),
        new(Id: "mat_stormcloak",  Kwda: GetFormKey("ArmorMaterialStormcloak"),        Item: GetFormKey("IngotIron"),        Perk: [ GetFormKey("skyre_SMTLeathercraft"), GetFormKey("SteelSmithing") ] ),
        new(Id: "mat_stormcloakh", Kwda: GetFormKey("ArmorMaterialBearStormcloak"),    Item: GetFormKey("IngotSteel"),       Perk: [ GetFormKey("skyre_SMTLeathercraft"), GetFormKey("SteelSmithing") ] ),
        new(Id: "mat_studded",     Kwda: GetFormKey("ArmorMaterialStudded"),           Item: GetFormKey("LeatherStrips"),    Perk: [ GetFormKey("skyre_SMTLeathercraft") ]                              ),
        new(Id: "mat_thievesgl",   Kwda: GetFormKey("ArmorMaterialThievesGuild"),      Item: GetFormKey("LeatherStrips"),    Perk: [ GetFormKey("skyre_SMTLeathercraft") ]                              ),
        new(Id: "mat_vampire",     Kwda: GetFormKey("DLC1ArmorMaterialVampire"),       Item: GetFormKey("LeatherStrips"),    Perk: [ GetFormKey("skyre_SMTLeathercraft") ]                              )
    ];

    private static List<DataMap> BuildFactionBindsMap() => [
        new(Id: "fact_bandit",     Kwda: GetFormKey("skyre_SPCMasqueradeBandit", true)     ),
        new(Id: "fact_forsworn",   Kwda: GetFormKey("skyre_SPCMasqueradeForsworn", true)   ),
        new(Id: "fact_imperial",   Kwda: GetFormKey("skyre_SPCMasqueradeImperial", true)   ),
        new(Id: "fact_stormcloak", Kwda: GetFormKey("skyre_SPCMasqueradeStormcloak", true) ),
        new(Id: "fact_thalmor",    Kwda: GetFormKey("skyre_SPCMasqueradeThalmor", true)    )
    ];
}

