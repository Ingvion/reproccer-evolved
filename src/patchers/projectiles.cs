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
    private static readonly (List<DataMap> AllMaterials,
                             List<DataMap> ArrowVariants,
                             List<DataMap> BoltVariants) Statics = BuildStaticsMap();

    private static EditorIDs EditorIDs;
    private static PatchingData RecordData;
    private static Logger Logger;
    private static readonly List<Report> AllReports = [];

    // material data used a lot here, so we're storing it globally to avoid passing as a parameter
    private static DataMap AmmoMaterial = new();
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
            RecordData = new PatchingData
            {
                IsArrow = ammo.Flags.HasFlag(Ammunition.Flag.NonBolt),
                NonPlayable = ammo.MajorFlags.HasFlag(Ammunition.MajorFlag.NonPlayable) || ammo.Flags.HasFlag(Ammunition.Flag.NonPlayable)
            };

            Logger = new Logger();
            AllReports.Clear();

            AmmoMaterial = TryGetMaterial(ammo);

            if (!RecordData.NonPlayable) ProcessRecipes(ammo);
            PatchAmmunition(ammo);
            if (!RecordData.NonPlayable) GenerateAmmunition(ammo, blacklist);

            ShowReport(ammo);
        }
    }

    /// <summary>
    /// Records loader.
    /// </summary>
    /// <returns>The list of ammo records eligible for patching.</returns>
    private static List<IAmmunitionGetter> GetRecords()
    {
        IEnumerable<IAmmunitionGetter> ammoWinners = Executor.State!.LoadOrder.PriorityOrder
            .Where(plugin => !Settings.General.IgnoredFiles.Any(name => name == plugin.ModKey.FileName))
            .Where(plugin => plugin.Enabled)
            .WinningOverrides<IAmmunitionGetter>();

        List<IAmmunitionGetter> ammoRecords = [];

        List<string> excludedNames = [.. Rules["excludedAmmunition"]!.AsArray().Select(value => value!.GetValue<string>())];
        foreach (var record in ammoWinners)
        {
            if (IsValid(record, excludedNames)) ammoRecords.Add(record);
        }

        Console.WriteLine($"\n~~~ {ammoRecords.Count} of {ammoWinners.Count()} ammunition records are eligible for patching ~~~\n\n"
            + "====================");
        return ammoRecords;
    }

    /// <summary>
    /// Checks if the ammo matches necessary conditions to be patched.
    /// </summary>
    /// <param name="ammo">Processed ammo record.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    /// <returns>Check result from a filter the record triggered as bool.</returns>
    private static bool IsValid(IAmmunitionGetter ammo, List<string> excludedNames)
    {
        Logger = new Logger();

        // invalid if found in the excluded records list by edid
        if (Settings.General.ExclByEdID && ammo.EditorID!.IsExcluded(excludedNames, true))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Logger.Info($"Found in the \"No patching\" list by EditorID");
                ShowReport(ammo);
            }
            return false;
        }

        // invalid if has no name
        if (ammo.Name is null) return false;

        // invalid if found in the excluded records list by name
        if (ammo.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Logger.Info($"Found in the \"No patching\" list by name");
                ShowReport(ammo);
            }
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns a material DataMap for the processed ammo.<br/>
    /// </summary>
    /// <param name="ammo">Processed ammo record.</param>
    /// <returns>Material DataMap with ammo material data, or empty DataMap if no material was detected.</returns>
    private static DataMap TryGetMaterial(IAmmunitionGetter ammo)
    {
        DataMap material = Statics.AllMaterials.FirstOrDefault(material => ammo.Keywords!.Contains(material.Kwda!));
        if (material.Id is null)
            material = Statics.AllMaterials.FirstOrDefault(material => material.Id.GetT9n().RegexMatch(ammo.Name!.ToString()!, true));
        if (material.Id is null)
        {
            Executor.State!.LinkCache.TryResolve<IProjectileGetter>(ammo.Projectile.FormKey, out var proj);
            if (proj is not null && proj.Name is not null)
                material = Statics.AllMaterials.FirstOrDefault(material => material.Id.GetT9n().RegexMatch(proj.Name!.ToString()!, true));
        }

        return material.Id is not null ? material : new DataMap { Perks = [] };
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
    /// Modifies crafting recipes.<br/>
    /// </summary>
    /// <param name="recipe">Processed recipe record.</param>
    private static void ModCraftingRecipe(IConstructibleObjectGetter recipe)
    {
        bool isModified = false;
        ConstructibleObject newRecipe = Executor.State!.PatchMod.ConstructibleObjects.GetOrAddAsOverride(recipe);

        // AmmoMaterial.Perks is used later, so we're making a copy to keep original perks list intact
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
                isModified = true;
            }
        }

        // the ammo is a bolt and there's no condition of HasPerk type with the Ballistics perk
        if (!RecordData.IsArrow && 
            newRecipe.Conditions.All(cond => cond.Data is not HasPerkConditionData hasPerk || 
            hasPerk.Perk.Link.FormKey != GetFormKey("skyre_MARBallistics")))
        {
            newRecipe.AddHasPerkCondition(GetFormKey("skyre_MARBallistics"), 0, 0);
            isModified = true;
        }

        if (!isModified)
            Executor.State!.PatchMod.ConstructibleObjects.Remove(newRecipe);
    }

    /// <summary>
    /// Patches ammo and projectiles data.<br/>
    /// </summary>
    /// <param name="ammo">Processed ammo record.</param>
    /// <param name="expl">Ammo projectile explosion, if any.</param>
    /// <param name="type">Ammo group as enumerator.</param>
    private static void PatchAmmunition(IAmmunitionGetter ammo, IExplosionGetter? expl = null, AmmoGroup type = AmmoGroup.Vanilla)
    {
        Executor.State!.LinkCache.TryResolve<IProjectileGetter>(ammo.Projectile.FormKey, out var proj);
        if (proj is null)
        {
            Logger.Error("No projectile attached to this ammo");
            return;
        }

        if (proj.Type != Projectile.TypeEnum.Arrow && proj.Type != Projectile.TypeEnum.Missile)
        {
            Logger.Error($"The projectile has unexpected type ({proj.Type})");
            return;
        }

        // projectile data
        Projectile? newProj = null;

        float newRange = 120000f;

        float baseGravity = GetBaseData("gravity", proj, ammo, RecordData.IsArrow ? 0.25f : 0.2f);
        float materialGravity = GetMaterialData("gravity", proj, ammo, 0, AmmoMaterial);
        float modifierGravity = GetModifierData("gravity", proj, ammo, 0);
        float newGravity = baseGravity + materialGravity + modifierGravity;

        float baseSpeed = GetBaseData("speed", proj, ammo, RecordData.IsArrow ? 5200f : 6500f);
        float materialSpeed = GetMaterialData("speed", proj, ammo, 0, AmmoMaterial);
        float modifierSpeed = GetModifierData("speed", proj, ammo, 0);
        float newSpeed = baseSpeed + materialSpeed + modifierSpeed;

        if ((newRange != proj.Range) || (newGravity != proj.Gravity) || (newSpeed != proj.Speed) || type != AmmoGroup.Vanilla)
        {
            newProj = Executor.State!.PatchMod.Projectiles.GetOrAddAsOverride(proj);

            newProj.Range = newRange;
            newProj.Gravity = newGravity.Validate(proj.Gravity, nameof(proj.Gravity), RecordData.IsArrow ? 0.25f : 0.2f);
            newProj.Speed = newSpeed.Validate(proj.Speed, nameof(proj.Speed), RecordData.IsArrow ? 5200f : 6500f);

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
            (type == AmmoGroup.DoubleReforged) ? ammo.Damage : GetBaseData("damage", proj, ammo, RecordData.IsArrow ? 7f : 13f);
        // for double-reforged ammo material influence should be 0
        float materialDamage = 
            (type == AmmoGroup.DoubleReforged) ? 0 : GetMaterialData("damage", proj, ammo, 0, AmmoMaterial);
        float modifierDamage = GetModifierData("damage", proj, ammo, 0);
        float newDamage = baseDamage + materialDamage + modifierDamage;

        float modWeight = GetModifierData("weightMult", proj, ammo, 1f);
        float modValue = GetModifierData("priceMult", proj, ammo, 1f);

        if ((newDamage != ammo.Damage) || (modWeight != 1f) || (modValue != 1f) || type != AmmoGroup.Vanilla)
        {
            newAmmo = Executor.State!.PatchMod.Ammunitions.GetOrAddAsOverride(ammo);

            modWeight = modWeight < 0 ? 1f : modWeight;
            modValue = modValue < 0 ? 1f : modValue;

            newAmmo.Damage = newDamage.Validate(ammo.Damage, "Damage", RecordData.IsArrow ? 7f : 13f);
            newAmmo.Weight *= modWeight;
            newAmmo.Value = (uint)(ammo.Value * modValue);
        }

        if (Settings.Debug.ShowVerboseData)
        {
            if (type == 0)
            {
                if (newAmmo is not null) Logger.Info($"Damage: {ammo.Damage} -> {newAmmo.Damage} " +
                    $"(base: {baseDamage}, material: {materialDamage}, modifier: {modifierDamage})");
                if (newProj is not null) Logger.Info($"Gravity: {(decimal)proj!.Gravity} -> {(decimal)newProj.Gravity} " +
                    $"(base: {baseGravity}, material: {materialGravity}, modifier: {modifierGravity})");
                if (newProj is not null) Logger.Info($"Speed: {proj!.Speed} -> {newProj.Speed} " +
                    $"(base: {baseSpeed}, material: {materialSpeed}, modifier: {modifierSpeed})");
                if (newAmmo is not null && modWeight != 1f) Logger.Info($"Weight: {Math.Round(ammo.Weight, 2)} -> {(decimal)newAmmo.Weight} " +
                    $"(original weight x {modWeight})");
                if (newAmmo is not null && modValue != 1f) Logger.Info($"Value: {ammo.Value} -> {newAmmo.Value} " +
                    $"(original price x {modValue})");
            }
            else
            {
                if (newAmmo is not null) Logger.Info($"Damage: {newAmmo.Damage} " + $"(original: {baseDamage + materialDamage}, modifier: {modifierDamage})");
                if (newProj is not null) Logger.Info($"Gravity: {(decimal)newProj!.Gravity} " + $"(original: {(decimal)(baseGravity + materialGravity)}, modifier: {modifierGravity})");
                if (newProj is not null) Logger.Info($"Speed: {newProj!.Speed} " + $"(original: {baseSpeed + materialSpeed}, modifier: {modifierSpeed})");
                if (newAmmo is not null && modWeight != 1f) Logger.Info($"Weight: {Math.Round(newAmmo.Weight, 2)} " + $"(original x {modWeight})");
                if (newAmmo is not null && modValue != 1f) Logger.Info($"Value: {newAmmo.Value} " + $"(original x {modValue})");
            }
        }

        AllReports.Add(new Report { Record = ammo, Log = Logger });
    }

    /// <summary>
    /// Validates data returned by data getters.<br/>
    /// </summary>
    /// <param name="newValue">New data value.</param>
    /// <param name="oldValue">Current data value.</param>
    /// <param name="name">Data name.</param>
    /// <param name="fallback">Fallback value if no rule is found or received value is incorrect.</param>
    /// <returns>The newValue if it met the validation criteria, or oldValue/fallback value, as floats.</returns>
    private static float Validate(this float newValue, float oldValue, string name, float fallback)
    {
        if (newValue <= 0)
        {
            Logger.Error($"{name} cannot be 0 and less! The fallback value of {fallback} will be used instead");
            return fallback;
        }

        if (name == "Gravity" && oldValue == 0)
        {
            Logger.Caution("Original gravity is 0, but most likely on purpose, and will not be changed");
            return oldValue;
        }

        if (name == "Speed" && newValue * 2 < oldValue)
        {
            Logger.Caution("Original speed is very high, but most likely on purpose, and will not be changed");
            return oldValue;
        }

        if (name == "Damage" && (oldValue == 0 || oldValue == 1))
        {
            Logger.Caution("Original damage is 0 or 1, but most likely on purpose, and will not be changed");
            return oldValue;
        }

        return newValue;
    }

    /// <summary>
    /// Returns patching data from the baseStats ruleset.<br/>
    /// </summary>
    /// <param name="data">Name of the data to obtain.</param>
    /// <param name="ammo">Processed ammo.</param>
    /// <param name="proj">Processed ammo projectile.</param>
    /// <param name="fallback">Fallback value if no rule is found.</param>
    /// <returns>The data value or fallback value, as float.</returns>
    private static float GetBaseData(string data, IProjectileGetter proj, IAmmunitionGetter ammo, float fallback)
    {
        JsonNode? baseNode = Helpers.RuleByName(ammo.Name!.ToString()!, Rules["baseStats"]!.AsArray(), data1: "names", data2: data, true);
        if (baseNode is null && proj.Name is not null)
            baseNode = Helpers.RuleByName(proj.Name!.ToString()!, Rules["baseStats"]!.AsArray(), data1: "names", data2: data, true);
        float? baseData = baseNode?.AsType<float>();

        if (baseData is null) Logger.Error($"Unable to determine ammo type {data}; the fallback value of {fallback} will be used");

        return baseData ?? fallback;
    }

    /// <summary>
    /// Returns patching data from the materialStats ruleset.<br/>
    /// </summary>
    /// <param name="data">Name of the data to obtain.</param>
    /// <param name="ammo">Processed ammo.</param>
    /// <param name="proj">Processed ammo projectile.</param>
    /// <param name="fallback">Fallback value if no rule is found.</param>
    /// <param name="material">Material DataMap with ammo material data.</param>
    /// <returns>The data value or fallback value, as float.</returns>
    private static float GetMaterialData(string data, IProjectileGetter proj, IAmmunitionGetter ammo, float fallback, DataMap material = new DataMap())
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
    /// <param name="ammo">Processed ammo.</param>
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
    /// Initiates special types creation for ammo.<br/>
    /// </summary>
    /// <param name="ammo">An ammo to create special types for.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    private static void GenerateAmmunition(IAmmunitionGetter ammo, List<string> excludedNames)
    {
        if (Settings.General.ExclByEdID && ammo.EditorID!.IsExcluded(excludedNames, true))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Logger.Info($"Found in the \"No special variants\" list by EditorID", false, true);
                return;
            }
        }

        if (ammo.Name!.ToString()!.IsExcluded(excludedNames))
        {
            if (Settings.Debug.ShowExcluded) Logger.Info($"Found in the \"No special variants\" list by name", false, true);
            return;
        }

        if (!RecordData.IsArrow) CreateHardenedBolts(ammo);
        CreateSpecialAmmo(ammo, AmmoGroup.Reforged);
    }

    /// <summary>
    /// Creates ammo and projectile records of hardened and tempered types (bolts only).<br/>
    /// </summary>
    /// <param name="ammo">An ammo to create special types for.</param>
    private static void CreateHardenedBolts(IAmmunitionGetter ammo)
    {
        List<DataMap> ingredients = [
            new DataMap{Items = [ GetFormKey("DwarvenOil") ], Qty = Settings.Projectiles.IngredsQty, Id = "INGR"},
            new DataMap{Items = [ AmmoMaterial.Items is null ? GetFormKey("IngotCorundum") : AmmoMaterial.Items[0] ], 
                Qty = Settings.Projectiles.IngredsQty, Id = "MISC"}
        ];

        List<FormKey> perks = [ GetFormKey("skyre_MARAdvancedFletching0") ];
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
    /// Creates ammo and projectile records of other special types for bolts and arrows.<br/>
    /// </summary>
    /// <param name="ammo">An ammo to create special types for.</param>
    /// <param name="type">Ammo group as enumerator (to pass onward).</param>
    private static void CreateSpecialAmmo(IAmmunitionGetter ammo, AmmoGroup group)
    {
        List<DataMap> variantsMap = RecordData.IsArrow ? Statics.ArrowVariants : Statics.BoltVariants;

        foreach (var variant in variantsMap)
        {
            List<FormKey> perks = [];
            variant.Perks.ForEach(perks.Add);

            if ((variant.Id == "ammo_barbed" || variant.Id == "ammo_hweight") && AmmoMaterial.Perks.Count > 0)
                AmmoMaterial.Perks.ForEach(perks.Add);

            List<DataMap> ingredients = [
                new DataMap{ Items = [ variant.Items[0] ], Qty = Settings.Projectiles.IngredsQty, Id = "INGR" },
                new DataMap{ Items = [ variant.Items[1] ], Qty = Settings.Projectiles.IngredsQty, Id = "MISC" }
            ];

            Executor.State!.LinkCache.TryResolve<IKeywordGetter>(variant.Kwda, out var kwda);
            Executor.State!.LinkCache.TryResolve<IExplosionGetter>(variant.Expl, out var expl);

            IAmmunitionGetter? newAmmo = FletchAmmo(ammo, variant.Id, variant.Desc, kwda, expl, group);
            if (newAmmo is not null)
                AddCraftingRecipe(newAmmo, ammo, perks, ingredients);
        }
    }

    /// <summary>
    /// Creates an ammo record, and a projectile record for it.<br/>
    /// </summary>
    /// <param name="ammo">Parent ammo (we're copying to skip filling values).</param>
    /// <param name="typeId">New ammo type ID.</param>
    /// <param name="desc">Ingame decription for the new ammo, if any.</param>
    /// <param name="kwda">New ammo type keyword, if any.</param>
    /// <param name="expl">New ammo type explosion, if any.</param>
    /// <param name="group">Ammo group as enumerator (passed onward).</param>
    /// <returns>A new ammo as Ammunition.</returns>
    private static Ammunition? FletchAmmo(IAmmunitionGetter ammo, string typeId, string? desc, 
        IKeywordGetter? kwda, IExplosionGetter? expl, AmmoGroup group)
    {
        Executor.State!.LinkCache.TryResolve<IProjectileGetter>(ammo.Projectile.FormKey, out var baseProj);
        if (baseProj is null) return null;

        Logger = new Logger();

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

        PatchAmmunition(newAmmo, expl, group);
        return newAmmo;
    }

    /// <summary>
    /// Creates a crafting recipe record for the ammo type.<br/>
    /// </summary>
    /// <param name="newAmmo">The ammo to create.</param>
    /// <param name="oldAmmo">Parent ammo, the recipe ingredient.</param>
    /// <param name="perks">List of type and material perks FormKeys.</param>
    /// <param name="ingredients">List of other recipe ingredients, and their quantity.</param>
    private static void AddCraftingRecipe(IAmmunitionGetter newAmmo, IAmmunitionGetter oldAmmo, List<FormKey> perks, List<DataMap> ingredients)
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
            Executor.State!.LinkCache.Resolve<IKeywordGetter>(GetFormKey("CraftingSmithingForge")).ToNullableLink();
        newRecipe.CreatedObjectCount = (ushort)Settings.Projectiles.AmmoQty;
    }

    // patcher specific helpers
    /// <summary>
    /// Returns the FormKey with id from the statics record.<br/>
    /// </summary>
    /// <param name="stringId">The id in the elements with the FormKey to return.</param>
    /// <returns>A FormKey from the statics list.</returns>
    private static FormKey GetFormKey(string stringId) => Executor.Statics!.First(elem => elem.Id == stringId).FormKey;

    /// <summary>
    /// Returns the winning override for this-parameter, and copies it to the patch file.<br/>
    /// </summary>
    /// <param name="ammo">The ammo record as IAmmunitionGetter.</param>
    /// <param name="markModified">True to mark as modified in the patching data.</param>
    /// <returns>The winning override as <see cref="Ammunition"/>.</returns>
    private static Ammunition AsOverride(this IAmmunitionGetter ammo, bool markModified = false)
    {
        if (markModified) RecordData.Modified = true;
        return Executor.State!.PatchMod.Ammunitions.GetOrAddAsOverride(ammo);
    }

    private static void ShowReport(this IAmmunitionGetter ammo)
    {
        if (AllReports.Count == 0)
        {
            Logger.ShowReport($"{ammo.Name}", $"{ammo.FormKey}", $"{ammo.EditorID}", RecordData.NonPlayable, false);
        }
        else
        {
            foreach (var report in AllReports)
            {
                IAmmunitionGetter newAmmo = (IAmmunitionGetter)report.Record!;
                Logger newLog = (Logger)report.Log!;
                newLog.ShowReport($"{newAmmo.Name}", $"{newAmmo.FormKey}", $"{newAmmo.EditorID}", RecordData.NonPlayable, false);
            }
        }
    }

    // patcher specific statics
    private static (List<DataMap>, List<DataMap>, List<DataMap>) BuildStaticsMap()
    {
        Executor.Statics!.AddRange(
        [
            new DataMap{Id = "DLC1BoltExplosionFire02",                FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x010d90|EXPL")               },
            new DataMap{Id = "DLC1BoltExplosionFrost02",               FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x010d91|EXPL")               },
            new DataMap{Id = "DLC1BoltExplosionShock02",               FormKey = Helpers.ParseFormKey("Dawnguard.esm|0x010d92|EXPL")               },
            new DataMap{Id = "skyre_ALCExplosiveArrow0Explosion",      FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000854|EXPL") },
            new DataMap{Id = "skyre_ALCAshenArrowExplosion",           FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000876|EXPL") },
            new DataMap{Id = "skyre_ENCAmmoSiphoningKeyword",          FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000b8f|KWDA") },
            new DataMap{Id = "skyre_MARAmmoBarbedKeyword",             FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d87|KWDA") },
            new DataMap{Id = "skyre_MARAmmoHeavyKeyword",              FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d88|KWDA") },
            new DataMap{Id = "FireSalt",                               FormKey = Helpers.ParseFormKey("Skyrim.esm|0x03ad5e|INGR")                  },
            new DataMap{Id = "FrostSalt",                              FormKey = Helpers.ParseFormKey("Skyrim.esm|0x03ad5f|INGR")                  },
            new DataMap{Id = "VoidSalt",                               FormKey = Helpers.ParseFormKey("Skyrim.esm|0x03ad60|INGR")                  },
            new DataMap{Id = "TrollFat",                               FormKey = Helpers.ParseFormKey("Skyrim.esm|0x03ad72|INGR")                  },
            new DataMap{Id = "glowDust",                               FormKey = Helpers.ParseFormKey("Skyrim.esm|0x03ad73|INGR")                  },
            new DataMap{Id = "DLC2GhoulAsh",                           FormKey = Helpers.ParseFormKey("Dragonborn.esm|0x01cd6d|INGR")              },
            new DataMap{Id = "skyre_ALCFuse",                          FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000851|PERK") },
            new DataMap{Id = "skyre_ALCSuffocation",                   FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000873|PERK") },
            new DataMap{Id = "skyre_MARAdvancedFletching0",            FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d84|PERK") },
            new DataMap{Id = "skyre_MARAdvancedFletching1",            FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d85|PERK") },
            new DataMap{Id = "skyre_MARAdvancedFletching2",            FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000d86|PERK") },
            new DataMap{Id = "skyre_ENCElementalBombard0",             FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000b8d|PERK") },
            new DataMap{Id = "skyre_ENCElementalBombard1",             FormKey = Helpers.ParseFormKey("Skyrim AE Redone - Core.esm|0x000b8e|PERK") }
        ]);

        List<DataMap> allMaterials = [
            new DataMap{Id = "mat_amber",      Kwda = GetFormKey("cc_WeapMaterialAmber"),           Items = [ GetFormKey("cc_IngotAmber") ],    Perks = [ GetFormKey("GlassSmithing") ]       },
            new DataMap{Id = "mat_bonemold",   Kwda = GetFormKey("DLC2ArmorMaterialBonemoldLight"), Items = [ GetFormKey("Firewood01") ],       Perks = [ GetFormKey("AdvancedArmors") ]      },
            new DataMap{Id = "mat_daedric",    Kwda = GetFormKey("WeapMaterialDaedric"),            Items = [ GetFormKey("IngotEbony") ],       Perks = [ GetFormKey("DaedricSmithing") ]     },
            new DataMap{Id = "mat_dark",       Kwda = GetFormKey("cc_WeapMaterialDark"),            Items = [ GetFormKey("IngotQuicksilver") ], Perks = [ GetFormKey("DaedricSmithing") ]     },
            new DataMap{Id = "mat_dragonbone", Kwda = GetFormKey("DLC1WeapMaterialDragonbone"),     Items = [ GetFormKey("DragonBone") ],       Perks = [ GetFormKey("DragonArmor") ]         },
            new DataMap{Id = "mat_draugr",     Kwda = GetFormKey("WeapMaterialDraugr"),             Items = [ GetFormKey("IngotQuicksilver") ], Perks = [ GetFormKey("AdvancedArmors") ]      },
            new DataMap{Id = "mat_draugrh",    Kwda = GetFormKey("WeapMaterialDraugrHoned"),        Items = [ GetFormKey("IngotQuicksilver") ], Perks = [ GetFormKey("AdvancedArmors") ]      },
            new DataMap{Id = "mat_dwarven",    Kwda = GetFormKey("WeapMaterialDwarven"),            Items = [ GetFormKey("IngotDwarven") ],     Perks = [ GetFormKey("DwarvenSmithing") ]     },
            new DataMap{Id = "mat_ebony",      Kwda = GetFormKey("WeapMaterialEbony"),              Items = [ GetFormKey("IngotEbony") ],       Perks = [ GetFormKey("EbonySmithing") ]       },
            new DataMap{Id = "mat_elven",      Kwda = GetFormKey("WeapMaterialElven"),              Items = [ GetFormKey("IngotMoonstone") ],   Perks = [ GetFormKey("ElvenSmithing") ]       },
            new DataMap{Id = "mat_falmer",     Kwda = GetFormKey("WeapMaterialFalmer"),             Items = [ GetFormKey("ChaurusChitin") ],    Perks = [ GetFormKey("ElvenSmithing") ]       },
            new DataMap{Id = "mat_forsworn",   Kwda = GetFormKey("WAF_WeapMaterialForsworn"),       Items = [ GetFormKey("IngotIron") ]                                                       },
            new DataMap{Id = "mat_glass",      Kwda = GetFormKey("WeapMaterialGlass"),              Items = [ GetFormKey("IngotMalachite") ],   Perks = [ GetFormKey("GlassSmithing") ]       },
            new DataMap{Id = "mat_golden",     Kwda = GetFormKey("cc_WeapMaterialGolden"),          Items = [ GetFormKey("IngotMoonstone") ],   Perks = [ GetFormKey("DaedricSmithing") ]     },
            new DataMap{Id = "mat_iron",       Kwda = GetFormKey("WeapMaterialIron"),               Items = [ GetFormKey("IngotIron") ]                                                       },
            new DataMap{Id = "mat_madness",    Kwda = GetFormKey("cc_WeapMaterialMadness"),         Items = [ GetFormKey("cc_IngotMadness") ],  Perks = [ GetFormKey("EbonySmithing") ]       },
            new DataMap{Id = "mat_nordic",     Kwda = GetFormKey("DLC2WeaponMaterialNordic"),       Items = [ GetFormKey("IngotQuicksilver") ], Perks = [ GetFormKey("AdvancedArmors") ]      },
            new DataMap{Id = "mat_orcish",     Kwda = GetFormKey("WeapMaterialOrcish"),             Items = [ GetFormKey("IngotOrichalcum") ],  Perks = [ GetFormKey("OrcishSmithing") ]      },
            new DataMap{Id = "mat_silver",     Kwda = GetFormKey("WeapMaterialSilver"),             Items = [ GetFormKey("IngotSilver") ],      Perks = [ GetFormKey("skyre_SMTTradecraft") ] },
            new DataMap{Id = "mat_stalhrim",   Kwda = GetFormKey("DLC2WeaponMaterialStalhrim"),     Items = [ GetFormKey("DLC2OreStalhrim") ],  Perks = [ GetFormKey("EbonySmithing") ]       },
            new DataMap{Id = "mat_steel",      Kwda = GetFormKey("WeapMaterialSteel"),              Items = [ GetFormKey("IngotSteel") ],       Perks = [ GetFormKey("SteelSmithing") ]       }
        ];

        List<DataMap> arrowVariants = [
            new DataMap{
                Id = "ammo_ashen",
                Items = [ GetFormKey("DLC2GhoulAsh"), GetFormKey("TrollFat") ],
                Desc = "desc_ashen",
                Expl = GetFormKey("skyre_ALCExplosiveArrow0Explosion"),
                Perks = [ GetFormKey("skyre_ALCSuffocation") ]
            },
            new DataMap{
                Id = "ammo_explosive",
                Items = [ GetFormKey("FireSalt"), GetFormKey("TrollFat") ],
                Desc = "desc_explosive",
                Expl = GetFormKey("skyre_ALCAshenArrowExplosion"),
                Perks = [ GetFormKey("skyre_ALCFuse") ]
            }
        ];

        List<DataMap> boltVariants = [
            new DataMap{
                Id = "ammo_barbed",
                Kwda = GetFormKey("skyre_MARAmmoBarbedKeyword"),
                Items = [ GetFormKey("IngotCorundum"), GetFormKey("DragonScales") ],
                Desc = "desc_ashen",
                Perks = [ GetFormKey("skyre_MARAdvancedFletching1") ]
            },
            new DataMap{
                Id = "ammo_flame",
                Items = [ GetFormKey("SoulGemPettyFilled"), GetFormKey("TrollFat") ],
                Desc = "desc_explosive",
                Expl = GetFormKey("DLC1BoltExplosionFire02"),
                Perks = [ GetFormKey("skyre_ENCElementalBombard0") ]
            },
            new DataMap{
                Id = "ammo_frost",
                Items = [ GetFormKey("SoulGemPettyFilled"), GetFormKey("FrostSalt") ],
                Desc = "desc_ashen",
                Expl = GetFormKey("DLC1BoltExplosionFrost02"),
                Perks = [ GetFormKey("skyre_ENCElementalBombard0") ]
            },
            new DataMap{
                Id = "ammo_hweight",
                Kwda = GetFormKey("skyre_MARAmmoHeavyKeyword"),
                Items = [ GetFormKey("IngotCorundum"), GetFormKey("DragonBone") ],
                Desc = "desc_explosive",
                Perks = [ GetFormKey("skyre_MARAdvancedFletching2") ]
            },
            new DataMap{
                Id = "ammo_shock",
                Items = [ GetFormKey("SoulGemPettyFilled"), GetFormKey("VoidSalt") ],
                Desc = "desc_ashen",
                Expl = GetFormKey("DLC1BoltExplosionShock02"),
                Perks = [ GetFormKey("skyre_ENCElementalBombard0") ]
            },
            new DataMap{
                Id = "ammo_siphon",
                Kwda = GetFormKey("skyre_ENCAmmoSiphoningKeyword"),
                Items = [ GetFormKey("SoulGemPettyFilled"), GetFormKey("glowDust") ],
                Desc = "desc_explosive",
                Perks = [ GetFormKey("skyre_ENCElementalBombard1") ]
            }
        ];

        return (allMaterials, arrowVariants, boltVariants);
    }
}

