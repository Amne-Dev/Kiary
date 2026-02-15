using System.Text;

namespace EncryptedDiary.WinUI;

internal static class TextSanitizer
{
    public static string Sanitize(string? value, bool allowNewLines)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder builder = new(value.Length);

        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];

            if (char.IsSurrogate(ch))
            {
                if (i + 1 < value.Length && char.IsSurrogatePair(value, i))
                {
                    builder.Append(ch);
                    builder.Append(value[i + 1]);
                    i++;
                }

                continue;
            }

            if (char.IsControl(ch))
            {
                bool allowed =
                    ch == '\t' ||
                    (allowNewLines && (ch == '\r' || ch == '\n'));

                if (!allowed)
                {
                    continue;
                }
            }

            if (!allowNewLines && (ch == '\r' || ch == '\n'))
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}

