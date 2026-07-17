namespace Celly.Common;

/// <summary>A source position: 1-based line, 0-based column (CEL convention).</summary>
public readonly record struct Location(int Line, int Column)
{
    public static readonly Location None = new(-1, -1);

    public override string ToString() => $"{Line}:{Column + 1}";
}
