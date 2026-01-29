using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using ReProccer.Utils;
using System.Text.Json.Nodes;

namespace ReProccer.Patchers;

public static class ProjectilesPatcher
{
    private static readonly Settings.AllSettings Settings = Executor.Settings!;
    private static readonly JsonObject Rules = Executor.Rules!["projectiles"]!.AsObject();
    private static readonly (List<StaticsData> AllMaterials,
                             List<StaticsData> ArrowVariants,
                             List<StaticsData> BoltVariants) Statics = BuildStaticsMap();

    private static EditorIDs EditorIDs;              // tracker to ensure editorIDs uniqueness for new records
    private static RecordData PatchingData;          // frequently requested data for current record
    private static readonly List<Report> Logs = [];  // list of logs for current record and records created from it


    private static StaticsData AmmoMaterial = new(); // material data used a lot in this patcher, storing it globally
    private enum AmmoGroup
    {
        Vanilla,
        Reforged,
        DoubleReforged,
    }

    public static void Run()
    {
        EditorIDs = new EditorIDs();

        List<IAmmunitionGetter> records = GetRecords();
        List<string> blacklist = [.. Rules["excludedAmmunitionVariants"]!.AsArray().Select(value => value!.GetValue<string>())];

        foreach (var ammo in records)
        {
            PatchingData = new RecordData
            {
                Log = new Logger(),
                IsArrow = ammo.Flags.HasFlag(Ammunition.Flag.NonBolt),
                NonPlayable = ammo.MajorFlags.HasFlag(Ammunition.MajorFlag.NonPlayable) || ammo.Flags.HasFlag(Ammunition.Flag.NonPlayable),
                Unique = true
            };

            Logs.Add(new Report { Record = ammo, Entry = PatchingData.Log });

            AmmoMaterial = TryGetMaterial(ammo);

            if (!PatchingData.NonPlayable) ProcessRecipes(ammo);
            PatchAmmunition(ammo, PatchingData.Log);
            if (!PatchingData.NonPlayable && !PatchingData.Unique) 
                GenerateAmmunition(ammo, blacklist);

            ShowReport();
        }
    }

    /// <summary>
    /// Records loader.
    /// </summary>
    /// <returns>The list of weapon records eligible for patching.</returns>
    private static List<IAmmunitionGetter> GetRecords()
    {
        IEnumerable<IAmmunitionGetter> conflictWinners = Executor.State!.LoadOrder.PriorityOrder
            .Where(plugin => !Settings.General.IgnoredFiles.Any(name => name == plugin.ModKey.FileName))
            .Where(plugin => plugin.Enabled)
            .WinningOverrides<IAmmunitionGetter>();

        List<IAmmunitionGetter> validRecords = [];
        List<string> excludedNames = [.. Rules["excludedAmmunition"]!.AsArray().Select(value => value!.GetValue<string>())];

        foreach (var record in conflictWinners)
        {
            if (IsValid(record, excludedNames)) validRecords.Add(record);
        }

        Console.WriteLine($"\n~~~ {validRecords.Count} of {conflictWinners.Count()} ammunition records are eligible for patching ~~~\n\n"
            + "====================");
        return validRecords;
    }

    /// <summary>
    /// Checks if the record matches necessary conditions to be patched.
    /// </summary>
    /// <param name="ammo">Processed record.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    /// <returns>Check result as bool.</returns>
    private static bool IsValid(IAmmunitionGetter ammo, List<string> excludedNames)
    {
        Logger log = new();
        Logs.Add(new Report { Record = ammo, Entry = log });

        // found in the excluded records list by edid
        if (Settings.General.ExclByEdID && ammo.EditorID!.IsExcluded(excludedNames, true))
        {
            if (Settings.Debug.ShowExcluded)
            {
                log.Info($"Found in the \"No patching\" list by EditorID");
                ShowReport();
            }
            return false;
        }

        // has no name
        if (ammo.Name is null) return false;

        // found in the excluded records list by name
        if (ammo.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
            {
                log.Info($"Found in the \"No patching\" list by name");
                ShowReport();
            }
            return false;
        }

        // has no keywords or kws array is empty (rare)
        if (ammo.Keywords is null || ammo.Keywords.Count == 0) return false;

        return true;
    }

    /// <summary>
    /// Returns a material data struct for the ammo.<br/>
    /// </summary>
    /// <param name="ammo">Processed ammo record.</param>
    /// <returns>StaticsData with ammo material data, or empty StaticsData if no material was detected.</returns>
    private static StaticsData TryGetMaterial(IAmmunitionGetter ammo)
    {
        StaticsData material = Statics.AllMaterials.FirstOrDefault(material => ammo.Keywords!.Contains(material.Kwda!));
        if (material.Id is null)
            material = Statics.AllMaterials.FirstOrDefault(material => material.Id.GetT9n().RegexMatch(ammo.Name!.ToString()!, true));
        if (material.Id is null)
        {
            Executor.State!.LinkCache.TryResolve<IProjectileGetter>(ammo.Projectile.FormKey, out var proj);
            if (proj is not null && proj.Name is not null)
                material = Statics.AllMaterials.FirstOrDefault(material => material.Id.GetT9n().RegexMatch(proj.Name!.ToString()!, true));
        }

        return material.Id is not null ? material : new StaticsData { Perks = [] };
    }

