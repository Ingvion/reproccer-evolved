using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using ReProccer.Utils;
using System.Text.Json.Nodes;

namespace ReProccer.Patchers;

public static class WeaponsPatcher
{
    private static readonly Settings.AllSettings Settings = Executor.Settings!;
    private static readonly JsonObject Rules = Executor.Rules!["weapons"]!.AsObject();

    private static EditorIDs EditorIDs;
    private static Weapon? ThisRecord;
    private static PatchingData RecordData;
    private static Logger Logger;

    public static void Run()
    {

    }
}