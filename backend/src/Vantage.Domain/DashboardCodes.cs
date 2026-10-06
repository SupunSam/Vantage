namespace Vantage.Domain;

/// <summary>
/// Dashboard codes are made from the name, never typed (C60): exactly 6 upper-case letters and digits. The first letters of the
/// words come first, then the rest of the name fills the gap. A code that is already taken ends in a short counter instead.
/// </summary>
public static class DashboardCodes
{
    public const int Length = 6;

    public static string Generate(string? name, Func<string, bool> isTaken)
    {
        var words = System.Text.RegularExpressions.Regex.Split(name ?? "", "[^A-Za-z0-9]+").Where(w => w.Length > 0).Select(w => w.ToUpperInvariant()).ToList();
        var chars = new List<char>();
        foreach (var w in words) chars.Add(w[0]);
        for (var i = 1; chars.Count < Length; i++)
        {
            var added = false;
            foreach (var w in words)
            {
                if (i < w.Length && chars.Count < Length) { chars.Add(w[i]); added = true; }
            }
            if (!added) break;
        }
        while (chars.Count < Length) chars.Add('0');
        var baseCode = new string(chars.Take(Length).ToArray());

        if (!isTaken(baseCode)) return baseCode;
        for (var n = 2; n < 36 * 36 * 36; n++)
        {
            var suffix = ToBase36(n);
            var code = baseCode[..(Length - suffix.Length)] + suffix;
            if (!isTaken(code)) return code;
        }
        throw new InvalidOperationException("No free dashboard code.");
    }

    private static string ToBase36(int n)
    {
        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var s = "";
        for (; n > 0; n /= 36) s = digits[n % 36] + s;
        return s;
    }
}
