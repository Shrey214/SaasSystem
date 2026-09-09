using System.Text.Json;
using System.Text.Json.Serialization;

namespace HotelSaas.BuildingBlocks.Messaging;

// One serialisation setting for every event in the system.
//
// Shared deliberately: a publisher and a consumer that disagree about
// casing produce a payload that deserialises to all-default values, which
// looks like a data bug rather than a configuration one.
public static class EventSerialization
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Absent is not the same as null. Consumers must tolerate unknown
        // fields (events are additive), and omitting nulls keeps payloads
        // small without changing meaning.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialise<T>(T value) => JsonSerializer.Serialize(value, Options);

    // The runtime-type overload is required, not preferred: the outbox holds
    // events as IDomainEvent, and the generic overload would serialise only
    // the interface members - producing an empty payload that looks like a
    // data bug.
    public static string Serialise(object value, Type runtimeType)
        => JsonSerializer.Serialize(value, runtimeType, Options);
}
