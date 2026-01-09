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

    private static (float, float) Validate(this float newValue, float oldValue, string name, float fallback, bool allowCustom = false)
    {
        if (newValue <= 0 && !allowCustom)
        {
            Logger.Error($"{name} cannot be 0 or negative! The fallback value of {fallback} will be used instead");
            return (fallback, fallback);
        }
        else if (allowCustom)
        {
            if (oldValue == 0 && name == "Gravity")
            {
                Logger.Caution("Original gravity is 0, but most likely on purpose, and will not be changed");
                return (oldValue, oldValue);
            }

            if (newValue * 2 < oldValue && name == "Speed")
            {
                Logger.Caution("Original speed is very high, but most likely on purpose, and will not be changed");
                return (oldValue, oldValue);
            }
        }

        return (newValue, newValue);
    }

    private static float GetBaseData(string data, IProjectileGetter proj, IAmmunitionGetter ammo, float fallback)
    {
        // base stats
        JsonNode? baseNode = Helpers.RuleByName(ammo.Name!.ToString()!, Rules["baseStats"]!.AsArray(), data1: "names", data2: data, true);
        if (baseNode is null && proj.Name is not null)
            baseNode = Helpers.RuleByName(proj.Name!.ToString()!, Rules["baseStats"]!.AsArray(), data1: "names", data2: data, true);
        float? baseData = baseNode?.AsType<float>();

        if (baseData is null) Logger.Error($"Unable to determine ammo type {data}; the fallback value of {fallback} will be used");

        return baseData ?? fallback;
    }

    private static float GetMaterialData(string data, IProjectileGetter proj, IAmmunitionGetter ammo, float fallback, DataMap material = new DataMap())
    {
        // material stats
        JsonNode? materialNode = Helpers.RuleByName(ammo.Name!.ToString()!, Rules["materialStats"]!.AsArray(), data1: "names", data2: data, true);
        if (materialNode is null && material.Id is not null)
            materialNode = Helpers.RuleByName(material.Id, Rules["materialStats"]!.AsArray(), data1: "id", data2: data, true);
        if (materialNode is null && proj.Name is not null)
            materialNode = Helpers.RuleByName(proj.Name!.ToString()!, Rules["materialStats"]!.AsArray(), data1: "names", data2: data, true);
        float? materialData = materialNode?.AsType<float>();

        return materialData ?? fallback;
    }

    private static float GetModifierData(string data, IProjectileGetter proj, IAmmunitionGetter ammo, float fallback)
    {
        // modifier stats
        JsonNode? modifierNode = Helpers.RuleByName(ammo.Name!.ToString()!, Rules["modifierStats"]!.AsArray(), data1: "names", data2: data, true);
        if (modifierNode is null && proj.Name is not null)
            modifierNode = Helpers.RuleByName(proj.Name!.ToString()!, Rules["modifierStats"]!.AsArray(), data1: "names", data2: data, true);
        float? modifierData = modifierNode?.AsType<float>();

        return modifierData ?? fallback;
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

    /// <summary>
    /// Displays info and errors.<br/>
    /// </summary>
    /// <param name="ammo">The ammo record as IAmmunitionGetter.</param>
    private static void ShowReport(this IAmmunitionGetter ammo) =>
        Logger.ShowReport($"{ammo.Name}", $"{ammo.FormKey}", $"{ammo.EditorID}", RecordData.NonPlayable, false);

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

