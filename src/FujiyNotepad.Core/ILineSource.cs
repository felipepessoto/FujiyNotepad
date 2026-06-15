namespace FujiyNotepad.Core
{
    /// <summary>
    /// Read access to decoded file lines by index, for line-oriented features such as regex Find.
    /// <see cref="LineProvider"/> is the production implementation; tests can supply a simple fake.
    /// </summary>
    public interface ILineSource
    {
        /// <summary>Number of lines currently available (only indexed lines while indexing is in progress).</summary>
        int LineCount { get; }

        /// <summary>Decoded text of line <paramref name="lineIndex"/> (0-based), without its terminator.</summary>
        string GetLine(int lineIndex);
    }
}
