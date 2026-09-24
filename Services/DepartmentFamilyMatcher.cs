namespace SIT.DepartmentSystem.Web.Services;

public static class DepartmentFamilyMatcher
{
    private const string WujiangSuffix = "-WJ";

    public static bool IsRecognizedLocation(string? location) =>
        string.Equals(location?.Trim(), "台北", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(location?.Trim(), "吳江", StringComparison.OrdinalIgnoreCase);

    public static bool MatchesLocation(string? department, string? location)
    {
        if (!TryParseDa40Family(department, out var isWujiang))
            return false;

        var normalizedLocation = location?.Trim();
        if (string.Equals(normalizedLocation, "台北", StringComparison.OrdinalIgnoreCase))
            return !isWujiang;

        if (string.Equals(normalizedLocation, "吳江", StringComparison.OrdinalIgnoreCase))
            return isWujiang;

        return false;
    }

    private static bool TryParseDa40Family(string? department, out bool isWujiang)
    {
        isWujiang = false;
        var normalized = department?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized))
            return false;

        if (normalized.EndsWith(WujiangSuffix, StringComparison.Ordinal))
        {
            isWujiang = true;
            normalized = normalized[..^WujiangSuffix.Length];
        }

        if (!normalized.StartsWith("DA", StringComparison.Ordinal))
            return false;

        var numericCode = normalized[2..];
        if (numericCode.Length == 0 || numericCode.Any(character => !char.IsAsciiDigit(character)))
            return false;

        var withoutLeadingZeros = numericCode.TrimStart('0');
        return withoutLeadingZeros.StartsWith("40", StringComparison.Ordinal);
    }
}
