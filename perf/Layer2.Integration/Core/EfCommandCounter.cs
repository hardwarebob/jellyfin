using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Jellyfin.PerfTests.Integration;

/// <summary>
/// Counts EF Core SQL command executions and rows read, as a host-independent operation-count
/// proxy for "how much database work did this endpoint call do" — not a duration. Subscribes to
/// EF Core's global <see cref="DiagnosticListener"/> feed (the same mechanism
/// Application Insights/OpenTelemetry EF instrumentation uses) rather than registering a
/// DbCommandInterceptor, since the real host (JellyfinApplicationFactory) configures its
/// DbContext internally — there's no hook to add an interceptor to it from outside.
/// </summary>
public sealed class EfCommandCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private const string EfCoreListenerName = "Microsoft.EntityFrameworkCore";
    private const string CommandExecutedEventName = "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted";

    private readonly List<IDisposable> _subscriptions = new();
    private int _commandCount;

    public int CommandCount => _commandCount;

    public static EfCommandCounter Start()
    {
        var counter = new EfCommandCounter();
        counter._subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(counter));
        return counter;
    }

    public void Reset() => Interlocked.Exchange(ref _commandCount, 0);

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == EfCoreListenerName)
        {
            _subscriptions.Add(listener.Subscribe(this));
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key == CommandExecutedEventName)
        {
            Interlocked.Increment(ref _commandCount);
        }
    }

    void IObserver<DiagnosticListener>.OnCompleted()
    {
    }

    void IObserver<DiagnosticListener>.OnError(Exception error)
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnCompleted()
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error)
    {
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions)
        {
            s.Dispose();
        }

        _subscriptions.Clear();
    }
}
