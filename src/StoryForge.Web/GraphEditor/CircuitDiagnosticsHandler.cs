using Microsoft.AspNetCore.Components.Server.Circuits;

namespace StoryForge.Web.GraphEditor;

// Temporary while chasing an unexplained ~20-45s circuit disconnect pattern seen in real Chrome (not just
// a sandboxed test harness) — logs every circuit lifecycle event to a plain file so it survives regardless
// of what the terminal happens to be showing when a disconnect happens. Safe to delete once that's solved.
public sealed class CircuitDiagnosticsHandler : CircuitHandler
{
    private static void Log(string message) => DiagnosticsLog.Write(message);

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Log($"circuit opened {circuit.Id}");
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Log($"connection up {circuit.Id}");
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Log($"connection down {circuit.Id}");
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Log($"circuit closed {circuit.Id}");
        return Task.CompletedTask;
    }
}
