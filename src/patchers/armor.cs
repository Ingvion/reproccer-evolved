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
    private static readonly (List<DataMap> AllMaterials,
                             List<DataMap> LightMaterials,
                             List<DataMap> FactionBinds) Statics = BuildStaticsMap();

    private static EditorIDs EditorIDs;
    private static PatchingData RecordData;
    private static Logger Logger;

    public static void Run()
    {
        UpdateGMST();
        EditorIDs = new EditorIDs();

        List<IArmorGetter> records = GetRecords();
        List<List<string>> blacklists = [
            [.. Rules["excludedFromRenaming"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedDreamcloth"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedFromRecipes"]!.AsArray().Select(value => value!.GetValue<string>())]
        ];

        foreach (var armor in records)
        {
            RecordData = new PatchingData
            {
                ArmorType = armor.BodyTemplate!.ArmorType,
                NonPlayable = armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable),
                Unique = armor.Keywords!.Contains(GetFormKey("skyre__NoMeltdownRecipes"))
            };

            Logger = new Logger();

            if (!armor.TemplateArmor.IsNull || (RecordData.ArmorType == ArmorType.Clothing
                && armor.Keywords!.Contains(GetFormKey("ArmorJewelry"))))
            {
                PatchRecordNames(armor, blacklists[0]);
                armor.ShowReport();
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
                    armor.ShowReport();
                    continue;
                }
                ProcessRecipes(armor, blacklists[2]);
            }


            armor.ShowReport();
        }
    }

    /// <summary>
    /// Modifies the fArmorScalingFactor and fMaxArmorRating game settings.
    /// </summary>
    private static void UpdateGMST()
    {
        FormKey armorScalingFactor = new("Skyrim.esm", 0x021a72);

        var conflictWinner = Executor.State!.LinkCache.Resolve<IGameSettingGetter>(armorScalingFactor);
        GameSetting record = Executor.State!.PatchMod.GameSettings.GetOrAddAsOverride(conflictWinner);

        if (record is GameSettingFloat gmstArmorScalingFactor)
            gmstArmorScalingFactor.Data = Settings.Armor.ArmorScalingFactor;

        FormKey maxArmorRating = new("Skyrim.esm", 0x037deb);

        conflictWinner = Executor.State!.LinkCache.Resolve<IGameSettingGetter>(maxArmorRating);
        record = Executor.State!.PatchMod.GameSettings.GetOrAddAsOverride(conflictWinner);

        if (record is GameSettingFloat gmstMaxArmorRating) 
            gmstMaxArmorRating.Data = Settings.Armor.MaxArmorRating;
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
    /// <param name="mustHave">The list of keywords formkeys of which at least one must be present on armor.</param>
    /// <returns>Check result from a filter the record triggered as <see cref="bool"/>.</returns>
    private static bool IsValid(IArmorGetter armor, List<string> excludedNames, List<FormKey> mustHave)
    {
        Logger = new Logger();

        // invalid if found in the excluded records list by edid
        if (Settings.General.ExclByEdID && armor.EditorID!.IsExcluded(excludedNames, true))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Logger.Info($"Found in the \"No patching\" list by EditorID (as {armor.EditorID})");
                armor.ShowReport();
            }
            return false;
        }

        // invalid if has no name
        if (armor.Name is null) return false;

        // invalid if found in the excluded records list by name
        if (armor.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Logger.Info($"Found in the \"No patching\" list by name");
                armor.ShowReport();
            }
            return false;
        }

        // invalid if has no body template)
        if (armor.BodyTemplate is null) return false;

        // valid if has a template (to skip keyword checks below)
        if (!armor.TemplateArmor.IsNull) return true;

        // invalid if has no keywords or have empty kw array (rare)
        if (armor.Keywords is null || armor.Keywords.Count == 0) return false;

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
            if (Settings.Debug.ShowExcluded) Logger.Info($"Found in the \"No renaming\" list");
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
                    if (!filterArr.Any(type => RecordData.ArmorType.ToString() == type.Replace(" ", ""))) continue;
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
            Logger.Info($"Was renamed to {name} in accordance with patching rules", true);
            armor.AsOverride(true).Name = name;
        }
    }

    /// <summary>
    /// Modifies armors materials according to the rules.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    private static void SetOverriddenData(IArmorGetter armor)
    {
        JsonNode? overrideNode = 
            Helpers.RuleByName(armor.Name!.ToString()!, Rules["materialOverrides"]!.AsArray(), data1: "names", data2: "material");
        string? overrideString = overrideNode?.AsNullableType<string>();

        if (overrideString is null) return;

        foreach (var entry1 in Statics.AllMaterials)
        {
            string id = entry1.Id.GetT9n();
            if (id == overrideString)
            {
                if (entry1.Kwda == DataMap.NullRef)
                {
                    Logger.Caution("A \"materialOverrides\" patching rule references a material from Creation Club's \"Saints and Seducers\"");
                    break;
                }

                foreach (var entry2 in Statics.AllMaterials)
                {
                    if (armor.Keywords!.Contains(entry2.Kwda!) && entry2.Kwda != entry1.Kwda)
                        armor.AsOverride().Keywords!.Remove(entry2.Kwda);
                }

                armor.AsOverride(true).Keywords!.Add(entry1.Kwda!);
                Logger.Info($"The material was forced to {overrideString} in accordance with patching rules", true);
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
            armor.AsOverride(true).Keywords!.Add(GetFormKey("skyre__ArmorShieldHeavy"));
            armor.AsOverride().BashImpactDataSet = new FormLinkNullable<IImpactDataSetGetter>(GetFormKey("WPNBashShieldHeavyImpactSet"));
            armor.AsOverride().AlternateBlockMaterial = new FormLinkNullable<IMaterialTypeGetter>(GetFormKey("MaterialShieldHeavy"));
        }
        else if (armorType == ArmorType.LightArmor && !armor.Keywords!.Contains(GetFormKey("skyre__ArmorShieldLight")))
        {
            armor.AsOverride(true).Keywords!.Add(GetFormKey("skyre__ArmorShieldLight"));
            armor.AsOverride().BashImpactDataSet = new FormLinkNullable<IImpactDataSetGetter>(GetFormKey("WPNBashShieldLightImpactSet"));
            armor.AsOverride().AlternateBlockMaterial = new FormLinkNullable<IMaterialTypeGetter>(GetFormKey("MaterialShieldLight"));
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

        if (slotFactor is null || materialFactor is null) return;

        float extraMod = GetExtraArmorMod(armor);
        double newArmorRating = Math.Floor((float)slotFactor * (int)materialFactor * extraMod);

        if ((float)newArmorRating != armor.ArmorRating)
        {
            armor.AsOverride(true).ArmorRating = (float)newArmorRating;
            Logger.Info($"Armor rating: {armor.ArmorRating} -> {armor.AsOverride().ArmorRating}", true);
        }
    }

    /// <summary>
    /// Returns the armor rating slot modifier according to the settings.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    /// <returns>Armor rating slot modifier as <see cref="float"/>, or null if the armor has no slot keyword.</returns>
    private static float? GetSlotFactor(IArmorGetter armor)
    {
        if (armor.Keywords!.Contains(GetFormKey("ArmorSlotBoots"))) return Settings.Armor.SlotBoots;
        if (armor.Keywords!.Contains(GetFormKey("ArmorSlotCuirass"))) return Settings.Armor.SlotCuirass;
        if (armor.Keywords!.Contains(GetFormKey("ArmorSlotGauntlets"))) return Settings.Armor.SlotGauntlets;
        if (armor.Keywords!.Contains(GetFormKey("ArmorSlotHelmet"))) return Settings.Armor.SlotHelmet;
        if (armor.Keywords!.Contains(GetFormKey("ArmorShield"))) return Settings.Armor.SlotShield;

        Logger.Error("Unable to determine the equip slot for the record");
        return null;
    }

    /// <summary>
    /// Returns the armor rating material modifier according to the rules.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    /// <returns>Armor rating material modifier as <see cref="int"/>, or null if there's no rule or value has incorrect type.</returns>
    private static int? GetMaterialFactor(IArmorGetter armor)
    {
        if (RecordData.Overridden) armor = armor.AsOverride();

        string? materialId = null;
        foreach (var entry in Statics.AllMaterials)
        {
            FormKey kwda = entry.Kwda!;
            if (kwda != DataMap.NullRef && armor.Keywords!.Contains(kwda))
            {
                materialId = entry.Id;
                break;
            }
        }

        JsonNode? factorNode = Helpers.RuleByName(armor.Name!.ToString()!, Rules["materials"]!.AsArray(), data1: "names", data2: "armor");
        if (factorNode is null && materialId is not null) 
            factorNode = Helpers.RuleByName(materialId!, Rules["materials"]!.AsArray(), data1: "id", data2: "armor");
        int? factorInt = factorNode?.AsType<int>();

        if (factorInt is not null)
        {
            if (materialId is null) Logger.Caution("Has a \"materials\" patching rule for its name, but no material keyword");
            return factorInt;
        }

        Logger.Error("Unable to determine the material");
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
        float? modifierFloat = modifierNode?.AsType<float>();

        if (modifierFloat is not null)
            return (float)(modifierFloat > 0.0f ? modifierFloat : 1.0f);

        return 1.0f;
    }

    /// <summary>
    /// Adds faction keywords for the Masquerade perk to clothing according to the rules.
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
                if (factions.Contains(entry.Id.GetT9n()) && !armor.Keywords!.Contains(entry.Kwda!))
                {
                    armor.AsOverride(true).Keywords!.Add(entry.Kwda);
                    addedFactions.Add($"{entry.Id.GetT9n()}");
                }
            }
        }
        if (addedFactions.Count > 0) Logger.Info($"Faction keywords added: {string.Join(", ", addedFactions)}", true);
    }

    /// <summary>
    /// Recipes processor.
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    private static void ProcessRecipes(IArmorGetter armor, List<string> excludedNames)
    {
        if (RecordData.Modified) armor = armor.AsOverride();

        if (armor.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
                Logger.Info($"Found in the \"No recipe modifications\" list");

            return;
        }

        foreach (var recipe in Executor.AllRecipes!)
        {
            if (recipe.CreatedObject.FormKey == armor.FormKey)
            {
                if (Settings.Armor.FixCraftRecipes && recipe.WorkbenchKeyword.FormKey == GetFormKey("CraftingSmithingForge"))
                    ModCraftingRecipe(recipe, armor);

                if (recipe.WorkbenchKeyword.FormKey == GetFormKey("CraftingSmithingArmorTable"))
                    ModTemperingRecipe(recipe, armor);
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
                if (material.Perk is not null
                    && recipe.Conditions.Any(condition => condition.Data is HasPerkConditionData hasPerk
                    && material.Perk.Any(perk => hasPerk.Perk.Link.FormKey == perk)))
                {
                    return;
                }
            }
        }

        foreach (var material in Statics.LightMaterials)
        {
            if (material.Perk is not null
                && material.Perk[0] == GetFormKey("skyre_SMTLeathercraft")
                && armor.Keywords!.Contains(material.Kwda!))
            {
                ConstructibleObject craftingRecipe = Executor.State!.PatchMod.ConstructibleObjects.GetOrAddAsOverride(recipe);
                craftingRecipe.AddHasPerkCondition(GetFormKey("skyre_SMTLeathercraft"));

                break;
            }
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
                .Where(entry => entry.Perk is not null)
                .SelectMany(entry => entry.Perk!)
                .Distinct()];
            List<FormKey> materialPerks = [.. Statics.AllMaterials
                .Where(entry => armor.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
                .Where(entry => entry.Perk is not null)
                .SelectMany(entry => entry.Perk!)
                .Distinct()];
            List<FormKey> materialItems = [.. Statics.AllMaterials
                .Where(entry => armor.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
                .Select(entry => entry.Item!)
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
    /// <param name="noRecipes">True for armor-type records with no material keywords.</param>
    private static void AddBreakdownRecipe(IArmorGetter armor, bool noRecipes = false)
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
                    && recipe.Items?.FirstOrDefault()?.Item == armor
                    && (recipe.WorkbenchKeyword.FormKey == GetFormKey("CraftingTanningRack")
                    || recipe.WorkbenchKeyword.FormKey == GetFormKey("CraftingSmelter")))
                {
                    Logger.Info($"Already has a breakdown recipe in the {recipe.FormKey.ModKey.FileName}");
                    return;
                }

                if (craftingRecipe is null
                    && recipe.CreatedObject.FormKey == armor.FormKey
                    && (recipe.WorkbenchKeyword.FormKey == GetFormKey("CraftingTanningRack")
                    || recipe.WorkbenchKeyword.FormKey == GetFormKey("CraftingSmithingForge")))
                {
                    craftingRecipe = recipe;
                }
            }
        }

        bool isBig = armor.BodyTemplate!.FirstPersonFlags.HasFlag(BipedObjectFlag.Body) || armor.BodyTemplate!.FirstPersonFlags.HasFlag(BipedObjectFlag.Shield);
        bool isDreamcloth = armor.Keywords!.Contains(GetFormKey("skyre__ArmorDreamcloth"));
        bool isClothing = RecordData.ArmorType == ArmorType.Clothing && !isDreamcloth;

        List<FormKey> armorPerks = !isClothing && !isDreamcloth ? [.. Statics.AllMaterials
            .Where(entry => armor.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
            .Where(entry => entry.Perk is not null)
            .SelectMany(entry => entry.Perk!)
            .Distinct()] : [];
        List<FormKey> armorItems = !isClothing && !isDreamcloth ? [.. Statics.AllMaterials
            .Where(entry => armor.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
            .Select(entry => entry.Item!)
            .Distinct()] : [GetFormKey("LeatherStrips")];

        if (!isClothing && !isDreamcloth && armorItems.Count == 0)
        {
            Logger.Error($"Unable to determine the breakdown recipe resulting item");
            return;
        }

        if (armorItems.IndexOf(GetFormKey("LeatherStrips")) is int index && index != -1)
        {
            if (isDreamcloth)
            {
                armorItems.RemoveAt(index);
                armorItems.Insert(index, GetFormKey("WispWrappings"));
            }
            else if (isBig)
            {
                armorItems.RemoveAt(index);
                armorItems.Insert(index, GetFormKey("Leather01"));
            }
        }

        bool isLeather = armorItems.Contains(GetFormKey("Leather01")) || armorItems.Contains(GetFormKey("LeatherStrips"));

        bool fromRecipe = false;
        int qty = 1;
        FormKey ingr = armorItems[0];
        if (craftingRecipe is not null)
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

        int mod = (isClothing ? 1 : 0) + (isLeather ? 2 : 0) + (isBig ? 1 : 0);
        float outputQty = (qty + mod) * (Settings.Armor.RefundAmount / 100f);
        int inputQty = (int)(outputQty < 1 && fromRecipe ? Math.Round(1 / outputQty) : 1);

        string newEditorID = "RP_ARMO_BREAK_" + (isDreamcloth ? armor.EditorID!.Replace("RP_ARMO_", "") : armor.EditorID);
        ConstructibleObject cobj = Executor.State!.PatchMod.ConstructibleObjects.AddNew();

        cobj.EditorID = EditorIDs.Unique(newEditorID);
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

        cobj.CreatedObject = Executor.State!.LinkCache.TryResolve<IMiscItemGetter>(ingr, out var ingrItem) ? ingrItem.ToNullableLink() 
                           : Executor.State!.LinkCache.Resolve<IIngredientGetter>(ingr).ToNullableLink();

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
            if (Settings.Debug.ShowExcluded)
            {
                Logger.Info($"Found in the \"No Dreamcloth variant\" list");
                return;
            }
        }

        if (!armor.TemplateArmor.IsNull || RecordData.Unique)
        {
            Logger.Info($"The Dreamcloth variant was not generated due to having a template or \"No breakdown\" keyword", true);
            return;
        }

        string label = Settings.Armor.DreamclothLabel == "" ? $" [{"name_dcloth".GetT9n()}]" : Settings.Armor.DreamclothLabel;
        string newName = armor.AsOverride().Name!.ToString() + label;
        string newEditorID = "RP_ARMO_" + armor.EditorID;

        Armor newArmor = Executor.State!.PatchMod.Armors.DuplicateInAsNewRecord(armor.AsOverride());
        if (!RecordData.Modified) Executor.State!.PatchMod.Armors.Remove(armor);

        newArmor.Name = newName;
        newArmor.EditorID = EditorIDs.Unique(newEditorID);
        newArmor.VirtualMachineAdapter = null;
        newArmor.Description = null;
        newArmor.Keywords!.Add(GetFormKey("skyre__ArmorDreamcloth"));

        float priceMult = Settings.Armor.DreamclothPrice / 100f;
        newArmor.Value = (uint)(priceMult * newArmor.Value);

        List<DataMap> ingredients = [
            new DataMap{Ingr = GetFormKey("SoulGemPettyFilled"), Qty = 2, Id = "SLGM"},
            new DataMap{Ingr = GetFormKey("LeatherStrips"),      Qty = 1, Id = "MISC"},
            new DataMap{Ingr = GetFormKey("WispWrappings"),      Qty = 1, Id = "INGR"}
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

        AddCraftingRecipe(newArmor, armor, ingredients);
        AddBreakdownRecipe(newArmor, true);
    }

    /// <summary>
    /// Generates the crafting recipe for the Dreamcloth variant (newArmor).<br/>
    /// </summary>
    /// <param name="newArmor">The Dreamcloth variant of the oldArmor as IArmorGetter.</param>
    /// <param name="oldArmor">The armor record as IArmorGetter.</param>
    /// <param name="ingredients">List of ingredients and their quantity.</param>
    private static void AddCraftingRecipe(IArmorGetter newArmor, IArmorGetter oldArmor, List<DataMap> ingredients)
    {
        string newEditorID = "RP_ARMO_CRAFT_" + oldArmor.EditorID;
        ConstructibleObject cobj = Executor.State!.PatchMod.ConstructibleObjects.AddNew();

        cobj.EditorID = EditorIDs.Unique(newEditorID);
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

            switch (entry.Id)
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
        if (!Settings.Armor.AllArmorRecipes) 
            cobj.AddGetItemCountCondition(oldArmor.FormKey, CompareOperator.GreaterThanOrEqualTo);

        cobj.AddGetEquippedCondition(oldArmor.FormKey, CompareOperator.NotEqualTo);

        cobj.CreatedObject = newArmor.ToNullableLink();
        cobj.WorkbenchKeyword = Executor.State!.LinkCache.Resolve<IKeywordGetter>(GetFormKey("CraftingTanningRack")).ToNullableLink();
        cobj.CreatedObjectCount = 1;
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
    /// <param name="armor">The armor record as IArmorGetter.</param>
    /// <param name="markModified">True to mark as modified in the patching data.</param>
    /// <returns>The winning override as Armor.</returns>
    private static Armor AsOverride(this IArmorGetter armor, bool markModified = false)
    {
        if (markModified) RecordData.Modified = true;
        return Executor.State!.PatchMod.Armors.GetOrAddAsOverride(armor);
    }

    /// <summary>
    /// Displays info.<br/>
    /// </summary>
    /// <param name="armor">The armor record as IArmorGetter.</param>
    private static void ShowReport(this IArmorGetter armor) => 
        Logger.ShowReport($"{armor.Name}", $"{armor.FormKey}", $"{armor.EditorID}", RecordData.NonPlayable, !armor.TemplateArmor.IsNull);

    // patcher specific statics
    private static (List<DataMap>, List<DataMap>, List<DataMap>) BuildStaticsMap()
    {
        Executor.Statics!.AddRange(
        [
            new DataMap{Id = "ArmorHeavy",                             FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbd2|KWDA")                  },
            new DataMap{Id = "ArmorLight",                             FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbd3|KWDA")                  },
            new DataMap{Id = "ArmorClothing",                          FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbe8|KWDA")                  },
            new DataMap{Id = "ArmorJewelry",                           FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbe9|KWDA")                  },
            new DataMap{Id = "ArmorSlotCuirass",                       FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06c0ec|KWDA")                  },
            new DataMap{Id = "ArmorSlotBoots",                         FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06c0ed|KWDA")                  },
            new DataMap{Id = "ArmorSlotHelmet",                        FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06c0ee|KWDA")                  },
            new DataMap{Id = "ArmorSlotGauntlets",                     FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06c0ef|KWDA")                  },
            new DataMap{Id = "CraftingTanningRack",                    FormKey = Helpers.ParseFormKey("Skyrim.esm|0x07866a|KWDA")                  },
            new DataMap{Id = "VendorItemArmor",                        FormKey = Helpers.ParseFormKey("Skyrim.esm|0x08f959|KWDA")                  },
            new DataMap{Id = "VendorItemJewelry",                      FormKey = Helpers.ParseFormKey("Skyrim.esm|0x08f95a|KWDA")                  },
            new DataMap{Id = "VendorItemClothing",                     FormKey = Helpers.ParseFormKey("Skyrim.esm|0x08f95b|KWDA")                  },
            new DataMap{Id = "ArmorShield",                            FormKey = Helpers.ParseFormKey("Skyrim.esm|0x0965b2|KWDA")                  },
            new DataMap{Id = "ClothingBody",                           FormKey = Helpers.ParseFormKey("Skyrim.esm|0x0a8657|KWDA")                  },
            new DataMap{Id = "CraftingSmithingArmorTable",             FormKey = Helpers.ParseFormKey("Skyrim.esm|0x0adb78|KWDA")                  },
            new DataMap{Id = "ClothingHead",                           FormKey = Helpers.ParseFormKey("Skyrim.esm|0x10cd11|KWDA")                  },
            new DataMap{Id = "MaterialShieldLight",                    FormKey = Helpers.ParseFormKey("Skyrim.esm|0x016978|KWDA", true)            },
            new DataMap{Id = "MaterialShieldHeavy",                    FormKey = Helpers.ParseFormKey("Skyrim.esm|0x016979|KWDA", true)            },
            new DataMap{Id = "WPNBashShieldLightImpactSet",            FormKey = Helpers.ParseFormKey("Skyrim.esm|0x0183fb|KWDA", true)            },
            new DataMap{Id = "WPNBashShieldHeavyImpactSet",            FormKey = Helpers.ParseFormKey("Skyrim.esm|0x0183fe|KWDA", true)            },
            new DataMap{Id = "skyre__ArmorShieldHeavy",                FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00080f|KWDA") },
            new DataMap{Id = "skyre__ArmorShieldLight",                FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000810|KWDA") },
            new DataMap{Id = "skyre__ArmorDreamcloth",                 FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000811|KWDA") },
            new DataMap{Id = "skyre__DreamclothBody",                  FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00098d|KWDA") },
            new DataMap{Id = "skyre_SPCMasqueradeBandit",              FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000f44|KWDA") },
            new DataMap{Id = "skyre_SPCMasqueradeForsworn",            FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000f45|KWDA") },
            new DataMap{Id = "skyre_SPCMasqueradeImperial",            FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000f46|KWDA") },
            new DataMap{Id = "skyre_SPCMasqueradeStormcloak",          FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000f47|KWDA") },
            new DataMap{Id = "skyre_SPCMasqueradeThalmor",             FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000f48|KWDA") },
            new DataMap{Id = "skyre_SMTLeathercraft",                  FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000ec4|PERK") },
            new DataMap{Id = "skyre_SMTWeavingMill",                   FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000ee1|PERK") }
        ]);

        List<DataMap> allMaterials = [
            new DataMap{Id = "mat_ancientnord", Kwda = GetFormKey("WAF_ArmorMaterialDraugr"),              Item = GetFormKey("IngotCorundum"),    Perk = [ GetFormKey("AdvancedArmors") ]                             },
            new DataMap{Id = "mat_blades",      Kwda = GetFormKey("ArmorMaterialBlades"),                  Item = GetFormKey("IngotSteel"),       Perk = [ GetFormKey("SteelSmithing") ]                              },
            new DataMap{Id = "mat_bonemoldh",   Kwda = GetFormKey("DLC2ArmorMaterialBonemoldHeavy"),       Item = GetFormKey("DLC2NetchLeather"), Perk = [ GetFormKey("AdvancedArmors") ]                             },
            new DataMap{Id = "mat_chitinh",     Kwda = GetFormKey("DLC2ArmorMaterialChitinHeavy"),         Item = GetFormKey("DLC2ChitinPlate"),  Perk = [ GetFormKey("ElvenSmithing") ]                              },
            new DataMap{Id = "mat_daedric",     Kwda = GetFormKey("ArmorMaterialDaedric"),                 Item = GetFormKey("IngotEbony"),       Perk = [ GetFormKey("DaedricSmithing") ]                            },
            new DataMap{Id = "mat_dawnguard",   Kwda = GetFormKey("DLC1ArmorMaterialDawnguard"),           Item = GetFormKey("IngotSteel"),       Perk = [ GetFormKey("SteelSmithing") ]                              },
            new DataMap{Id = "mat_dawnguardh",  Kwda = GetFormKey("DLC1ArmorMaterialHunter"),              Item = GetFormKey("IngotSteel"),       Perk = [ GetFormKey("SteelSmithing") ]                              },
            new DataMap{Id = "mat_dragonplate", Kwda = GetFormKey("ArmorMaterialDragonplate"),             Item = GetFormKey("DragonBone"),       Perk = [ GetFormKey("DragonArmor") ]                                },
            new DataMap{Id = "mat_dwarven",     Kwda = GetFormKey("ArmorMaterialDwarven"),                 Item = GetFormKey("IngotDwarven"),     Perk = [ GetFormKey("DwarvenSmithing") ]                            },
            new DataMap{Id = "mat_ebony",       Kwda = GetFormKey("ArmorMaterialEbony"),                   Item = GetFormKey("IngotEbony"),       Perk = [ GetFormKey("EbonySmithing") ]                              },
            new DataMap{Id = "mat_falmerhr",    Kwda = GetFormKey("DLC1ArmorMaterialFalmerHardened"),      Item = GetFormKey("ChaurusChitin"),    Perk = [ GetFormKey("ElvenSmithing") ]                              },
            new DataMap{Id = "mat_falmerhv",    Kwda = GetFormKey("DLC1ArmorMaterialFalmerHeavy"),         Item = GetFormKey("ChaurusChitin"),    Perk = [ GetFormKey("ElvenSmithing") ]                              },
            new DataMap{Id = "mat_falmer",      Kwda = GetFormKey("DLC1ArmorMaterialFalmerHeavyOriginal"), Item = GetFormKey("ChaurusChitin"),    Perk = [ GetFormKey("ElvenSmithing") ]                              },
            new DataMap{Id = "mat_golden",      Kwda = GetFormKey("cc_ArmorMaterialGolden"),               Item = GetFormKey("IngotGold"),        Perk = [ GetFormKey("DaedricSmithing") ]                            },
            new DataMap{Id = "mat_imperialh",   Kwda = GetFormKey("ArmorMaterialImperialHeavy"),           Item = GetFormKey("IngotSteel"),       Perk = [ GetFormKey("SteelSmithing") ]                              },
            new DataMap{Id = "mat_iron",        Kwda = GetFormKey("ArmorMaterialIron"),                    Item = GetFormKey("IngotIron")                                                                            },
            new DataMap{Id = "mat_ironb",       Kwda = GetFormKey("ArmorMaterialIronBanded"),              Item = GetFormKey("IngotIron")                                                                            },
            new DataMap{Id = "mat_madness",     Kwda = GetFormKey("cc_ArmorMaterialMadness"),              Item = GetFormKey("cc_IngotMadness"),  Perk = [ GetFormKey("EbonySmithing") ]                              },
            new DataMap{Id = "mat_nordic",      Kwda = GetFormKey("DLC2ArmorMaterialNordicHeavy"),         Item = GetFormKey("IngotQuicksilver"), Perk = [ GetFormKey("AdvancedArmors") ]                             },
            new DataMap{Id = "mat_orcish",      Kwda = GetFormKey("ArmorMaterialOrcish"),                  Item = GetFormKey("IngotOrichalcum"),  Perk = [ GetFormKey("OrcishSmithing") ]                             },
            new DataMap{Id = "mat_stalhrimh",   Kwda = GetFormKey("DLC2ArmorMaterialStalhrimHeavy"),       Item = GetFormKey("DLC2OreStalhrim"),  Perk = [ GetFormKey("GlassSmithing"), GetFormKey("EbonySmithing") ] },
            new DataMap{Id = "mat_steel",       Kwda = GetFormKey("ArmorMaterialSteel"),                   Item = GetFormKey("IngotSteel"),       Perk = [ GetFormKey("SteelSmithing") ]                              },
            new DataMap{Id = "mat_steelp",      Kwda = GetFormKey("ArmorMaterialSteelPlate"),              Item = GetFormKey("IngotSteel"),       Perk = [ GetFormKey("AdvancedArmors") ]                             },
            new DataMap{Id = "mat_wolf",        Kwda = GetFormKey("WAF_ArmorWolf"),                        Item = GetFormKey("IngotSteel"),       Perk = [ GetFormKey("SteelSmithing") ]                              }
        ];

        List<DataMap> lightMaterials = [
            new DataMap{Id = "mat_amber",       Kwda = GetFormKey("cc_ArmorMaterialAmber"),          Item = GetFormKey("cc_IngotAmber"),    Perk = [ GetFormKey("GlassSmithing"), GetFormKey("EbonySmithing") ]         },
            new DataMap{Id = "mat_bonemold",    Kwda = GetFormKey("DLC2ArmorMaterialBonemoldLight"), Item = GetFormKey("DLC2NetchLeather"), Perk = [ GetFormKey("AdvancedArmors") ]                                     },
            new DataMap{Id = "mat_chitin",      Kwda = GetFormKey("DLC2ArmorMaterialChitinLight"),   Item = GetFormKey("DLC2ChitinPlate"),  Perk = [ GetFormKey("ElvenSmithing") ]                                      },
            new DataMap{Id = "mat_dark",        Kwda = GetFormKey("cc_ArmorMaterialDark"),           Item = GetFormKey("IngotQuicksilver"), Perk = [ GetFormKey("DaedricSmithing") ]                                    },
            new DataMap{Id = "mat_dragonscale", Kwda = GetFormKey("ArmorMaterialDragonscale"),       Item = GetFormKey("DragonScales"),     Perk = [ GetFormKey("DragonArmor") ]                                        },
            new DataMap{Id = "mat_elven",       Kwda = GetFormKey("ArmorMaterialElven"),             Item = GetFormKey("IngotMoonstone"),   Perk = [ GetFormKey("ElvenSmithing") ]                                      },
            new DataMap{Id = "mat_elveng",      Kwda = GetFormKey("ArmorMaterialElvenGilded"),       Item = GetFormKey("IngotMoonstone"),   Perk = [ GetFormKey("ElvenSmithing") ]                                      },
            new DataMap{Id = "mat_forsworn",    Kwda = GetFormKey("ArmorMaterialForsworn"),          Item = GetFormKey("LeatherStrips")                                                                                },
            new DataMap{Id = "mat_glass",       Kwda = GetFormKey("ArmorMaterialGlass"),             Item = GetFormKey("IngotMalachite"),   Perk = [ GetFormKey("GlassSmithing") ]                                      },
            new DataMap{Id = "mat_guard",       Kwda = GetFormKey("WAF_ArmorMaterialGuard"),         Item = GetFormKey("IngotIron"),        Perk = [ GetFormKey("skyre_SMTLeathercraft"), GetFormKey("SteelSmithing") ] },
            new DataMap{Id = "mat_hide",        Kwda = GetFormKey("ArmorMaterialHide"),              Item = GetFormKey("LeatherStrips")                                                                                },
            new DataMap{Id = "mat_imperial",    Kwda = GetFormKey("ArmorMaterialImperialLight"),     Item = GetFormKey("IngotSteel"),       Perk = [ GetFormKey("skyre_SMTLeathercraft"), GetFormKey("SteelSmithing") ] },
            new DataMap{Id = "mat_imperials",   Kwda = GetFormKey("ArmorMaterialImperialStudded"),   Item = GetFormKey("LeatherStrips"),    Perk = [ GetFormKey("skyre_SMTLeathercraft") ]                              },
            new DataMap{Id = "mat_leather",     Kwda = GetFormKey("ArmorMaterialLeather"),           Item = GetFormKey("LeatherStrips"),    Perk = [ GetFormKey("skyre_SMTLeathercraft") ]                              },
            new DataMap{Id = "mat_nightingale", Kwda = GetFormKey("ArmorNightingale"),               Item = GetFormKey("LeatherStrips"),    Perk = [ GetFormKey("skyre_SMTLeathercraft") ]                              },
            new DataMap{Id = "mat_scaled",      Kwda = GetFormKey("ArmorMaterialScaled"),            Item = GetFormKey("IngotCorundum"),    Perk = [ GetFormKey("AdvancedArmors") ]                                     },
            new DataMap{Id = "mat_shrouded",    Kwda = GetFormKey("ArmorDarkBrotherhood"),           Item = GetFormKey("LeatherStrips"),    Perk = [ GetFormKey("skyre_SMTLeathercraft") ]                              },
            new DataMap{Id = "mat_stalhrim",    Kwda = GetFormKey("DLC2ArmorMaterialStalhrimLight"), Item = GetFormKey("DLC2OreStalhrim"),  Perk = [ GetFormKey("GlassSmithing"), GetFormKey("EbonySmithing") ]         },
            new DataMap{Id = "mat_stormcloak",  Kwda = GetFormKey("ArmorMaterialStormcloak"),        Item = GetFormKey("IngotIron"),        Perk = [ GetFormKey("skyre_SMTLeathercraft"), GetFormKey("SteelSmithing") ] },
            new DataMap{Id = "mat_stormcloakh", Kwda = GetFormKey("ArmorMaterialBearStormcloak"),    Item = GetFormKey("IngotSteel"),       Perk = [ GetFormKey("skyre_SMTLeathercraft"), GetFormKey("SteelSmithing") ] },
            new DataMap{Id = "mat_studded",     Kwda = GetFormKey("ArmorMaterialStudded"),           Item = GetFormKey("LeatherStrips"),    Perk = [ GetFormKey("skyre_SMTLeathercraft") ]                              },
            new DataMap{Id = "mat_thievesgl",   Kwda = GetFormKey("ArmorMaterialThievesGuild"),      Item = GetFormKey("LeatherStrips"),    Perk = [ GetFormKey("skyre_SMTLeathercraft") ]                              },
            new DataMap{Id = "mat_vampire",     Kwda = GetFormKey("DLC1ArmorMaterialVampire"),       Item = GetFormKey("LeatherStrips"),    Perk = [ GetFormKey("skyre_SMTLeathercraft") ]                              }
        ];

        allMaterials.AddRange(lightMaterials);
        List<DataMap> factionBinds = [
            new DataMap{Id = "fact_bandit",     Kwda = GetFormKey("skyre_SPCMasqueradeBandit")     },
            new DataMap{Id = "fact_forsworn",   Kwda = GetFormKey("skyre_SPCMasqueradeForsworn")   },
            new DataMap{Id = "fact_imperial",   Kwda = GetFormKey("skyre_SPCMasqueradeImperial")   },
            new DataMap{Id = "fact_stormcloak", Kwda = GetFormKey("skyre_SPCMasqueradeStormcloak") },
            new DataMap{Id = "fact_thalmor",    Kwda = GetFormKey("skyre_SPCMasqueradeThalmor")    }
        ];

        return (allMaterials, lightMaterials, factionBinds);
    }
}

