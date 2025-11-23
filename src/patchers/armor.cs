using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using ReProccer.Utils;
using System.Text.Json.Nodes;

namespace ReProccer.Patchers;

public static class Armor
{
    private static readonly IPatcherState<ISkyrimMod, ISkyrimModGetter> State = Executor.State!;
    private static readonly Lazy<Config.Armor> Settings = new();
    private static readonly JsonObject Rules = Executor.Rules!["armor"]!.AsObject();
	
    public static void Run()
    {
		
	}
}

