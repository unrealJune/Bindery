using System.Text;
using Microsoft.AspNetCore.Html;

namespace Bindery.Host.Ui;

/// <summary>
/// Renders a word as an electromechanical flip-dot board, server-side.
/// </summary>
/// <remarks>
/// This is the one loud element of the design system, and it is built here rather than in
/// JavaScript for a specific reason: the Content-Security-Policy has no <c>unsafe-inline</c>
/// (see <see cref="Security.SecurityHeaders"/>), so a dot cannot carry its colour or its
/// animation delay in a <c>style</c> attribute. Every per-dot value is therefore quantised
/// into a class name that <c>bindery.css</c> already defines: <c>g0</c>–<c>g6</c> for the
/// ocean gradient ramp and <c>s0</c>–<c>s23</c> for the diagonal reveal sweep.
///
/// The 5x7 glyph table and the diagonal sweep are transcribed from the Shoreline design
/// system's reference renderer so the board matches the system it comes from rather than
/// approximating it.
///
/// It lives in the host rather than the F# core because its output is HTML: this is
/// presentation plumbing, not a domain type.
/// </remarks>
public static class FlipDot
{
    private const int GlyphWidth = 5;
    private const int GlyphHeight = 7;

    /// <summary>Columns of dead space between glyphs.</summary>
    private const int Gap = 1;

    /// <summary>Gradient buckets, matching the <c>--dot-*</c> ramp in the stylesheet.</summary>
    private const int Ramp = 7;

    /// <summary>Quantised sweep steps, matching the <c>.s0</c>–<c>.s23</c> delay classes.</summary>
    private const int SweepSteps = 24;

    /// <summary>
    /// A board wider than this stops being a status word and starts being a paragraph. Longer
    /// input is truncated rather than allowed to overflow its container on a phone.
    /// </summary>
    private const int MaxGlyphs = 12;

