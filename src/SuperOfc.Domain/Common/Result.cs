namespace SuperOfc.Domain.Common;

/// <summary>A business error with a stable code such as "Payroll.PeriodLocked".</summary>
public sealed record Error(string Code, string Message)
{
    public static Error Forbidden { get; } = new("Auth.Forbidden", "You do not have permission to do this.");

    public static Error NotFound(string entity) => new($"{entity}.NotFound", $"{entity} was not found.");

    public static Error Validation(string message) => new("Validation", message);

    public static Error Validation(string field, string message) => new($"Validation.{field}", message);

    public static Error Conflict(string message) => new("Concurrency.Conflict", message);
}

public class Result
{
    protected Result(IReadOnlyList<Error> errors)
    {
        Errors = errors;
    }

    public IReadOnlyList<Error> Errors { get; }

    public bool IsSuccess => Errors.Count == 0;

    public bool IsFailure => !IsSuccess;

    public Error? FirstError => Errors.Count > 0 ? Errors[0] : null;

    public static Result Success() => new([]);

    public static Result Failure(Error error) => new([error]);

    public static Result Failure(IEnumerable<Error> errors)
    {
        var list = errors.ToList();
        if (list.Count == 0)
        {
            throw new DomainException("A failure needs at least one error.");
        }

        return new Result(list);
    }

    /// <summary>Combine several results; success only if all succeed.</summary>
    public static Result Combine(params Result[] results)
    {
        var errors = results.SelectMany(r => r.Errors).ToList();
        return errors.Count == 0 ? Success() : new Result(errors);
    }

    public static implicit operator Result(Error error) => Failure(error);

    public override string ToString()
        => IsSuccess ? "Success" : string.Join("; ", Errors.Select(e => $"{e.Code}: {e.Message}"));
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, IReadOnlyList<Error> errors)
        : base(errors)
    {
        _value = value;
    }

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Result has no value: {this}");

    public static Result<T> Success(T value) => new(value, []);

    public static new Result<T> Failure(Error error) => new(default, [error]);

    public static new Result<T> Failure(IEnumerable<Error> errors) => new(default, errors.ToList());

    public Result<TOut> Map<TOut>(Func<T, TOut> map)
        => IsSuccess ? Result<TOut>.Success(map(Value)) : Result<TOut>.Failure(Errors);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);
}
