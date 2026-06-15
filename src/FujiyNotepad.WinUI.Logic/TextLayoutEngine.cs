using System.Text;
using FujiyNotepad.Core;

namespace FujiyNotepad.WinUI.Logic
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

        private int tabSize = 4;

        private double lineHeight;
        private double charWidth;

        private readonly Dictionary<int, LineColumns> columnsCache = new();

        private static readonly IReadOnlyList<HighlightRect> NoHighlights = Array.Empty<HighlightRect>();
        private ILineHighlighter? highlighter;

        private LineProvider? provider;
        private int totalLines;
        private int firstVisibleLine;
        private double horizontalOffset;
        private double horizontalExtentPx;

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
        public int MaxFirstLine => Math.Max(0, totalLines - FullyVisibleLineCount);
        public double CharWidthPx => charWidth;
        public double HorizontalExtentPx => Math.Max(horizontalExtentPx, ViewportWidth);
        public TextPosition CaretPosition => caret;

        /// <summary>True when there is a non-empty selection (the anchor and caret differ).</summary>
        public bool HasSelection => anchor != caret;

        /// <summary>
        /// The start of the current selection in document order (equals the caret when nothing is selected).
        /// After <see cref="SelectMatch"/> this is the start of the matched text, which "Find Previous" uses
        /// as the upper bound so it never re-finds the current match.
        /// </summary>
        public TextPosition SelectionStart => NormalizedSelection().start;

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

        public void SetProvider(LineProvider? newProvider)
        {
            provider = newProvider;
            totalLines = newProvider?.LineCount ?? 0;
            firstVisibleLine = 0;
            horizontalOffset = 0;
            caret = anchor = new TextPosition(0, 0);
            desiredColumn = -1;
            columnsCache.Clear();
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
            // Only scroll when the match line is off-screen; if it is already visible, keep the scroll
            // position and just highlight (the horizontal nudge below is a no-op when it is fully visible).
            if (lineIndex < firstVisibleLine || lineIndex >= firstVisibleLine + FullyVisibleLineCount)
            {
                SetFirstVisibleLine(Math.Min(lineIndex, MaxFirstLine));
            }
            EnsureCaretVisibleHorizontally();
            RaiseBlinkReset();
            RaiseCaretChanged();
            RaiseRedraw();
        }

        #endregion

        #region Scrolling

        private void SetFirstVisibleLine(int value)
        {
            value = Math.Clamp(value, 0, MaxFirstLine);
            if (value == firstVisibleLine)
            {
                return;
            }
            firstVisibleLine = value;
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
            SetFirstVisibleLine(firstVisibleLine + lines);
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
                SetFirstVisibleLine(firstVisibleLine - 1);
            }
            else if (y > ViewportHeight)
            {
                SetFirstVisibleLine(firstVisibleLine + 1);
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

            int line = firstVisibleLine + (int)Math.Floor(y / lineHeight);
            line = Math.Clamp(line, 0, totalLines - 1);

            LineColumns columns = GetColumns(line);
            double column = (x - TextPadding + horizontalOffset) / charWidth;
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
                    MoveCaretByLine(-1, shift);
                    break;
                case NavKey.Down:
                    MoveCaretByLine(+1, shift);
                    break;
                case NavKey.PageUp:
                    MoveCaretByLine(-FullyVisibleLineCount, shift);
                    break;
                case NavKey.PageDown:
                    MoveCaretByLine(+FullyVisibleLineCount, shift);
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
            EnsureCaretVisibleVertically();
            EnsureCaretVisibleHorizontally();
            RaiseBlinkReset();
            RaiseCaretChanged();
            RaiseRedraw();
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
            double rightInset = charWidth + TextPadding * 2;
            if (caretX < horizontalOffset)
            {
                SetHorizontalOffset(caretX);
            }
            else if (caretX > horizontalOffset + ViewportWidth - rightInset)
            {
                SetHorizontalOffset(caretX - ViewportWidth + rightInset);
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
                    double x0 = startCol * charWidth - horizontalOffset + TextPadding;
                    double x1 = endCol * charWidth - horizontalOffset + TextPadding;
                    if (x1 > x0)
                    {
                        lineHasSelection = true;
                        selectionX = x0;
                        selectionWidth = x1 - x0;
                    }
                }

                bool hasCaret = hasFocus && caretVisible && caret.Line == lineIndex;
                double caretX = hasCaret ? columns.ColumnOfCharIndex(caret.Column) * charWidth - horizontalOffset + TextPadding : 0;

                lines.Add(new VisibleLine
                {
                    LineIndex = lineIndex,
                    Y = y,
                    Display = columns.Display,
                    TextX = TextPadding - horizontalOffset,
                    HasSelection = lineHasSelection,
                    SelectionX = selectionX,
                    SelectionWidth = selectionWidth,
                    HasCaret = hasCaret,
                    CaretX = caretX,
                    Matches = highlighter == null ? NoHighlights : ComputeHighlights(columns),
                });
            }

            double newExtent = Math.Max(maxWidth + TextPadding * 2, ViewportWidth);
            if (Math.Abs(newExtent - horizontalExtentPx) > 0.5)
            {
                horizontalExtentPx = newExtent;
                RaiseViewChanged();
            }
            return lines;
        }

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

        // Maps the highlighter's character-index match spans on this line to viewport-pixel rectangles, using
        // the same column/scroll math as the selection. Off-screen rectangles are kept (cheaply clipped by the
        // renderer); a zero-width span is dropped.
        private IReadOnlyList<HighlightRect> ComputeHighlights(LineColumns columns)
        {
            IReadOnlyList<(int Start, int Length)> spans = highlighter!.Find(columns.Source);
            if (spans.Count == 0)
            {
                return NoHighlights;
            }

            var rects = new List<HighlightRect>(spans.Count);
            foreach ((int start, int length) in spans)
            {
                int startCol = columns.ColumnOfCharIndex(start);
                int endCol = columns.ColumnOfCharIndex(start + length);
                double x0 = startCol * charWidth - horizontalOffset + TextPadding;
                double x1 = endCol * charWidth - horizontalOffset + TextPadding;
                if (x1 > x0)
                {
                    rects.Add(new HighlightRect(x0, x1 - x0));
                }
            }
            return rects.Count > 0 ? rects : NoHighlights;
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
