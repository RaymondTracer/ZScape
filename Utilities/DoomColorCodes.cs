using System.Text;

namespace ZScape.Utilities;

/// <summary>
/// Represents a segment of colored text.
/// </summary>
public class ColoredTextSegment
{
    public string Text { get; set; } = "";
    public string ColorHex { get; set; } = "#FFFFFF";
}

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
    /// Doom color code palette (a-v).
    /// Based on Doomseeker's colorChart.
    /// </summary>
    private static readonly string[] ColorChart =
    [
        "#FF91A4", // a - Brick/Pink
        "#D2B48C", // b - Tan
        "#808080", // c - Gray
        "#32CD32", // d - Green
        "#918151", // e - Brown
        "#F4C430", // f - Gold
        "#E32636", // g - Red
        "#0000FF", // h - Blue
        "#FF8C00", // i - Orange
        "#C0C0C0", // j - White/Silver
        "#FFD700", // k - Yellow
        "#E34234", // l - Untranslated (Red)
        "#000000", // m - Black
        "#4169E1", // n - Blue
        "#FFDEAD", // o - Cream
        "#465945", // p - Olive
        "#228B22", // q - Dark Green
        "#800000", // r - Dark Red
        "#704214", // s - Dark Brown
        "#A020F0", // t - Purple
        "#404040", // u - Dark Gray
        "#007F7F", // v - Cyan
    ];

    /// <summary>
    /// Parses a string with Doom color codes and returns a list of colored text segments.
    /// </summary>
    /// <param name="text">The text containing color codes.</param>
    /// <param name="defaultColor">The default color hex code (e.g., "#FFFFFF").</param>
    /// <returns>A list of colored text segments.</returns>
    public static List<ColoredTextSegment> ParseColorCodes(string? text, string defaultColor = "#FFFFFF")
    {
        var segments = new List<ColoredTextSegment>();
        
        if (string.IsNullOrEmpty(text))
            return segments;
        
        var currentText = new StringBuilder();
        var currentColor = defaultColor;
        
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            
            if (c == EscapeColorChar)
            {
                // Save current segment if any
                if (currentText.Length > 0)
                {
                    segments.Add(new ColoredTextSegment { Text = currentText.ToString(), ColorHex = currentColor });
                    currentText.Clear();
                }
                
                i++;
                if (i >= text.Length)
                    break;
                
                char colorChar = char.ToLower(text[i]);
                
                // Handle special cases
                if (colorChar == '[')
                {
                    // Named color: find closing bracket
                    int end = text.IndexOf(']', i);
                    if (end != -1)
                    {
                        string colorName = text.Substring(i + 1, end - i - 1);
                        // Try to use the color name directly (hex or named)
                        if (colorName.StartsWith('#') || IsKnownColorName(colorName))
                        {
                            currentColor = colorName.StartsWith('#') ? colorName : NameToHex(colorName);
                        }
                        i = end;
                    }
                }
                else if (colorChar == '-')
                {
                    // Reset to default
                    currentColor = defaultColor;
                }
                else if (colorChar == '+')
                {
                    // Previous color (simplified: just use default)
                    currentColor = defaultColor;
                }
                else if (colorChar == '*')
                {
                    // Chat color (green)
                    currentColor = "#32CD32";
                }
                else if (colorChar == '!')
                {
                    // Team color (dark green)
                    currentColor = "#228B22";
                }
                else if (colorChar >= 'a' && colorChar <= 'v')
                {
                    int colorIndex = colorChar - 'a';
                    if (colorIndex >= 0 && colorIndex < ColorChart.Length)
                    {
                        currentColor = ColorChart[colorIndex];
                    }
                }
                // Skip unknown color codes
                continue;
            }
            
            // Skip non-printable characters
            if (c < 32 || c > 126)
                continue;
            
            currentText.Append(c);
        }
        
        // Add remaining text
        if (currentText.Length > 0)
        {
            segments.Add(new ColoredTextSegment { Text = currentText.ToString(), ColorHex = currentColor });
        }
        
        return segments;
    }
    
    private static bool IsKnownColorName(string name)
    {
        // Basic color names
        return name.ToLower() switch
        {
            "red" or "blue" or "green" or "yellow" or "orange" or "purple" or 
            "cyan" or "white" or "black" or "gray" or "grey" or "pink" or
            "brown" or "gold" or "silver" => true,
            _ => false
        };
    }
    
    private static string NameToHex(string name)
    {
        return name.ToLower() switch
        {
            "red" => "#FF0000",
            "blue" => "#0000FF",
            "green" => "#00FF00",
            "yellow" => "#FFFF00",
            "orange" => "#FFA500",
            "purple" => "#800080",
            "cyan" => "#00FFFF",
            "white" => "#FFFFFF",
            "black" => "#000000",
            "gray" or "grey" => "#808080",
            "pink" => "#FFC0CB",
            "brown" => "#A52A2A",
            "gold" => "#FFD700",
            "silver" => "#C0C0C0",
            _ => "#FFFFFF"
        };
    }

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
