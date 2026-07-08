namespace StudyScheduler.Domain.Primitives;

/// <summary>
/// Outcome of a domain operation that validates user input: success, or one or more
/// <see cref="Error"/>s. Programmer errors (broken preconditions of the code itself) keep
/// throwing exceptions instead. Deliberately no Bind/Map railway combinators — call sites use
/// plain checks, which is all this codebase needs.
/// </summary>
public class Result
{
    private static readonly Result Succeeded = new(true, []);

    protected Result(bool isSuccess, IReadOnlyList<Error> errors)
    {
        IsSuccess = isSuccess;
        Errors = errors;
    }

    public bool IsSuccess { get; }

    public IReadOnlyList<Error> Errors { get; }

    public static Result Success() => Succeeded;

    public static Result Failure(params Error[] errors)
    {
        // An empty failure would silently pass ToValidationProblem-style mapping — a bug.
        if (errors.Length == 0)
            throw new ArgumentException("A failure requires at least one error.", nameof(errors));

        return new Result(false, errors);
    }
}

/// <summary>A <see cref="Result"/> carrying the produced value on success.</summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(bool isSuccess, T? value, IReadOnlyList<Error> errors)
        : base(isSuccess, errors)
    {
        _value = value;
    }

    /// <summary>The produced value; accessing it on a failed result is a programmer error.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot access the value of a failed result.");

    public static Result<T> Success(T value) => new(true, value, []);

    public static new Result<T> Failure(params Error[] errors)
    {
        if (errors.Length == 0)
            throw new ArgumentException("A failure requires at least one error.", nameof(errors));

        return new Result<T>(false, default, errors);
    }
}
