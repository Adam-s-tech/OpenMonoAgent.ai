using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Acp;

public sealed class AcpToolExecutor : IToolExecutor
{
    private readonly AcpSession _session;
    private readonly SseWriter _writer;
    private readonly TimeSpan _resultsTimeout;

    public AcpToolExecutor(AcpSession session, SseWriter writer, TimeSpan resultsTimeout)
    {
        _session = session;
        _writer = writer;
        _resultsTimeout = resultsTimeout;
    }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ITool? tool, ToolContext ctx, CancellationToken ct)
    {
        // 1. Stream the tool_call event to the connected client.
        await _writer.WriteEventAsync("tool_call", AcpToolBridge.ToToolCallPayload(call));

        // 2. Register a TCS keyed by call.Id. Throws on duplicate id.
        var tcs = _session.RegisterPendingCall(call);

        // 3. Suspend until the client POSTs back a tool_result, ct fires, or we time out.
        return await tcs.Task.WaitAsync(_resultsTimeout, ct);
    }
}