    /// <summary>
    /// Initiates recipes processing.
    /// </summary>
    /// <param name="ammo">Processed ammo record.</param>
    private static void ProcessRecipes(IAmmunitionGetter ammo)
    {
        foreach (var recipe in Executor.AllRecipes!)
        {
            if (recipe.CreatedObject.FormKey == ammo.FormKey && !recipe.WorkbenchKeyword.IsNull)
                ModCraftingRecipe(recipe);
        }
    }

    /// <summary>
    /// Modifies ammo crafting recipes.<br/>
    /// </summary>
    /// <param name="recipe">Processed recipe record.</param>
    private static void ModCraftingRecipe(IConstructibleObjectGetter recipe)
    {
        // ammo without crafting recipes (unique) cannot have special ammo variaties
        PatchingData.Unique = false;
        PatchingData.Modified = false;

        ConstructibleObject newRecipe = Executor.State!.PatchMod.ConstructibleObjects.GetOrAddAsOverride(recipe);

        // AmmoMaterial.Perks is used later, making a copy to keep original perks list intact
        List<FormKey> perks = [];
        AmmoMaterial.Perks.ForEach(perks.Add);

        if (newRecipe.Conditions.Count > 0)
        {
            if (!Settings.Projectiles.KeepConditions)
            {
                for (int i = newRecipe.Conditions.Count - 1; i >= 0; i--)
                {
                    // the condition is not of HasPerk type or specified perk is not in the list of all material perks
                    if (newRecipe.Conditions[i].Data is not HasPerkConditionData hasPerk || 
                        Statics.AllMaterials.All(entry => entry.Perks.All(perk => perk != hasPerk.Perk.Link.FormKey)))
                    {
                        newRecipe.Conditions.Remove(newRecipe.Conditions[i]);
                    }
                    else
                    {
                        // the condition already have a valid material perk specified
                        if (perks.Contains(hasPerk.Perk.Link.FormKey))
                        {
                            perks.Remove(hasPerk.Perk.Link.FormKey);
                        }
                        // the condition have the material perk specified,
                        // but it's not related to the ammo material in SkyRe system
                        else if (perks.Count > 0)
                        {
                            newRecipe.Conditions.Remove(newRecipe.Conditions[i]);
                        }
                    }
                }

            }
            else
            {
                // a condition of HasPerk type with any material perk specified already exists
                if (newRecipe.Conditions.Any(cond => cond.Data is HasPerkConditionData hasPerk &&
                    Statics.AllMaterials.Any(entry => entry.Perks.Any(perk => perk == hasPerk.Perk.Link.FormKey))))
                {
                    perks = [];
                }
            }
        }

        if (perks.Count > 0)
        {
            foreach (var perk in perks)
            {
                Condition.Flag flag = perks.IndexOf(perk) == perks.Count - 1 ? 0 : Condition.Flag.OR;
                newRecipe.AddHasPerkCondition(perk, flag);
                PatchingData.Modified = true;
            }
        }

        // the ammo is a bolt and there's no condition of HasPerk type with the Ballistics perk
        if (!PatchingData.IsArrow && 
            newRecipe.Conditions.All(cond => cond.Data is not HasPerkConditionData hasPerk || 
            hasPerk.Perk.Link.FormKey != "skyre_MARBallistics".GetFormKey()))
        {
            newRecipe.AddHasPerkCondition("skyre_MARBallistics".GetFormKey(), 0, 0);
            PatchingData.Modified = true;
        }

        if (!PatchingData.Modified)
            Executor.State!.PatchMod.ConstructibleObjects.Remove(newRecipe);
    }

