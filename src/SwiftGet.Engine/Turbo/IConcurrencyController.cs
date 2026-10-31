using System;

namespace SwiftGet.Engine.Turbo;

/// <summary>
/// Operational state of the adaptive concurrency controller.
/// </summary>
public enum ControllerPhase
{
    SlowStart,
    SteadyState,
    ProbingUp,
    ProbingDown,
    Backoff
}

/// <summary>
/// Controls connection concurrency per host dynamically using AIMD and plateau detection.
/// </summary>
public interface IConcurrencyController
{
    /// <summary>
    /// Current recommended connection count for the download item.
    /// </summary>
    int TargetConnections { get; }

    /// <summary>
    /// Current operational phase of the controller.
    /// </summary>
    ControllerPhase CurrentPhase { get; }

    /// <summary>
    /// Evaluates current metrics and updates the target connection count.
    /// </summary>
    int Evaluate(CapacityEstimate estimate, TimeSpan elapsedSinceLastEval);

    /// <summary>
    /// Signals a network failure, connection reset, or HTTP 429/503 throttle.
    /// </summary>
    void ReportThrottled(TimeSpan? retryAfter = null);

    /// <summary>
    /// Signals that a connection succeeded or segment downloaded cleanly.
    /// </summary>
    void ReportSuccess();
}
