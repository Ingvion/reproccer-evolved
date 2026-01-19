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
    private static readonly (List<StaticsData> AllMaterials,
                             List<StaticsData> LightMaterials,
                             List<StaticsData> FactionBinds) Statics = ListStatics();

    private static EditorIDs EditorIDs;              // tracker to ensure editorIDs uniqueness for new records
    private static RecordData PatchingData;          // frequently requested data for current record
    private static readonly List<Report> Logs = [];  // list of logs for current record and records created from it

    public static void Run()
    {
        UpdateGMST();
        EditorIDs = new EditorIDs();

        List<IArmorGetter> records = GetRecords();
        List<List<string>> blacklists = [
            [.. Rules["excludedFromRenaming"]!.AsArray().Select(value => value!.GetValue<string>()) ],
            [.. Rules["excludedDreamcloth"]!.AsArray().Select(value => value!.GetValue<string>()) ],
            [.. Rules["excludedFromRecipes"]!.AsArray().Select(value => value!.GetValue<string>()) ]
        ];

        foreach (var armor in records)
        {
            PatchingData = new RecordData
            {
                Log = new Logger(),
                ArmorType = armor.BodyTemplate!.ArmorType,
                NonPlayable = armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable),
                Unique = armor.Keywords!.Contains("skyre__NoMeltdownRecipes".GetFormKey())
            };

            Logs.Add(new Report { Record = armor, Entry = PatchingData.Log });

            if (!armor.TemplateArmor.IsNull || (PatchingData.ArmorType == ArmorType.Clothing
                && armor.Keywords!.Contains("ArmorJewelry".GetFormKey())))
            {
                PatchRecordNames(armor, blacklists[0]);
                ShowReport();
                continue;
            }

            SetOverriddenData(armor);

            if (PatchingData.ArmorType != ArmorType.Clothing)
            {
                PatchShieldWeight(armor, PatchingData.ArmorType);
                PatchArmorRating(armor);
            }

            if (!PatchingData.NonPlayable)
            {
                PatchRecordNames(armor, blacklists[0]);
                PatchMasqueradeKeywords(armor);
                if (PatchingData.ArmorType == ArmorType.Clothing)
                {
                    ProcessClothing(armor, blacklists[1]);
                    ShowReport();
                    continue;
                }
                ProcessRecipes(armor, blacklists[2]);
            }


            ShowReport();
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
        IEnumerable<IArmorGetter> conflictWinners = Executor.State!.LoadOrder.PriorityOrder
            .Where(plugin => !Settings.General.IgnoredFiles.Any(name => name == plugin.ModKey.FileName))
            .Where(plugin => plugin.Enabled)
            .WinningOverrides<IArmorGetter>();

        List<IArmorGetter> validRecords = [];
        List<string> excludedNames = [.. Rules["excludedArmor"]!.AsArray().Select(value => value!.GetValue<string>())];
        List<FormKey> mustHave = [
            "ArmorHeavy".GetFormKey(),
            "ArmorLight".GetFormKey(),
            "ArmorShield".GetFormKey(),
            "ArmorClothing".GetFormKey(),
            "ArmorJewelry".GetFormKey()
        ];

        foreach (var record in conflictWinners)
        {
            if (IsValid(record, excludedNames, mustHave)) validRecords.Add(record);
        }

        Console.WriteLine($"\n~~~ {validRecords.Count} of {conflictWinners.Count()} armor records are eligible for patching ~~~\n\n"
            + "====================");
        return validRecords;
    }

    /// <summary>
    /// Checks if the record matches necessary conditions to be patched.
    /// </summary>
    /// <param name="record">Processed record.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    /// <param name="mustHave">The list of keywords (as FormKeys) of which at least one must be present in record's keywords.</param>
    /// <returns>Check result as bool.</returns>
    private static bool IsValid(IArmorGetter armor, List<string> excludedNames, List<FormKey> mustHave)
    {
        Logger Log = new();
        Logs.Add(new Report { Record = armor, Entry = Log });

        // found in the excluded records list by edID
        if (Settings.General.ExclByEdID && armor.EditorID!.IsExcluded(excludedNames, true))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Log.Info("Found in the \"No patching\" list by EditorID");
                ShowReport();
            }
            return false;
        }

        // has no name
        if (armor.Name is null) return false;

        // found in the excluded records list by name
        if (armor.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Log.Info($"Found in the \"No patching\" list by name");
                ShowReport();
            }
            return false;
        }

        // has no body template
        if (armor.BodyTemplate is null) return false;

        // has a template (to skip keyword checks below)
        if (!armor.TemplateArmor.IsNull) return true;

        // has no keywords or kws array is empty (rare)
        if (armor.Keywords is null || armor.Keywords.Count == 0) return false;

        // has none of the required keywords
        if (!mustHave.Any(keyword => armor.Keywords.Contains(keyword))) return false;

        return true;
    }

    /// <summary>
    /// Renames the record according to the rules.
    /// </summary>
    /// <param name="armor">Processed armor record.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    private static void PatchRecordNames(IArmorGetter armor, List<string> excludedNames)
    {
        if (armor.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded) PatchingData.Log.Info($"Found in the \"No renaming\" list");
            return;
        }

        string name = armor.Name!.ToString()!;

        /* char[] flags options:
         * i - case-insensitive search
         * g - replace all matches
         * p - allow as substring
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
                    if (!filterArr.Any(type => PatchingData.ArmorType.ToString() == type.Replace(" ", ""))) continue;
                }

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

        if (name != armor.Name.ToString())
        {
            PatchingData.Log.Info($"Was renamed to {name} in accordance with patching rules", true);
            armor.AsOverride(true).Name = name;
        }
    }

    /// <summary>
    /// Modifies armor material according to the rules.
    /// </summary>
    /// <param name="armor">Processed armor record.</param>
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
                if (entry1.Kwda == StaticsData.NullRef)
                {
                    PatchingData.Log.Caution("A \"materialOverrides\" patching rule references a material from Creation Club's \"Saints and Seducers\"");
                    break;
                }

                foreach (var entry2 in Statics.AllMaterials)
                {
                    if (armor.Keywords!.Contains(entry2.Kwda!) && entry2.Kwda != entry1.Kwda)
                        armor.AsOverride().Keywords!.Remove(entry2.Kwda);
                }

                armor.AsOverride(true).Keywords!.Add(entry1.Kwda!);
                PatchingData.Log.Info($"The material was forced to {overrideString} in accordance with patching rules", true);
                PatchingData.Overridden = true;
                break;
            }
        }
    }

    /// <summary>
    /// Distributes shield weight keywords and modifies shields' impact data set and material type.
    /// </summary>
    /// <param name="armor">Processed armor record.</param>
    /// <param name="armorType">An armor type as enum.</param>
    private static void PatchShieldWeight(IArmorGetter armor, ArmorType armorType)
    {
        if (!armor.Keywords!.Contains("ArmorShield".GetFormKey())) return;

        if (armorType == ArmorType.HeavyArmor && !armor.Keywords!.Contains("skyre__ArmorShieldHeavy".GetFormKey()))
        {
            armor.AsOverride(true).Keywords!.Add("skyre__ArmorShieldHeavy".GetFormKey());
            armor.AsOverride().BashImpactDataSet = new FormLinkNullable<IImpactDataSetGetter>("WPNBashShieldHeavyImpactSet".GetFormKey());
            armor.AsOverride().AlternateBlockMaterial = new FormLinkNullable<IMaterialTypeGetter>("MaterialShieldHeavy".GetFormKey());
        }
        else if (armorType == ArmorType.LightArmor && !armor.Keywords!.Contains("skyre__ArmorShieldLight".GetFormKey()))
        {
            armor.AsOverride(true).Keywords!.Add("skyre__ArmorShieldLight".GetFormKey());
            armor.AsOverride().BashImpactDataSet = new FormLinkNullable<IImpactDataSetGetter>("WPNBashShieldLightImpactSet".GetFormKey());
            armor.AsOverride().AlternateBlockMaterial = new FormLinkNullable<IMaterialTypeGetter>("MaterialShieldLight".GetFormKey());
        }
    }

    /// <summary>
    /// Modifies armor rating based on other methods' results.
    /// </summary>
    /// <param name="armor">Processed armor record.</param>
    private static void PatchArmorRating(IArmorGetter armor)
    {
        float slotFactor = GetSlotFactor(armor);
        if (slotFactor == 0) return;

        int materialFactor = GetMaterialFactor(armor);
        if (materialFactor == 0) return;

        float extraMod = GetExtraArmorMod(armor);
        float newArmorRating = (float)Math.Floor(slotFactor * materialFactor * extraMod);

        if (newArmorRating != armor.ArmorRating)
        {
            armor.AsOverride(true).ArmorRating = newArmorRating;
            PatchingData.Log.Info($"Armor rating: {armor.ArmorRating} -> {armor.AsOverride().ArmorRating} " +
                $"(material: {materialFactor}, slot: x{slotFactor}, mult: x{extraMod})", true);
        }
    }

    /// <summary>
    /// Returns armor slot factor value according to the settings.
    /// </summary>
    /// <param name="armor">Processed armor record.</param>
    /// <returns>Armor rating slot factor as float, or 0 if no slot keyword was found.</returns>
    private static float GetSlotFactor(IArmorGetter armor)
    {
        if (armor.Keywords!.Contains("ArmorSlotBoots".GetFormKey())) 
            return Settings.Armor.SlotBoots;
        if (armor.Keywords!.Contains("ArmorSlotCuirass".GetFormKey())) 
            return Settings.Armor.SlotCuirass;
        if (armor.Keywords!.Contains("ArmorSlotGauntlets".GetFormKey())) 
            return Settings.Armor.SlotGauntlets;
        if (armor.Keywords!.Contains("ArmorSlotHelmet".GetFormKey())) 
            return Settings.Armor.SlotHelmet;
        if (armor.Keywords!.Contains("ArmorShield".GetFormKey())) 
            return Settings.Armor.SlotShield;

        PatchingData.Log.Error("Unable to determine the equip slot for the record");
        return default;
    }

    /// <summary>
    /// Returns material factor value for armor rating according to the rules.
    /// </summary>
    /// <param name="armor">Processed armor record.</param>
    /// <returns>Armor material factor as int, or 0 if there's no rule or value has incorrect type.</returns>
    private static int GetMaterialFactor(IArmorGetter armor)
    {
        if (PatchingData.Overridden) armor = armor.AsOverride();

        string? materialId = null;
        foreach (var entry in Statics.AllMaterials)
        {
            FormKey kwda = entry.Kwda!;
            if (kwda != StaticsData.NullRef && armor.Keywords!.Contains(kwda))
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
            if (materialId is null) PatchingData.Log.Caution("Has a \"materials\" patching rule for its name, but no material keyword");
            return (int)factorInt;
        }

        PatchingData.Log.Error("Unable to determine the material");
        return default;
    }

    /// <summary>
    /// Returns armor rating multiplier according to the rules.
    /// </summary>
    /// <param name="armor">Processed armor record.</param>
    /// <returns>Armor rating multipier as float, or 1 if value is incorrect or <= 0.</returns>
    private static float GetExtraArmorMod(IArmorGetter armor)
    {
        JsonNode? modifierNode = Helpers.RuleByName(armor.Name!.ToString()!, Rules["armorModifiers"]!.AsArray(), data1: "names", data2: "multiplier");
        float? modifierData = modifierNode?.AsType<float>();

        if (modifierData is not null)
            return (float)(modifierData > 0.0f ? modifierData : 1.0f);

        return 1.0f;
    }

    /// <summary>
    /// Distributes Masquerade perk related faction keywords according to the rules.
    /// </summary>
    /// <param name="armor">Processed armor record.</param>
    private static void PatchMasqueradeKeywords(IArmorGetter armor)
    {
        JsonArray rules = Rules["masquerade"]!.AsArray();
        List<string> addedFactions = [];

        foreach (var rule in rules)
        {
            string[] namesArr = rule!["names"]!.ToString().Split(',', StringSplitOptions.TrimEntries);
            if (!namesArr.Any(word => armor.Name!.ToString()!.Contains(word))) continue;

            string[] filterArr = rule!["filter"]?.ToString().Replace(" ", "").Split(',') ?? [];
            if (filterArr.Length != 0 && !filterArr.Any(type => type == PatchingData.ArmorType.ToString())) continue;

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
        if (addedFactions.Count > 0) PatchingData.Log.Info($"Faction keywords added: {string.Join(", ", addedFactions)}", true);
    }

    /// <summary>
    /// Initiates recipes processing.
    /// </summary>
    /// <param name="armor">Processed armor record.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    private static void ProcessRecipes(IArmorGetter armor, List<string> excludedNames)
    {
        if (PatchingData.Modified) armor = armor.AsOverride();

        if (armor.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
                PatchingData.Log.Info($"Found in the \"No recipe modifications\" list", false, true);

            return;
        }

        foreach (var recipe in Executor.AllRecipes!)
        {
            if (recipe.CreatedObject.FormKey == armor.FormKey)
            {
                if (Settings.Armor.FixCraftRecipes && recipe.WorkbenchKeyword.FormKey == "CraftingSmithingForge".GetFormKey())
                    ModCraftingRecipe(recipe, armor);

                if (recipe.WorkbenchKeyword.FormKey == "CraftingSmithingArmorTable".GetFormKey())
                    ModTemperingRecipe(recipe, armor);
            }
        }

        AddBreakdownRecipe(armor, PatchingData.Log);
    }

    /// <summary>
    /// Modifies crafting recipe.<br/><br/>
    /// The method adds a HasPerk-type condition with the Leathercraft perk to the crafting recipe
    /// if the armor have keywords associated with this perk (see the materials map).<br/> 
    /// Only crafting recipes with no HasPerk-type conditions for other smithing perks will be modified. 
    /// </summary>
    /// <param name="recipe">Constructible object record (a crafting recipe).</param>
    /// <param name="armor">Processed armor record.</param>
    private static void ModCraftingRecipe(IConstructibleObjectGetter recipe, IArmorGetter armor)
    {
        foreach (var material in Statics.LightMaterials)
        {
            if (recipe.Conditions.Count > 0)
            {
                if (material.Perks.Count > 0
                    && recipe.Conditions.Any(condition => condition.Data is HasPerkConditionData hasPerk
                    && material.Perks.Any(perk => hasPerk.Perk.Link.FormKey == perk)))
                {
                    return;
                }
            }
        }

        foreach (var material in Statics.LightMaterials)
        {
            if (material.Perks.Count > 0
                && material.Perks[0] == "skyre_SMTLeathercraft".GetFormKey()
                && armor.Keywords!.Contains(material.Kwda!))
            {
                ConstructibleObject craftingRecipe = Executor.State!.PatchMod.ConstructibleObjects.GetOrAddAsOverride(recipe);
                craftingRecipe.AddHasPerkCondition("skyre_SMTLeathercraft".GetFormKey());

                break;
            }
        }
    }

    /// <summary>
    /// Modifies tempering recipe.<br/><br/>
    /// The method removes existing HasPerk-type conditions with smithing perks, and adds new ones
    /// corresponding to the armor keywords.<br/>
    /// If more than 1 perk is associated with a keyword, all but the last HasPerk-type conditions will have the OR flag.
    /// </summary>
    /// <param name="recipe">Constructible object record (a tempering recipe).</param>
    /// <param name="armor">Processed armor record.</param>
    private static void ModTemperingRecipe(IConstructibleObjectGetter recipe, IArmorGetter armor)
    {
        if (recipe.Conditions.Count > 0 && recipe.Conditions.Any(condition => condition.Data is EPTemperingItemIsEnchantedConditionData))
        {
            List<FormKey> allPerks = [.. Statics.AllMaterials
                .Where(entry => entry.Perks.Count > 0)
                .SelectMany(entry => entry.Perks)
                .Distinct()];
            List<FormKey> materialPerks = [.. Statics.AllMaterials
                .Where(entry => armor.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
                .Where(entry => entry.Perks.Count > 0)
                .SelectMany(entry => entry.Perks)
                .Distinct()];
            List<FormKey> materialItems = [.. Statics.AllMaterials
                .Where(entry => armor.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
                .Select(entry => entry.Items[0])
                .Distinct()];


            int index = materialItems.IndexOf("LeatherStrips".GetFormKey());
            if (index != -1)
            {
                materialItems.RemoveAt(index);
                materialItems.Insert(index, "Leather01".GetFormKey());
            }

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
    /// Generates a breakdown recipe.
    /// </summary>
    /// <param name="armor">Processed armor record.</param>
    /// <param name="log">Log instance for cases when armor record is created by the patcher.</param>
    /// <param name="noRecipes">True to omit crafting recipes lookup.</param>
    private static void AddBreakdownRecipe(IArmorGetter armor, Logger log, bool noRecipes = false)
    {
        if (PatchingData.Unique)
        {
            log.Info($"The breakdown recipe was not generated due to the \"No breakdown\" keyword", true);
            return;
        }

        IConstructibleObjectGetter? craftingRecipe = null;
        if (!noRecipes)
        {
            foreach (var recipe in Executor.AllRecipes!)
            {
                if (Settings.General.SkipExisting
                    && recipe.Items?.FirstOrDefault()?.Item == armor
                    && (recipe.WorkbenchKeyword.FormKey == "CraftingTanningRack".GetFormKey()
                    || recipe.WorkbenchKeyword.FormKey == "CraftingSmelter".GetFormKey()))
                {
                    log.Info($"Already has a breakdown recipe in the {recipe.FormKey.ModKey.FileName}");
                    return;
                }

                if (craftingRecipe is null
                    && recipe.CreatedObject.FormKey == armor.FormKey
                    && (recipe.WorkbenchKeyword.FormKey == "CraftingTanningRack".GetFormKey()
                    || recipe.WorkbenchKeyword.FormKey == "CraftingSmithingForge".GetFormKey()))
                {
                    craftingRecipe = recipe;
                }
            }
        }

        bool isBig = armor.BodyTemplate!.FirstPersonFlags.HasFlag(BipedObjectFlag.Body) 
            || armor.BodyTemplate!.FirstPersonFlags.HasFlag(BipedObjectFlag.Shield);
        bool isDreamcloth = armor.Keywords!.Contains("skyre__ArmorDreamcloth".GetFormKey());
        bool isClothing = PatchingData.ArmorType == ArmorType.Clothing && !isDreamcloth;

        List<FormKey> armorPerks = !isClothing && !isDreamcloth ? [.. Statics.AllMaterials
            .Where(entry => armor.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
            .Where(entry => entry.Perks.Count > 0)
            .SelectMany(entry => entry.Perks)
            .Distinct()] : [];
        List<FormKey> armorItems = !isClothing && !isDreamcloth ? [.. Statics.AllMaterials
            .Where(entry => armor.Keywords!.Any(keyword => keyword.FormKey == entry.Kwda))
            .Select(entry => entry.Items[0])
            .Distinct()] : ["LeatherStrips".GetFormKey()];

        if (!isClothing && !isDreamcloth && armorItems.Count == 0)
        {
            log.Error($"Unable to determine the breakdown recipe resulting item");
            return;
        }

        if (armorItems.IndexOf("LeatherStrips".GetFormKey()) is int index && index != -1)
        {
            if (isDreamcloth)
            {
                armorItems.RemoveAt(index);
                armorItems.Insert(index, "WispWrappings".GetFormKey());
            }
            else if (isBig)
            {
                armorItems.RemoveAt(index);
                armorItems.Insert(index, "Leather01".GetFormKey());
            }
        }

        bool isLeather = armorItems.Contains("Leather01".GetFormKey()) || armorItems.Contains("LeatherStrips".GetFormKey());

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

        cobj.AddHasPerkCondition("skyre_SMTBreakdown".GetFormKey());
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
            (isLeather || isClothing ? "CraftingTanningRack".GetFormKey() : "CraftingSmelter".GetFormKey())
            .ToNullableLink();
        cobj.CreatedObjectCount = (ushort)Math.Clamp(Math.Floor(outputQty), 1, qty + (fromRecipe ? 0 : mod));
    }

    /// <summary>
    /// Clothing processor.
    /// </summary>
    /// <param name="armor">Processed armor record.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    private static void ProcessClothing(IArmorGetter armor, List<string> excludedNames)
    {
        if (!Settings.Armor.NoClothingBreak) AddBreakdownRecipe(armor, PatchingData.Log, true);

        CreateDreamcloth(armor, excludedNames);
    }

    /// <summary>
    /// Creates the armor record for Dreamcloth variety.<br/>
    /// </summary>
    /// <param name="armor">Original armor record.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    /// <param name="localLog">Additional Log entry to display info on the new record.</param>
    private static void CreateDreamcloth(IArmorGetter armor, List<string> excludedNames)
    {
        if (armor.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
            {
                PatchingData.Log.Info("Found in the \"No Dreamcloth\" list", true);
                return;
            }
        }

        if (!armor.TemplateArmor.IsNull || PatchingData.Unique)
        {
            PatchingData.Log.Info($"The clothing is templated or has \"No breakdown\" keyword, and cannot have Dreamcloth variety", true);
            return;
        }

        Logger localLog = new();

        string label = Settings.Armor.DreamclothLabel == "" ? $" [{"name_dcloth".GetT9n()}]" : Settings.Armor.DreamclothLabel;
        string newName = armor.AsOverride().Name!.ToString() + label;
        string newEditorID = "RP_ARMO_" + armor.EditorID;

        Armor newArmor = Executor.State!.PatchMod.Armors.DuplicateInAsNewRecord(armor.AsOverride());
        if (!PatchingData.Modified) Executor.State!.PatchMod.Armors.Remove(armor);

        newArmor.Name = newName;
        newArmor.EditorID = EditorIDs.Unique(newEditorID);
        newArmor.VirtualMachineAdapter = null;
        newArmor.Description = null;
        newArmor.Keywords!.Add("skyre__ArmorDreamcloth".GetFormKey());

        float priceMult = Settings.Armor.DreamclothPrice / 100f;
        newArmor.Value = (uint)(priceMult * newArmor.Value);

        List<RecipeData> ingredients = [
            new RecipeData{ Items = [ "SoulGemPettyFilled".GetFormKey() ], Qty = 2 },
            new RecipeData{ Items = [ "LeatherStrips".GetFormKey()      ], Qty = 1 },
            new RecipeData{ Items = [ "WispWrappings".GetFormKey()      ], Qty = 1 }
        ];

        if (newArmor.Keywords!.Contains("ClothingBody".GetFormKey()))
        {
            ingredients[0] = ingredients[0] with { Items = [ "SoulGemCommonFilled".GetFormKey() ], Qty = 1 };
            ingredients[1] = ingredients[1] with { Qty = 3 };
            ingredients[2] = ingredients[2] with { Qty = 2 };

            newArmor.Keywords!.Add("skyre__DreamclothBody".GetFormKey());
        }
        else if (newArmor.Keywords!.Contains("ClothingHead".GetFormKey()))
        {
            ingredients[0] = ingredients[0] with { Items = [ "SoulGemLesserFilled".GetFormKey() ], Qty = 1 };
            ingredients[1] = ingredients[1] with { Qty = 2 };
        }

        // Log entry for this particular record
        localLog.Info($"New value: {newArmor.Value} (original value: {armor.Value}, Dreamcloth mult: x{priceMult})", true);
        Logs.Add(new Report { Record = newArmor, Entry = localLog });

        AddCraftingRecipe(newArmor, armor, ingredients, localLog);
        AddBreakdownRecipe(newArmor, localLog, true);
    }

    /// <summary>
    /// Generates a crafting recipe for the Dreamcloth variety.<br/>
    /// </summary>
    /// <param name="newArmor">Dreamcloth variety.</param>
    /// <param name="oldArmor">Original armor record.</param>
    /// <param name="ingredients">List of ingredients and their quantity.</param>
    /// <param name="log">Log instance for the new record.</param>
    private static void AddCraftingRecipe(IArmorGetter newArmor, IArmorGetter oldArmor, List<RecipeData> ingredients, Logger log)
    {
        string newEditorID = "RP_ARMO_CRAFT_" + oldArmor.EditorID;
        ConstructibleObject newRecipe = Executor.State!.PatchMod.ConstructibleObjects.AddNew();

        newRecipe.EditorID = EditorIDs.Unique(newEditorID);
        newRecipe.Items = [];

        ContainerItem baseItem = new();
        baseItem.Item = oldArmor.ToNullableLink();
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
            else if (Executor.State!.LinkCache.TryResolve<ISoulGemGetter>(entry.Items[0], out var slgmItem))
            {
                newItem.Item = slgmItem.ToNullableLink();
            }
            else
            {
                log.Error($"Ingredient {entry.Items[0]} has unexpected record type!");
                Executor.State!.PatchMod.ConstructibleObjects.Remove(newRecipe);
                return;
            }

            ContainerEntry newEntry = new();
            newEntry.Item = newItem;
            newEntry.Item.Count = entry.Qty;
            newRecipe.Items.Add(newEntry);
        }

        newRecipe.AddHasPerkCondition("skyre_SMTWeavingMill".GetFormKey());
        if (!Settings.Armor.AllArmorRecipes)
            newRecipe.AddGetItemCountCondition(oldArmor.FormKey, CompareOperator.GreaterThanOrEqualTo);

        newRecipe.AddGetEquippedCondition(oldArmor.FormKey, CompareOperator.NotEqualTo);

        newRecipe.CreatedObject = newArmor.ToNullableLink();
        newRecipe.WorkbenchKeyword = Executor.State!.LinkCache.Resolve<IKeywordGetter>("CraftingTanningRack".GetFormKey()).ToNullableLink();
        newRecipe.CreatedObjectCount = 1;
    }


    // patcher specific helpers

    /// <summary>
    /// Copies the winning override of this-parameter to the patch file, and returns it.<br/>
    /// </summary>
    /// <param name="armor">Processed armor record.</param>
    /// <param name="isModified">True to mark as modified in the patching data.</param>
    /// <returns>The winning override for processed armor record.</returns>
    private static Armor AsOverride(this IArmorGetter armor, bool isModified = false)
    {
        if (isModified) PatchingData.Modified = true;
        return Executor.State!.PatchMod.Armors.GetOrAddAsOverride(armor);
    }

    /// <summary>
    /// Displays patching results for the current record and records created on its basis.<br/>
    /// </summary>
    private static void ShowReport()
    {
        foreach (var report in Logs)
        {
            IArmorGetter armor = (IArmorGetter)report.Record!;
            Logger log = (Logger)report.Entry!;
            log.Report($"{armor.Name}", $"{armor.FormKey}", $"{armor.EditorID}", PatchingData.NonPlayable, !armor.TemplateArmor.IsNull);
        }

        Logs.Clear();
    }

    // patcher specific statics

    /// <summary>
    /// Appends patcher-specific records to the shared statics list, generates patcher-specific collections of statics.<br/>
    /// </summary>
    /// <returns>A tuple of statics collections for armor records: all materials, light materials only, faction keywords data.</returns>
    private static (List<StaticsData>, List<StaticsData>, List<StaticsData>) ListStatics()
    {
        Executor.Statics!.AddRange(
        [
            new StaticsData{ Id = "MaterialShieldLight",                    FormKey = Helpers.ParseFormKey("Skyrim.esm|0x016978|KWDA", true)            },
            new StaticsData{ Id = "MaterialShieldHeavy",                    FormKey = Helpers.ParseFormKey("Skyrim.esm|0x016979|KWDA", true)            },
            new StaticsData{ Id = "WPNBashShieldLightImpactSet",            FormKey = Helpers.ParseFormKey("Skyrim.esm|0x0183fb|KWDA", true)            },
            new StaticsData{ Id = "WPNBashShieldHeavyImpactSet",            FormKey = Helpers.ParseFormKey("Skyrim.esm|0x0183fe|KWDA", true)            },
            new StaticsData{ Id = "ArmorHeavy",                             FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbd2|KWDA")                  },
            new StaticsData{ Id = "ArmorLight",                             FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbd3|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialDaedric",                   FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbd4|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialDragonplate",               FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbd5|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialDragonscale",               FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbd6|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialDwarven",                   FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbd7|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialEbony",                     FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbd8|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialElven",                     FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbd9|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialElvenGilded",               FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbda|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialLeather",                   FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbdb|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialGlass",                     FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbdc|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialHide",                      FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbdd|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialScaled",                    FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbde|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialStudded",                   FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbdf|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialImperialLight",             FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbe0|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialImperialStudded",           FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbe1|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialImperialHeavy",             FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbe2|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialIron",                      FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbe3|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialIronBanded",                FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbe4|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialOrcish",                    FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbe5|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialSteel",                     FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbe6|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialSteelPlate",                FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbe7|KWDA")                  },
            new StaticsData{ Id = "ArmorClothing",                          FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbe8|KWDA")                  },
            new StaticsData{ Id = "ArmorJewelry",                           FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06bbe9|KWDA")                  },
            new StaticsData{ Id = "ArmorSlotCuirass",                       FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06c0ec|KWDA")                  },
            new StaticsData{ Id = "ArmorSlotBoots",                         FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06c0ed|KWDA")                  },
            new StaticsData{ Id = "ArmorSlotHelmet",                        FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06c0ee|KWDA")                  },
            new StaticsData{ Id = "ArmorSlotGauntlets",                     FormKey = Helpers.ParseFormKey("Skyrim.esm|0x06c0ef|KWDA")                  },
            new StaticsData{ Id = "CraftingTanningRack",                    FormKey = Helpers.ParseFormKey("Skyrim.esm|0x07866a|KWDA")                  },
            new StaticsData{ Id = "VendorItemArmor",                        FormKey = Helpers.ParseFormKey("Skyrim.esm|0x08f959|KWDA")                  },
            new StaticsData{ Id = "VendorItemJewelry",                      FormKey = Helpers.ParseFormKey("Skyrim.esm|0x08f95a|KWDA")                  },
            new StaticsData{ Id = "VendorItemClothing",                     FormKey = Helpers.ParseFormKey("Skyrim.esm|0x08f95b|KWDA")                  },
            new StaticsData{ Id = "ArmorShield",                            FormKey = Helpers.ParseFormKey("Skyrim.esm|0x0965b2|KWDA")                  },
            new StaticsData{ Id = "ClothingBody",                           FormKey = Helpers.ParseFormKey("Skyrim.esm|0x0a8657|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialStormcloak",                FormKey = Helpers.ParseFormKey("Skyrim.esm|0x0ac13a|KWDA")                  },
            new StaticsData{ Id = "CraftingSmithingArmorTable",             FormKey = Helpers.ParseFormKey("Skyrim.esm|0x0adb78|KWDA")                  },
            new StaticsData{ Id = "ClothingHead",                           FormKey = Helpers.ParseFormKey("Skyrim.esm|0x10cd11|KWDA")                  },
            new StaticsData{ Id = "ArmorNightingale",                       FormKey = Helpers.ParseFormKey("Skyrim.esm|0x10fd61|KWDA")                  },
            new StaticsData{ Id = "ArmorDarkBrotherhood",                   FormKey = Helpers.ParseFormKey("Skyrim.esm|0x10fd62|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialBlades",                    FormKey = Helpers.ParseFormKey("Update.esm|0x0009c0|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialForsworn",                  FormKey = Helpers.ParseFormKey("Update.esm|0x0009b9|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialThievesGuild",              FormKey = Helpers.ParseFormKey("Update.esm|0x0009bc|KWDA")                  },
            new StaticsData{ Id = "ArmorMaterialBearStormcloak",            FormKey = Helpers.ParseFormKey("Update.esm|0x0009be|KWDA")                  },
            new StaticsData{ Id = "WAF_ArmorWolf",                          FormKey = Helpers.ParseFormKey("Update.esm|0xaf0107|KWDA")                  },
            new StaticsData{ Id = "WAF_ArmorMaterialGuard",                 FormKey = Helpers.ParseFormKey("Update.esm|0xaf0112|KWDA")                  },
            new StaticsData{ Id = "WAF_DLC1ArmorDawnguardHeavy",            FormKey = Helpers.ParseFormKey("Update.esm|0xaf0117|KWDA")                  },
            new StaticsData{ Id = "WAF_DLC1ArmorDawnguardLight",            FormKey = Helpers.ParseFormKey("Update.esm|0xaf0118|KWDA")                  },
            new StaticsData{ Id = "WAF_ArmorMaterialDraugr",                FormKey = Helpers.ParseFormKey("Update.esm|0xaf0135|KWDA")                  },
            new StaticsData{ Id = "WAF_ArmorMaterialThalmor",               FormKey = Helpers.ParseFormKey("Update.esm|0xaf0222|KWDA")                  },
            new StaticsData{ Id = "DLC1ArmorMaterialHunter",                FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x0050c4|KWDA")               },
            new StaticsData{ Id = "DLC1ArmorMaterialDawnguard",             FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x012ccd|KWDA")               },
            new StaticsData{ Id = "DLC1ArmorMaterialFalmerHardened",        FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x012cce|KWDA")               },
            new StaticsData{ Id = "DLC1ArmorMaterialFalmerHeavy",           FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x012ccf|KWDA")               },
            new StaticsData{ Id = "DLC1ArmorMaterialFalmerHeavyOriginal",   FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x012cd0|KWDA")               },
            new StaticsData{ Id = "DLC1ArmorMaterialVampire",               FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x01463e|KWDA")               },
            new StaticsData{ Id = "DLC2ArmorMaterialBonemoldHeavy",         FormKey = Helpers.ParseFormKey("Dragonborn.esm|0x024101|KWDA")              },
            new StaticsData{ Id = "DLC2ArmorMaterialChitinLight",           FormKey = Helpers.ParseFormKey("Dragonborn.esm|0x024102|KWDA")              },
            new StaticsData{ Id = "DLC2ArmorMaterialChitinHeavy",           FormKey = Helpers.ParseFormKey("Dragonborn.esm|0x024103|KWDA")              },
            new StaticsData{ Id = "DLC2ArmorMaterialNordicLight",           FormKey = Helpers.ParseFormKey("Dragonborn.esm|0x024104|KWDA")              },
            new StaticsData{ Id = "DLC2ArmorMaterialNordicHeavy",           FormKey = Helpers.ParseFormKey("Dragonborn.esm|0x024105|KWDA")              },
            new StaticsData{ Id = "DLC2ArmorMaterialStalhrimHeavy",         FormKey = Helpers.ParseFormKey("Dragonborn.esm|0x024106|KWDA")              },
            new StaticsData{ Id = "DLC2ArmorMaterialStalhrimLight",         FormKey = Helpers.ParseFormKey("Dragonborn.esm|0x024107|KWDA")              },
            new StaticsData{ Id = "cc_ArmorMaterialGolden",                 FormKey = Helpers.ParseFormKey("ccBGSSSE025-AdvDSGS.esm|0x21bd63|KWDA")     },
            new StaticsData{ Id = "cc_ArmorMaterialDark",                   FormKey = Helpers.ParseFormKey("ccBGSSSE025-AdvDSGS.esm|0x21bd64|KWDA")     },
            new StaticsData{ Id = "cc_ArmorMaterialMadness",                FormKey = Helpers.ParseFormKey("ccBGSSSE025-AdvDSGS.esm|0x21bd65|KWDA")     },
            new StaticsData{ Id = "cc_ArmorMaterialAmber",                  FormKey = Helpers.ParseFormKey("ccBGSSSE025-AdvDSGS.esm|0x21bd66|KWDA")     },
            new StaticsData{ Id = "skyre__ArmorShieldHeavy",                FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00080f|KWDA") },
            new StaticsData{ Id = "skyre__ArmorShieldLight",                FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000810|KWDA") },
            new StaticsData{ Id = "skyre__ArmorDreamcloth",                 FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000811|KWDA") },
            new StaticsData{ Id = "skyre__DreamclothBody",                  FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x00098d|KWDA") },
            new StaticsData{ Id = "skyre_SPCMasqueradeBandit",              FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000f44|KWDA") },
            new StaticsData{ Id = "skyre_SPCMasqueradeForsworn",            FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000f45|KWDA") },
            new StaticsData{ Id = "skyre_SPCMasqueradeImperial",            FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000f46|KWDA") },
            new StaticsData{ Id = "skyre_SPCMasqueradeStormcloak",          FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000f47|KWDA") },
            new StaticsData{ Id = "skyre_SPCMasqueradeThalmor",             FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000f48|KWDA") },
            new StaticsData{ Id = "skyre_SMTLeathercraft",                  FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000ec4|PERK") },
            new StaticsData{ Id = "skyre_SMTWeavingMill",                   FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000ee1|PERK") }
        ]);

        List<StaticsData> allMaterials = [
            new StaticsData{ Id = "mat_ancientnord", Kwda = "WAF_ArmorMaterialDraugr".GetFormKey(),              Items = [ "IngotCorundum".GetFormKey() ],    Perks = [ "AdvancedArmors".GetFormKey() ]                              },
            new StaticsData{ Id = "mat_blades",      Kwda = "ArmorMaterialBlades".GetFormKey(),                  Items = [ "IngotSteel".GetFormKey() ],       Perks = [ "SteelSmithing".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_bonemoldh",   Kwda = "DLC2ArmorMaterialBonemoldHeavy".GetFormKey(),       Items = [ "DLC2NetchLeather".GetFormKey() ], Perks = [ "AdvancedArmors".GetFormKey() ]                              },
            new StaticsData{ Id = "mat_chitinh",     Kwda = "DLC2ArmorMaterialChitinHeavy".GetFormKey(),         Items = [ "DLC2ChitinPlate".GetFormKey() ],  Perks = [ "ElvenSmithing".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_daedric",     Kwda = "ArmorMaterialDaedric".GetFormKey(),                 Items = [ "IngotEbony".GetFormKey() ],       Perks = [ "DaedricSmithing".GetFormKey() ]                             },
            new StaticsData{ Id = "mat_dawnguard",   Kwda = "DLC1ArmorMaterialDawnguard".GetFormKey(),           Items = [ "IngotSteel".GetFormKey() ],       Perks = [ "SteelSmithing".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_dawnguardh",  Kwda = "DLC1ArmorMaterialHunter".GetFormKey(),              Items = [ "IngotSteel".GetFormKey() ],       Perks = [ "SteelSmithing".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_dragonplate", Kwda = "ArmorMaterialDragonplate".GetFormKey(),             Items = [ "DragonBone".GetFormKey() ],       Perks = [ "DragonArmor".GetFormKey() ]                                 },
            new StaticsData{ Id = "mat_dwarven",     Kwda = "ArmorMaterialDwarven".GetFormKey(),                 Items = [ "IngotDwarven".GetFormKey() ],     Perks = [ "DwarvenSmithing".GetFormKey() ]                             },
            new StaticsData{ Id = "mat_ebony",       Kwda = "ArmorMaterialEbony".GetFormKey(),                   Items = [ "IngotEbony".GetFormKey() ],       Perks = [ "EbonySmithing".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_falmerhr",    Kwda = "DLC1ArmorMaterialFalmerHardened".GetFormKey(),      Items = [ "ChaurusChitin".GetFormKey() ],    Perks = [ "ElvenSmithing".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_falmerhv",    Kwda = "DLC1ArmorMaterialFalmerHeavy".GetFormKey(),         Items = [ "ChaurusChitin".GetFormKey() ],    Perks = [ "ElvenSmithing".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_falmer",      Kwda = "DLC1ArmorMaterialFalmerHeavyOriginal".GetFormKey(), Items = [ "ChaurusChitin".GetFormKey() ],    Perks = [ "ElvenSmithing".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_golden",      Kwda = "cc_ArmorMaterialGolden".GetFormKey(),               Items = [ "IngotGold".GetFormKey() ],        Perks = [ "DaedricSmithing".GetFormKey() ]                             },
            new StaticsData{ Id = "mat_imperialh",   Kwda = "ArmorMaterialImperialHeavy".GetFormKey(),           Items = [ "IngotSteel".GetFormKey() ],       Perks = [ "SteelSmithing".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_iron",        Kwda = "ArmorMaterialIron".GetFormKey(),                    Items = [ "IngotIron".GetFormKey() ],        Perks = [ ]                                                            },
            new StaticsData{ Id = "mat_ironb",       Kwda = "ArmorMaterialIronBanded".GetFormKey(),              Items = [ "IngotIron".GetFormKey() ],        Perks = [ ]                                                            },
            new StaticsData{ Id = "mat_madness",     Kwda = "cc_ArmorMaterialMadness".GetFormKey(),              Items = [ "cc_IngotMadness".GetFormKey() ],  Perks = [ "EbonySmithing".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_nordic",      Kwda = "DLC2ArmorMaterialNordicHeavy".GetFormKey(),         Items = [ "IngotQuicksilver".GetFormKey() ], Perks = [ "AdvancedArmors".GetFormKey() ]                              },
            new StaticsData{ Id = "mat_orcish",      Kwda = "ArmorMaterialOrcish".GetFormKey(),                  Items = [ "IngotOrichalcum".GetFormKey() ],  Perks = [ "OrcishSmithing".GetFormKey() ]                              },
            new StaticsData{ Id = "mat_stalhrimh",   Kwda = "DLC2ArmorMaterialStalhrimHeavy".GetFormKey(),       Items = [ "DLC2OreStalhrim".GetFormKey() ],  Perks = [ "GlassSmithing".GetFormKey(), "EbonySmithing".GetFormKey() ] },
            new StaticsData{ Id = "mat_steel",       Kwda = "ArmorMaterialSteel".GetFormKey(),                   Items = [ "IngotSteel".GetFormKey() ],       Perks = [ "SteelSmithing".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_steelp",      Kwda = "ArmorMaterialSteelPlate".GetFormKey(),              Items = [ "IngotSteel".GetFormKey() ],       Perks = [ "AdvancedArmors".GetFormKey() ]                              },
            new StaticsData{ Id = "mat_wolf",        Kwda = "WAF_ArmorWolf".GetFormKey(),                        Items = [ "IngotSteel".GetFormKey() ],       Perks = [ "SteelSmithing".GetFormKey() ]                               }
        ];

        List<StaticsData> lightMaterials = [
            new StaticsData{ Id = "mat_amber",       Kwda = "cc_ArmorMaterialAmber".GetFormKey(),          Items = [ "cc_IngotAmber".GetFormKey() ],    Perks = [ "GlassSmithing".GetFormKey(), "EbonySmithing".GetFormKey() ]         },
            new StaticsData{ Id = "mat_bonemold",    Kwda = "DLC2ArmorMaterialBonemoldLight".GetFormKey(), Items = [ "DLC2NetchLeather".GetFormKey() ], Perks = [ "AdvancedArmors".GetFormKey() ]                                      },
            new StaticsData{ Id = "mat_chitin",      Kwda = "DLC2ArmorMaterialChitinLight".GetFormKey(),   Items = [ "DLC2ChitinPlate".GetFormKey() ],  Perks = [ "ElvenSmithing".GetFormKey() ]                                       },
            new StaticsData{ Id = "mat_dark",        Kwda = "cc_ArmorMaterialDark".GetFormKey(),           Items = [ "IngotQuicksilver".GetFormKey() ], Perks = [ "DaedricSmithing".GetFormKey() ]                                     },
            new StaticsData{ Id = "mat_dragonscale", Kwda = "ArmorMaterialDragonscale".GetFormKey(),       Items = [ "DragonScales".GetFormKey() ],     Perks = [ "DragonArmor".GetFormKey() ]                                         },
            new StaticsData{ Id = "mat_elven",       Kwda = "ArmorMaterialElven".GetFormKey(),             Items = [ "IngotMoonstone".GetFormKey() ],   Perks = [ "ElvenSmithing".GetFormKey() ]                                       },
            new StaticsData{ Id = "mat_elveng",      Kwda = "ArmorMaterialElvenGilded".GetFormKey(),       Items = [ "IngotMoonstone".GetFormKey() ],   Perks = [ "ElvenSmithing".GetFormKey() ]                                       },
            new StaticsData{ Id = "mat_forsworn",    Kwda = "ArmorMaterialForsworn".GetFormKey(),          Items = [ "LeatherStrips".GetFormKey() ],    Perks = [ ]                                                                    },
            new StaticsData{ Id = "mat_glass",       Kwda = "ArmorMaterialGlass".GetFormKey(),             Items = [ "IngotMalachite".GetFormKey() ],   Perks = [ "GlassSmithing".GetFormKey() ]                                       },
            new StaticsData{ Id = "mat_guard",       Kwda = "WAF_ArmorMaterialGuard".GetFormKey(),         Items = [ "IngotIron".GetFormKey() ],        Perks = [ "skyre_SMTLeathercraft".GetFormKey(), "SteelSmithing".GetFormKey() ] },
            new StaticsData{ Id = "mat_hide",        Kwda = "ArmorMaterialHide".GetFormKey(),              Items = [ "LeatherStrips".GetFormKey() ],    Perks = [ ]                                                                    },
            new StaticsData{ Id = "mat_imperial",    Kwda = "ArmorMaterialImperialLight".GetFormKey(),     Items = [ "IngotSteel".GetFormKey() ],       Perks = [ "skyre_SMTLeathercraft".GetFormKey(), "SteelSmithing".GetFormKey() ] },
            new StaticsData{ Id = "mat_imperials",   Kwda = "ArmorMaterialImperialStudded".GetFormKey(),   Items = [ "LeatherStrips".GetFormKey() ],    Perks = [ "skyre_SMTLeathercraft".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_leather",     Kwda = "ArmorMaterialLeather".GetFormKey(),           Items = [ "LeatherStrips".GetFormKey() ],    Perks = [ "skyre_SMTLeathercraft".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_nightingale", Kwda = "ArmorNightingale".GetFormKey(),               Items = [ "LeatherStrips".GetFormKey() ],    Perks = [ "skyre_SMTLeathercraft".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_scaled",      Kwda = "ArmorMaterialScaled".GetFormKey(),            Items = [ "IngotCorundum".GetFormKey() ],    Perks = [ "AdvancedArmors".GetFormKey() ]                                      },
            new StaticsData{ Id = "mat_shrouded",    Kwda = "ArmorDarkBrotherhood".GetFormKey(),           Items = [ "LeatherStrips".GetFormKey() ],    Perks = [ "skyre_SMTLeathercraft".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_stalhrim",    Kwda = "DLC2ArmorMaterialStalhrimLight".GetFormKey(), Items = [ "DLC2OreStalhrim".GetFormKey() ],  Perks = [ "GlassSmithing".GetFormKey(), "EbonySmithing".GetFormKey() ]         },
            new StaticsData{ Id = "mat_stormcloak",  Kwda = "ArmorMaterialStormcloak".GetFormKey(),        Items = [ "IngotIron".GetFormKey() ],        Perks = [ "skyre_SMTLeathercraft".GetFormKey(), "SteelSmithing".GetFormKey() ] },
            new StaticsData{ Id = "mat_stormcloakh", Kwda = "ArmorMaterialBearStormcloak".GetFormKey(),    Items = [ "IngotSteel".GetFormKey() ],       Perks = [ "skyre_SMTLeathercraft".GetFormKey(), "SteelSmithing".GetFormKey() ] },
            new StaticsData{ Id = "mat_studded",     Kwda = "ArmorMaterialStudded".GetFormKey(),           Items = [ "LeatherStrips".GetFormKey() ],    Perks = [ "skyre_SMTLeathercraft".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_thievesgl",   Kwda = "ArmorMaterialThievesGuild".GetFormKey(),      Items = [ "LeatherStrips".GetFormKey() ],    Perks = [ "skyre_SMTLeathercraft".GetFormKey() ]                               },
            new StaticsData{ Id = "mat_vampire",     Kwda = "DLC1ArmorMaterialVampire".GetFormKey(),       Items = [ "LeatherStrips".GetFormKey() ],    Perks = [ "skyre_SMTLeathercraft".GetFormKey() ]                               }
        ];

        allMaterials.AddRange(lightMaterials);
        List<StaticsData> factionBinds = [
            new StaticsData{ Id = "fact_bandit",     Kwda = "skyre_SPCMasqueradeBandit".GetFormKey()     },
            new StaticsData{ Id = "fact_forsworn",   Kwda = "skyre_SPCMasqueradeForsworn".GetFormKey()   },
            new StaticsData{ Id = "fact_imperial",   Kwda = "skyre_SPCMasqueradeImperial".GetFormKey()   },
            new StaticsData{ Id = "fact_stormcloak", Kwda = "skyre_SPCMasqueradeStormcloak".GetFormKey() },
            new StaticsData{ Id = "fact_thalmor",    Kwda = "skyre_SPCMasqueradeThalmor".GetFormKey()    }
        ];

        return (allMaterials, lightMaterials, factionBinds);
    }
}

