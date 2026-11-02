using ReProccer.Utils;
using System.Text.Json.Nodes;

namespace ReProccer.Patchers;

public static class MagicEffectsPatcher
{
    private static readonly Settings.AllSettings Settings = Executor.Settings!;
    private static readonly JsonObject Rules = Executor.Rules!["magiceffects"]!.AsObject();

    private static Logger Logger;

    public static void Run()
    {
        // stub
    }
}