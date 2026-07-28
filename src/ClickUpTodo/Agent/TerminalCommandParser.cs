using System.Text;

namespace ClickUpTodo.Agent;

/// <summary>
/// Pure, quote-aware tokeniser for the user-configured terminal launch command (#385). Splits a
/// shell-style command line (e.g. <c>alacritty -e {}</c>, <c>'my term' --exec {}</c>) into an argv,
/// honouring single- and double-quoted runs so an emulator path with spaces survives as one token.
///
/// The tokeniser is deliberately minimal — it does no escape/variable/glob processing, just quote-run
/// grouping — because the result is an argv handed straight to <see cref="TerminalCommandPlanner"/>,
/// not a string re-parsed by a shell. A token equal to <see cref="Placeholder"/> marks where the
/// planner splices the OS host invocation of the command to run; see the planner for the expansion.
/// </summary>
public static class TerminalCommandParser
{
    /// <summary>The token that marks where the launched command is spliced into the template.</summary>
    public const string Placeholder = "{}";

    /// <summary>
    /// Tokenise <paramref name="command"/> into an argv. Blank/whitespace ⇒ an empty list (no custom
    /// command). Single- and double-quoted runs group whitespace into one token; empty quoted runs
    /// (<c>""</c>) collapse away rather than emit a blank argument.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return [];

        var tokens = new List<string>();
        var sb = new StringBuilder();
        var started = false; // a token is in progress (so a lone "" is recognised, then dropped as empty)
        var quote = '\0';

        foreach (var c in command)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    sb.Append(c);
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                started = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (started && sb.Length > 0)
                    tokens.Add(sb.ToString());
                sb.Clear();
                started = false;
                continue;
            }

            sb.Append(c);
            started = true;
        }

        if (started && sb.Length > 0)
            tokens.Add(sb.ToString());

        return tokens;
    }
}
