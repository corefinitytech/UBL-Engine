using System.Globalization;
using System.Text;

namespace Ubl.Engine.Core.Cad;

/// <summary>
/// Turns raw AutoCAD MTEXT and TEXT strings into plain text. Readers differ in what they strip (ACadSharp 3.8
/// leaves \L underline and \S stacked fractions in), so the engine decodes the raw value itself.
/// </summary>
public static class MTextDecoder
{
    public static string Decode(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c is '{' or '}') { i++; continue; }
            if (c == '%' && i + 2 < raw.Length && raw[i + 1] == '%') { i = Percent(raw, i, sb); continue; }
            if (c != '\\' || i + 1 >= raw.Length) { sb.Append(c); i++; continue; }

            char d = raw[i + 1];
            switch (d)
            {
                case 'P': case 'X': case 'N': sb.Append('\n'); i += 2; break;          // paragraph, dim-text line, column break
                case '~': sb.Append(' '); i += 2; break;                                 // non-breaking space
                case '\\': case '{': case '}': sb.Append(d); i += 2; break;              // escaped characters
                case 'L': case 'l': case 'O': case 'o': case 'K': case 'k': i += 2; break; // underline / overline / strike toggles
                case 'S':                                                                  // stacked text: \S1/2; \S1^2; \S1#2;
                {
                    int end = raw.IndexOf(';', i + 2);
                    string body = end < 0 ? raw[(i + 2)..] : raw[(i + 2)..end];
                    var parts = body.Split('^', '/', '#');
                    if (sb.Length > 0 && char.IsDigit(sb[^1])) sb.Append(' ');
                    sb.Append(parts.Length >= 2 ? $"{parts[0].Trim()}/{parts[1].Trim()}" : body);
                    i = end < 0 ? raw.Length : end + 1;
                    break;
                }
                case 'U' when i + 6 < raw.Length && raw[i + 2] == '+':                     // \U+00B0
                    if (int.TryParse(raw.AsSpan(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                        sb.Append((char)code);
                    i += 7;
                    break;
                default:                                                                   // \f..; \H..; \W..; \Q..; \T..; \A..; \C..; \c..; \p..;
                {
                    int end = raw.IndexOf(';', i + 2);
                    i = end < 0 ? i + 2 : end + 1;
                    break;
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>Single-line TEXT: only the %% control codes apply.</summary>
    public static string DecodeText(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == '%' && i + 2 < raw.Length && raw[i + 1] == '%') { i = Percent(raw, i, sb); continue; }
            sb.Append(raw[i]);
            i++;
        }
        return sb.ToString();
    }

    private static int Percent(string raw, int i, StringBuilder sb)
    {
        switch (char.ToLowerInvariant(raw[i + 2]))
        {
            case 'c': sb.Append('Ø'); break;   // diameter
            case 'd': sb.Append('°'); break;   // degree
            case 'p': sb.Append('±'); break;   // plus-minus
            case '%': sb.Append('%'); break;
            case 'u': case 'o': break;               // underline / overline toggles
            default: sb.Append(raw, i, 3); break;
        }
        return i + 3;
    }
}