    /// <summary>
    /// Modifies ammo and projectiles data.<br/>
    /// </summary>
    /// <param name="ammo">Processed ammo record.</param>
    /// <param name="log">Processed ammo message log.</param>
    /// <param name="expl">Ammo projectile explosion (if any).</param>
    /// <param name="type">Ammo group as enumerator.</param>
    private static void PatchAmmunition(IAmmunitionGetter ammo, Logger log, IExplosionGetter? expl = null, AmmoGroup type = AmmoGroup.Vanilla)
    {
        Executor.State!.LinkCache.TryResolve<IProjectileGetter>(ammo.Projectile.FormKey, out var proj);
        if (proj is null)
        {
            log.Error("No projectile attached to this ammo");
            return;
        }

        if (proj.Type != Projectile.TypeEnum.Arrow && proj.Type != Projectile.TypeEnum.Missile)
        {
            log.Error($"The projectile has unexpected type ({proj.Type})");
            return;
        }

        // projectile data
        Projectile? newProj = null;

        float newRange = 120000f;

        float baseGravity = GetBaseData("gravity", proj, ammo, PatchingData.IsArrow ? 0.25f : 0.2f, log);
        float materialGravity = GetMaterialData("gravity", proj, ammo, 0, AmmoMaterial);
        float modifierGravity = GetModifierData("gravity", proj, ammo, 0);
        float newGravity = (baseGravity + materialGravity + modifierGravity)
            .Validate(proj.Gravity, nameof(proj.Gravity), PatchingData.IsArrow ? 0.25f : 0.2f, log);

        float baseSpeed = GetBaseData("speed", proj, ammo, PatchingData.IsArrow ? 5200f : 6500f, log);
        float materialSpeed = GetMaterialData("speed", proj, ammo, 0, AmmoMaterial);
        float modifierSpeed = GetModifierData("speed", proj, ammo, 0);
        float newSpeed = (baseSpeed + materialSpeed + modifierSpeed)
            .Validate(proj.Speed, nameof(proj.Speed), PatchingData.IsArrow ? 5200f : 6500f, log);

        if ((newRange != proj.Range) || (newGravity != proj.Gravity) || (newSpeed != proj.Speed) || type != AmmoGroup.Vanilla)
        {
            newProj = Executor.State!.PatchMod.Projectiles.GetOrAddAsOverride(proj);

            newProj.Range = newRange;
            newProj.Gravity = newGravity;
            newProj.Speed = newSpeed.Validate(proj.Speed, nameof(proj.Speed), PatchingData.IsArrow ? 5200f : 6500f, log);

            if (expl is not null)
            {
                newProj.Flags |= Projectile.Flag.Explosion;
                newProj.Flags &= ~Projectile.Flag.CanBePickedUp;
                newProj.Explosion = expl.ToLink();
            }
        }

        // ammo data
        Ammunition? newAmmo = null;

        // for double-reforged ammo base damage value is the reforged parent's damage value
        float baseDamage = 
            (type == AmmoGroup.DoubleReforged) ? ammo.Damage : GetBaseData("damage", proj, ammo, PatchingData.IsArrow ? 7f : 13f, log);
        // for double-reforged ammo material influence should be 0
        float materialDamage = 
            (type == AmmoGroup.DoubleReforged) ? 0 : GetMaterialData("damage", proj, ammo, 0, AmmoMaterial);
        float modifierDamage = GetModifierData("damage", proj, ammo, 0);
        float newDamage = baseDamage + materialDamage + modifierDamage;
        newDamage = newDamage.Validate(ammo.Damage, "Damage", PatchingData.IsArrow ? 7f : 13f, log);

        float modWeight = GetModifierData("weightMult", proj, ammo, 1f);
        float modValue = GetModifierData("priceMult", proj, ammo, 1f);

        if ((newDamage != ammo.Damage) || (modWeight != 1f) || (modValue != 1f) || type != AmmoGroup.Vanilla)
        {
            newAmmo = Executor.State!.PatchMod.Ammunitions.GetOrAddAsOverride(ammo);

            modWeight = modWeight < 0 ? 1f : modWeight;
            modValue = modValue < 0 ? 1f : modValue;

            newAmmo.Damage = newDamage;
            newAmmo.Weight *= modWeight;
            newAmmo.Value = (uint)(ammo.Value * modValue);
        }

        if (Settings.Debug.ShowVerboseData)
        {
            if (type == 0)
            {
                if (newAmmo is not null) log.Info($"Damage: {ammo.Damage} -> {newAmmo.Damage} " +
                    $"(base: {baseDamage}, material: {materialDamage}, modifier: {modifierDamage})");
                if (newProj is not null) log.Info($"Gravity: {(decimal)proj!.Gravity} -> {(decimal)newProj.Gravity} " +
                    $"(base: {baseGravity}, material: {materialGravity}, modifier: {modifierGravity})");
                if (newProj is not null) log.Info($"Speed: {proj!.Speed} -> {newProj.Speed} " +
                    $"(base: {baseSpeed}, material: {materialSpeed}, modifier: {modifierSpeed})");
                if (newAmmo is not null && modWeight != 1f) log.Info($"Weight: {Math.Round(ammo.Weight, 2)} -> {(decimal)newAmmo.Weight} " +
                    $"(original weight x {modWeight})");
                if (newAmmo is not null && modValue != 1f) log.Info($"Value: {ammo.Value} -> {newAmmo.Value} " +
                    $"(original price x {modValue})");
            }
            else
            {
                if (newAmmo is not null) log.Info($"Damage: {newAmmo.Damage} " + $"(original: {baseDamage + materialDamage}, modifier: {modifierDamage})");
                if (newProj is not null) log.Info($"Gravity: {(decimal)newProj!.Gravity} " + $"(original: {(decimal)(baseGravity + materialGravity)}, modifier: {modifierGravity})");
                if (newProj is not null) log.Info($"Speed: {newProj!.Speed} " + $"(original: {baseSpeed + materialSpeed}, modifier: {modifierSpeed})");
                if (newAmmo is not null && modWeight != 1f) log.Info($"Weight: {Math.Round(newAmmo.Weight, 2)} " + $"(original, mult: x{modWeight})");
                if (newAmmo is not null && modValue != 1f) log.Info($"Value: {newAmmo.Value} " + $"(original, mult: x{modValue})");
            }
        }
    }

