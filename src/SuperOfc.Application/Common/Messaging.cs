using SuperOfc.Domain.Common;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Common;

/// <summary>Return type for commands that produce no value.</summary>
public readonly record struct Unit
{
    public static Unit Value => default;
}

/// <summary>Any use-case request.</summary>
#pragma warning disable CA1040 // Marker interface is intentional.
public interface IRequest<TResult>
{
}

/// <summary>A request that changes state. Must carry <see cref="RequiresPermissionAttribute"/> or <see cref="AllowAnonymousAttribute"/>.</summary>
public interface ICommand<TResult> : IRequest<TResult>
{
}

/// <summary>A read-only request. Requires <see cref="Permission.ViewData"/> unless attributed otherwise.</summary>
public interface IQuery<TResult> : IRequest<TResult>
{
}
#pragma warning restore CA1040

public interface IRequestHandler<in TRequest, TResult>
    where TRequest : IRequest<TResult>
{
    Task<Result<TResult>> HandleAsync(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Runs a request in its own DI scope with validation, authorization and logging.</summary>
public interface IDispatcher
{
    Task<Result<TResult>> SendAsync<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default);
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequiresPermissionAttribute(Permission permission) : Attribute
{
    public Permission Permission { get; } = permission;
}

/// <summary>Request can run without a signed-in user (login, first-run setup).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class AllowAnonymousAttribute : Attribute
{
}
