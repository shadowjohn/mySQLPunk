using System.Globalization;

namespace MySqlPunk.Core.Services;

public sealed class QueryResultValueComparer : IComparer<object?>
{
    public static QueryResultValueComparer Instance { get; } = new();

    private QueryResultValueComparer()
    {
    }

    public int Compare(object? left, object? right)
    {
        left = NormalizeNull(left);
        right = NormalizeNull(right);
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        if (TryCompareNumbers(left, right, out var numericComparison))
        {
            return numericComparison;
        }

        if (left is byte[] leftBytes && right is byte[] rightBytes)
        {
            return CompareBytes(leftBytes, rightBytes);
        }

        if (left is string leftText && right is string rightText)
        {
            return StringComparer.CurrentCulture.Compare(leftText, rightText);
        }

        if (left.GetType() == right.GetType() && left is IComparable comparable)
        {
            try
            {
                return comparable.CompareTo(right);
            }
            catch (ArgumentException)
            {
                // Some provider values advertise IComparable but reject otherwise valid instances.
            }
        }

        var typeComparison = StringComparer.Ordinal.Compare(
            left.GetType().FullName ?? left.GetType().Name,
            right.GetType().FullName ?? right.GetType().Name);
        if (typeComparison != 0)
        {
            return typeComparison;
        }

        return StringComparer.CurrentCulture.Compare(
            Convert.ToString(left, CultureInfo.CurrentCulture) ?? string.Empty,
            Convert.ToString(right, CultureInfo.CurrentCulture) ?? string.Empty);
    }

    private static object? NormalizeNull(object? value) => value is null or DBNull ? null : value;

    private static bool TryCompareNumbers(object left, object right, out int comparison)
    {
        comparison = 0;
        if (!IsNumber(left) || !IsNumber(right))
        {
            return false;
        }

        if (IsFloatingPoint(left) || IsFloatingPoint(right))
        {
            comparison = Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture));
            return true;
        }

        comparison = Convert.ToDecimal(left, CultureInfo.InvariantCulture)
            .CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture));
        return true;
    }

    private static bool IsFloatingPoint(object value) => value is float or double;

    private static bool IsNumber(object value) => value is
        sbyte or byte or short or ushort or int or uint or long or ulong or
        float or double or decimal;

    private static int CompareBytes(IReadOnlyList<byte> left, IReadOnlyList<byte> right)
    {
        var sharedLength = Math.Min(left.Count, right.Count);
        for (var index = 0; index < sharedLength; index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Count.CompareTo(right.Count);
    }
}