    /// <summary>
    /// Validates data returned from rulesets.<br/>
    /// </summary>
    /// <param name="newValue">New data value.</param>
    /// <param name="oldValue">Current data value.</param>
    /// <param name="name">Data name.</param>
    /// <param name="fallback">Fallback value if no rule was found or received value is incorrect.</param>
    /// <param name="log">Processed ammo message log.</param>
    /// <returns>The newValue if met the validation criteria, otherwise oldValue/fallback value.</returns>
    private static float Validate(this float newValue, float oldValue, string name, float fallback, Logger log)
    {
        if (newValue <= 0)
        {
            log.Error($"{name} cannot be 0 and less! The fallback value of {fallback} will be used instead");
            return fallback;
        }

        if (name == "Gravity" && oldValue == 0)
        {
            log.Caution("Original gravity is 0, but most likely on purpose, and will not be changed");
            return oldValue;
        }

        if (name == "Speed" && newValue * 2 < oldValue)
        {
            log.Caution("Original speed is very high, but most likely on purpose, and will not be changed");
            return oldValue;
        }

        if (name == "Damage" && (oldValue == 0 || oldValue == 1))
        {
            log.Caution("Original damage is 0 or 1, but most likely on purpose, and will not be changed");
            return oldValue;
        }

        return newValue;
    }

    /// <summary>
    /// Returns patching data from the baseStats ruleset.<br/>
    /// </summary>
    /// <param name="data">Name of the data to obtain.</param>
    /// <param name="ammo">Processed ammo record.</param>
    /// <param name="proj">Processed ammo projectile.</param>
    /// <param name="fallback">Fallback value if no rule is found.</param>
    /// <param name="log">Processed ammo message log.</param> 
    /// <returns>The data value or fallback value, as float.</returns>
    private static float GetBaseData(string data, IProjectileGetter proj, IAmmunitionGetter ammo, float fallback, Logger log)
    {
        JsonNode? baseNode = Helpers.RuleByName(ammo.Name!.ToString()!, Rules["baseStats"]!.AsArray(), data1: "names", data2: data, true);
        if (baseNode is null && proj.Name is not null)
            baseNode = Helpers.RuleByName(proj.Name!.ToString()!, Rules["baseStats"]!.AsArray(), data1: "names", data2: data, true);
        float? baseData = baseNode?.AsType<float>();

        if (baseData is null) log.Error($"Unable to determine ammo type {data}; the fallback value of {fallback} will be used");

        return baseData ?? fallback;
    }

    /// <summary>
    /// Returns patching data from the materialStats ruleset.<br/>
    /// </summary>
    /// <param name="data">Name of the data to obtain.</param>
    /// <param name="ammo">Processed ammo record.</param>
    /// <param name="proj">Processed ammo projectile.</param>
    /// <param name="fallback">Fallback value if no rule is found.</param>
    /// <param name="material">Material StaticsData with ammo material data.</param>
    /// <returns>The data value or fallback value, as float.</returns>
    private static float GetMaterialData(string data, IProjectileGetter proj, IAmmunitionGetter ammo, float fallback, StaticsData material = new StaticsData())
    {
        JsonNode? materialNode = Helpers.RuleByName(ammo.Name!.ToString()!, Rules["materialStats"]!.AsArray(), data1: "names", data2: data, true);
        if (materialNode is null && material.Id is not null)
            materialNode = Helpers.RuleByName(material.Id, Rules["materialStats"]!.AsArray(), data1: "id", data2: data, true);
        if (materialNode is null && proj.Name is not null)
            materialNode = Helpers.RuleByName(proj.Name!.ToString()!, Rules["materialStats"]!.AsArray(), data1: "names", data2: data, true);
        float? materialData = materialNode?.AsType<float>();

        return materialData ?? fallback;
    }

    /// <summary>
    /// Returns patching data from the modifierStats ruleset.<br/>
    /// </summary>
    /// <param name="data">Name of the data to obtain.</param>
    /// <param name="ammo">Processed ammo record.</param>
    /// <param name="proj">Processed ammo projectile.</param>
    /// <param name="fallback">Fallback value if no rule is found.</param>
    /// <returns>The data value or fallback value, as float.</returns>
    private static float GetModifierData(string data, IProjectileGetter proj, IAmmunitionGetter ammo, float fallback)
    {
        JsonNode? modifierNode = Helpers.RuleByName(ammo.Name!.ToString()!, Rules["modifierStats"]!.AsArray(), data1: "names", data2: data, true);
        if (modifierNode is null && proj.Name is not null)
            modifierNode = Helpers.RuleByName(proj.Name!.ToString()!, Rules["modifierStats"]!.AsArray(), data1: "names", data2: data, true);
        float? modifierData = modifierNode?.AsType<float>();

        return modifierData ?? fallback;
    }

