using System.Text;

namespace ZScape.Utilities;

/// <summary>
/// Utility class for handling Doom/Zandronum color codes in strings.
/// Color codes use escape character \x1c (char 28) followed by:
/// - A single character (a-v, +, -, !, *) for predefined colors
/// - A bracket-enclosed string like [colorname] for custom/PWAD colors
/// </summary>
public static class DoomColorCodes
{
    /// <summary>
    /// The escape character used to start color codes in Doom engine games.
    /// This is character 28 (0x1c or \034 in octal).
    /// </summary>
    public const char EscapeColorChar = '\x1c';

    /// <summary>
    /// Strips all color codes from a string, returning plain text.
    /// </summary>
    /// <param name="text">The text containing color codes.</param>
    /// <returns>The text with all color codes removed.</returns>
    public static string StripColorCodes(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var result = new StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            // Check for non-printable characters (outside ASCII 32-126 range)
            if (c < 32 || c > 126)
            {
                // Check if this is a color escape sequence
                if (c == EscapeColorChar)
                {
                    // Skip the color code that follows
                    int colorCodeIdx = i + 1;
                    bool isRange = false;

                    // Find out what exactly needs to be removed
                    // Either a single character, a bracket-enclosed range, or nothing if invalid
                    for (; colorCodeIdx < text.Length; colorCodeIdx++)
                    {
                        char symbol = text[colorCodeIdx];
                        if (symbol == '[')
                        {
                            isRange = true;
                        }
                        else if ((isRange && symbol == ']') || !isRange)
                        {
                            break;
                        }
                    }

                    // If we started a range but didn't find the end, just skip the escape char
                    if (isRange && colorCodeIdx >= text.Length)
                    {
                        i++; // Skip just the escape char, keep processing
                    }
                    else
                    {
                        i = colorCodeIdx; // Skip past the entire color code
                    }
                }
                // Skip other non-printable characters
                continue;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    /// <summary>
    /// Checks if a string contains any color codes.
    /// </summary>
    /// <param name="text">The text to check.</param>
    /// <returns>True if the text contains color codes, false otherwise.</returns>
    public static bool ContainsColorCodes(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        return text.Contains(EscapeColorChar);
    }
}
