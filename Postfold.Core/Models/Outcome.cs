using Postfold.Core.Enums;
using System;

namespace Postfold.Core.Models;

public sealed record Outcome<TValue>(TValue? Value, Failure Failure)
{
    public bool IsSuccess => Failure == Failure.None;

    public static Outcome<TValue> Success(TValue value)
    {
        return new Outcome<TValue>(value, Failure.None);
    }

    public static Outcome<TValue> Fail(Failure failure)
    {
        if (failure == Failure.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        return new Outcome<TValue>(default, failure);
    }
}
