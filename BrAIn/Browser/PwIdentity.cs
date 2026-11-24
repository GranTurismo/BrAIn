using System.Text;
using System.Text.RegularExpressions;

namespace BrAIn.Browser;

/// <summary>
/// "Suggest email &amp; strong password" on a sign-up form: the login half. The suggestions come from the logins the user already
/// has in the vault (the e-mail addresses, phone numbers and usernames they sign in with elsewhere), picked to fit the field
/// (<c>email</c>, <c>phone</c>, <c>username</c>) and ordered by how much they are used.
///
/// What a web page may learn: only a masked label per suggestion (<c>a•••@example.com</c>, <c>+•• ••• •• 12 34</c>) and, for the one the
/// user clicked, the full value — which then sits in a field the page can read anyway. The page's own script can watch what is pushed
/// into it, so the other identifiers must never be sent in full. The full list stays host-side (see BrowserController.PwIdents).
/// </summary>
internal static partial class PwIdentity
{
    internal const int Max = 5;

    internal enum Kind { Email, Phone, Username }

    internal static Kind? ParseKind(string? s) => s switch { "email" => Kind.Email, "phone" => Kind.Phone, "username" => Kind.Username, _ => null };

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRx();

    [GeneratedRegex(@"^\+?[\d\s().\-]{7,24}$")]
    private static partial Regex PhoneRx();

    internal static Kind Classify(string user)
    {
        if (EmailRx().IsMatch(user)) return Kind.Email;
        if (PhoneRx().IsMatch(user) && user.Count(char.IsDigit) >= 7) return Kind.Phone;
        return Kind.Username;
    }

    /// <summary>The identifiers that suit <paramref name="kind"/>, most used first (then most recent), at most <see cref="Max"/>, each once
    /// (case-insensitively). A username box takes any plain username first and an e-mail address after them; an e-mail or phone box takes
    /// only its own kind — never a made-up one.</summary>
    internal static List<string> Pick(IEnumerable<PasswordStore.Entry> entries, Kind kind)
    {
        var groups = entries
            .Select(e => (User: (e.User ?? "").Trim(), e.Used))
            .Where(e => e.User.Length > 0 && e.User.Length <= 120)
            .GroupBy(e => e.User, StringComparer.OrdinalIgnoreCase)
            .Select(g => (User: g.First().User, Count: g.Count(), Used: g.Max(x => x.Used), Kind: Classify(g.First().User)))
            .ToList();
        IEnumerable<(string User, int Count, long Used, Kind Kind)> pool = kind switch
        {
            Kind.Username => groups.Where(g => g.Kind != Kind.Phone),
            _ => groups.Where(g => g.Kind == kind),
        };
        return pool
            .OrderBy(g => kind == Kind.Username && g.Kind == Kind.Email ? 1 : 0)
            .ThenByDescending(g => g.Count).ThenByDescending(g => g.Used)
            .Take(Max).Select(g => g.User).ToList();
    }

    /// <summary>What the page is shown instead of the value.</summary>
    internal static string Mask(string user)
    {
        switch (Classify(user))
        {
            case Kind.Email:
            {
                var at = user.LastIndexOf('@');
                var local = user[..at];
                return (local.Length > 0 ? local[..1] : "") + "•••" + user[at..];
            }
            case Kind.Phone:
            {
                // every digit but the last four
                var digits = user.Count(char.IsDigit);
                var seen = 0;
                var sb = new StringBuilder();
                foreach (var ch in user)
                {
                    if (!char.IsDigit(ch)) { sb.Append(ch); continue; }
                    seen++;
                    sb.Append(digits - seen < 4 ? ch : '•');
                }
                return sb.ToString();
            }
            default:
                return user.Length <= 2 ? user[..1] + "•••" : user[..2] + "•••";
        }
    }
}
