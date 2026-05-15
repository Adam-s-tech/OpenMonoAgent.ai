using System.Text.Json;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Acp;

public static class AcpToolBridge
{
    public static object ToToolCallPayload(ToolCall call)
        => new
        {
            id = call.Id,
            name = call.Name,
            input = ParseArgumentsAsJson(call.Arguments),
        };

    public static ToolResult FromToolResult(string id, string result)
        => ToolResult.Success(result);

    private static JsonElement ParseArgumentsAsJson(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return JsonDocument.Parse("{}").RootElement.Clone();
        try
        {
            return JsonDocument.Parse(arguments).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }
}
