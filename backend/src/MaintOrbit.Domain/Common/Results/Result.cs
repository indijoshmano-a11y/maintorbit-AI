namespace MaintOrbit.Domain.Common.Results;

/// <summary>
/// The outcome of an operation that can fail in an expected way.
/// </summary>
/// <remarks>
/// EX-1: "Expected failures return a result; exceptions are for the genuinely exceptional", and
/// the stated reason is that it "makes failure visible in signatures". An operation that returns
/// <see cref="Result"/> cannot have its failure path overlooked by a caller who did not think to
/// read the implementation.
/// <para>
/// Deliberately minimal. This is the first use, and a result abstraction grows teeth it does not
/// need — mapping, combination, railway operators — long before anything requires them.
/// </para>
/// </remarks>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        // A successful result carrying an error, or a failure carrying none, would make both
        // properties unreliable and every call site defensive.
        if (isSuccess != error.IsNone)
        {
            throw new ArgumentException(
                "A success must carry no error, and a failure must carry one.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Whether the operation failed.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>The failure, or <see cref="Results.Error.None"/> on success.</summary>
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);
}

/// <summary>
/// The outcome of an operation that produces a value when it succeeds.
/// </summary>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error) => _value = value;

    /// <summary>
    /// The produced value.
    /// </summary>
    /// <exception cref="InvalidOperationException">The operation failed.</exception>
    /// <remarks>
    /// Throws rather than returning <see langword="null"/> on failure. Reading the value of a
    /// failed result is a programming error, and returning a default would let it pass silently
    /// into whatever used it.
    /// </remarks>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            $"A failed result has no value. Error: {Error.Code}.");
}
