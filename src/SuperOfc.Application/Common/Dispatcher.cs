using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuperOfc.Application.Abstractions;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Common;

public sealed class Dispatcher(IServiceScopeFactory scopeFactory, ICurrentUser currentUser, ILogger<Dispatcher> logger) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, HandlerInvoker> Invokers = new();

    public async Task<Result<TResult>> SendAsync<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestType = request.GetType();

        var permissionError = CheckPermission(requestType);
        if (permissionError is not null)
        {
            logger.LogWarning("Denied {Request} for {User}", requestType.Name, currentUser.UserName);
            return permissionError;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var validationErrors = await ValidateAsync(services, request, requestType, cancellationToken).ConfigureAwait(false);
        if (validationErrors.Count > 0)
        {
            return Result<TResult>.Failure(validationErrors);
        }

        var invoker = Invokers.GetOrAdd(requestType, t => HandlerInvoker.Create(t, typeof(TResult)));
        var stopwatch = Stopwatch.StartNew();
        var result = (Result<TResult>)await invoker.InvokeAsync(services, request, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (result.IsSuccess)
        {
            logger.LogInformation("{Request} succeeded in {ElapsedMs} ms", requestType.Name, stopwatch.ElapsedMilliseconds);
        }
        else
        {
            logger.LogInformation("{Request} failed in {ElapsedMs} ms: {Errors}", requestType.Name, stopwatch.ElapsedMilliseconds, string.Join(", ", result.Errors.Select(e => e.Code)));
        }

        return result;
    }

    private Error? CheckPermission(Type requestType)
    {
        if (requestType.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
        {
            return null;
        }

        if (!currentUser.IsAuthenticated)
        {
            return new Error("Auth.NotSignedIn", "Please sign in first.");
        }

        var required = requestType.GetCustomAttribute<RequiresPermissionAttribute>()?.Permission ?? Permission.ViewData;
        return PermissionMatrix.Has(currentUser.Role, required) ? null : Error.Forbidden;
    }

    private static async Task<List<Error>> ValidateAsync(IServiceProvider services, object request, Type requestType, CancellationToken cancellationToken)
    {
        var validatorType = typeof(IValidator<>).MakeGenericType(requestType);
        var errors = new List<Error>();
        foreach (var validator in services.GetServices(validatorType).Cast<IValidator>())
        {
            var context = new ValidationContext<object>(request);
            var result = await validator.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
            // Nested inputs ("Input.Code") map to the last segment so forms can show errors under the field.
            errors.AddRange(result.Errors.Select(f => new Error($"Validation.{f.PropertyName.Split('.')[^1]}", f.ErrorMessage)));
        }

        return errors;
    }

    private abstract class HandlerInvoker
    {
        public static HandlerInvoker Create(Type requestType, Type resultType)
            => (HandlerInvoker)Activator.CreateInstance(typeof(HandlerInvoker<,>).MakeGenericType(requestType, resultType))!;

        public abstract Task<object> InvokeAsync(IServiceProvider services, object request, CancellationToken cancellationToken);
    }

    private sealed class HandlerInvoker<TRequest, TResult> : HandlerInvoker
        where TRequest : IRequest<TResult>
    {
        public override async Task<object> InvokeAsync(IServiceProvider services, object request, CancellationToken cancellationToken)
        {
            var handler = services.GetRequiredService<IRequestHandler<TRequest, TResult>>();
            return await handler.HandleAsync((TRequest)request, cancellationToken).ConfigureAwait(false);
        }
    }
}
