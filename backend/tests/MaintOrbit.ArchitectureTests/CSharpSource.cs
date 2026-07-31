using System.Text;

namespace MaintOrbit.ArchitectureTests;

/// <summary>
/// Strips comments and string literals so source rules match code rather than prose.
/// </summary>
/// <remarks>
/// Needed because this codebase documents the rules it follows. <c>U-3</c> is explained in an
/// XML comment that names <c>DateTime.UtcNow</c>, and the message a validator produces quotes the
/// setting it rejects. A rule that searched raw text would report both as violations, and the
/// usual response to a gate that cries wolf is to weaken the gate.
/// <para>
/// Replaces removed spans with spaces rather than deleting them, so line and column numbers
/// survive and a reported violation still points at the right place.
/// </para>
/// </remarks>
internal static class CSharpSource
{
    public static string StripCommentsAndLiterals(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var output = new StringBuilder(source.Length);
        var index = 0;

        while (index < source.Length)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (current == '/' && next == '/')
            {
                index = SkipToEndOfLine(source, index, output);
            }
            else if (current == '/' && next == '*')
            {
                index = SkipBlockComment(source, index, output);
            }
            else if (current == '@' && next == '"')
            {
                index = SkipVerbatimString(source, index, output);
            }
            else if (current == '"')
            {
                index = SkipString(source, index, output);
            }
            else if (current == '\'')
            {
                index = SkipCharacter(source, index, output);
            }
            else
            {
                output.Append(current);
                index++;
            }
        }

        return output.ToString();
    }

    private static int SkipToEndOfLine(string source, int index, StringBuilder output)
    {
        while (index < source.Length && source[index] != '\n')
        {
            output.Append(' ');
            index++;
        }

        return index;
    }

    private static int SkipBlockComment(string source, int index, StringBuilder output)
    {
        output.Append("  ");
        index += 2;

        while (index < source.Length)
        {
            if (source[index] == '*' && index + 1 < source.Length && source[index + 1] == '/')
            {
                output.Append("  ");
                return index + 2;
            }

            // Newlines are preserved so line numbers do not shift.
            output.Append(source[index] == '\n' ? '\n' : ' ');
            index++;
        }

        return index;
    }

    private static int SkipVerbatimString(string source, int index, StringBuilder output)
    {
        output.Append("  ");
        index += 2;

        while (index < source.Length)
        {
            if (source[index] == '"')
            {
                // "" is an escaped quote inside a verbatim string, not the end of it.
                if (index + 1 < source.Length && source[index + 1] == '"')
                {
                    output.Append("  ");
                    index += 2;
                    continue;
                }

                output.Append(' ');
                return index + 1;
            }

            output.Append(source[index] == '\n' ? '\n' : ' ');
            index++;
        }

        return index;
    }

    private static int SkipString(string source, int index, StringBuilder output)
    {
        output.Append(' ');
        index++;

        while (index < source.Length && source[index] != '"')
        {
            if (source[index] == '\\')
            {
                output.Append(' ');
                index++;
            }

            if (index >= source.Length || source[index] == '\n')
            {
                break;
            }

            output.Append(' ');
            index++;
        }

        if (index < source.Length && source[index] == '"')
        {
            output.Append(' ');
            index++;
        }

        return index;
    }

    private static int SkipCharacter(string source, int index, StringBuilder output)
    {
        output.Append(' ');
        index++;

        while (index < source.Length && source[index] != '\'')
        {
            if (source[index] == '\\')
            {
                output.Append(' ');
                index++;
            }

            if (index >= source.Length || source[index] == '\n')
            {
                break;
            }

            output.Append(' ');
            index++;
        }

        if (index < source.Length && source[index] == '\'')
        {
            output.Append(' ');
            index++;
        }

        return index;
    }
}
