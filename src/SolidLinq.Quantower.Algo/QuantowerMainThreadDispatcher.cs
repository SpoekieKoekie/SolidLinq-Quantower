using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using SolidLinq.Quantower;
using TradingPlatform.BusinessLayer;

namespace SolidLinq.Quantower.Algo;

/// <summary>Runs broker calls on Quantower market-data callbacks (NewQuote / NewLast).</summary>
internal sealed class QuantowerMainThreadDispatcher
{
    private readonly ConcurrentQueue<PendingWork> _queue = new();
    private Symbol? _symbol;

    public void Bind(Symbol symbol)
    {
        Unbind();
        _symbol = symbol;
        _symbol.NewQuote += OnMarket;
        _symbol.NewLast += OnMarket;
    }

    public void Unbind()
    {
        if (_symbol == null) return;
        _symbol.NewQuote -= OnMarket;
        _symbol.NewLast -= OnMarket;
        _symbol = null;
    }

    public Task<ExecutionResult> RunAsync(Func<ExecutionResult> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(new PendingWork(work, tcs));
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(45), ct);
    }

    private void OnMarket(Symbol s, Quote q) => DrainPending();

    private void OnMarket(Symbol s, Last last) => DrainPending();

    public void DrainPending()
    {
        while (_queue.TryDequeue(out var item))
        {
            try
            {
                item.Tcs.TrySetResult(item.Work());
            }
            catch (Exception ex)
            {
                item.Tcs.TrySetResult(new ExecutionResult(false, "error", null, ex.Message));
            }
        }
    }

    private readonly record struct PendingWork(Func<ExecutionResult> Work, TaskCompletionSource<ExecutionResult> Tcs);
}
