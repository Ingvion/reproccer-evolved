using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using ReProccer.Utils;
using System.Text.Json.Nodes;

namespace ReProccer.Patchers;

public static class ArmorPatcher
{
    private static readonly Settings.AllSettings Settings = Executor.Settings!;
    private static readonly JsonObject Rules = Executor.Rules!["armor"]!.AsObject();
    private static readonly (List<DataMap> LightMaterials,
                             List<DataMap> AllMaterials,
                             List<DataMap> FactionBinds) Statics = BuildStaticsMap();

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
                && armor.Keywords!.Contains(GetFormKey("ArmorJewelry"))))
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
                ProcessRecipes(armor, blacklists[2]);
            }


            ShowReport(armor, Report);
        }
    }

    /// <summary>
    /// Modifies the fArmorScalingFactor and fMaxArmorRating game settings.
    /// </summary>
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

    /// <summary>
    /// Records loader.
    /// </summary>
    /// <returns>The list of armor records eligible for patching.</returns>
    private static List<IArmorGetter> GetRecords()
    {
        IEnumerable<IArmorGetter> armoWinners = Executor.State!.LoadOrder.PriorityOrder
            .Where(plugin => !Settings.General.IgnoredFiles.Any(name => name == plugin.ModKey.FileName))
            .Where(plugin => plugin.Enabled)
            .WinningOverrides<IArmorGetter>();

        Console.WriteLine($"~~~ {armoWinners.Count()} armor records found, filtering... ~~~\n\n"
            + "====================");

        List<IArmorGetter> armoRecords = [];

        List<string> excludedNames = [.. Rules["excludedArmor"]!.AsArray().Select(value => value!.GetValue<string>())];
        List<FormKey> mustHave = [
            GetFormKey("ArmorHeavy"),
            GetFormKey("ArmorLight"),
            GetFormKey("ArmorShield"),
            GetFormKey("ArmorClothing"),
            GetFormKey("ArmorJewelry")
];
        foreach (var record in armoWinners)
        {
            if (IsValid(record, excludedNames, mustHave)) armoRecords.Add(record);
        }

        Console.WriteLine($"\n~~~ {armoRecords.Count} armor records are eligible for patching ~~~\n\n"
            + "====================");
        return armoRecords;
    }

    /// <summary>
    /// Checks if the armor matches necessary conditions to be patched.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    /// <param name="excludedNames">The list of keywords formkeys of which at least one must be present on armor.</param>
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

    /// <summary>
    /// Renames any type of armor records according to the rules.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
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

    /// <summary>
    /// Modifies armors materials according to the rules.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
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
        foreach (var entry1 in Statics.AllMaterials)
        {
            string id = entry1.Id.ToString();
            id = id.GetT9n();

            if (id == overrideString)
            {
                if (entry1.Kwda == nullRef)
                {
                    Report![1].Add("The relevant \"materialOverrides\" rule references a material from Creation Club's \"Saints and Seducers\"");
                    break;
                }

                foreach (var entry2 in Statics.AllMaterials)
                {
                    if (armor.Keywords!.Contains((FormKey)entry2.Kwda!) && entry2.Kwda != entry1.Kwda)
                    {
                        GetAsOverride(armor).Keywords!.Remove((FormKey)entry2.Kwda);
                    }
                }

                GetAsOverride(armor).Keywords!.Add((FormKey)entry1.Kwda!);
                RecordData.Overridden = true;
                break;
            }
        }
    }

    /// <summary>
    /// Distributes shield weight keywords and modifies shields' impact data set and material type.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    /// <param name="armorType">The armor type as enum.</param>
    private static void PatchShieldWeight(IArmorGetter armor, ArmorType armorType)
    {
        if (!armor.Keywords!.Contains(GetFormKey("ArmorShield"))) return;

        if (armorType == ArmorType.HeavyArmor && !armor.Keywords!.Contains(GetFormKey("skyre__ArmorShieldHeavy")))
        {
            GetAsOverride(armor).Keywords!.Add(GetFormKey("skyre__ArmorShieldHeavy"));
            GetAsOverride(armor).BashImpactDataSet = new FormLinkNullable<IImpactDataSetGetter>(GetFormKey("WPNBashShieldHeavyImpactSet"));
            GetAsOverride(armor).AlternateBlockMaterial = new FormLinkNullable<IMaterialTypeGetter>(GetFormKey("MaterialShieldHeavy"));
        }
        else if (armorType == ArmorType.LightArmor && !armor.Keywords!.Contains(GetFormKey("skyre__ArmorShieldLight")))
        {
            GetAsOverride(armor).Keywords!.Add(GetFormKey("skyre__ArmorShieldLight"));
            GetAsOverride(armor).BashImpactDataSet = new FormLinkNullable<IImpactDataSetGetter>(GetFormKey("WPNBashShieldLightImpactSet"));
            GetAsOverride(armor).AlternateBlockMaterial = new FormLinkNullable<IMaterialTypeGetter>(GetFormKey("MaterialShieldLight"));
        }
    }

    /// <summary>
    /// Modifies the armor rating based on other methods' results.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
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

    /// <summary>
    /// Returns the armor rating slot modifier according to the settings.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    /// <returns>Armor rating slot modifier as <see cref="float"/>, or null if the armor has no slot keyword.</returns>
    private static float? GetSlotFactor(IArmorGetter armor)
    {
        if (armor.Keywords!.Contains(GetFormKey("ArmorSlotBoots")))
        {
            return Settings.Armor.SlotBoots;
        }
        else if (armor.Keywords!.Contains(GetFormKey("ArmorSlotCuirass")))
        {
            return Settings.Armor.SlotCuirass;
        }
        else if (armor.Keywords!.Contains(GetFormKey("ArmorSlotGauntlets")))
        {
            return Settings.Armor.SlotGauntlets;
        }
        else if (armor.Keywords!.Contains(GetFormKey("ArmorSlotHelmet")))
        {
            return Settings.Armor.SlotHelmet;
        }
        else if (armor.Keywords!.Contains(GetFormKey("ArmorShield")))
        {
            return Settings.Armor.SlotShield;
        }

        Report![2].Add("Unable to determine the equip slot for the record.");
        return null;
    }

    /// <summary>
    /// Returns the armor rating material modifier according to the rules.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    /// <returns>Armor rating material modifier as <see cref="int"/>, or null if there's no rule or value has incorrect type.</returns>
    private static int? GetMaterialFactor(IArmorGetter armor)
    {
        string? materialId = null;
        FormKey nullRef = new("Skyrim.esm", 0x000000);
        foreach (var entry in Statics.AllMaterials)
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

    /// <summary>
    /// Returns the armor rating multipier according to the rules.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    /// <returns>Armor rating multipier as <see cref="float"/>.</returns>
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

    /// <summary>
    /// Adds faction keywords for the Masquerade perk to clothig in accordance to the rules.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
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
            foreach (var entry in Statics.FactionBinds)
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

    /// <summary>
    /// Recipes processor.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    /// <param name="allRecipes">List of all constructible object records.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    private static void ProcessRecipes(IArmorGetter armor, List<string> excludedNames)
    {
        if (RecordData.Modified) armor = GetAsOverride(armor);

        if (armor.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
                Report![0].Add($"Found in the \"No recipe modifications\" list");

            return;
        }

        foreach (var cobj in Executor.AllRecipes!)
        {
            if (cobj.CreatedObject.FormKey == armor.FormKey)
            {
                if (Settings.Armor.FixCraftRecipes && cobj.WorkbenchKeyword.FormKey == GetFormKey("CraftingSmithingForge"))
                    ModCraftingRecipe(cobj, armor);

                if (cobj.WorkbenchKeyword.FormKey == GetFormKey("CraftingSmithingArmorTable"))
                    ModTemperingRecipe(cobj, armor);
            }
        }

        AddBreakdownRecipe(armor);
    }

    /// <summary>
    /// Modifies crafting recipes for the armor.<br/><br/>
    /// The method adds the HasPerk-type condition with the Leathercraft perk to the crafting recipe<br/>
    /// if the armor have keywords with this perk associated with them (see the materials map below).<br/> 
    /// Only crafting recipes with no HasPerk-type conditions with other smithing perks will be modified. 
    /// </summary>
    /// <param name="recipe">The tempering recipe record as IConstructibleObjectGetter.</param>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    private static void ModCraftingRecipe(IConstructibleObjectGetter recipe, IArmorGetter armor)
    {
        foreach (var material in Statics.LightMaterials)
        {
            if (recipe.Conditions.Count != 0)
            {
                if (material.Perk != null
                    && recipe.Conditions.Any(condition => condition.Data is HasPerkConditionData hasPerk
                    && material.Perk.Any(perk => hasPerk.Perk.Link.FormKey == perk)))
                {
                    return;
                }
            }
        }

        bool isLeather = false;
        foreach (var material in Statics.LightMaterials)
        {
            if (material.Perk != null
                && material.Perk[0] == GetFormKey("skyre_SMTLeathercraft")
                && armor.Keywords!.Contains((FormKey)material.Kwda!))
            {
                isLeather = true;
                break;
            }
        }

        if (isLeather)
        {
            ConstructibleObject cobj = Executor.State!.PatchMod.ConstructibleObjects.GetOrAddAsOverride(recipe);
            cobj.AddHasPerkCondition(GetFormKey("skyre_SMTLeathercraft"));
        }
    }

    /// <summary>
    /// Modifies tempering recipes for the armor.<br/><br/>
    /// The method removes existing HasPerk-type conditions, where perk is a smithing perk, and adds<br/>
    /// new ones corresponding to the armor's keywords. If more than 1 perk is associated with a keyword,<br/>
    /// all but the last HasPerk-type conditions will have the OR flag.
    /// </summary>
    /// <param name="recipe">The tempering recipe record as IConstructibleObjectGetter.</param>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    private static void ModTemperingRecipe(IConstructibleObjectGetter recipe, IArmorGetter armor)
    {
        if (recipe.Conditions.Count != 0 && recipe.Conditions.Any(condition => condition.Data is EPTemperingItemIsEnchantedConditionData))
        {
            List<FormKey> allPerks = [.. Statics.AllMaterials
                .Where(entry => entry.Perk != null)
                .SelectMany(entry => entry.Perk!)
                .Distinct()];
            List<FormKey> materialPerks = [.. Statics.AllMaterials
                .Where(entry => armor.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
                .Where(entry => entry.Perk != null)
                .SelectMany(entry => entry.Perk!)
                .Distinct()];
            List<FormKey> materialItems = [.. Statics.AllMaterials
                .Where(entry => armor.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
                .Select(entry => (FormKey)entry.Item!)
                .Distinct()];


            int index = materialItems.IndexOf(GetFormKey("LeatherStrips"));
            if (index != -1)
            {
                materialItems.RemoveAt(index);
                materialItems.Insert(index, GetFormKey("Leather01"));
            }

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
    /// Generates a breakdown recipe for the armor.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    /// <param name="allRecipes">The list of all recipes as ConstructibleObject (could be null).</param>
    private static void AddBreakdownRecipe(IArmorGetter armor, bool newRecord = false)
    {
        if (RecordData.Unique)
        {
            Report![3].Add($"Cannot have a breakdown recipe due to the \"No breakdown\" keyword");
            return;
        }

        IConstructibleObjectGetter? craftingRecipe = null;
        if (!newRecord)
        {
            foreach (var recipe in Executor.AllRecipes!)
            {
                if (Settings.General.SkipExisting
                    && recipe.Items?.FirstOrDefault()?.Item == armor
                    && (recipe.WorkbenchKeyword.FormKey == GetFormKey("CraftingTanningRack")
                    || recipe.WorkbenchKeyword.FormKey == GetFormKey("CraftingSmelter")))
                {
                    Report![3].Add($"Already has a breakdown recipe in the {recipe.FormKey.ModKey.FileName}");
                    return;
                }

                if (craftingRecipe == null
                    && recipe.CreatedObject.FormKey == armor.FormKey
                    && (recipe.WorkbenchKeyword.FormKey == GetFormKey("CraftingTanningRack")
                    || recipe.WorkbenchKeyword.FormKey == GetFormKey("CraftingSmithingForge")))
                {
                    craftingRecipe = recipe;
                }
            }
        }

        List<FormKey> armorPerks = [.. Statics.AllMaterials
            .Where(entry => armor.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
            .Where(entry => entry.Perk != null)
            .SelectMany(entry => entry.Perk!)
            .Distinct()];
        List<FormKey> armorItems = [.. Statics.AllMaterials
            .Where(entry => armor.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
            .Select(entry => (FormKey)entry.Item!)
            .Distinct()];

        if (armorItems.Count == 0)
        {
            Report![1].Add($"Unable to determine the breakdown recipe resulting item");
            return;
        }

        bool isBig = armor.BodyTemplate != null
            && (armor.BodyTemplate.FirstPersonFlags.HasFlag(BipedObjectFlag.Body) || armor.BodyTemplate.FirstPersonFlags.HasFlag(BipedObjectFlag.Shield));

        bool isClothing = RecordData.ArmorType == ArmorType.Clothing && !armor.Keywords!.Contains(GetFormKey("skyre__ArmorDreamcloth"));

        bool isLeather = armorItems.Contains(GetFormKey("LeatherStrips")) && !armor.Keywords!.Contains(GetFormKey("skyre__ArmorDreamcloth"));
        if (isLeather)
        {
            int index = armorItems.IndexOf(GetFormKey("LeatherStrips"));
            if (index != -1)
            {
                armorItems.RemoveAt(index);
                armorItems.Insert(index, GetFormKey("Leather01"));
            }
        }

        bool fromRecipe = false;
        int qty = 1;
        FormKey ingr = armorItems[0];
        if (craftingRecipe != null)
        {
            foreach (var entry in armorItems)
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

        int mod = (isClothing ? 2 : 0) + (isLeather ? 2 : 0) + (isBig ? 1 : 0);
        float outputQty = (qty + mod) * (Settings.Armor.RefundAmount / 100f);
        int inputQty = (int)(outputQty < 1 && fromRecipe ? Math.Round(1 / outputQty) : 1);

        string newEdId = "RP_BREAK_ARMO_" + armor.EditorID;
        ConstructibleObject cobj = Executor.State!.PatchMod.ConstructibleObjects.AddNew();

        cobj.EditorID = newEdId;
        cobj.Items = [];

        ContainerItem newItem = new();
        newItem.Item = armor.ToNullableLink();
        ContainerEntry newEntry = new();
        newEntry.Item = newItem;
        newEntry.Item.Count = inputQty;
        cobj.Items.Add(newEntry);

        cobj.AddHasPerkCondition(GetFormKey("skyre_SMTBreakdown"));
        if (armorPerks.Count > 0)
        {
            Condition.Flag flag = Condition.Flag.OR;
            foreach (var perk in armorPerks)
            {
                if (armorPerks.IndexOf(perk) == armorPerks.Count - 1) flag = 0;
                cobj.AddHasPerkCondition(perk, flag);
            }
        }
        cobj.AddGetItemCountCondition(armor.FormKey, CompareOperator.GreaterThanOrEqualTo);
        cobj.AddGetEquippedCondition(armor.FormKey, CompareOperator.NotEqualTo);

        cobj.CreatedObject = Executor.State!.LinkCache.Resolve<IMiscItemGetter>(ingr).ToNullableLink();
        cobj.WorkbenchKeyword = Executor.State!.LinkCache.Resolve<IKeywordGetter>
            (isLeather || isClothing ? GetFormKey("CraftingTanningRack") : GetFormKey("CraftingSmelter"))
            .ToNullableLink();
        cobj.CreatedObjectCount = (ushort)Math.Clamp(Math.Floor(outputQty), 1, qty + (fromRecipe ? 0 : mod));
    }

    /// <summary>
    /// Clothing processor.<br/>
    /// </summary>
    private static void ProcessClothing(IArmorGetter armor, List<string> excludedNames)
    {
        if (!Settings.Armor.NoClothingBreak) AddBreakdownRecipe(armor, true);

        CreateDreamcloth(armor, excludedNames);
    }

    /// <summary>
    /// Generates the Dreamcloth variant for the armor.<br/>
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    private static void CreateDreamcloth(IArmorGetter armor, List<string> excludedNames)
    {
        if (armor.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded) Report![0].Add($"Found in the \"No Dreamcloth variant\" list");
            return;
        }

        if (!armor.TemplateArmor.IsNull || RecordData.Unique)
        {
            Report![3].Add($"Cannot have a Dreamcloth variant due to having a template or \"No breakdown\" keyword");
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
        newArmor.Keywords!.Add(GetFormKey("skyre__ArmorDreamcloth"));

        float priceMult = Settings.Armor.DreamclothPrice / 100f;
        newArmor.Value = (uint)(priceMult * newArmor.Value);

        List<IngredientsMap> ingredients = [
            new(Ingr: GetFormKey("SoulGemPettyFilled"), Qty: 2, Type: "SLGM"),
            new(Ingr: GetFormKey("LeatherStrips"),      Qty: 1, Type: "MISC"),
            new(Ingr: GetFormKey("WispWrappings"),      Qty: 1, Type: "INGR")
        ];

        if (newArmor.Keywords!.Contains(GetFormKey("ClothingBody")))
        {
            ingredients[0] = ingredients[0] with { Ingr = GetFormKey("SoulGemCommonFilled"), Qty = 1 };
            ingredients[1] = ingredients[1] with { Qty = 3 };
            ingredients[2] = ingredients[2] with { Qty = 2 };

            newArmor.Keywords!.Add(GetFormKey("skyre__DreamclothBody"));
        }
        else if (newArmor.Keywords!.Contains(GetFormKey("ClothingHead")))
        {
            ingredients[0] = ingredients[0] with { Ingr = GetFormKey("SoulGemLesserFilled"), Qty = 1 };
            ingredients[1] = ingredients[1] with { Qty = 2 };
        }

        AddCraftingRecipe(GetAsOverride(newArmor), armor, ingredients);
        //AddBreakdownRecipe(newArmor, GetFormKey("WispWrappings"), []);
    }

    /// <summary>
    /// Generates the crafting recipe for the Dreamcloth variant (newArmor).<br/>
    /// </summary>
    /// <param name="newArmor">The Dreamcloth variant of the oldArmor as IArmorGetter.</param>
    /// <param name="oldArmor">The armor record as IArmorGetter.</param>
    /// <param name="ingredients">List of ingredients and their quantity.</param>
    private static void AddCraftingRecipe(IArmorGetter newArmor, IArmorGetter oldArmor, List<IngredientsMap> ingredients)
    {
        string newEdId = "RP_CRAFT_ARMO_" + oldArmor.EditorID;
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

        cobj.AddHasPerkCondition(GetFormKey("skyre_SMTWeavingMill"));
        if (!Settings.Armor.ShowAllRecipes)
        {
            cobj.AddGetItemCountCondition(oldArmor.FormKey, CompareOperator.GreaterThanOrEqualTo);
        }

        cobj.CreatedObject = newArmor.ToNullableLink();
        cobj.WorkbenchKeyword = Executor.State!.LinkCache.Resolve<IKeywordGetter>(GetFormKey("CraftingTanningRack")).ToNullableLink();
        cobj.CreatedObjectCount = 1;
    }

    // local patcher helpers

    /// <summary>
    /// Returns the FormKey with id from the statics record.<br/>
    /// </summary>
    /// <param name="id">The id in the elements with the FormKey to return.</param>
    /// <returns>A FormKey from the statics list.</returns>
    private static FormKey GetFormKey(string id) => Executor.Statics!.First(elem => elem.Id == id).Formkey;

    /// <summary>
    /// Returns the winning override for this-parameter, and copies it to the patch file.<br/>
    /// Marks it as modified in local record data.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    /// <returns>The winning override as Armor.</returns>
    private static Armor GetAsOverride(this IArmorGetter armor)
    {
        if (!RecordData.Modified) RecordData.Modified = true;
        return PatchedRecord?.FormKey != armor.FormKey ? Executor.State!.PatchMod.Armors.GetOrAddAsOverride(armor) : PatchedRecord;
    }

    /// <summary>
    /// Displays info and errors.<br/>
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    /// <param name="msgList">The list of list of strings with messages.</param>
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
            foreach (var msg in messages)
            {
                Console.WriteLine($"--- {msg}");
            }
            Console.WriteLine("====================");
        }
    }

    // armor patcher statics
    private static (List<DataMap>, List<DataMap>, List<DataMap>) BuildStaticsMap()
    {
        Executor.Statics!.AddRange(
        [
            new(Id: "ArmorHeavy",                             Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06bbd2|KWDA")                  ),
            new(Id: "ArmorLight",                             Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06bbd3|KWDA")                  ),
            new(Id: "ArmorClothing",                          Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06bbe8|KWDA")                  ),
            new(Id: "ArmorJewelry",                           Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06bbe9|KWDA")                  ),
            new(Id: "ArmorSlotCuirass",                       Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06c0ec|KWDA")                  ),
            new(Id: "ArmorSlotBoots",                         Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06c0ed|KWDA")                  ),
            new(Id: "ArmorSlotHelmet",                        Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06c0ee|KWDA")                  ),
            new(Id: "ArmorSlotGauntlets",                     Formkey: Helpers.ParseFormKey("Skyrim.esm|0x06c0ef|KWDA")                  ),
            new(Id: "CraftingTanningRack",                    Formkey: Helpers.ParseFormKey("Skyrim.esm|0x07866a|KWDA")                  ),
            new(Id: "VendorItemArmor",                        Formkey: Helpers.ParseFormKey("Skyrim.esm|0x08f959|KWDA")                  ),
            new(Id: "VendorItemJewelry",                      Formkey: Helpers.ParseFormKey("Skyrim.esm|0x08f95a|KWDA")                  ),
            new(Id: "VendorItemClothing",                     Formkey: Helpers.ParseFormKey("Skyrim.esm|0x08f95b|KWDA")                  ),
            new(Id: "ArmorShield",                            Formkey: Helpers.ParseFormKey("Skyrim.esm|0x0965b2|KWDA")                  ),
            new(Id: "ClothingBody",                           Formkey: Helpers.ParseFormKey("Skyrim.esm|0x0a8657|KWDA")                  ),
            new(Id: "CraftingSmithingArmorTable",             Formkey: Helpers.ParseFormKey("Skyrim.esm|0x0adb78|KWDA")                  ),
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
            new(Id: "skyre_SMTWeavingMill",                   Formkey: Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000ee1|PERK") )
        ]);

        List<DataMap> allMaterials = [
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

        List<DataMap> lightMaterials = [
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

        lightMaterials.AddRange(allMaterials);
        List<DataMap> factionBinds = [
            new(Id: "fact_bandit",     Kwda: GetFormKey("skyre_SPCMasqueradeBandit")     ),
            new(Id: "fact_forsworn",   Kwda: GetFormKey("skyre_SPCMasqueradeForsworn")   ),
            new(Id: "fact_imperial",   Kwda: GetFormKey("skyre_SPCMasqueradeImperial")   ),
            new(Id: "fact_stormcloak", Kwda: GetFormKey("skyre_SPCMasqueradeStormcloak") ),
            new(Id: "fact_thalmor",    Kwda: GetFormKey("skyre_SPCMasqueradeThalmor")    )
        ];

        return (allMaterials, lightMaterials, factionBinds);
    }
}

