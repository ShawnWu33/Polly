using System;
using Polly.Hedging.Utils;
using Polly.Strategy;

namespace Polly.Hedging.Controller;

#pragma warning disable CA1031 // Do not catch general exception types

/// <summary>
/// Represents a single hedging attempt execution alongside all the necessary resources. These are:
///
/// <list type="bullet">
/// <item>
/// Distinct <see cref="ResilienceContext"/> instance for this execution.
/// One exception are primary task where the main context is reused.
/// </item>
/// <item>
/// The cancellation token associated with the execution.
/// </item>
/// </list>
/// </summary>
internal sealed class TaskExecution
{
    private readonly ResilienceContext _cachedContext = ResilienceContext.Get();
    private readonly HedgingHandler.Handler _handler;
    private CancellationTokenSource? _cancellationSource;
    private CancellationTokenRegistration? _cancellationRegistration;
    private ResilienceContext? _activeContext;

    public TaskExecution(HedgingHandler.Handler handler) => _handler = handler;

    public Task? ExecutionTask { get; private set; }

    public Outcome Outcome { get; private set; }

    public bool IsHandled { get; private set; }

    public bool IsAccepted { get; private set; }

    public ResilienceProperties Properties { get; } = new();

    public ResilienceContext Context => _activeContext ?? throw new InvalidOperationException("TaskExecution is not initialized.");

    public HedgedTaskType Type { get; set; }

    public Action<TaskExecution>? OnReset { get; set; }

    public void AcceptOutcome()
    {
        if (ExecutionTask?.IsCompleted == true)
        {
            IsAccepted = true;
        }
        else
        {
            throw new InvalidOperationException("Unable to accept outcome for a task that is not completed.");
        }
    }

    public void Cancel()
    {
        if (!IsAccepted)
        {
            _cancellationSource!.Cancel();
        }
    }

    public async ValueTask<bool> InitializeAsync<TResult, TState>(
        HedgedTaskType type,
        ContextSnapshot snapshot,
        Func<ResilienceContext, TState, ValueTask<TResult>> primaryCallback,
        TState state,
        int attempt)
    {
        Type = type;
        _cancellationSource = CancellationTokenSourcePool.Get();
        Properties.Replace(snapshot.OriginalProperties);

        if (snapshot.OriginalCancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = snapshot.OriginalCancellationToken.Register(o => ((CancellationTokenSource)o!).Cancel(), _cancellationSource);
        }

        PrepareContext(ref snapshot);

        if (type == HedgedTaskType.Secondary)
        {
            Func<Task<TResult>>? action = null;

            try
            {
                action = _handler.TryCreateHedgedAction<TResult>(Context, attempt);
                if (action == null)
                {
                    await ResetAsync().ConfigureAwait(false);
                    return false;
                }
            }
            catch (Exception e)
            {
                ExecutionTask = ExecuteCreateActionException<TResult>(e);
                return true;
            }

            ExecutionTask = ExecuteSecondaryActionAsync(action);
        }
        else
        {
            ExecutionTask = ExecutePrimaryActionAsync(primaryCallback, state);
        }

        return true;
    }

    public async ValueTask ResetAsync()
    {
        OnReset?.Invoke(this);

        if (_cancellationRegistration is CancellationTokenRegistration registration)
        {
#if NETCOREAPP
            await registration.DisposeAsync().ConfigureAwait(false);
#else
            registration.Dispose();
#endif
        }

        _cancellationRegistration = null;

        if (!IsAccepted)
        {
            await DisposeHelper.TryDisposeSafeAsync(Outcome.Result!).ConfigureAwait(false);

            // not accepted executions are always cancelled, so the cancellation source must be
            // disposed instead of returning it to the pool
            _cancellationSource!.Dispose();
        }
        else
        {
            // accepted outcome means that the cancellation source can be be returned to the pool
            // since it was most likely not cancelled
            CancellationTokenSourcePool.Return(_cancellationSource!);
        }

        IsAccepted = false;
        Outcome = default;
        IsHandled = false;
        Properties.Clear();
        OnReset = null;
        _activeContext = null;
        _cachedContext.Reset();
        _cancellationSource = null!;
    }

    private async Task ExecuteSecondaryActionAsync<TResult>(Func<Task<TResult>> action)
    {
        Outcome<TResult> outcome;

        try
        {
            var result = await action().ConfigureAwait(Context.ContinueOnCapturedContext);
            outcome = new Outcome<TResult>(result);
        }
        catch (Exception e)
        {
            outcome = new Outcome<TResult>(e);
        }

        await UpdateOutcomeAsync(outcome).ConfigureAwait(Context.ContinueOnCapturedContext);
    }

    private async Task ExecuteCreateActionException<TResult>(Exception e)
    {
        await UpdateOutcomeAsync(new Outcome<TResult>(e)).ConfigureAwait(Context.ContinueOnCapturedContext);
    }

    private async Task ExecutePrimaryActionAsync<TResult, TState>(Func<ResilienceContext, TState, ValueTask<TResult>> primaryCallback, TState state)
    {
        Outcome<TResult> outcome;

        try
        {
            var result = await primaryCallback(Context, state).ConfigureAwait(Context.ContinueOnCapturedContext);
            outcome = new Outcome<TResult>(result);
        }
        catch (Exception e)
        {
            outcome = new Outcome<TResult>(e);
        }

        await UpdateOutcomeAsync(outcome).ConfigureAwait(Context.ContinueOnCapturedContext);
    }

    private async Task UpdateOutcomeAsync<TResult>(Outcome<TResult> outcome)
    {
        Outcome = outcome.AsOutcome();
        IsHandled = await _handler.ShouldHandleAsync(outcome, new HandleHedgingArguments(Context)).ConfigureAwait(Context.ContinueOnCapturedContext);
    }

    private void PrepareContext(ref ContextSnapshot snapshot)
    {
        if (Type == HedgedTaskType.Primary)
        {
            // now just replace the properties
            _activeContext = snapshot.Context;
        }
        else
        {
            // secondary hedged tasks get their own unique context
            _activeContext = _cachedContext;
            _activeContext.InitializeFrom(snapshot.Context);
        }

        _activeContext.Properties = Properties;
        _activeContext.CancellationToken = _cancellationSource!.Token;
    }
}
