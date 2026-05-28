using JetBrains.Annotations;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Persistence;

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
internal sealed class CommandAuditBehavior<TRequest, TResult>(IWriteUnitOfWork unitOfWork)
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
        if (request is ICommandBase command)
        {
            await CommandAuditWriter.WriteAsync(unitOfWork, command, cancellationToken);
        }

        return await next();
    }
}

/// <summary>
/// Mediator pipeline behavior that persists an audit row for every dispatched command of the
/// <see cref="IRequest"/> shape (no result). Wraps the next handler so the audit-write step always
/// runs first. Non-command requests pass through without an audit write.
/// </summary>
/// <typeparam name="TRequest">The mediator request type passed through the pipeline.</typeparam>
[UsedImplicitly]
internal sealed class CommandAuditBehavior<TRequest>(IWriteUnitOfWork unitOfWork)
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
        if (request is ICommandBase command)
        {
            await CommandAuditWriter.WriteAsync(unitOfWork, command, cancellationToken);
        }

        await next();
    }
}
