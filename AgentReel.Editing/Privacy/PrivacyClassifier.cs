using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace AgentReel.Editing.Privacy;

public enum PrivacyKind
{
    Email,
    ApiKey,
    Jwt,
    PrivateKey,
    Secret,
    Password,
    Iban,
    CreditCard,
    Phone,
    IpAddress
}

public enum PrivacySeverity
{
    Low,
    Medium,
    High
}

/// <summary>
/// A sensitive part of a line of text: characters <see cref="Start"/> to <see cref="Start"/> + <see cref="Length"/>.
/// </summary>
public sealed record TextMatch(PrivacyKind Kind, PrivacySeverity Severity, int Start, int Length, string Text);

/// <summary>
/// Finds private data in one line of (OCR) text. Pure, so it is easy to test; thresholds are chosen to flag real
/// secrets even with OCR noise while leaving prose, file paths, version numbers and commit hashes alone:
/// <list type="bullet">
/// <item>Known token formats (sk-, ghp_, github_pat_, xox?-, AKIA, AIza, glpat-, npm_, Stripe keys, JWTs) are always High.</item>
/// <item>Unknown tokens count as secrets only if they have at least 20 characters, mix lower case, upper case and digits,
/// carry at least 3.5 bits of Shannon entropy per character and switch character classes often (see <see cref="SwitchRatio"/>),
/// which tells random strings from CamelCase names and paths. Hex-only strings (commit hashes, digests, GUIDs) are
/// never flagged by entropy alone.</item>
/// <item>Values after labels like password, Passwort, token, secret or api_key with ':' or '=' are passwords.</item>
/// <item>IBANs need a valid mod-97 checksum, card numbers a valid Luhn checksum; phone numbers need a leading '+'.</item>
/// <item>IPv4 addresses are only Low, version numbers look the same.</item>
/// </list>
/// </summary>
public static partial class PrivacyClassifier
{
    public const int MinSecretLength = 20;
    public const double MinSecretEntropy = 3.5;
    public const double MinSwitchRatio = 0.42;

    public static List<TextMatch> Classify(string line)
    {
        var matches = new List<TextMatch>();

        void Add(PrivacyKind kind, PrivacySeverity severity, int start, int length)
        {
            //The first, most specific rule wins; later rules must not report the same characters again.
            if (length <= 0 || matches.Any(a => start < a.Start + a.Length && a.Start < start + length))
                return;

            matches.Add(new TextMatch(kind, severity, start, length, line.Substring(start, length)));
        }

        foreach (Match match in PrivateKeyRegex().Matches(line))
            Add(PrivacyKind.PrivateKey, PrivacySeverity.High, match.Index, match.Length);

        foreach (Match match in JwtRegex().Matches(line))
            Add(PrivacyKind.Jwt, PrivacySeverity.High, match.Index, match.Length);

        foreach (Match match in ApiKeyRegex().Matches(line))
            Add(PrivacyKind.ApiKey, PrivacySeverity.High, match.Index, match.Length);

        foreach (Match match in LabeledValueRegex().Matches(line))
        {
            var value = match.Groups["value"];

            if (!IsPlaceholder(value.Value))
                Add(PrivacyKind.Password, PrivacySeverity.High, value.Index, value.Length);
        }

        foreach (Match match in EmailRegex().Matches(line))
            Add(PrivacyKind.Email, PrivacySeverity.Medium, match.Index, match.Length);

        foreach (Match match in IbanRegex().Matches(line))
        {
            if (IsValidIban(match.Value))
                Add(PrivacyKind.Iban, PrivacySeverity.High, match.Index, match.Length);
        }

        foreach (Match match in CardRegex().Matches(line))
        {
            var digits = Digits(match.Value);

            if (digits.Length is >= 13 and <= 19 && digits[0] is >= '2' and <= '6' && IsLuhnValid(digits))
                Add(PrivacyKind.CreditCard, PrivacySeverity.High, match.Index, match.Length);
        }

        foreach (Match match in PhoneRegex().Matches(line))
        {
            if (Digits(match.Value).Length is >= 8 and <= 15)
                Add(PrivacyKind.Phone, PrivacySeverity.Medium, match.Index, match.Length);
        }

        foreach (Match match in TokenRegex().Matches(line))
        {
            if (IsHighEntropySecret(match.Value))
                Add(PrivacyKind.Secret, match.Length >= 32 ? PrivacySeverity.High : PrivacySeverity.Medium, match.Index, match.Length);
        }

        foreach (Match match in Ipv4Regex().Matches(line))
        {
            if (match.Value.Split('.').All(a => int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var part) && part <= 255) && !match.Value.StartsWith("127.", StringComparison.Ordinal) && match.Value != "0.0.0.0")
                Add(PrivacyKind.IpAddress, PrivacySeverity.Low, match.Index, match.Length);
        }

