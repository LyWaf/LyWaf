namespace LyWaf.Policy;

/// <summary>Matches three-digit HTTP status patterns; each x independently matches one digit.</summary>
public static class HealthStatusCodes
{
    public const string Pattern = @"^[1-5xX][0-9xX]{2}(?:,[1-5xX][0-9xX]{2})*$";

    public static bool Matches(string patterns, int code)
    {
        if (code is < 100 or > 599) return false;
        var digits = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var raw in patterns.Split(','))
        {
            var pattern = raw.Trim();
            if (pattern.Length != 3) continue;
            if (Enumerable.Range(0, 3).All(i => pattern[i] is 'x' or 'X' || pattern[i] == digits[i])) return true;
        }
        return false;
    }
}