    private static readonly Dictionary<char, byte[]> Font = new()
    {
        ['A'] = [0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001],
        ['B'] = [0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110],
        ['C'] = [0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110],
        ['D'] = [0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110],
        ['E'] = [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111],
        ['F'] = [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000],
        ['G'] = [0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01110],
        ['H'] = [0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001],
        ['I'] = [0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        ['J'] = [0b00111, 0b00010, 0b00010, 0b00010, 0b00010, 0b10010, 0b01100],
        ['K'] = [0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001],
        ['L'] = [0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111],
        ['M'] = [0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001],
        ['N'] = [0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001],
        ['O'] = [0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
        ['P'] = [0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000],
        ['Q'] = [0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101],
        ['R'] = [0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001],
        ['S'] = [0b01110, 0b10001, 0b10000, 0b01110, 0b00001, 0b10001, 0b01110],
        ['T'] = [0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100],
        ['U'] = [0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
        ['V'] = [0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100],
        ['W'] = [0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b10101, 0b01010],
        ['X'] = [0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001],
        ['Y'] = [0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100],
        ['Z'] = [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111],
        ['0'] = [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
        ['1'] = [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        ['2'] = [0b01110, 0b10001, 0b00001, 0b00110, 0b01000, 0b10000, 0b11111],
        ['3'] = [0b01110, 0b10001, 0b00001, 0b00110, 0b00001, 0b10001, 0b01110],
        ['4'] = [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
        ['5'] = [0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110],
        ['6'] = [0b01110, 0b10000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
        ['7'] = [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
        ['8'] = [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
        ['9'] = [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00001, 0b01110],
        ['!'] = [0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00000, 0b00100],
        ['?'] = [0b01110, 0b10001, 0b00001, 0b00110, 0b00100, 0b00000, 0b00100],
        ['.'] = [0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00100],
        [','] = [0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00100, 0b01000],
        ['-'] = [0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000],
        ['&'] = [0b01100, 0b10010, 0b10100, 0b01000, 0b10101, 0b10010, 0b01101],
        ['/'] = [0b00001, 0b00010, 0b00010, 0b00100, 0b01000, 0b01000, 0b10000],
        [':'] = [0b00000, 0b00100, 0b00100, 0b00000, 0b00100, 0b00100, 0b00000],
        ['+'] = [0b00000, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0b00000],
        [' '] = [0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000],
    };

    /// <summary>
    /// A board carrying <paramref name="word"/>.
    /// </summary>
    /// <param name="word">The status word. Uppercased; unsupported characters become spaces.</param>
    /// <param name="label">
    /// What a screen reader should hear. The board itself is a picture of a word, so it is
    /// announced once here and every dot is hidden.
    /// </param>
    /// <param name="modifier">An extra class on the board, e.g. <c>flipdot-lg</c>.</param>
    public static IHtmlContent Board(string word, string? label = null, string? modifier = null)
    {
        var glyphs = Normalise(word);
        var builder = new StringBuilder();

        builder.Append("<div class=\"flipdot");
        if (!string.IsNullOrEmpty(modifier))
        {
            builder.Append(' ').Append(Encode(modifier));
        }

        builder.Append("\" role=\"img\" aria-label=\"")
               .Append(Encode(label ?? word))
               .Append("\">");

        // A board with no glyphs would emit seven empty rows and collapse to a hairline; an
        // empty board is better expressed as no board at all.
        if (glyphs.Count == 0)
        {
            return new HtmlString(builder.Append("</div>").ToString());
        }

        var totalColumns = (glyphs.Count * GlyphWidth) + ((glyphs.Count - 1) * Gap);

        for (var row = 0; row < GlyphHeight; row++)
        {
            builder.Append("<div class=\"flipdot-row\">");

            var column = 0;
            for (var index = 0; index < glyphs.Count; index++)
            {
                var bits = glyphs[index][row];

                for (var bit = 0; bit < GlyphWidth; bit++)
                {
                    var lit = (bits >> (GlyphWidth - 1 - bit)) & 1;
                    AppendDot(builder, lit == 1, column, row, totalColumns);
                    column++;
                }

                if (index < glyphs.Count - 1)
                {
                    for (var space = 0; space < Gap; space++)
                    {
                        AppendDot(builder, false, column, row, totalColumns);
                        column++;
                    }
                }
            }

            builder.Append("</div>");
        }

        return new HtmlString(builder.Append("</div>").ToString());
    }

    private static void AppendDot(StringBuilder builder, bool lit, int column, int row, int totalColumns)
    {
        builder.Append("<i class=\"fd");

        if (lit)
        {
            builder.Append(" on g").Append(GradientBucket(column, row, totalColumns));
        }

        // Unlit dots sweep too: the board is a physical object whose every dot flips, and a
        // sweep that only touched lit dots would read as text fading in rather than as a board.
        builder.Append(" s").Append(SweepStep(column, row, totalColumns)).Append("\"></i>");
    }

    /// <summary>
    /// The ocean ramp sampled left to right with a slight vertical skew, so a board shimmers
    /// diagonally the way the source renderer's gradient does.
    /// </summary>
    /// <remarks>
    /// The reference implementation jitters this per dot with <c>Math.random</c>. That cannot
    /// be reproduced here: the same board has to render identically on every request, or an
    /// htmx poll would reshuffle every colour two seconds apart. The vertical skew supplies
    /// the diagonal banding the jitter was decorating.
    /// </remarks>
    private static int GradientBucket(int column, int row, int totalColumns)
    {
        var t = totalColumns > 1 ? (double)column / (totalColumns - 1) : 0d;
        t += ((row / (double)GlyphHeight) - 0.5d) * 0.15d;
        t = ((t % 1d) + 1d) % 1d;

        return Math.Clamp((int)(t * Ramp), 0, Ramp - 1);
    }

    /// <summary>
    /// The diagonal reveal: dots open in a wave running from the top-left corner, quantised
    /// into the fixed set of delay classes the stylesheet defines.
    /// </summary>
    private static int SweepStep(int column, int row, int totalColumns)
    {
        var span = totalColumns + GlyphHeight - 2;
        if (span <= 0)
        {
            return 0;
        }

        var progress = (column + row) / (double)span;

        return Math.Clamp((int)(progress * (SweepSteps - 1)), 0, SweepSteps - 1);
    }

    private static List<byte[]> Normalise(string word)
    {
        var glyphs = new List<byte[]>();

        foreach (var character in word.ToUpperInvariant())
        {
            if (glyphs.Count == MaxGlyphs)
            {
                break;
            }

            // An unknown character becomes a space rather than being dropped, so a board keeps
            // the word's real rhythm instead of silently closing the gap.
            glyphs.Add(Font.TryGetValue(character, out var glyph) ? glyph : Font[' ']);
        }

        return glyphs;
    }

    private static string Encode(string value) =>
        System.Text.Encodings.Web.HtmlEncoder.Default.Encode(value);
}
