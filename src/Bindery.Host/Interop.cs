using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Bindery.Host;

/// <summary>
/// The seam between the F# core and the C# host.
/// </summary>
/// <remarks>
/// F# options and lists are perfectly usable from C#, just verbose. Everything awkward
/// about the boundary lives in this one file so that no other host file has to mention
/// <c>FSharpOption</c>, and so that the core stays free of C#-shaped compromises.
/// </remarks>
internal static class Interop
{
    /// <summary>An option of a reference type as a nullable reference.</summary>
    public static T? OrNull<T>(this FSharpOption<T>? option) where T : class =>
        FSharpOption<T>.get_IsSome(option) ? option!.Value : null;

    /// <summary>An option of a value type as a nullable value.</summary>
    public static T? OrNullable<T>(this FSharpOption<T>? option) where T : struct =>
        FSharpOption<T>.get_IsSome(option) ? option!.Value : null;

    public static T OrDefault<T>(this FSharpOption<T>? option, T fallback) =>
        FSharpOption<T>.get_IsSome(option) ? option!.Value : fallback;

    public static bool HasValue<T>(this FSharpOption<T>? option) => FSharpOption<T>.get_IsSome(option);

    public static FSharpOption<T> ToOption<T>(this T? value) where T : class =>
        value is null ? FSharpOption<T>.None : FSharpOption<T>.Some(value);

    public static FSharpOption<T> ToValueOption<T>(this T? value) where T : struct =>
        value.HasValue ? FSharpOption<T>.Some(value.Value) : FSharpOption<T>.None;

    public static FSharpOption<T> ToValueOption<T>(this T value) where T : struct =>
        FSharpOption<T>.Some(value);

    public static IReadOnlyList<T> AsList<T>(this FSharpList<T> list) => [.. list];

    public static FSharpList<T> ToFSharpList<T>(this IEnumerable<T> items) => ListModule.OfSeq(items);

    public static FSharpMap<TKey, TValue> ToFSharpMap<TKey, TValue>(
        this IEnumerable<KeyValuePair<TKey, TValue>> pairs)
        where TKey : notnull, IComparable =>
        MapModule.OfSeq(pairs.Select(pair => Tuple.Create(pair.Key, pair.Value)));

    /// <summary>
    /// Turns an F# <c>Result</c> into a value or an exception message. Used where a
    /// failure genuinely is exceptional; elsewhere, match on the result.
    /// </summary>
    public static bool TryGet<TValue, TError>(
        this FSharpResult<TValue, TError> result,
        out TValue value,
        out TError error)
    {
        if (result.IsOk)
        {
            value = result.ResultValue;
            error = default!;
            return true;
        }

        value = default!;
        error = result.ErrorValue;
        return false;
    }
}
