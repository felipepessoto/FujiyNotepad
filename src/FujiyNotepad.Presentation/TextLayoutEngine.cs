using System.Text;
using FujiyNotepad.Core;

namespace FujiyNotepad.Presentation
{
    /// <summary>A caret/selection position: a 0-based line and a 0-based source character index in it.</summary>
    public readonly struct TextPosition : IComparable<TextPosition>, IEquatable<TextPosition>
    {
        public TextPosition(int line, int column)
        {
            Line = line;
            Column = column;
        }

        public int Line { get; }
        public int Column { get; }

        public int CompareTo(TextPosition other) => Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);
        public bool Equals(TextPosition other) => Line == other.Line && Column == other.Column;
        public override bool Equals(object? obj) => obj is TextPosition p && Equals(p);
        public override int GetHashCode() => HashCode.Combine(Line, Column);
        public static bool operator <(TextPosition a, TextPosition b) => a.CompareTo(b) < 0;
        public static bool operator >(TextPosition a, TextPosition b) => a.CompareTo(b) > 0;
        public static bool operator <=(TextPosition a, TextPosition b) => a.CompareTo(b) <= 0;
        public static bool operator >=(TextPosition a, TextPosition b) => a.CompareTo(b) >= 0;
        public static bool operator ==(TextPosition a, TextPosition b) => a.Equals(b);
        public static bool operator !=(TextPosition a, TextPosition b) => !a.Equals(b);
    }

    /// <summary>A UI-agnostic navigation/selection command fed to <see cref="TextLayoutEngine.HandleKey"/>.</summary>
    public enum NavKey
    {
        Left,
        Right,
        Up,
        Down,
        PageUp,
        PageDown,
        LineStart,
        LineEnd,
        DocumentStart,
        DocumentEnd,
        SelectAll,
        Copy,
    }

    /// <summary>The outcome of feeding a key to <see cref="TextLayoutEngine.HandleKey"/>.</summary>
    public readonly struct KeyResult
    {
        public KeyResult(bool handled, string? copyText)
        {
            Handled = handled;
            CopyText = copyText;
        }

        /// <summary>True if the engine consumed the key (the host should mark the event handled).</summary>
        public bool Handled { get; }

        /// <summary>Non-null when a copy was requested; the host should place this text on the clipboard.</summary>
        public string? CopyText { get; }
    }

    /// <summary>A match-highlight rectangle on a line, in viewport pixels (the line's Y/height are known).</summary>
    public readonly record struct HighlightRect(double X, double Width);

    /// <summary>
    /// A persistent-highlight-rule rectangle: like <see cref="HighlightRect"/> but carrying the rule's packed
    /// 0xAARRGGBB colour, since each rule paints in its own colour (unlike the single-colour Find highlight).
    /// </summary>
    public readonly record struct RuleHighlightRect(double X, double Width, uint Argb);

    /// <summary>
    /// Size of the current selection. <see cref="Lines"/> is 0 when nothing is selected, otherwise the number
    /// of lines it touches. <see cref="Characters"/> is the selected character count, or -1 when the selection
    /// is too large to count cheaply (only the line count is shown then).
    /// </summary>
    public readonly record struct SelectionStats(long Characters, int Lines);

    /// <summary>
    /// One visible line's layout, in viewport pixels, ready for a renderer to paint. The
    /// <see cref="TextLayoutEngine"/> computes these (pure math); the host only issues the corresponding
    /// draw-text / fill-rectangle calls, so the non-trivial "what to draw" logic stays unit-testable.
    /// </summary>
    public readonly struct VisibleLine
    {
        /// <summary>0-based source line index.</summary>
        public int LineIndex { get; init; }

        /// <summary>Top of the line in viewport pixels.</summary>
        public double Y { get; init; }

        /// <summary>The tab-expanded text to draw.</summary>
        public string Display { get; init; }

        /// <summary>X (viewport px) at which the text starts (accounts for the horizontal scroll offset).</summary>
        public double TextX { get; init; }

        /// <summary>True if part of this line is selected.</summary>
        public bool HasSelection { get; init; }

        /// <summary>Left edge of the selection highlight in viewport pixels.</summary>
        public double SelectionX { get; init; }

        /// <summary>Width of the selection highlight in viewport pixels.</summary>
        public double SelectionWidth { get; init; }

        /// <summary>True if the caret sits on this line and should be drawn.</summary>
        public bool HasCaret { get; init; }

        /// <summary>X (viewport px) of the caret.</summary>
        public double CaretX { get; init; }

        /// <summary>
        /// Highlight rectangles for every Find match on this line (empty when highlighting is off). Painted
        /// under the text; the currently-selected match is drawn over its highlight by the selection layer.
        /// </summary>
        public IReadOnlyList<HighlightRect> Matches { get; init; }

        /// <summary>
        /// Persistent highlight-rule rectangles on this line, each in its rule's colour (empty when no rules are
        /// set). Painted under the Find highlight and the text, so a search still reads over a coloured line.
        /// </summary>
        public IReadOnlyList<RuleHighlightRect> RuleHighlights { get; init; }

        /// <summary>True when this line is bookmarked (the gutter paints a marker for it).</summary>
        public bool IsBookmarked { get; init; }

        /// <summary>
        /// Whitespace/control markers to overlay on this line (empty unless "Show Whitespace" is on). Each gives
        /// a display column, width, kind (space/tab/control) and whether it is part of the trailing run.
        /// </summary>
        public IReadOnlyList<WhitespaceMarker> Whitespace { get; init; }

        /// <summary>
        /// In word-wrap mode, true for the 2nd…Nth display rows of a wrapped source line (the gutter shows the
        /// line number only on the first row). Always false in the non-wrap, one-row-per-line model.
        /// </summary>
        public bool IsWrapContinuation { get; init; }
    }

    /// <summary>
    /// The framework-independent model and layout math behind the WinUI text canvas: it tracks the scroll
    /// position (in whole-line units), the caret and character-level selection, performs hit-testing, and
    /// computes the per-line render model (<see cref="GetVisibleLines"/>). It has no Win2D/WinUI/WinRT
    /// dependency, so it unit-tests on a normal .NET test host. Monospace metrics are injected via
    /// <see cref="SetMetrics"/> (the host measures the font with Win2D and pushes the result here). The host
    /// feeds the live viewport size and forwards the engine's <see cref="ViewChanged"/> /
    /// <see cref="CaretChanged"/> / <see cref="RedrawRequested"/> / <see cref="CaretBlinkResetRequested"/>
    /// signals to the UI.
    /// </summary>
    public sealed class TextLayoutEngine
    {
        private const int MaxCopyChars = 25_000_000;

        /// <summary>
        /// Inner horizontal padding (viewport pixels) between the control's left/right border and the text,
        /// so the first character of each line isn't pressed against the edge. It is applied to the rendered
        /// text/caret/selection X, subtracted back out in hit-testing, and added to the horizontal extent so
        /// every coordinate calculation stays consistent.
        /// </summary>
        public const double TextPadding = 8d;

        // Horizontal padding inside the line-number gutter (on each side of the number).
        private const double GutterPaddingX = 6d;

        private int tabSize = 4;

        private double lineHeight;
        private double charWidth;

        private readonly Dictionary<int, LineColumns> columnsCache = new();

        private static readonly IReadOnlyList<HighlightRect> NoHighlights = Array.Empty<HighlightRect>();
        private ILineHighlighter? highlighter;

        private static readonly IReadOnlyList<RuleHighlightRect> NoRuleHighlights = Array.Empty<RuleHighlightRect>();
        private HighlightRuleSet? highlightRules;

        private static readonly IReadOnlyList<WhitespaceMarker> NoWhitespace = Array.Empty<WhitespaceMarker>();

        private readonly BookmarkSet bookmarks = new();

        private bool showLineNumbers = false;
        private bool showWhitespace = false;

        private ILineSource? provider;
        private int totalLines;
        private int firstVisibleLine;
        private double horizontalOffset;
        private double horizontalExtentPx;

        // Word wrap (issue #31). When on, one source line renders as N display rows wrapped to the viewport
        // width, and there is no horizontal scroll. The vertical scrollbar stays in *source-line* units (so the
        // constant-memory "extent = line count" model is preserved); firstVisibleRow is which wrapped row of
        // firstVisibleLine sits at the very top, giving smooth scrolling through a line longer than the viewport.
        private bool wordWrap;
        private int firstVisibleRow;

        private TextPosition caret;
        private TextPosition anchor;
        private int desiredColumn = -1;

        /// <summary>Raised when the scroll position or horizontal extent changes (host re-syncs scrollbars).</summary>
        public event Action? ViewChanged;

        /// <summary>Raised when the caret moves (host updates the status bar).</summary>
        public event Action<TextPosition>? CaretChanged;

        /// <summary>Raised when the surface needs repainting (host invalidates the canvas).</summary>
        public event Action? RedrawRequested;

        /// <summary>Raised when the caret should become solid and restart blinking (host resets its blink timer).</summary>
        public event Action? CaretBlinkResetRequested;

        #region Viewport + metrics (fed by the host)

        /// <summary>The live viewport width in pixels (the host pushes <c>ActualWidth</c> here).</summary>
        public double ViewportWidth { get; set; }

        /// <summary>The live viewport height in pixels (the host pushes <c>ActualHeight</c> here).</summary>
        public double ViewportHeight { get; set; }

        /// <summary>Injects the monospace cell metrics (the host measures the font and pushes them here).</summary>
        public void SetMetrics(double charWidthPx, double lineHeightPx)
        {
            charWidth = charWidthPx;
            lineHeight = lineHeightPx;
        }

        /// <summary>
        /// Tab width in columns (default 4, minimum 1). Changing it re-expands every line, so the cached
        /// column maps are cleared and the surface is refreshed.
        /// </summary>
        public int TabSize
        {
            get => tabSize;
            set
            {
                int clamped = Math.Max(1, value);
                if (clamped == tabSize)
                {
                    return;
                }
                tabSize = clamped;
                columnsCache.Clear();
                RaiseViewChanged();
                RaiseRedraw();
            }
        }

        #endregion

        #region Public surface mirrored by the host

        public int FirstVisibleLine
        {
            get => firstVisibleLine;
            set => SetFirstVisibleLine(value);
        }

        public double HorizontalOffset
        {
            get => horizontalOffset;
            set => SetHorizontalOffset(value);
        }

        public int TotalLines => totalLines;
        public int FullyVisibleLineCount => lineHeight > 0 ? Math.Max(1, (int)(ViewportHeight / lineHeight)) : 1;
        public int MaxFirstLine => wordWrap
            ? Math.Max(0, totalLines - 1)
            : Math.Max(0, totalLines - FullyVisibleLineCount);
        public double CharWidthPx => charWidth;
        public double HorizontalExtentPx => Math.Max(horizontalExtentPx, ViewportWidth);
        public TextPosition CaretPosition => caret;

        /// <summary>
        /// Word wrap (issue #31). Off by default — the whole non-wrap path is unchanged. Turning it on clears
        /// the horizontal scroll, keeps the current source line at the top, and re-lays out.
        /// </summary>
        public bool WordWrap
        {
            get => wordWrap;
            set
            {
                if (wordWrap == value)
                {
                    return;
                }
                wordWrap = value;
                firstVisibleRow = 0;
                horizontalOffset = 0;
                firstVisibleLine = Math.Clamp(firstVisibleLine, 0, MaxFirstLine);
                RaiseViewChanged();
                RaiseRedraw();
            }
        }

        // The wrap budget in display columns: the viewport width minus the gutter, left padding and a right
        // padding, divided by the cell width. At least 1 so a line always makes progress.
        private int WrapColumns
        {
            get
            {
                if (charWidth <= 0)
                {
                    return 1;
                }
                double avail = ViewportWidth - TextOriginX - TextPadding;
                return Math.Max(1, (int)Math.Floor(avail / charWidth));
            }
        }

        private IReadOnlyList<LineWrapper.WrapRow> WrapRowsOf(int lineIndex) =>
            LineWrapper.Wrap(GetColumns(lineIndex), WrapColumns);

        /// <summary>
        /// The raw source text of the line the caret is on, for screen readers (issue #75). Bounded to a single
        /// line so even a multi-gigabyte file stays cheap — assistive tech reads each line as the caret moves
        /// through the file. Returns an empty string when no file is open.
        /// </summary>
        public string GetCaretLineText()
        {
            if (provider == null || totalLines == 0)
            {
                return string.Empty;
            }

            int line = Math.Clamp(caret.Line, 0, totalLines - 1);
            return GetColumns(line).Source;
        }

        /// <summary>
        /// Whether the line-number gutter is drawn. The gutter is a fixed-width column on the left (it does not
        /// scroll horizontally); turning it on/off shifts the text area and re-syncs the view.
        /// </summary>
        public bool ShowLineNumbers
        {
            get => showLineNumbers;
            set
            {
                if (value != showLineNumbers)
                {
                    showLineNumbers = value;
                    RaiseViewChanged();
                    RaiseRedraw();
                }
            }
        }

        /// <summary>
        /// Whether whitespace/control markers (space dots, tab arrows, trailing emphasis, control boxes) are
        /// overlaid on the rendered text. Pure overlay - it doesn't change layout, only requests a redraw.
        /// </summary>
        public bool ShowWhitespace
        {
            get => showWhitespace;
            set
            {
                if (value != showWhitespace)
                {
                    showWhitespace = value;
                    RaiseRedraw();
                }
            }
        }

        /// <summary>
        /// Width in pixels of the line-number gutter (0 when off): wide enough for the largest line number,
        /// with a minimum of two digits so it doesn't jitter for tiny files.
        /// </summary>
        public double GutterWidthPx
        {
            get
            {
                if (!showLineNumbers || totalLines <= 0 || charWidth <= 0)
                {
                    return 0;
                }
                int digits = Math.Max(2, DigitCount(totalLines));
                return digits * charWidth + GutterPaddingX * 2;
            }
        }

        // Screen x of text column 0 before the horizontal scroll offset: the left padding, plus the gutter.
        private double TextOriginX => TextPadding + GutterWidthPx;

        private static int DigitCount(int value)
        {
            int digits = 1;
            while (value >= 10)
            {
                value /= 10;
                digits++;
            }
            return digits;
        }

        /// <summary>True when there is a non-empty selection (the anchor and caret differ).</summary>
        public bool HasSelection => anchor != caret;

        /// <summary>
        /// The start of the current selection in document order (equals the caret when nothing is selected).
        /// After <see cref="SelectMatch"/> this is the start of the matched text, which "Find Previous" uses
        /// as the upper bound so it never re-finds the current match.
        /// </summary>
        public TextPosition SelectionStart => NormalizedSelection().start;

        // A multi-line selection spanning more lines than this isn't character-counted (the count would mean
        // decoding that many lines on the UI thread on every selection change); only its line count is shown.
        private const int MaxSelectionLinesForCharCount = 5000;

        /// <summary>
        /// The size of the current selection (characters and lines) for the status bar. Returns zero lines when
        /// there is no selection. The character count is exact for a single-line or a moderate multi-line
        /// selection; for a very large multi-line selection it is -1 (uncounted) and only the line count applies.
        /// </summary>
        public SelectionStats GetSelectionStats()
        {
            if (provider == null || anchor == caret)
            {
                return new SelectionStats(0, 0);
            }

            (TextPosition start, TextPosition end) = NormalizedSelection();

            if (start.Line == end.Line)
            {
                return new SelectionStats(Math.Max(0, end.Column - start.Column), 1);
            }

            int lines = end.Line - start.Line + 1;
            if (lines > MaxSelectionLinesForCharCount)
            {
                return new SelectionStats(-1, lines);
            }

            long characters = 0;
            for (int line = start.Line; line <= end.Line; line++)
            {
                int lineLength = GetColumns(line).Source.Length;
                if (line == start.Line)
                {
                    characters += Math.Max(0, lineLength - start.Column);
                }
                else if (line == end.Line)
                {
                    characters += Math.Min(end.Column, lineLength);
                }
                else
                {
                    characters += lineLength;
                }

                if (line < end.Line)
                {
                    characters += 1; // the line break between the two lines
                }
            }

            return new SelectionStats(characters, lines);
        }

        /// <summary>
        /// The time difference between the leading timestamps of the first and last selected lines, or
        /// <c>null</c> when there is no multi-line selection or either endpoint line does not begin with a
        /// recognized timestamp. Drives the status bar's log-duration readout (issue #67). Reads only the two
        /// endpoint lines, so it stays cheap even for a large selection.
        /// </summary>
        public TimeSpan? GetSelectionTimestampDelta()
        {
            if (provider == null || anchor == caret)
            {
                return null;
            }

            (TextPosition start, TextPosition end) = NormalizedSelection();
            if (start.Line == end.Line)
            {
                return null;
            }

            if (TimestampParser.TryParseLeading(GetColumns(start.Line).Source, out DateTimeOffset first) &&
                TimestampParser.TryParseLeading(GetColumns(end.Line).Source, out DateTimeOffset last))
            {
                return last - first;
            }

            return null;
        }

        /// <summary>
        /// Sets (or clears, with <c>null</c>) the highlighter used to paint every Find match in the viewport,
        /// and requests a redraw. Each visible line's <see cref="VisibleLine.Matches"/> is then computed from
        /// this highlighter on the next <see cref="GetVisibleLines"/>.
        /// </summary>
        public void SetHighlighter(ILineHighlighter? value)
        {
            highlighter = value;
            RaiseRedraw();
        }

        /// <summary>
        /// Sets (or clears, with <c>null</c>) the persistent highlight rules and requests a redraw. These are a
        /// separate channel from <see cref="SetHighlighter"/> (Find), so they survive opening/closing the Find
        /// bar and apply across every open file. Each visible line's <see cref="VisibleLine.RuleHighlights"/> is
        /// recomputed from this rule set on the next <see cref="GetVisibleLines"/>.
        /// </summary>
        public void SetHighlightRules(HighlightRuleSet? value)
        {
            highlightRules = value is { Count: > 0 } ? value : null;
            RaiseRedraw();
        }

        public void SetProvider(ILineSource? newProvider)
        {
            provider = newProvider;
            totalLines = newProvider?.LineCount ?? 0;
            firstVisibleLine = 0;
            firstVisibleRow = 0;
            horizontalOffset = 0;
            caret = anchor = new TextPosition(0, 0);
            desiredColumn = -1;
            columnsCache.Clear();
            bookmarks.Clear(); // bookmarks are line indices into the previous file
            RaiseViewChanged();
            RaiseCaretChanged();
            RaiseRedraw();
        }

        public void UpdateTotalLines(int count)
        {
            if (count == totalLines)
            {
                return;
            }

            totalLines = count;
            if (firstVisibleLine > MaxFirstLine)
            {
                firstVisibleLine = MaxFirstLine;
            }
            RaiseViewChanged();
            RaiseRedraw();
        }

        public void GoToLine(int lineIndex)
        {
            if (provider == null || totalLines == 0)
            {
                return;
            }

            lineIndex = Math.Clamp(lineIndex, 0, totalLines - 1);
            caret = anchor = new TextPosition(lineIndex, 0);
            desiredColumn = -1;
            SetFirstVisibleLine(Math.Min(lineIndex, MaxFirstLine));
            RaiseBlinkReset();
            RaiseCaretChanged();
            RaiseRedraw();
        }

        // ----- Bookmarks (toggle on the caret line; jump next/previous with wrap-around) -----

        /// <summary>Whether any line is bookmarked (lets the host enable/disable navigation commands).</summary>
        public bool HasBookmarks => bookmarks.Count > 0;

        /// <summary>The bookmarked line indices in ascending order (e.g. for the scrollbar marker margin).</summary>
        public IReadOnlyCollection<int> BookmarkLines => bookmarks.Lines;

        /// <summary>Toggles the bookmark on the caret's line and redraws; returns true if it is now bookmarked.</summary>
        public bool ToggleBookmarkAtCaret()
        {
            bool added = bookmarks.Toggle(caret.Line);
            RaiseRedraw();
            return added;
        }

        /// <summary>Moves the caret to the next bookmarked line (wrapping); no-op when there are no bookmarks.</summary>
        public void GoToNextBookmark()
        {
            int? next = bookmarks.NextAfter(caret.Line);
            if (next.HasValue)
            {
                GoToLine(next.Value);
            }
        }

        /// <summary>Moves the caret to the previous bookmarked line (wrapping); no-op when there are none.</summary>
        public void GoToPreviousBookmark()
        {
            int? prev = bookmarks.PreviousBefore(caret.Line);
            if (prev.HasValue)
            {
                GoToLine(prev.Value);
            }
        }

        /// <summary>Removes every bookmark and redraws.</summary>
        public void ClearBookmarks()
        {
            if (bookmarks.Count > 0)
            {
                bookmarks.Clear();
                RaiseRedraw();
            }
        }

        /// <summary>
        /// Moves the caret to <paramref name="column"/> on line <paramref name="lineIndex"/> (each clamped to
        /// the document / the line length), clears any selection, scrolls the line to the top and nudges the
        /// caret horizontally into view. Used by <c>Go To Offset</c> to land on the exact character at a byte
        /// position; <see cref="GoToLine"/> is the column-0 special case.
        /// </summary>
        public void GoToLineColumn(int lineIndex, int column)
        {
            if (provider == null || totalLines == 0)
            {
                return;
            }

            lineIndex = Math.Clamp(lineIndex, 0, totalLines - 1);
            int lineLen = GetColumns(lineIndex).Source.Length;
            int col = Math.Clamp(column, 0, lineLen);
            caret = anchor = new TextPosition(lineIndex, col);
            desiredColumn = -1;
            SetFirstVisibleLine(Math.Min(lineIndex, MaxFirstLine));
            EnsureCaretVisibleHorizontally();
            RaiseBlinkReset();
            RaiseCaretChanged();
            RaiseRedraw();
        }

        public void SelectMatch(int lineIndex, int startColumn, int length)
        {
            if (provider == null || totalLines == 0)
            {
                return;
            }

            lineIndex = Math.Clamp(lineIndex, 0, totalLines - 1);
            int lineLen = GetColumns(lineIndex).Source.Length;
            int start = Math.Clamp(startColumn, 0, lineLen);
            int end = Math.Clamp(startColumn + length, start, lineLen);
            anchor = new TextPosition(lineIndex, start);
            caret = new TextPosition(lineIndex, end);
            desiredColumn = -1;
            if (wordWrap)
            {
                // Make the match's wrapped row visible (the match may be deep in a long, wrapped line).
                EnsureCaretVisibleVerticallyWrapped();
            }
            else
            {
                // Only scroll when the match line is off-screen; if it is already visible, keep the scroll
                // position and just highlight (the horizontal nudge below is a no-op when it is fully visible).
                if (lineIndex < firstVisibleLine || lineIndex >= firstVisibleLine + FullyVisibleLineCount)
                {
                    SetFirstVisibleLine(Math.Min(lineIndex, MaxFirstLine));
                }
                EnsureCaretVisibleHorizontally();
            }
            RaiseBlinkReset();
            RaiseCaretChanged();
            RaiseRedraw();
        }

        #endregion

        #region Scrolling

        private void SetFirstVisibleLine(int value)
        {
            value = Math.Clamp(value, 0, MaxFirstLine);
            if (value == firstVisibleLine && firstVisibleRow == 0)
            {
                return;
            }
            firstVisibleLine = value;
            firstVisibleRow = 0; // a source-line scroll (scrollbar/Go To) lands at the top of that line
            RaiseViewChanged();
            RaiseRedraw();
        }

        private void SetHorizontalOffset(double value)
        {
            double max = Math.Max(0, HorizontalExtentPx - ViewportWidth);
            value = Math.Clamp(value, 0, max);
            if (Math.Abs(value - horizontalOffset) < 0.01)
            {
                return;
            }
            horizontalOffset = value;
            RaiseViewChanged();
            RaiseRedraw();
        }

        /// <summary>Scrolls in response to a mouse-wheel notch (positive delta scrolls up).</summary>
        public void ScrollByWheelDelta(int delta)
        {
            int linesPerNotch = 3;
            int lines = -(delta / 120) * linesPerNotch;
            if (wordWrap)
            {
                ScrollDisplayRows(lines);
            }
            else
            {
                SetFirstVisibleLine(firstVisibleLine + lines);
            }
        }

        #endregion

        #region Pointer

        /// <summary>Places the caret (and, unless extending a selection, the anchor) at a pressed point.</summary>
        public void PointerPress(double x, double y, bool shift)
        {
            TextPosition pos = HitTest(x, y);
            if (!shift)
            {
                anchor = pos;
            }
            caret = pos;
            desiredColumn = -1;
            RaiseBlinkReset();
            RaiseCaretChanged();
            RaiseRedraw();
        }

        /// <summary>Selects the whole word (or run of same-class characters) under a double-clicked point.</summary>
        public void SelectWordAt(double x, double y)
        {
            if (provider == null || totalLines == 0 || lineHeight <= 0)
            {
                return;
            }

            TextPosition pos = HitTest(x, y);
            string line = GetColumns(pos.Line).Source;
            (int start, int end) = WordBoundaries(line, pos.Column);
            anchor = new TextPosition(pos.Line, start);
            caret = new TextPosition(pos.Line, end);
            desiredColumn = -1;
            RaiseBlinkReset();
            RaiseCaretChanged();
            RaiseRedraw();
        }

        private enum CharClass { Word, Whitespace, Other }

        private static CharClass ClassOf(char c)
        {
            if (char.IsWhiteSpace(c)) return CharClass.Whitespace;
            if (char.IsLetterOrDigit(c) || c == '_') return CharClass.Word;
            return CharClass.Other;
        }

        // Expands from a character index to the surrounding run of same-class characters (word, whitespace,
        // or other), giving familiar double-click word selection. Returns [start, end) source-char columns.
        internal static (int start, int end) WordBoundaries(string line, int index)
        {
            if (line.Length == 0)
            {
                return (0, 0);
            }
            index = Math.Clamp(index, 0, line.Length - 1);

            CharClass cls = ClassOf(line[index]);
            int start = index;
            while (start > 0 && ClassOf(line[start - 1]) == cls)
            {
                start--;
            }
            int end = index + 1;
            while (end < line.Length && ClassOf(line[end]) == cls)
            {
                end++;
            }
            return (start, end);
        }
        /// <summary>Extends the selection to a dragged point, auto-scrolling past the top/bottom edges.</summary>
        public void PointerDrag(double x, double y)
        {
            // Auto-scroll when dragging past the top/bottom edge.
            if (y < 0)
            {
                if (wordWrap) { ScrollDisplayRows(-1); } else { SetFirstVisibleLine(firstVisibleLine - 1); }
            }
            else if (y > ViewportHeight)
            {
                if (wordWrap) { ScrollDisplayRows(1); } else { SetFirstVisibleLine(firstVisibleLine + 1); }
            }

            caret = HitTest(x, y);
            RaiseCaretChanged();
            RaiseRedraw();
        }

        /// <summary>Maps a viewport point to the nearest caret position. Metrics must be set first.</summary>
        public TextPosition HitTest(double x, double y)
        {
            if (provider == null || totalLines == 0 || lineHeight <= 0)
            {
                return new TextPosition(0, 0);
            }

            if (wordWrap)
            {
                return HitTestWrapped(x, y);
            }

            int line = firstVisibleLine + (int)Math.Floor(y / lineHeight);
            line = Math.Clamp(line, 0, totalLines - 1);

            LineColumns columns = GetColumns(line);
            double column = (x - TextOriginX + horizontalOffset) / charWidth;
            int charIndex = columns.CharIndexOfColumn(column);
            return new TextPosition(line, charIndex);
        }

        #endregion

        #region Keyboard

        /// <summary>
        /// Applies a navigation/selection/copy command. Returns whether it was consumed and, for copy, the
        /// text the host should place on the clipboard.
        /// </summary>
        public KeyResult HandleKey(NavKey key, bool shift)
        {
            if (provider == null || totalLines == 0)
            {
                return new KeyResult(false, null);
            }

            string? copyText = null;

            switch (key)
            {
                case NavKey.Left:
                    MoveCaretByChar(-1, shift);
                    break;
                case NavKey.Right:
                    MoveCaretByChar(+1, shift);
                    break;
                case NavKey.Up:
                    if (wordWrap) { MoveCaretByDisplayRow(-1, shift); } else { MoveCaretByLine(-1, shift); }
                    break;
                case NavKey.Down:
                    if (wordWrap) { MoveCaretByDisplayRow(+1, shift); } else { MoveCaretByLine(+1, shift); }
                    break;
                case NavKey.PageUp:
                    if (wordWrap) { MoveCaretByDisplayRow(-VisibleRowCount, shift); } else { MoveCaretByLine(-FullyVisibleLineCount, shift); }
                    break;
                case NavKey.PageDown:
                    if (wordWrap) { MoveCaretByDisplayRow(+VisibleRowCount, shift); } else { MoveCaretByLine(+FullyVisibleLineCount, shift); }
                    break;
                case NavKey.LineStart:
                    MoveCaretTo(new TextPosition(caret.Line, 0), shift);
                    break;
                case NavKey.LineEnd:
                    MoveCaretTo(new TextPosition(caret.Line, LineLength(caret.Line)), shift);
                    break;
                case NavKey.DocumentStart:
                    MoveCaretTo(new TextPosition(0, 0), shift);
                    break;
                case NavKey.DocumentEnd:
                    int last = totalLines - 1;
                    MoveCaretTo(new TextPosition(last, LineLength(last)), shift);
                    break;
                case NavKey.SelectAll:
                    SelectAll();
                    break;
                case NavKey.Copy:
                    copyText = BuildCopyText();
                    break;
                default:
                    return new KeyResult(false, null);
            }

            return new KeyResult(true, copyText);
        }

        private int LineLength(int line) => GetColumns(line).Source.Length;

        private void MoveCaretByChar(int delta, bool shift)
        {
            int line = caret.Line;
            int col = caret.Column + delta;

            if (col < 0)
            {
                if (line > 0)
                {
                    line--;
                    col = LineLength(line);
                }
                else
                {
                    col = 0;
                }
            }
            else if (col > LineLength(line))
            {
                if (line < totalLines - 1)
                {
                    line++;
                    col = 0;
                }
                else
                {
                    col = LineLength(line);
                }
            }

            desiredColumn = -1;
            ApplyCaret(new TextPosition(line, col), shift);
        }

        private void MoveCaretByLine(int delta, bool shift)
        {
            if (desiredColumn < 0)
            {
                desiredColumn = caret.Column;
            }
            int line = Math.Clamp(caret.Line + delta, 0, totalLines - 1);
            int col = Math.Min(desiredColumn, LineLength(line));
            ApplyCaret(new TextPosition(line, col), shift, keepDesired: true);
        }

        private void MoveCaretTo(TextPosition pos, bool shift)
        {
            desiredColumn = -1;
            int line = Math.Clamp(pos.Line, 0, totalLines - 1);
            ApplyCaret(new TextPosition(line, Math.Clamp(pos.Column, 0, LineLength(line))), shift);
        }

        private void ApplyCaret(TextPosition pos, bool shift, bool keepDesired = false)
        {
            caret = pos;
            if (!shift)
            {
                anchor = caret;
            }
            if (!keepDesired)
            {
                desiredColumn = -1;
            }
            if (wordWrap)
            {
                EnsureCaretVisibleVerticallyWrapped();
            }
            else
            {
                EnsureCaretVisibleVertically();
                EnsureCaretVisibleHorizontally();
            }
            RaiseBlinkReset();
            RaiseCaretChanged();
            RaiseRedraw();
        }

        // ----- Word-wrap navigation/scrolling (issue #31). Only reached when WordWrap is on. -----

        private int VisibleRowCount => lineHeight > 0 ? Math.Max(1, (int)(ViewportHeight / lineHeight)) : 1;

        private static int ComparePos(int line1, int row1, int line2, int row2)
            => line1 != line2 ? line1.CompareTo(line2) : row1.CompareTo(row2);

        // Advances a (source line, wrapped row) position by <paramref name="delta"/> display rows (negative =
        // up), clamping at the document's first/last row.
        private (int line, int row) AdvanceDisplayRows(int line, int row, int delta)
        {
            int wrapCols = WrapColumns;
            while (delta > 0)
            {
                int rowsInLine = LineWrapper.RowCount(GetColumns(line), wrapCols);
                if (row + 1 < rowsInLine)
                {
                    row++;
                }
                else if (line + 1 < totalLines)
                {
                    line++;
                    row = 0;
                }
                else
                {
                    break;
                }
                delta--;
            }
            while (delta < 0)
            {
                if (row > 0)
                {
                    row--;
                }
                else if (line > 0)
                {
                    line--;
                    row = LineWrapper.RowCount(GetColumns(line), wrapCols) - 1;
                }
                else
                {
                    break;
                }
                delta++;
            }
            return (line, row);
        }

        private void SetTopPosition(int line, int row)
        {
            line = Math.Clamp(line, 0, Math.Max(0, totalLines - 1));
            row = Math.Max(0, row);
            if (line == firstVisibleLine && row == firstVisibleRow)
            {
                return;
            }
            firstVisibleLine = line;
            firstVisibleRow = row;
            RaiseViewChanged();
            RaiseRedraw();
        }

        private void ScrollDisplayRows(int delta)
        {
            (int line, int row) = AdvanceDisplayRows(firstVisibleLine, firstVisibleRow, delta);
            SetTopPosition(line, row);
        }

        // Moves the caret up/down by display rows, preserving the visual column within the row (desiredColumn is
        // the row-local display column here).
        private void MoveCaretByDisplayRow(int delta, bool shift)
        {
            int wrapCols = WrapColumns;
            LineColumns curCols = GetColumns(caret.Line);
            IReadOnlyList<LineWrapper.WrapRow> curRows = LineWrapper.Wrap(curCols, wrapCols);
            int curRow = LineWrapper.RowOfCharIndex(curRows, caret.Column);
            int caretDisplayCol = curCols.ColumnOfCharIndex(caret.Column);

            if (desiredColumn < 0)
            {
                desiredColumn = caretDisplayCol - curRows[curRow].StartColumn;
            }
            int localCol = desiredColumn;

            (int line, int row) = AdvanceDisplayRows(caret.Line, curRow, delta);
            LineColumns cols = GetColumns(line);
            IReadOnlyList<LineWrapper.WrapRow> rows = LineWrapper.Wrap(cols, wrapCols);
            row = Math.Clamp(row, 0, rows.Count - 1);
            int targetDisplayCol = rows[row].StartColumn + localCol;
            int charIndex = Math.Clamp(cols.CharIndexOfColumn(targetDisplayCol), rows[row].StartChar, rows[row].EndChar);
            ApplyCaret(new TextPosition(line, charIndex), shift, keepDesired: true);
        }

        private void EnsureCaretVisibleVerticallyWrapped()
        {
            int wrapCols = WrapColumns;
            int caretRow = LineWrapper.RowOfCharIndex(LineWrapper.Wrap(GetColumns(caret.Line), wrapCols), caret.Column);

            if (ComparePos(caret.Line, caretRow, firstVisibleLine, firstVisibleRow) < 0)
            {
                SetTopPosition(caret.Line, caretRow);
                return;
            }

            int visible = VisibleRowCount;
            (int lastLine, int lastRow) = AdvanceDisplayRows(firstVisibleLine, firstVisibleRow, visible - 1);
            if (ComparePos(caret.Line, caretRow, lastLine, lastRow) > 0)
            {
                (int topLine, int topRow) = AdvanceDisplayRows(caret.Line, caretRow, -(visible - 1));
                SetTopPosition(topLine, topRow);
            }
        }

        private TextPosition HitTestWrapped(double x, double y)
        {
            int wrapCols = WrapColumns;
            int rowsDown = Math.Max(0, (int)Math.Floor(y / lineHeight));
            (int line, int row) = AdvanceDisplayRows(firstVisibleLine, firstVisibleRow, rowsDown);
            LineColumns columns = GetColumns(line);
            IReadOnlyList<LineWrapper.WrapRow> rows = LineWrapper.Wrap(columns, wrapCols);
            row = Math.Clamp(row, 0, rows.Count - 1);
            double column = (x - TextOriginX) / charWidth + rows[row].StartColumn;
            int charIndex = Math.Clamp(columns.CharIndexOfColumn(column), rows[row].StartChar, rows[row].EndChar);
            return new TextPosition(line, charIndex);
        }

        private void SelectAll()
        {
            if (totalLines == 0)
            {
                return;
            }
            anchor = new TextPosition(0, 0);
            int last = totalLines - 1;
            caret = new TextPosition(last, LineLength(last));
            desiredColumn = -1;
            RaiseRedraw();
            RaiseCaretChanged(); // refresh the status bar (selection stats + timestamp delta)
        }

        private void EnsureCaretVisibleVertically()
        {
            if (caret.Line < firstVisibleLine)
            {
                SetFirstVisibleLine(caret.Line);
            }
            else if (caret.Line >= firstVisibleLine + FullyVisibleLineCount)
            {
                SetFirstVisibleLine(caret.Line - FullyVisibleLineCount + 1);
            }
        }

        private void EnsureCaretVisibleHorizontally()
        {
            if (charWidth <= 0)
            {
                return;
            }
            double caretX = GetColumns(caret.Line).ColumnOfCharIndex(caret.Column) * charWidth;
            // Keep a padding-sized gap on whichever edge the caret is scrolled to, matching the rendered inset.
            // The visible text width is the viewport minus the fixed gutter.
            double rightInset = charWidth + TextPadding * 2;
            double textViewport = ViewportWidth - GutterWidthPx;
            if (caretX < horizontalOffset)
            {
                SetHorizontalOffset(caretX);
            }
            else if (caretX > horizontalOffset + textViewport - rightInset)
            {
                SetHorizontalOffset(caretX - textViewport + rightInset);
            }
        }

        #endregion

        #region Copy / selection

        /// <summary>
        /// Builds the text for the current selection (CRLF between lines), or <c>null</c> when nothing is
        /// selected. The host is responsible for placing the result on the clipboard.
        /// </summary>
        public string? BuildCopyText()
        {
            (TextPosition start, TextPosition end) = NormalizedSelection();
            if (start == end)
            {
                return null;
            }

            var sb = new StringBuilder();
            if (start.Line == end.Line)
            {
                string line = GetColumns(start.Line).Source;
                sb.Append(Slice(line, start.Column, end.Column));
            }
            else
            {
                string firstLine = GetColumns(start.Line).Source;
                sb.Append(Slice(firstLine, start.Column, firstLine.Length)).Append("\r\n");

                for (int l = start.Line + 1; l < end.Line && sb.Length < MaxCopyChars; l++)
                {
                    sb.Append(GetColumns(l).Source).Append("\r\n");
                }

                if (sb.Length < MaxCopyChars)
                {
                    string lastLine = GetColumns(end.Line).Source;
                    sb.Append(Slice(lastLine, 0, end.Column));
                }
            }

            return sb.ToString();
        }

        private static string Slice(string s, int start, int end)
        {
            start = Math.Clamp(start, 0, s.Length);
            end = Math.Clamp(end, start, s.Length);
            return s.Substring(start, end - start);
        }

        /// <summary>
        /// Builds the "Copy with Line Numbers" text: every full source line the selection spans (or just the
        /// caret's line when there is no selection), each prefixed with its 1-based line number. Returns null
        /// when no file is open. A selection that ends exactly at the start of a line does not include that line.
        /// </summary>
        public string? BuildCopyTextWithLineNumbers()
        {
            if (provider == null || totalLines == 0)
            {
                return null;
            }

            (TextPosition start, TextPosition end) = NormalizedSelection();
            int firstLine = start.Line;
            int lastLine = start == end
                ? caret.Line
                : (end.Column == 0 && end.Line > start.Line ? end.Line - 1 : end.Line);
            lastLine = Math.Min(lastLine, totalLines - 1);

            var lines = new List<string>(lastLine - firstLine + 1);
            for (int l = firstLine; l <= lastLine; l++)
            {
                lines.Add(GetColumns(l).Source);
            }
            return LineNumberedCopy.Format(firstLine + 1, lines);
        }

        internal (TextPosition start, TextPosition end) NormalizedSelection()
            => anchor <= caret ? (anchor, caret) : (caret, anchor);

        #endregion

        #region Render model

        /// <summary>
        /// Computes the per-line render model for the current viewport (selection spans, caret, text
        /// position), and refreshes the horizontal extent. The host paints each <see cref="VisibleLine"/>
        /// with its graphics API. Metrics must be set first via <see cref="SetMetrics"/>.
        /// </summary>
        public IReadOnlyList<VisibleLine> GetVisibleLines(bool hasFocus, bool caretVisible)
        {
            var lines = new List<VisibleLine>();
            if (provider == null || totalLines == 0 || lineHeight <= 0)
            {
                horizontalExtentPx = ViewportWidth;
                return lines;
            }

            if (wordWrap)
            {
                return GetVisibleLinesWrapped(hasFocus, caretVisible);
            }

            int renderCount = (int)Math.Ceiling(ViewportHeight / lineHeight) + 1;
            (TextPosition selStart, TextPosition selEnd) = NormalizedSelection();
            bool hasSelection = selStart != selEnd;

            double maxWidth = 0;
            for (int i = 0; i < renderCount; i++)
            {
                int lineIndex = firstVisibleLine + i;
                if (lineIndex >= totalLines)
                {
                    break;
                }

                double y = i * lineHeight;
                LineColumns columns = GetColumns(lineIndex);
                maxWidth = Math.Max(maxWidth, columns.TotalColumns * charWidth);

                bool lineHasSelection = false;
                double selectionX = 0;
                double selectionWidth = 0;
                if (hasSelection && lineIndex >= selStart.Line && lineIndex <= selEnd.Line)
                {
                    int startCol = lineIndex == selStart.Line ? columns.ColumnOfCharIndex(selStart.Column) : 0;
                    int endCol = lineIndex == selEnd.Line ? columns.ColumnOfCharIndex(selEnd.Column) : columns.TotalColumns + 1;
                    double x0 = startCol * charWidth - horizontalOffset + TextOriginX;
                    double x1 = endCol * charWidth - horizontalOffset + TextOriginX;
                    if (x1 > x0)
                    {
                        lineHasSelection = true;
                        selectionX = x0;
                        selectionWidth = x1 - x0;
                    }
                }

                bool hasCaret = hasFocus && caretVisible && caret.Line == lineIndex;
                double caretX = hasCaret ? columns.ColumnOfCharIndex(caret.Column) * charWidth - horizontalOffset + TextOriginX : 0;

                lines.Add(new VisibleLine
                {
                    LineIndex = lineIndex,
                    Y = y,
                    Display = columns.Display,
                    TextX = TextOriginX - horizontalOffset,
                    HasSelection = lineHasSelection,
                    SelectionX = selectionX,
                    SelectionWidth = selectionWidth,
                    HasCaret = hasCaret,
                    CaretX = caretX,
                    Matches = highlighter == null ? NoHighlights : ComputeHighlights(columns, 0, columns.Source.Length, horizontalOffset),
                    RuleHighlights = highlightRules == null ? NoRuleHighlights : ComputeRuleHighlights(columns, 0, columns.Source.Length, horizontalOffset),
                    IsBookmarked = bookmarks.Count > 0 && bookmarks.Contains(lineIndex),
                    Whitespace = showWhitespace
                        ? WhitespaceMarkers.Compute(columns, LineEndingOf(lineIndex))
                        : NoWhitespace,
                });
            }

            double newExtent = Math.Max(maxWidth + GutterWidthPx + TextPadding * 2, ViewportWidth);
            if (Math.Abs(newExtent - horizontalExtentPx) > 0.5)
            {
                horizontalExtentPx = newExtent;
                RaiseViewChanged();
            }
            return lines;
        }

        // Word-wrap render model (issue #31): one source line emits N display rows, each carrying its own sliced
        // text and row-local selection/caret/highlight positions. The gutter line number and bookmark tick show
        // only on a line's first row. There is no horizontal scroll, so the extent is just the viewport width.
        private IReadOnlyList<VisibleLine> GetVisibleLinesWrapped(bool hasFocus, bool caretVisible)
        {
            var lines = new List<VisibleLine>();
            int wrapCols = WrapColumns;
            (TextPosition selStart, TextPosition selEnd) = NormalizedSelection();
            bool hasSelection = selStart != selEnd;

            double y = 0;
            int srcLine = firstVisibleLine;
            while (srcLine < totalLines && y < ViewportHeight)
            {
                LineColumns columns = GetColumns(srcLine);
                IReadOnlyList<LineWrapper.WrapRow> rows = LineWrapper.Wrap(columns, wrapCols);
                int startRow = srcLine == firstVisibleLine ? Math.Clamp(firstVisibleRow, 0, rows.Count - 1) : 0;

                for (int r = startRow; r < rows.Count && y < ViewportHeight; r++)
                {
                    LineWrapper.WrapRow row = rows[r];
                    double originPx = row.StartColumn * charWidth;

                    bool rowHasSel = false;
                    double selX = 0, selW = 0;
                    if (hasSelection && srcLine >= selStart.Line && srcLine <= selEnd.Line)
                    {
                        int selStartChar = srcLine == selStart.Line ? selStart.Column : 0;
                        int selEndChar = srcLine == selEnd.Line ? selEnd.Column : columns.Source.Length;
                        int s = Math.Max(selStartChar, row.StartChar);
                        int e = Math.Min(selEndChar, row.EndChar);
                        if (e >= s)
                        {
                            int startCol = columns.ColumnOfCharIndex(s) - row.StartColumn;
                            int endCol = columns.ColumnOfCharIndex(e) - row.StartColumn;
                            // Show the wrap break / newline as selected when the selection continues past this row.
                            if (srcLine < selEnd.Line || selEndChar > row.EndChar)
                            {
                                endCol += 1;
                            }
                            double x0 = startCol * charWidth + TextOriginX;
                            double x1 = endCol * charWidth + TextOriginX;
                            if (x1 > x0)
                            {
                                rowHasSel = true;
                                selX = x0;
                                selW = x1 - x0;
                            }
                        }
                    }

                    bool hasCaret = hasFocus && caretVisible && caret.Line == srcLine
                                    && LineWrapper.RowOfCharIndex(rows, caret.Column) == r;
                    double caretX = hasCaret
                        ? (columns.ColumnOfCharIndex(caret.Column) - row.StartColumn) * charWidth + TextOriginX
                        : 0;

                    lines.Add(new VisibleLine
                    {
                        LineIndex = srcLine,
                        Y = y,
                        Display = columns.DisplaySlice(row.StartChar, row.EndChar),
                        TextX = TextOriginX,
                        HasSelection = rowHasSel,
                        SelectionX = selX,
                        SelectionWidth = selW,
                        HasCaret = hasCaret,
                        CaretX = caretX,
                        Matches = highlighter == null ? NoHighlights : ComputeHighlights(columns, row.StartChar, row.EndChar, originPx),
                        RuleHighlights = highlightRules == null ? NoRuleHighlights : ComputeRuleHighlights(columns, row.StartChar, row.EndChar, originPx),
                        IsBookmarked = r == 0 && bookmarks.Count > 0 && bookmarks.Contains(srcLine),
                        Whitespace = NoWhitespace, // whitespace markers aren't shown in wrap mode (v1)
                        IsWrapContinuation = r > 0,
                    });
                    y += lineHeight;
                }
                srcLine++;
            }

            horizontalExtentPx = ViewportWidth;
            return lines;
        }

        // The terminator of a line, when the provider can report it (real file or filtered view); None otherwise.
        // Only consulted while "Show Whitespace" is on, so the extra index lookup is paid for visible lines only.
        private LineEnding LineEndingOf(int lineIndex) =>
            provider is ILineEndingSource endings ? endings.GetLineEnding(lineIndex) : LineEnding.None;

        private LineColumns GetColumns(int lineIndex)
        {
            if (columnsCache.TryGetValue(lineIndex, out LineColumns? cached))
            {
                return cached;
            }

            string text = provider!.GetLine(lineIndex);
            LineColumns columns = LineColumns.Build(text, tabSize);
            if (columnsCache.Count >= 4096)
            {
                columnsCache.Clear();
            }
            columnsCache[lineIndex] = columns;
            return columns;
        }

        // Maps the highlighter's character-index match spans to viewport-pixel rectangles. Spans are clipped to
        // [clipStart, clipEnd) (the whole line in non-wrap mode, a wrapped row's char range in wrap mode) and
        // positioned with originPx as the left scroll/row origin. A zero-width result is dropped.
        private IReadOnlyList<HighlightRect> ComputeHighlights(LineColumns columns, int clipStart, int clipEnd, double originPx)
        {
            IReadOnlyList<(int Start, int Length)> spans = highlighter!.Find(columns.Source);
            if (spans.Count == 0)
            {
                return NoHighlights;
            }

            var rects = new List<HighlightRect>(spans.Count);
            foreach ((int start, int length) in spans)
            {
                int s = Math.Max(start, clipStart);
                int e = Math.Min(start + length, clipEnd);
                if (e <= s)
                {
                    continue;
                }
                int startCol = columns.ColumnOfCharIndex(s);
                int endCol = columns.ColumnOfCharIndex(e);
                double x0 = startCol * charWidth - originPx + TextOriginX;
                double x1 = endCol * charWidth - originPx + TextOriginX;
                if (x1 > x0)
                {
                    rects.Add(new HighlightRect(x0, x1 - x0));
                }
            }
            return rects.Count > 0 ? rects : NoHighlights;
        }

        // Maps the persistent rule set's coloured character spans to viewport-pixel rectangles (same clip/origin
        // math as ComputeHighlights), carrying each rule's colour through to the renderer.
        private IReadOnlyList<RuleHighlightRect> ComputeRuleHighlights(LineColumns columns, int clipStart, int clipEnd, double originPx)
        {
            IReadOnlyList<HighlightSpan> spans = highlightRules!.Find(columns.Source);
            if (spans.Count == 0)
            {
                return NoRuleHighlights;
            }

            var rects = new List<RuleHighlightRect>(spans.Count);
            foreach (HighlightSpan span in spans)
            {
                int s = Math.Max(span.Start, clipStart);
                int e = Math.Min(span.Start + span.Length, clipEnd);
                if (e <= s)
                {
                    continue;
                }
                int startCol = columns.ColumnOfCharIndex(s);
                int endCol = columns.ColumnOfCharIndex(e);
                double x0 = startCol * charWidth - originPx + TextOriginX;
                double x1 = endCol * charWidth - originPx + TextOriginX;
                if (x1 > x0)
                {
                    rects.Add(new RuleHighlightRect(x0, x1 - x0, span.Argb));
                }
            }
            return rects.Count > 0 ? rects : NoRuleHighlights;
        }

        #endregion

        #region Test seams

        /// <summary>Anchor end of the current selection (test/inspection only).</summary>
        internal TextPosition Anchor => anchor;

        /// <summary>Normalized (start, end) selection endpoints (test/inspection only).</summary>
        internal (TextPosition start, TextPosition end) Selection => NormalizedSelection();

        #endregion

        private void RaiseViewChanged() => ViewChanged?.Invoke();
        private void RaiseCaretChanged() => CaretChanged?.Invoke(caret);
        private void RaiseRedraw() => RedrawRequested?.Invoke();
        private void RaiseBlinkReset() => CaretBlinkResetRequested?.Invoke();
    }
}
