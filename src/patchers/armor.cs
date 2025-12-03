using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using ReProccer.Utils;
using System.Text.Json.Nodes;

namespace ReProccer.Patchers;

public static class ArmorPatcher
{
    private static readonly Config.AllSettings Settings = Executor.Settings!;
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
        (List<IArmorGetter> records, List<IConstructibleObjectGetter> recipes) = GetRecords();
        List<List<string>> blacklists = [
            [.. Rules["excludedFromRenaming"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedDreamcloth"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedFromRecipes"]!.AsArray().Select(value => value!.GetValue<string>())]
        ];

        foreach (var armor in records)
        {
            PatchedRecord = null;
            // 0 info, 1 caution, 2 errors, 3 report
            Report = [[], [], [], []];

            // storing some data publicly to avoid sequential passing of arguments
            RecordData = new PatchingData(
                nonPlayable: armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable),
                hasUniqueKeyword: armor.Keywords!.Contains(GetFormKey("skyre__NoMeltdownRecipes")),
                armorType: armor.BodyTemplate!.ArmorType);

            if (!armor.TemplateArmor.IsNull || (RecordData.ArmorType == ArmorType.Clothing
                && armor.Keywords!.Contains(GetFormKey("ArmorJewelry", true))))
            {
                PatchRecordNames(armor, blacklists[0]);
                ShowReport(armor, Report);
                continue;
            }

            SetOverriddenData(armor);

            if (RecordData.ArmorType != ArmorType.Clothing)
            {
                PatchShieldWeight(armor, RecordData.ArmorType);
                PatchArmorRating(armor);
            }

            if (!RecordData.NonPlayable)
            {
                PatchRecordNames(armor, blacklists[0]);
                PatchMasqueradeKeywords(armor);
                if (RecordData.ArmorType == ArmorType.Clothing)
                {
                    ProcessClothing(armor, blacklists[1]);
                    ShowReport(armor, Report);
                    continue;
                }
            }

