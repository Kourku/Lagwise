using System;
using System.Globalization;
using System.Text;

namespace Lagwise.Core;

/// <summary>
/// Text that came from the network (a reverse-DNS name, a certificate name, a status page, a redirect address, a server's
/// reason phrase) is shown and put in reports only after passing through here. Control and invisible formatting
/// characters are removed, because they can reorder or hide text (U+202E turns "gpj.exe" into "exe.jpg"), and the length
/// is limited, so a hostile answer cannot fill the window or a report.
/// </summary>
public static class UntrustedText
{
    public static string? Clean(string? text, int maxLength = 200)
    {
        if (text == null) return null;
        var sb = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        foreach (char c in text)
        {
            if (sb.Length > maxLength) break;
            switch (char.GetUnicodeCategory(c))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');   // a line break or tab becomes a space
                    break;
                case UnicodeCategory.Format:              // bidirectional overrides, zero-width characters
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        string result = sb.ToString().Trim();
        if (result.Length <= maxLength) return result;
        int cut = maxLength - 1;
        if (char.IsHighSurrogate(result[cut - 1])) cut--;     // never split a character made of two halves
        return result[..cut] + "…";
    }

    /// <summary>A reverse-DNS answer, kept only when it is a valid host name (anything else is shown as no name).</summary>
    public static string HostName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        string trimmed = name.TrimEnd('.');
        if (trimmed.Length == 0 || trimmed.Length > 253) return string.Empty;
        // names travel in DNS as ASCII (international ones as xn--); Uri.CheckHostName would also accept U+202E and the like
        foreach (string label in trimmed.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return string.Empty;
            foreach (char c in label)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return string.Empty;
        }
        return trimmed;
    }

    /// <summary>
    /// One CSV field: always quoted, quotes doubled, and a leading = + - @ (which a spreadsheet would run as a formula,
    /// possibly one that starts a program) made inert with an apostrophe.
    /// </summary>
    public static string CsvField(string? value)
    {
        string v = value ?? string.Empty;
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n') v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }
}
