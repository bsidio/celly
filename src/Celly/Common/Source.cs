namespace Celly.Common;

/// <summary>
/// An expression source text with line-offset information for mapping character offsets
/// (UTF-16 code unit indices) to line/column locations.
/// </summary>
public sealed class Source
{
    private readonly int[] _lineOffsets; // offset of the first character of each line

    private Source(string text, string description)
    {
        Text = text;
        Description = description;
        var offsets = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                offsets.Add(i + 1);
            }
        }

        _lineOffsets = [.. offsets];
    }

    /// <summary>The raw expression text.</summary>
    public string Text { get; }

    /// <summary>A human-readable description of the source (e.g. a file name), used in error messages.</summary>
    public string Description { get; }

    public static Source FromText(string text, string description = "<input>") => new(text, description);

    /// <summary>Maps a character offset to a 1-based line / 0-based column location.</summary>
    public Location LocationOf(int offset)
    {
        if (offset < 0)
        {
            return Location.None;
        }

        var line = Array.BinarySearch(_lineOffsets, offset);
        if (line < 0)
        {
            line = ~line - 1;
        }

        return new Location(line + 1, offset - _lineOffsets[line]);
    }

    /// <summary>Returns the text of the given 1-based line, or null when out of range.</summary>
    public string? Snippet(int line)
    {
        if (line < 1 || line > _lineOffsets.Length)
        {
            return null;
        }

        var start = _lineOffsets[line - 1];
        var end = line < _lineOffsets.Length ? _lineOffsets[line] - 1 : Text.Length;
        return Text[start..end];
    }
}
