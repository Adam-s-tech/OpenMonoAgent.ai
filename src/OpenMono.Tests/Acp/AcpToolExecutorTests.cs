using System.Text;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Acp;
using OpenMono.Session;
using OpenMono.Tools;
using Xunit;

namespace OpenMono.Tests.Acp;

public sealed class AcpToolExecutorTests
{
    private static AcpSession NewSession() => new()
    {
        Id = "sess_test",
        StartedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
        Model = "test-model",
    };

    private static ToolCall NewCall(string id, string name = "FileRead", string args = "{\"path\":\"/tmp/x\"}")
        => new() { Id = id, Name = name, Arguments = args };

    private static async Task WaitForPendingAsync(AcpSession session, int expectedCount = 1, int timeoutMs = 1_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (session.PendingCallIds.Count < expectedCount && DateTime.UtcNow < deadline)
            await Task.Delay(5);
    }

    private static JsonElement ParseSseDataLine(string sse, string expectedEvent)
    {
        // SSE frame: "event: <name>\ndata: <json>\n\n"
        var blocks = sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            string? ev = null;
            string? data = null;
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("event: ")) ev = line[7..].Trim();
                else if (line.StartsWith("data: ")) data = line[6..].Trim();
            }
            if (ev == expectedEvent && data is not null)
                return JsonDocument.Parse(data).RootElement.Clone();
        }
        throw new InvalidOperationException($"No SSE frame with event '{expectedEvent}' found in:\n{sse}");
    }

    [Fact]
    public async Task ExecuteAsync_emits_tool_call_event_and_registers_pending()
    {
        var session = NewSession();
        var ms = new MemoryStream();
        var writer = new SseWriter(ms, CancellationToken.None);
        var executor = new AcpToolExecutor(session, writer, TimeSpan.FromSeconds(5));

        var executeTask = executor.ExecuteAsync(NewCall("call_1"), tool: null, ctx: null!, CancellationToken.None);
        await WaitForPendingAsync(session);

        executeTask.IsCompleted.Should().BeFalse();
        session.PendingCallIds.Should().Contain("call_1");

        var sse = Encoding.UTF8.GetString(ms.ToArray());
        var payload = ParseSseDataLine(sse, "tool_call");
        payload.GetProperty("id").GetString().Should().Be("call_1");
        payload.GetProperty("name").GetString().Should().Be("FileRead");
        payload.GetProperty("input").GetProperty("path").GetString().Should().Be("/tmp/x");

        // Clean up: resolve so we don't leave a dangling task.
        session.TryResolvePendingCall("call_1", ToolResult.Success("done"));
        await executeTask;
    }

    [Fact]
    public async Task ExecuteAsync_returns_resolved_ToolResult_when_session_resolves_pending_call()
    {
        var session = NewSession();
        var writer = new SseWriter(new MemoryStream(), CancellationToken.None);
        var executor = new AcpToolExecutor(session, writer, TimeSpan.FromSeconds(5));

        var executeTask = executor.ExecuteAsync(NewCall("call_2"), null, null!, CancellationToken.None);
        await WaitForPendingAsync(session);

        var resolved = session.TryResolvePendingCall("call_2", ToolResult.Success("file contents"));
        resolved.Should().BeTrue();

        var result = await executeTask;
        result.ModelPreview.Should().Be("file contents");
        result.Class.Should().Be(ResultClass.Success);
        session.PendingCallIds.Should().BeEmpty("resolved calls should be removed from the pending map");
    }

    [Fact]
    public async Task ExecuteAsync_throws_on_duplicate_call_id()
    {
        var session = NewSession();
        var writer = new SseWriter(new MemoryStream(), CancellationToken.None);
        var executor = new AcpToolExecutor(session, writer, TimeSpan.FromSeconds(5));

        var firstTask = executor.ExecuteAsync(NewCall("call_dup"), null, null!, CancellationToken.None);
        await WaitForPendingAsync(session);

        Func<Task> act = async () => await executor.ExecuteAsync(NewCall("call_dup"), null, null!, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Duplicate pending tool call id*");

        // Clean up the first.
        session.TryResolvePendingCall("call_dup", ToolResult.Success("ok"));
        await firstTask;
    }

    [Fact]
    public async Task ExecuteAsync_times_out_when_no_results_arrive_within_window()
    {
        var session = NewSession();
        var writer = new SseWriter(new MemoryStream(), CancellationToken.None);
        var executor = new AcpToolExecutor(session, writer, TimeSpan.FromMilliseconds(50));

        Func<Task> act = async () =>
            await executor.ExecuteAsync(NewCall("call_timeout"), null, null!, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task ExecuteAsync_propagates_cancellation()
    {
        var session = NewSession();
        var writer = new SseWriter(new MemoryStream(), CancellationToken.None);
        var executor = new AcpToolExecutor(session, writer, TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource();
        var executeTask = executor.ExecuteAsync(NewCall("call_cancel"), null, null!, cts.Token);
        await WaitForPendingAsync(session);

        cts.Cancel();

        Func<Task> act = async () => await executeTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void TryResolvePendingCall_returns_false_for_unknown_call_id()
    {
        var session = NewSession();
        session.TryResolvePendingCall("never_registered", ToolResult.Success("x")).Should().BeFalse();
    }

    [Fact]
    public void Bridge_ToToolCallPayload_serialises_to_lowercase_keys_with_parsed_input()
    {
        var call = NewCall("call_x", "Bash", "{\"command\":\"ls -la\"}");

        var payload = AcpToolBridge.ToToolCallPayload(call);
        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("id").GetString().Should().Be("call_x");
        root.GetProperty("name").GetString().Should().Be("Bash");
        root.GetProperty("input").GetProperty("command").GetString().Should().Be("ls -la");
    }

    [Fact]
    public void Bridge_ToToolCallPayload_falls_back_to_empty_object_for_invalid_args_json()
    {
        var call = NewCall("call_y", "Bash", "not-json{");

        var payload = AcpToolBridge.ToToolCallPayload(call);
        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("input").ValueKind.Should().Be(JsonValueKind.Object);
        doc.RootElement.GetProperty("input").EnumerateObject().Should().BeEmpty();
    }
}