        return matches.OrderBy(o => o.Start).ToList();
    }

    /// <summary>
    /// A token is a likely secret if it is long, mixes lower case, upper case and digits, is not just hex, and looks random.
    /// </summary>
    public static bool IsHighEntropySecret(string token)
    {
        if (token.Length < MinSecretLength)
            return false;

        if (token.All(Uri.IsHexDigit))
            return false;

        if (!token.Any(char.IsLower) || !token.Any(char.IsUpper) || !token.Any(char.IsDigit))
            return false;

        return Entropy(token) >= MinSecretEntropy && SwitchRatio(token) >= MinSwitchRatio;
    }

    /// <summary>
    /// How often the character class (lower, upper, digit, other) changes from one character to the next. Random tokens
    /// switch at about 60 % of the positions, words, CamelCase names and paths like "Studio/bin/Release/net9" at about 30–40 %.
    /// </summary>
    public static double SwitchRatio(string text)
    {
        if (text.Length < 2)
            return 0;

        static int Class(char c) => char.IsLower(c) ? 0 : char.IsUpper(c) ? 1 : char.IsDigit(c) ? 2 : 3;

        var switches = 0;

        for (var i = 1; i < text.Length; i++)
        {
            if (Class(text[i]) != Class(text[i - 1]))
                switches++;
        }

        return (double)switches / (text.Length - 1);
    }

    /// <summary>
    /// Shannon entropy in bits per character.
    /// </summary>
    public static double Entropy(string text)
    {
        if (text.Length == 0)
            return 0;

        return text.GroupBy(g => g).Sum(s =>
        {
            var p = (double)s.Count() / text.Length;
            return -p * Math.Log2(p);
        });
    }

    public static bool IsLuhnValid(string digits)
    {
        var sum = 0;
        var doubled = false;

        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var digit = digits[i] - '0';

            if (doubled && (digit *= 2) > 9)
                digit -= 9;

            sum += digit;
            doubled = !doubled;
        }

        return sum % 10 == 0;
    }

    public static bool IsValidIban(string text)
    {
        var iban = new string(text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        if (iban.Length is < 15 or > 34)
            return false;

        var rearranged = iban[4..] + iban[..4];
        var numeric = string.Concat(rearranged.Select(s => char.IsLetter(s) ? (s - 'A' + 10).ToString(CultureInfo.InvariantCulture) : s.ToString()));

        return BigInteger.Parse(numeric, CultureInfo.InvariantCulture) % 97 == 1;
    }

    /// <summary>
    /// A short label for people that does not reveal the value: the kind, the first characters and the length.
    /// </summary>
    public static string Mask(PrivacyKind kind, string text)
    {
        var shown = kind switch
        {
            //The token prefix tells which service it is for and is not secret.
            PrivacyKind.ApiKey or PrivacyKind.Jwt => text[..Math.Min(text.Length, Math.Clamp(text.IndexOfAny(['-', '_']) + 1, 3, 6))],
            PrivacyKind.Iban => text[..Math.Min(2, text.Length)],
            PrivacyKind.PrivateKey => "-----BEGIN",
            _ => text[..Math.Min(text.Length, text.Length >= 12 ? 2 : 1)]
        };

        return $"{shown}… ({text.Length} chars)";
    }

    private static bool IsPlaceholder(string value) =>
        (value.All(char.IsAsciiDigit) && value.Length < 6) || value.Trim('*', '•', '●', '.', 'x', 'X').Length == 0 || value.StartsWith("${", StringComparison.Ordinal) || value.StartsWith('<') ||
        value is "null" or "none" or "true" or "false" or "required" or "optional" or "string" or "undefined";

    private static string Digits(string text) => new(text.Where(char.IsAsciiDigit).ToArray());

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}(?:\.[A-Za-z0-9_-]{5,})?|\beyJ[A-Za-z0-9_-]{30,}")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\b(?:sk-ant-[A-Za-z0-9_-]{10,}|sk-(?:proj-|live-|test-)?[A-Za-z0-9_-]{16,}|[spr]k_(?:live|test)_[A-Za-z0-9]{10,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,}|glpat-[A-Za-z0-9_-]{20,}|npm_[A-Za-z0-9]{30,})")]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(@"(?i)(?:password|passwort|passwd|kennwort|pwd|secret|token|api[_ -]?key|access[_ -]?key|client[_ -]?secret|private[_ -]?key)(?:_[A-Za-z0-9_]*)?['""]?\s*[:=]\s*['""]?(?<value>[^\s'"",;]{4,})")]
    private static partial Regex LabeledValueRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,3})?\b")]
    private static partial Regex IbanRegex();

    [GeneratedRegex(@"\b\d(?:[ -]?\d){12,18}\b")]
    private static partial Regex CardRegex();

    [GeneratedRegex(@"\+\d{1,3}[ -]?(?:\(\d{1,4}\)[ -]?)?\d{2,5}(?:[ -]?\d{2,5}){1,4}\b")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"[A-Za-z0-9+/=_-]{20,}")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b")]
    private static partial Regex Ipv4Regex();
}
