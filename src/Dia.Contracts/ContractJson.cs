using System.Text.Json;

namespace Dia.Contracts;

public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