            ProcessRecipes(armor, recipes, blacklists[2]);
            ShowReport(armor, Report);
        }
    }

    private static void UpdateGMST()
    {
        FormKey armorScalingFactor = new("Skyrim.esm", 0x021a72);

        IGameSettingGetter conflictWinner = Executor.State!.LinkCache.Resolve<IGameSettingGetter>(armorScalingFactor);
        GameSetting record = Executor.State!.PatchMod.GameSettings.GetOrAddAsOverride(conflictWinner);

        if (record is GameSettingFloat gmstArmorScalingFactor)
        {
            gmstArmorScalingFactor.Data = Settings.Armor.ArmorScalingFactor;
        }

        FormKey maxArmorRating = new("Skyrim.esm", 0x037deb);

        conflictWinner = Executor.State!.LinkCache.Resolve<IGameSettingGetter>(maxArmorRating);
        record = Executor.State!.PatchMod.GameSettings.GetOrAddAsOverride(conflictWinner);

        if (record is GameSettingFloat gmstMaxArmorRating)
        {
            gmstMaxArmorRating.Data = Settings.Armor.MaxArmorRating;
        }
    }

    private static (List<IArmorGetter> armors, List<IConstructibleObjectGetter> recipes) GetRecords()
    {
        IEnumerable<IArmorGetter> armoWinners = Executor.State!.LoadOrder.PriorityOrder
            .Where(plugin => !Settings.General.IgnoredFiles.Any(name => name == plugin.ModKey.FileName))
            .Where(plugin => plugin.Enabled)
            .WinningOverrides<IArmorGetter>();

        Console.WriteLine($"~~~ {armoWinners.Count()} armor records found, filtering... ~~~\n\n"
            + "====================");

        List<IArmorGetter> armoRecords = [];
        List<IConstructibleObjectGetter> cobjRecords = [.. Executor.State!.LoadOrder.PriorityOrder
            .Where(plugin => !Settings.General.IgnoredFiles.Any(name => name == plugin.ModKey.FileName))
            .Where(plugin => plugin.Enabled)
            .WinningOverrides<IConstructibleObjectGetter>()];

        List<string> excludedNames = [.. Rules["excludedArmor"]!.AsArray().Select(value => value!.GetValue<string>())];
        List<FormKey> mustHave = [
            GetFormKey("ArmorHeavy", true),
            GetFormKey("ArmorLight", true),
            GetFormKey("ArmorShield", true),
            GetFormKey("ArmorClothing", true),
            GetFormKey("ArmorJewelry", true)
];
        foreach (var record in armoWinners)
        {
            if (IsValid(record, excludedNames, mustHave)) armoRecords.Add(record);
        }

        Console.WriteLine($"\n~~~ {armoRecords.Count} armor records are eligible for patching ~~~\n\n"
            + "====================");
        return (armoRecords, cobjRecords);
    }

    private static bool IsValid(IArmorGetter armor, List<string> excludedNames, List<FormKey> mustHave)
    {
        Report = [[], [], [], []];

        // invalid if found in the excluded records list by edid
        if (Settings.General.ExclByEdID && armor.EditorID!.IsExcluded(excludedNames, true))
        {
            if (Settings.Debug.ShowExcluded) 
            { 
                Report![0].Add($"found in the \"No patching\" list by EditorID (as {armor.EditorID})");
                ShowReport(armor, Report);
            }
            return false;
        }

        // invalid if has no name
        if (armor.Name == null) return false;

        // invalid if found in the excluded records list by name
        if (armor.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Report![0].Add($"found in the \"No patching\" list by name");
                ShowReport(armor, Report);
            }
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

    private static void PatchRecordNames(IArmorGetter armor, List<string> excludedNames)
    {
        if (armor.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded) Report![0].Add($"Found in the \"No renaming\" list");
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
                    if (!filterArr.Any(type => RecordData.ArmorType.ToString() == type.Replace(" ", "")))
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
            Report![3].Add($"Was renamed to {name}");
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
            Report![2].Add("The material name returned from the relevant \"materialOverrides\" rule should be a string.");
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
                    Report![1].Add("The relevant \"materialOverrides\" rule references a material from Creation Club's \"Saints and Seducers\"");
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
            Report![3].Add($"Armor rating modified: {armor.ArmorRating} -> {GetAsOverride(armor).ArmorRating}");
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

        Report![2].Add("Unable to determine the equip slot for the record.");
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
            if (materialId == null) Report![1].Add("The record has a \"materials\" rule for its name but no material keyword.");
            return factorInt;
        }

        if (factorNode != null && factorInt == null)
        {
            Report![2].Add("The armor value in the relevant \"materials\" rule should be a number.");
        }

        Report![2].Add("Unable to determine the material.");
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
            Report![2].Add("The multiplier value in the relevant \"armorModifiers\" rule should be a number.");
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
            if (filterArr.Length != 0 && !filterArr.Any(type => type == RecordData.ArmorType.ToString())) continue;

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
        if (addedFactions.Count > 0) Report![3].Add($"Faction keywords added: {string.Join(", ", addedFactions)}");
    }

    private static void ProcessClothing(IArmorGetter armor, List<string> excluded)
    {
        if (!Settings.Armor.NoClothingBreak)
        {
            //AddMeltdownRecipe(armor);
        }

        CreateDreamcloth(armor, excluded);
    }

    private static void CreateDreamcloth(IArmorGetter armor, List<string> excludedNames)
    {
        if (excludedNames.Count > 0
            && excludedNames.Any(name => armor.Name!.ToString()!.Contains(name)))
        {
            if (Settings.Debug.ShowExcluded) Report![0].Add($"Found in the \"No Dreamcloth variant\" list");
            return;
        }

        if (!armor.TemplateArmor.IsNull || RecordData.Unique)
        {
            Report![3].Add($"Cannot have a Dreamcloth variant due to being unique or having a template");
            return;
        }

        bool isModified = RecordData.Modified;

        string label = Settings.Armor.DreamclothLabel == "" ? $" [{"name_dcloth".GetT9n()}]" : Settings.Armor.DreamclothLabel;
        string newName = GetAsOverride(armor).Name!.ToString() + label;
        string newEdId = "RP_ARMO_" + armor.EditorID!.ToString();

        Armor newArmor = Executor.State!.PatchMod.Armors.DuplicateInAsNewRecord(GetAsOverride(armor));
        if (!isModified) Executor.State!.PatchMod.Armors.Remove(armor);

        newArmor.Name = newName;
        newArmor.EditorID = newEdId;
        newArmor.VirtualMachineAdapter = null;
        newArmor.Description = null;
        newArmor.Keywords!.Add(GetFormKey("skyre__ArmorDreamcloth", true));

        float priceMult = Settings.Armor.DreamclothPrice / 100f;
        newArmor.Value = (uint)(priceMult * newArmor.Value);

        List<IngredientsMap> ingredients = [
            new(Ingr: GetFormKey("SoulGemPettyFilled"), Qty: 2, Type: "SLGM"),
            new(Ingr: GetFormKey("LeatherStrips"),      Qty: 1, Type: "MISC"),
            new(Ingr: GetFormKey("WispWrappings"),      Qty: 1, Type: "INGR")
        ];

        if (newArmor.Keywords!.Contains(GetFormKey("ClothingBody", true)))
        {
            ingredients[0] = ingredients[0] with { Ingr = GetFormKey("SoulGemCommonFilled"), Qty = 1 };
            ingredients[1] = ingredients[1] with { Qty = 3 };
            ingredients[2] = ingredients[2] with { Qty = 2 };

            newArmor.Keywords!.Add(GetFormKey("skyre__DreamclothBody", true));
        }
        else if (newArmor.Keywords!.Contains(GetFormKey("ClothingHead", true)))
        {
            ingredients[0] = ingredients[0] with { Ingr = GetFormKey("SoulGemLesserFilled"), Qty = 1 };
            ingredients[1] = ingredients[1] with { Qty = 2 };
        }

        AddCraftingRecipe(GetAsOverride(newArmor), armor, GetFormKey("skyre_SMTWeavingMill"), ingredients);
        //AddMeltdownRecipe(newArmor, GetFormKey("WispWrappings"), []);
    }

    private static void AddCraftingRecipe(IArmorGetter newArmor, IArmorGetter oldArmor, FormKey perk, List<IngredientsMap> ingredients)
    {
        string newEdId = "RP_CRAFT_ARMO_" + oldArmor.EditorID!.ToString();
        ConstructibleObject cobj = Executor.State!.PatchMod.ConstructibleObjects.AddNew();

        cobj.EditorID = newEdId;
        cobj.Items = [];

        ContainerItem baseItem = new();
        baseItem.Item = oldArmor.ToNullableLink();
        ContainerEntry baseEntry = new();
        baseEntry.Item = baseItem;
        baseEntry.Item.Count = 1;
        cobj.Items.Add(baseEntry);

        foreach (var entry in ingredients)
        {
            ContainerItem newItem = new();

            switch (entry.Type)
            {
                case "SLGM":
                    newItem.Item = Executor.State!.LinkCache.Resolve<ISoulGemGetter>(entry.Ingr).ToNullableLink();
                    break;

                case "MISC":
                    newItem.Item = Executor.State!.LinkCache.Resolve<IMiscItemGetter>(entry.Ingr).ToNullableLink();
                    break;

                case "INGR":
                    newItem.Item = Executor.State!.LinkCache.Resolve<IIngredientGetter>(entry.Ingr).ToNullableLink();
                    break;
            }

            ContainerEntry newEntry = new();
            newEntry.Item = newItem;
            newEntry.Item.Count = entry.Qty;
            cobj.Items.Add(newEntry);
        }

        cobj.AddHasPerkCondition(perk);
        if (!Settings.Armor.ShowAllRecipes)
        {
            cobj.AddGetItemCountCondition(oldArmor.FormKey, CompareOperator.GreaterThanOrEqualTo, 0, -1); 
        }

        cobj.CreatedObject = newArmor.ToNullableLink();
        cobj.WorkbenchKeyword = Executor.State!.LinkCache.Resolve<IKeywordGetter>(GetFormKey("CraftingTanningRack")).ToNullableLink();
        cobj.CreatedObjectCount = 1;
    }

    // local patcher helpers
    private static FormKey GetFormKey(string id, bool local = false)
    {
        return (local ? LocalStatics! : Executor.Statics!).First(elem => elem.Id == id).Formkey;
    }

    private static Armor GetAsOverride(this IArmorGetter armor)
    {
        if (!RecordData.Modified) RecordData.Modified = true;
        return PatchedRecord?.FormKey != armor.FormKey ? Executor.State!.PatchMod.Armors.GetOrAddAsOverride(armor) : PatchedRecord;
    }

    private static void ShowReport(IArmorGetter armor, List<List<string>> msgList)
    {
        if (msgList[0].Count > 0) Log(armor, "~ INFO", msgList[0]);
        if (msgList[1].Count > 0) Log(armor, "> CAUTION", msgList[1]);
        if (msgList[2].Count > 0) Log(armor, "# ERROR", msgList[2]);

        if (!Settings.Debug.ShowVerboseData) return;

        string[] filter = Settings.Debug.VerboseDataFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (filter.Length > 0 && !filter.Any(value => armor.Name!.ToString()!.Contains(value.Trim()))) return;
        if (msgList[3].Count > 0) Log(armor, "+ REPORT", msgList[3]);
    }

    private static void Log(IArmorGetter armor, string prefix, List<string> messages)
    {
        if (Settings.Debug.ShowNonPlayable || !RecordData.NonPlayable)
        {
            string note = RecordData.NonPlayable ? " | NON-PLAYABLE" : "";
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

