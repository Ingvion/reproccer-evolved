using System.Text.Json.Nodes;

namespace ReProccer.Patchers;

public static class IngredientsPatcher
{
    private static readonly JsonObject Rules = Executor.Rules!["alchemy"]!.AsObject();

    public static void Run()
    {
        List<List<string>> blacklists = [
            [.. Rules["excludedEffects"]!.AsArray().Select(value => value!.GetValue<string>())],
            [.. Rules["excludedIngredients"]!.AsArray().Select(value => value!.GetValue<string>())]
        ];
    }
}