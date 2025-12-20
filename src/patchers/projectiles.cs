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

    public static void Run()
    {
        EditorIDs = new EditorIDs();

        List<IAmmunitionGetter> records = GetRecords();
        List<List<string>> blacklists = [
            [.. Rules["excludedAmmunitionVariants"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedAmmunition"]!.AsArray().Select(value => value!.GetValue<string>())]
        ];

        foreach (var ammo in records)
        {

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

        Console.WriteLine($"\n~~~ {ammoWinners.Count()} ammunition records found, filtering... ~~~\n");

        List<IAmmunitionGetter> ammoRecords = [];

        List<string> excludedNames = [.. Rules["excludedAmmunition"]!.AsArray().Select(value => value!.GetValue<string>())];
        foreach (var record in ammoWinners)
        {
            if (IsValid(record, excludedNames)) ammoRecords.Add(record);
        }

        Console.WriteLine($"~~~ {ammoRecords.Count} ammunition records are eligible for patching ~~~\n\n"
            + "====================");
        return ammoRecords;
    }

    /// <summary>
    /// Checks if the ammo matches necessary conditions to be patched.
    /// </summary>
    /// <param name="ammo">The ammo record as IAmmunitionGetter.</param>
    /// <param name="excludedNames">The list of excluded strings.</param>
    /// <returns>Check result from a filter the record triggered as <see cref="bool"/>.</returns>
    private static bool IsValid(IAmmunitionGetter ammo, List<string> excludedNames)
    {
        Logger = new Logger();

        // invalid if found in the excluded records list by edid
        if (Settings.General.ExclByEdID && ammo.EditorID!.IsExcluded(excludedNames, true))
        {
            if (Settings.Debug.ShowExcluded)
            {
                Logger.Info($"Found in the \"No patching\" list by EditorID (as {ammo.EditorID})");
                //ShowReport(ammo);
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
                //ShowReport(ammo);
            }
            return false;
        }

        return true;
    }

    // patcher specific helpers

    /// <summary>
    /// Returns the FormKey with id from the statics record.<br/>
    /// </summary>
    /// <param name="id">The id in the elements with the FormKey to return.</param>
    /// <returns>A FormKey from the statics list.</returns>
    private static FormKey GetFormKey(string stringId) => Executor.Statics!.First(elem => elem.Id == stringId).FormKey;

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
            new DataMap{Id = "mat_amber",      Kwda = GetFormKey("cc_WeapMaterialAmber"),           Items = [ GetFormKey("cc_IngotAmber") ],    Perks = [ GetFormKey("GlassSmithing"), GetFormKey("EbonySmithing") ]       },
            new DataMap{Id = "mat_bonemold",   Kwda = GetFormKey("DLC2ArmorMaterialBonemoldLight"), Items = [ GetFormKey("Firewood01") ],       Perks = [ GetFormKey("AdvancedArmors")]                                    },
            new DataMap{Id = "mat_daedric",    Kwda = GetFormKey("WeapMaterialDaedric"),            Items = [ GetFormKey("IngotEbony") ],       Perks = [ GetFormKey("DaedricSmithing")]                                   },
            new DataMap{Id = "mat_dark",       Kwda = GetFormKey("cc_WeapMaterialDark"),            Items = [ GetFormKey("IngotQuicksilver") ], Perks = [ GetFormKey("DaedricSmithing")]                                   },
            new DataMap{Id = "mat_dragonbone", Kwda = GetFormKey("DLC1WeapMaterialDragonbone"),     Items = [ GetFormKey("DragonBone") ],       Perks = [ GetFormKey("DragonArmor")]                                       },
            new DataMap{Id = "mat_draugr",     Kwda = GetFormKey("WeapMaterialDraugr"),             Items = [ GetFormKey("IngotQuicksilver") ], Perks = [ GetFormKey("AdvancedArmors")]                                    },
            new DataMap{Id = "mat_draugrh",    Kwda = GetFormKey("WeapMaterialDraugrHoned"),        Items = [ GetFormKey("IngotQuicksilver") ], Perks = [ GetFormKey("AdvancedArmors") ]                                   },
            new DataMap{Id = "mat_dwarven",    Kwda = GetFormKey("WeapMaterialDwarven"),            Items = [ GetFormKey("IngotDwarven") ],     Perks = [ GetFormKey("DwarvenSmithing") ]                                  },
            new DataMap{Id = "mat_ebony",      Kwda = GetFormKey("WeapMaterialEbony"),              Items = [ GetFormKey("IngotEbony") ],       Perks = [ GetFormKey("EbonySmithing") ]                                    },
            new DataMap{Id = "mat_elven",      Kwda = GetFormKey("WeapMaterialElven"),              Items = [ GetFormKey("IngotMoonstone") ],   Perks = [ GetFormKey("ElvenSmithing") ]                                    },
            new DataMap{Id = "mat_falmer",     Kwda = GetFormKey("WeapMaterialFalmer"),             Items = [ GetFormKey("ChaurusChitin") ],    Perks = [ GetFormKey("ElvenSmithing") ]                                    },
            new DataMap{Id = "mat_forsworn",   Kwda = GetFormKey("WAF_WeapMaterialForsworn"),       Items = [ GetFormKey("IngotIron") ]                                                                                    },
            new DataMap{Id = "mat_glass",      Kwda = GetFormKey("WeapMaterialGlass"),              Items = [ GetFormKey("IngotMalachite") ],   Perks = [ GetFormKey("GlassSmithing") ]                                    },
            new DataMap{Id = "mat_golden",     Kwda = GetFormKey("cc_WeapMaterialGolden"),          Items = [ GetFormKey("IngotMoonstone") ],   Perks = [ GetFormKey("DaedricSmithing") ]                                  },
            new DataMap{Id = "mat_iron",       Kwda = GetFormKey("WeapMaterialIron"),               Items = [ GetFormKey("IngotIron") ]                                                                                    },
            new DataMap{Id = "mat_madness",    Kwda = GetFormKey("cc_WeapMaterialMadness"),         Items = [ GetFormKey("cc_IngotMadness") ],  Perks = [ GetFormKey("EbonySmithing") ]                                    },
            new DataMap{Id = "mat_nordic",     Kwda = GetFormKey("DLC2WeaponMaterialNordic"),       Items = [ GetFormKey("IngotQuicksilver") ], Perks = [ GetFormKey("AdvancedArmors") ]                                   },
            new DataMap{Id = "mat_orcish",     Kwda = GetFormKey("WeapMaterialOrcish"),             Items = [ GetFormKey("IngotOrichalcum") ],  Perks = [ GetFormKey("OrcishSmithing") ]                                   },
            new DataMap{Id = "mat_silver",     Kwda = GetFormKey("WeapMaterialSilver"),             Items = [ GetFormKey("IngotSilver") ],      Perks = [ GetFormKey("skyre_SMTTradecraft"), GetFormKey("SteelSmithing") ] },
            new DataMap{Id = "mat_stalhrim",   Kwda = GetFormKey("DLC2WeaponMaterialStalhrim"),     Items = [ GetFormKey("DLC2OreStalhrim") ],  Perks = [ GetFormKey("GlassSmithing"), GetFormKey("EbonySmithing") ]       },
            new DataMap{Id = "mat_steel",      Kwda = GetFormKey("WeapMaterialSteel"),              Items = [ GetFormKey("IngotSteel") ],       Perks = [ GetFormKey("SteelSmithing") ]                                    }
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

