using System.Globalization;
using System.Text;

namespace Emtehani.Domain.Services;

public static class AcademicYear
{
    /// <summary>
    /// Egyptian school year starts in September. Dates before September belong to the
    /// academic year that started the previous September.
    /// </summary>
    public static string For(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        var start = utc.Month >= 9 ? utc.Year : utc.Year - 1;
        return $"{start}/{start + 1}";
    }

    public static string Current() => For(DateTime.UtcNow);
}

public static class IdentityNormalizer
{
    private static readonly char[] ArabicDiacritics =
    [
        '\u064B', '\u064C', '\u064D', '\u064E', '\u064F', '\u0650',
        '\u0651', '\u0652', '\u0653', '\u0654', '\u0655', '\u0656',
        '\u0657', '\u0658', '\u0670'
    ];

    /// <summary>Normalises names for duplicate detection without changing the display name.</summary>
    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        var builder = new StringBuilder(text.Length);
        var previousWasSpace = false;
        foreach (var raw in text)
        {
            if (Array.IndexOf(ArabicDiacritics, raw) >= 0 || raw == '\u0640') continue;
            var ch = raw switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ؤ' => 'و',
                'ئ' => 'ي',
                'ة' => 'ه',
                _ => raw
            };
            if (char.IsWhiteSpace(ch))
            {
                if (previousWasSpace || builder.Length == 0) continue;
                builder.Append(' ');
                previousWasSpace = true;
                continue;
            }
            // Ignore punctuation so "سارة، أحمد" and "سارة أحمد" collide.
            if (char.IsPunctuation(ch) || char.IsSymbol(ch)) continue;
            builder.Append(ch);
            previousWasSpace = false;
        }
        return builder.ToString().Trim();
    }

    /// <summary>
    /// Returns digits-only phones and canonicalises Egyptian international numbers
    /// (+20 / 0020) to the local 01xxxxxxxxx form so one phone cannot register twice.
    /// </summary>
    public static string? NormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var builder = new StringBuilder(value.Length);
        foreach (var raw in value.Trim())
        {
            var ch = raw switch
            {
                >= '\u0660' and <= '\u0669' => (char)('0' + (raw - '\u0660')),
                >= '\u06F0' and <= '\u06F9' => (char)('0' + (raw - '\u06F0')),
                _ => raw
            };
            if (ch is >= '0' and <= '9') builder.Append(ch);
        }
        var digits = builder.ToString();
        if (digits.StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];
        if (digits.StartsWith("20", StringComparison.Ordinal) && digits.Length == 12)
            digits = "0" + digits[2..];
        if (digits.Length is < 8 or > 15) return null;
        if (digits.All(ch => ch == '0')) return null;
        return digits;
    }

    public static string? PhoneConflictMessage(string field) =>
        field == "student" ? "رقم هاتف الطالب غير صالح. أدخل رقماً صحيحاً بين 8 و15 رقماً." : "رقم هاتف ولي الأمر غير صالح. أدخل رقماً صحيحاً بين 8 و15 رقماً.";

    public static string? FormatPhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return NormalizePhone(value);
    }
}