    /// <summary>
    /// Initiates special types creation for the ammo record.<br/>
    /// </summary>
    /// <param name="ammo">Processed ammo record.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    private static void GenerateAmmunition(IAmmunitionGetter ammo, List<string> excludedNames)
    {
        if (Settings.General.ExclByEdID && ammo.EditorID!.IsExcluded(excludedNames, true))
        {
            if (Settings.Debug.ShowExcluded)
            {
                PatchingData.Log.Info($"Found in the \"No special variants\" list by EditorID", false, true);
                return;
            }
        }

        if (ammo.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded) PatchingData.Log.Info($"Found in the \"No special variants\" list by name", false, true);
            return;
        }

        if (!PatchingData.IsArrow) CreateHardenedBolts(ammo);
        CreateSpecialAmmo(ammo, AmmoGroup.Reforged);
    }

    /// <summary>
    /// Creates hardened and tempered types (bolts only) for the ammo record.<br/>
    /// </summary>
    /// <param name="ammo">Processed ammo record.</param>
    private static void CreateHardenedBolts(IAmmunitionGetter ammo)
    {
        List<RecipeData> ingredients = [
            new RecipeData{Items = [ "DwarvenOil".GetFormKey() ], Qty = Settings.Projectiles.IngredsQty },
            new RecipeData{Items = [ AmmoMaterial.Items is null ? "IngotCorundum".GetFormKey() : AmmoMaterial.Items[0] ], 
                Qty = Settings.Projectiles.IngredsQty }
        ];

        List<FormKey> perks = ["skyre_MARAdvancedFletching0".GetFormKey() ];
        AmmoMaterial.Perks.ForEach(perks.Add);

        IAmmunitionGetter? hardBolt = FletchAmmo(ammo, "ammo_hardened", null, null, null, AmmoGroup.Reforged);
        if (hardBolt is not null)
        {
            AddCraftingRecipe(hardBolt, ammo, perks, ingredients);
            CreateSpecialAmmo(hardBolt, AmmoGroup.DoubleReforged);
        }

        IAmmunitionGetter? tempBolt = FletchAmmo(ammo, "ammo_tempered", null, null, null, AmmoGroup.Reforged);
        if (tempBolt is not null)
        {
            AddCraftingRecipe(tempBolt, hardBolt!, perks, ingredients);
            CreateSpecialAmmo(tempBolt, AmmoGroup.DoubleReforged);
        }
    }

    /// <summary>
    /// Creates other special types of bolts and arrows for the ammo record.<br/>
    /// </summary>
    /// <param name="ammo">An ammo to create special types for.</param>
    /// <param name="type">Ammo group as enumerator (to pass onward).</param>
    private static void CreateSpecialAmmo(IAmmunitionGetter ammo, AmmoGroup group)
    {
        List<StaticsData> variantsMap = PatchingData.IsArrow ? Statics.ArrowVariants : Statics.BoltVariants;

        foreach (var variant in variantsMap)
        {
            List<FormKey> perks = [];
            variant.Perks.ForEach(perks.Add);

            if ((variant.Id == "ammo_barbed" || variant.Id == "ammo_hweight") && AmmoMaterial.Perks.Count > 0)
                AmmoMaterial.Perks.ForEach(perks.Add);

            List<RecipeData> ingredients = [
                new RecipeData{ Items = [ variant.Items[0] ], Qty = Settings.Projectiles.IngredsQty },
                new RecipeData{ Items = [ variant.Items[1] ], Qty = Settings.Projectiles.IngredsQty }
            ];

            Executor.State!.LinkCache.TryResolve<IKeywordGetter>(variant.Kwda, out var kwda);
            Executor.State!.LinkCache.TryResolve<IExplosionGetter>(variant.Expl, out var expl);

            IAmmunitionGetter? newAmmo = FletchAmmo(ammo, variant.Id, variant.Desc, kwda, expl, group);
            if (newAmmo is not null)
                AddCraftingRecipe(newAmmo, ammo, perks, ingredients);
        }
    }

    /// <summary>
    /// Creates an ammo record and a projectile record for it.<br/>
    /// </summary>
    /// <param name="ammo">Parent ammo record.</param>
    /// <param name="typeId">New ammo type ID.</param>
    /// <param name="desc">Ingame decription for the new ammo, if any.</param>
    /// <param name="kwda">New ammo type keyword, if any.</param>
    /// <param name="expl">New ammo type explosion, if any.</param>
    /// <param name="group">Ammo group as enumerator (passed onward).</param>
    /// <returns>A new ammo.</returns>
    private static Ammunition? FletchAmmo(IAmmunitionGetter ammo, string typeId, string? desc, 
        IKeywordGetter? kwda, IExplosionGetter? expl, AmmoGroup group)
    {
        Executor.State!.LinkCache.TryResolve<IProjectileGetter>(ammo.Projectile.FormKey, out var baseProj);
        if (baseProj is null) return null;

        Ammunition newAmmo = Executor.State!.PatchMod.Ammunitions.DuplicateInAsNewRecord(ammo);

        newAmmo.Name = ammo.Name! + $" - {typeId.GetT9n()}";
        string newEditorID = (ammo.EditorID!.Contains("RP_AMMO_") ? "" : "RP_AMMO_") + ammo.EditorID! + $"_{typeId.GetT9n("english")}";
        newAmmo.EditorID = EditorIDs.Unique(newEditorID);

        if (desc is not null) newAmmo.Description = desc.GetT9n();
        if (kwda is not null) newAmmo.Keywords!.Add(kwda);

        Projectile newProj = Executor.State!.PatchMod.Projectiles.DuplicateInAsNewRecord(baseProj);
        newAmmo.Projectile = newProj.ToLink();

        newProj.Name = newAmmo.Name;
        newEditorID = newEditorID.Replace("AMMO", "PROJ");
        newProj.EditorID = EditorIDs.Unique(newEditorID);

        Logger log = new();
        Logs.Add(new Report { Record = newAmmo, Entry = log });

        PatchAmmunition(newAmmo, log, expl, group);
        return newAmmo;
    }

    /// <summary>
    /// Creates a crafting recipe record for the ammo type.<br/>
    /// </summary>
    /// <param name="newAmmo">The ammo to create.</param>
    /// <param name="oldAmmo">Parent ammo, the recipe ingredient.</param>
    /// <param name="perks">List of type and material perks FormKeys.</param>
    /// <param name="ingredients">List of other recipe ingredients, and their quantity.</param>
    private static void AddCraftingRecipe(IAmmunitionGetter newAmmo, IAmmunitionGetter oldAmmo, List<FormKey> perks, List<RecipeData> ingredients)
    {
        ConstructibleObject newRecipe = Executor.State!.PatchMod.ConstructibleObjects.AddNew();
        string newEditorID = "RP_AMMO_CRAFT_" + newAmmo.EditorID!.Replace("RP_AMMO_", "");
        newRecipe.EditorID = EditorIDs.Unique(newEditorID);
        newRecipe.Items = [];

        ContainerItem baseItem = new();
        baseItem.Item = oldAmmo.ToNullableLink();
        ContainerEntry baseEntry = new();
        baseEntry.Item = baseItem;
        baseEntry.Item.Count = Settings.Projectiles.AmmoQty;
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

            ContainerEntry newEntry = new();
            newEntry.Item = newItem;
            newEntry.Item.Count = Settings.Projectiles.IngredsQty;
            newRecipe.Items.Add(newEntry);
        }

        perks.ForEach(perk => newRecipe.AddHasPerkCondition(perk));

        if (!Settings.Projectiles.AllAmmoRecipes)
            newRecipe.AddGetItemCountCondition(oldAmmo.FormKey, CompareOperator.GreaterThanOrEqualTo, 0, -1, Settings.Projectiles.AmmoQty);

        newRecipe.CreatedObject = newAmmo.ToNullableLink();
        if (newRecipe.WorkbenchKeyword.IsNull) newRecipe.WorkbenchKeyword =
            Executor.State!.LinkCache.Resolve<IKeywordGetter>("CraftingSmithingForge".GetFormKey()).ToNullableLink();
        newRecipe.CreatedObjectCount = (ushort)Settings.Projectiles.AmmoQty;
    }

    // patcher specific helpers

    /// <summary>
    /// Displays patching results for the current record and records created on its basis.<br/>
    /// </summary>
    private static void ShowReport()
    {
        foreach (var report in Logs)
        {
            IAmmunitionGetter ammo = (IAmmunitionGetter)report.Record!;
            Logger log = (Logger)report.Entry!;
            log.Report($"{ammo.Name}", $"{ammo.FormKey}", $"{ammo.EditorID}", PatchingData.NonPlayable, false);
        }

        Logs.Clear();
    }

    // patcher specific statics

    /// <summary>
    /// Appends patcher-specific records to the shared statics list, generates patcher-specific collections of statics.<br/>
    /// </summary>
    /// <returns>A tuple of statics collections for ammo records: all ammo materials, arrows special types, bolts special types.</returns>
    private static (List<StaticsData>, List<StaticsData>, List<StaticsData>) BuildStaticsMap()
    {
        Executor.Statics!.AddRange(
        [
            new StaticsData{Id = "DLC1BoltExplosionFire02",                FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x010d90|EXPL")               },
            new StaticsData{Id = "DLC1BoltExplosionFrost02",               FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x010d91|EXPL")               },
            new StaticsData{Id = "DLC1BoltExplosionShock02",               FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x010d92|EXPL")               },
            new StaticsData{Id = "skyre_ALCExplosiveArrow0Explosion",      FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000854|EXPL") },
            new StaticsData{Id = "skyre_ALCAshenArrowExplosion",           FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000876|EXPL") },
            new StaticsData{Id = "skyre_ENCAmmoSiphoningKeyword",          FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000b8f|KWDA") },
            new StaticsData{Id = "skyre_MARAmmoBarbedKeyword",             FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d87|KWDA") },
            new StaticsData{Id = "skyre_MARAmmoHeavyKeyword",              FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d88|KWDA") },
            new StaticsData{Id = "FireSalt",                               FormKey = Helpers.ParseFormKey("Skyrim.esm|0x03ad5e|INGR")                  },
            new StaticsData{Id = "FrostSalt",                              FormKey = Helpers.ParseFormKey("Skyrim.esm|0x03ad5f|INGR")                  },
            new StaticsData{Id = "VoidSalt",                               FormKey = Helpers.ParseFormKey("Skyrim.esm|0x03ad60|INGR")                  },
            new StaticsData{Id = "TrollFat",                               FormKey = Helpers.ParseFormKey("Skyrim.esm|0x03ad72|INGR")                  },
            new StaticsData{Id = "glowDust",                               FormKey = Helpers.ParseFormKey("Skyrim.esm|0x03ad73|INGR")                  },
            new StaticsData{Id = "DLC2GhoulAsh",                           FormKey = Helpers.ParseFormKey("Dragonborn.esm|0x01cd6d|INGR")              },
            new StaticsData{Id = "skyre_ALCFuse",                          FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000851|PERK") },
            new StaticsData{Id = "skyre_ALCSuffocation",                   FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000873|PERK") },
            new StaticsData{Id = "skyre_MARAdvancedFletching0",            FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d84|PERK") },
            new StaticsData{Id = "skyre_MARAdvancedFletching1",            FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d85|PERK") },
            new StaticsData{Id = "skyre_MARAdvancedFletching2",            FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d86|PERK") },
            new StaticsData{Id = "skyre_ENCElementalBombard0",             FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000b8d|PERK") },
            new StaticsData{Id = "skyre_ENCElementalBombard1",             FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000b8e|PERK") }
        ]);

        List<StaticsData> allMaterials = [
            new StaticsData{Id = "mat_amber",      Kwda = "cc_WeapMaterialAmber".GetFormKey(),           Items = [ "cc_IngotAmber".GetFormKey() ],    Perks = [ "GlassSmithing".GetFormKey() ]       },
            new StaticsData{Id = "mat_bonemold",   Kwda = "DLC2ArmorMaterialBonemoldLight".GetFormKey(), Items = [ "Firewood01".GetFormKey() ],       Perks = [ "AdvancedArmors".GetFormKey() ]      },
            new StaticsData{Id = "mat_daedric",    Kwda = "WeapMaterialDaedric".GetFormKey(),            Items = [ "IngotEbony".GetFormKey() ],       Perks = [ "DaedricSmithing".GetFormKey() ]     },
            new StaticsData{Id = "mat_dark",       Kwda = "cc_WeapMaterialDark".GetFormKey(),            Items = [ "IngotQuicksilver".GetFormKey() ], Perks = [ "DaedricSmithing".GetFormKey() ]     },
            new StaticsData{Id = "mat_dragonbone", Kwda = "DLC1WeapMaterialDragonbone".GetFormKey(),     Items = [ "DragonBone".GetFormKey() ],       Perks = [ "DragonArmor".GetFormKey() ]         },
            new StaticsData{Id = "mat_draugr",     Kwda = "WeapMaterialDraugr".GetFormKey(),             Items = [ "IngotQuicksilver".GetFormKey() ], Perks = [ "AdvancedArmors".GetFormKey() ]      },
            new StaticsData{Id = "mat_draugrh",    Kwda = "WeapMaterialDraugrHoned".GetFormKey(),        Items = [ "IngotQuicksilver".GetFormKey() ], Perks = [ "AdvancedArmors".GetFormKey() ]      },
            new StaticsData{Id = "mat_dwarven",    Kwda = "WeapMaterialDwarven".GetFormKey(),            Items = [ "IngotDwarven".GetFormKey() ],     Perks = [ "DwarvenSmithing".GetFormKey() ]     },
            new StaticsData{Id = "mat_ebony",      Kwda = "WeapMaterialEbony".GetFormKey(),              Items = [ "IngotEbony".GetFormKey() ],       Perks = [ "EbonySmithing".GetFormKey() ]       },
            new StaticsData{Id = "mat_elven",      Kwda = "WeapMaterialElven".GetFormKey(),              Items = [ "IngotMoonstone".GetFormKey() ],   Perks = [ "ElvenSmithing".GetFormKey() ]       },
            new StaticsData{Id = "mat_falmer",     Kwda = "WeapMaterialFalmer".GetFormKey(),             Items = [ "ChaurusChitin".GetFormKey() ],    Perks = [ "ElvenSmithing".GetFormKey() ]       },
            new StaticsData{Id = "mat_forsworn",   Kwda = "WAF_WeapMaterialForsworn".GetFormKey(),       Items = [ "IngotIron".GetFormKey() ],        Perks = [ /* to avoid null checking */ ]      },
            new StaticsData{Id = "mat_glass",      Kwda = "WeapMaterialGlass".GetFormKey(),              Items = [ "IngotMalachite".GetFormKey() ],   Perks = [ "GlassSmithing".GetFormKey() ]       },
            new StaticsData{Id = "mat_golden",     Kwda = "cc_WeapMaterialGolden".GetFormKey(),          Items = [ "IngotMoonstone".GetFormKey() ],   Perks = [ "DaedricSmithing".GetFormKey() ]     },
            new StaticsData{Id = "mat_iron",       Kwda = "WeapMaterialIron".GetFormKey(),               Items = [ "IngotIron".GetFormKey() ],        Perks = [ /* to avoid null checking */ ]      },
            new StaticsData{Id = "mat_madness",    Kwda = "cc_WeapMaterialMadness".GetFormKey(),         Items = [ "cc_IngotMadness".GetFormKey() ],  Perks = [ "EbonySmithing".GetFormKey() ]       },
            new StaticsData{Id = "mat_nordic",     Kwda = "DLC2WeaponMaterialNordic".GetFormKey(),       Items = [ "IngotQuicksilver".GetFormKey() ], Perks = [ "AdvancedArmors".GetFormKey() ]      },
            new StaticsData{Id = "mat_orcish",     Kwda = "WeapMaterialOrcish".GetFormKey(),             Items = [ "IngotOrichalcum".GetFormKey() ],  Perks = [ "OrcishSmithing".GetFormKey() ]      },
            new StaticsData{Id = "mat_silver",     Kwda = "WeapMaterialSilver".GetFormKey(),             Items = [ "IngotSilver".GetFormKey() ],      Perks = [ "skyre_SMTTradecraft".GetFormKey() ] },
            new StaticsData{Id = "mat_stalhrim",   Kwda = "DLC2WeaponMaterialStalhrim".GetFormKey(),     Items = [ "DLC2OreStalhrim".GetFormKey() ],  Perks = [ "EbonySmithing".GetFormKey() ]       },
            new StaticsData{Id = "mat_steel",      Kwda = "WeapMaterialSteel".GetFormKey(),              Items = [ "IngotSteel".GetFormKey() ],       Perks = [ "SteelSmithing".GetFormKey() ]       }
        ];

        List<StaticsData> arrowVariants = [
            new StaticsData{
                Id = "ammo_ashen",
                Items = [ "DLC2GhoulAsh".GetFormKey(), "TrollFat".GetFormKey() ],
                Desc = "desc_ashen",
                Expl = "skyre_ALCExplosiveArrow0Explosion".GetFormKey(),
                Perks = [ "skyre_ALCSuffocation".GetFormKey() ]
            },
            new StaticsData{
                Id = "ammo_explosive",
                Items = [ "FireSalt".GetFormKey(), "TrollFat".GetFormKey() ],
                Desc = "desc_explosive",
                Expl = "skyre_ALCAshenArrowExplosion".GetFormKey(),
                Perks = [ "skyre_ALCFuse".GetFormKey() ]
            }
        ];

        List<StaticsData> boltVariants = [
            new StaticsData{
                Id = "ammo_barbed",
                Kwda = "skyre_MARAmmoBarbedKeyword".GetFormKey(),
                Items = [ "IngotCorundum".GetFormKey(), "DragonScales".GetFormKey() ],
                Desc = "desc_barbed",
                Perks = [ "skyre_MARAdvancedFletching1".GetFormKey() ]
            },
            new StaticsData{
                Id = "ammo_flame",
                Items = [ "SoulGemPettyFilled".GetFormKey(), "TrollFat".GetFormKey() ],
                Desc = "desc_flame",
                Expl = "DLC1BoltExplosionFire02".GetFormKey(),
                Perks = [ "skyre_ENCElementalBombard0".GetFormKey() ]
            },
            new StaticsData{
                Id = "ammo_frost",
                Items = [ "SoulGemPettyFilled".GetFormKey(), "FrostSalt".GetFormKey() ],
                Desc = "desc_frost",
                Expl = "DLC1BoltExplosionFrost02".GetFormKey(),
                Perks = [ "skyre_ENCElementalBombard0".GetFormKey() ]
            },
            new StaticsData{
                Id = "ammo_hweight",
                Kwda = "skyre_MARAmmoHeavyKeyword".GetFormKey(),
                Items = [ "IngotCorundum".GetFormKey(), "DragonBone".GetFormKey() ],
                Desc = "desc_hweight",
                Perks = [ "skyre_MARAdvancedFletching2".GetFormKey() ]
            },
            new StaticsData{
                Id = "ammo_shock",
                Items = [ "SoulGemPettyFilled".GetFormKey(), "VoidSalt".GetFormKey() ],
                Desc = "desc_shock",
                Expl = "DLC1BoltExplosionShock02".GetFormKey(),
                Perks = [ "skyre_ENCElementalBombard0".GetFormKey() ]
            },
            new StaticsData{
                Id = "ammo_siphon",
                Kwda = "skyre_ENCAmmoSiphoningKeyword".GetFormKey(),
                Items = [ "SoulGemPettyFilled".GetFormKey(), "glowDust".GetFormKey() ],
                Desc = "desc_siphon",
                Perks = [ "skyre_ENCElementalBombard1".GetFormKey() ]
            }
        ];

        return (allMaterials, arrowVariants, boltVariants);
    }
}

