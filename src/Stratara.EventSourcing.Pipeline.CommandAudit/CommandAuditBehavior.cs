using JetBrains.Annotations;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Session;

namespace Stratara.EventSourcing.Pipeline.CommandAudit;

internal static class CommandAuditWriter
{
    internal static async Task WriteAsync(
        IWriteUnitOfWork unitOfWork,
        ICommandBase command,
        CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var auditRepository = unitOfWork.CreateCommandAuditRepository(transaction);
        await auditRepository.AddAsync(command, cancellationToken);
        await transaction.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The causation an enclosing command left on the session, or <see langword="null"/> where there is none: recording a
    /// command makes its own id the session's causation, and a command dispatched from inside another one's handler
    /// shares that handler's session.
    /// </summary>
    internal static string? EnclosingCausation(ISessionContextProvider? sessionContextProvider) =>
        sessionContextProvider?.Current?.CausationId is { Length: > 0 } causationId ? causationId : null;

    /// <summary>
    /// Hands the enclosing command's causation back once the command it dispatched has run, so what its handler appends
    /// afterwards is caused by it. The outermost command keeps its own id as the causation, for what runs after it in
    /// the same scope.
    /// </summary>
    internal static void RestoreCausation(ISessionContextProvider? sessionContextProvider, string? enclosingCausation)
    {
        if (enclosingCausation is not null && sessionContextProvider?.Current is { } current)
        {
            sessionContextProvider.Set(current with { CausationId = enclosingCausation });
        }
    }
}

/// <summary>
/// Mediator pipeline behavior that persists an audit row for every dispatched command of the
/// <see cref="IRequest{TResult}"/> shape. Wraps the next handler so the audit-write step always
/// runs first; the result of the inner handler is returned unchanged. Non-command requests
/// (pure queries) pass through without an audit write.
/// </summary>
/// <typeparam name="TRequest">The mediator request type passed through the pipeline.</typeparam>
/// <typeparam name="TResult">The result type returned by the inner handler.</typeparam>
[UsedImplicitly]
internal sealed class CommandAuditBehavior<TRequest, TResult>(IWriteUnitOfWork unitOfWork, ISessionContextProvider? sessionContextProvider = null)
    : IPipelineBehavior<TRequest, TResult>
    where TRequest : IRequest<TResult>
{
    /// <summary>
    /// Records an audit row when <paramref name="request"/> implements <see cref="ICommandBase"/>,
    /// then delegates to <paramref name="next"/> and returns its result.
    /// </summary>
    /// <param name="request">The incoming mediator request.</param>
    /// <param name="next">The continuation of the pipeline.</param>
    /// <param name="cancellationToken">Token to observe while writing the audit row and awaiting <paramref name="next"/>.</param>
    /// <returns>The result returned by the inner handler.</returns>
    public async Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken)
    {
        if (request is not ICommandBase command)
        {
            return await next();
        }

        var enclosingCausation = CommandAuditWriter.EnclosingCausation(sessionContextProvider);
        await CommandAuditWriter.WriteAsync(unitOfWork, command, cancellationToken);
        try
        {
            return await next();
        }
        finally
        {
            CommandAuditWriter.RestoreCausation(sessionContextProvider, enclosingCausation);
        }
    }
}

/// <summary>
/// Mediator pipeline behavior that persists an audit row for every dispatched command of the
/// <see cref="IRequest"/> shape (no result). Wraps the next handler so the audit-write step always
/// runs first. Non-command requests pass through without an audit write.
/// </summary>
/// <typeparam name="TRequest">The mediator request type passed through the pipeline.</typeparam>
[UsedImplicitly]
internal sealed class CommandAuditBehavior<TRequest>(IWriteUnitOfWork unitOfWork, ISessionContextProvider? sessionContextProvider = null)
    : IPipelineBehavior<TRequest>
    where TRequest : IRequest
{
    /// <summary>
    /// Records an audit row when <paramref name="request"/> implements <see cref="ICommandBase"/>,
    /// then delegates to <paramref name="next"/>.
    /// </summary>
    /// <param name="request">The incoming mediator request.</param>
    /// <param name="next">The continuation of the pipeline.</param>
    /// <param name="cancellationToken">Token to observe while writing the audit row and awaiting <paramref name="next"/>.</param>
    public async Task HandleAsync(TRequest request, Func<Task> next, CancellationToken cancellationToken)
    {
        if (request is not ICommandBase command)
        {
            await next();
            return;
        }

        var enclosingCausation = CommandAuditWriter.EnclosingCausation(sessionContextProvider);
        await CommandAuditWriter.WriteAsync(unitOfWork, command, cancellationToken);
        try
        {
            await next();
        }
        finally
        {
            CommandAuditWriter.RestoreCausation(sessionContextProvider, enclosingCausation);
        }
    }
}
