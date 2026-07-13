using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClickUpTodo.Configuration;

/// <summary>
/// The single System.Text.Json contract every <see cref="IStateStore"/> backend serialises values
/// with. Shared so the file backend (<see cref="JsonFileStateStore"/>) and the LiteDB backend
/// (<see cref="LiteDbStateStore"/>) produce byte-for-byte identical payloads — camelCase, indented,
/// enums as readable strings — which keeps <c>config.json</c> stable and makes the backends
/// interchangeable (a value written by one deserialises cleanly through the other).
/// </summary>
internal static class StateJson
{
    /// <summary>Serializer options matching the original <see cref="ConfigStore"/> guarantees.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Persist enums (e.g. the F3 view's fields/operators) as readable strings, not ordinals.
        Converters = { new JsonStringEnumConverter() },
    };
}
