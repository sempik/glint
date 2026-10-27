using System.Text;

namespace Glint.CommandBar;

/// <summary>Name scoring shared by the app, file and system providers. All inputs are expected lower-case.</summary>
internal static class Matcher
{
    /// <summary>First letter of every word plus camelCase humps: "Visual Studio Code" → "vsc".</summary>
    public static string Initials(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c)) continue;
            var prev = i > 0 ? name[i - 1] : ' ';
            if (!char.IsLetterOrDigit(prev) || (char.IsUpper(c) && char.IsLower(prev)))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>0 means no match. Tiers: prefix > word start > initials > substring > all words > subsequence.</summary>
    public static double Score(string name, string initials, string query, bool fuzzy)
    {
        if (query.Length == 0 || name.Length == 0) return 0;
        var coverage = (double)query.Length / name.Length;

        var idx = name.IndexOf(query, StringComparison.Ordinal);
        if (idx == 0) return 100 + coverage * 10;
        if (idx > 0)
        {
            for (var i = idx; i > 0; i = name.IndexOf(query, i + 1, StringComparison.Ordinal))
                if (!char.IsLetterOrDigit(name[i - 1])) return 80 + coverage * 10;
        }
        if (query.Length >= 2 && initials.StartsWith(query, StringComparison.Ordinal))
            return 75 + (double)query.Length / Math.Max(1, initials.Length) * 5;
        if (idx > 0) return 60 + coverage * 10;

        if (query.Contains(' '))
        {
            var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 1 && tokens.All(t => name.Contains(t, StringComparison.Ordinal))) return 55;
        }

        if (fuzzy && query.Length >= 2)
        {
            int qi = 0, first = -1, last = -1;
            for (var i = 0; i < name.Length && qi < query.Length; i++)
            {
                if (name[i] != query[qi]) continue;
                if (first < 0) first = i;
                last = i;
                qi++;
            }
            if (qi == query.Length) return 20 + 20.0 * query.Length / (last - first + 1);
        }
        return 0;
    }
}
