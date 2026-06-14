using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FujiyNotepad.Core;

namespace FujiyNotepad.UI.Controls
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

    /// <summary>
    /// A from-scratch, read-only text surface that simulates a TextBox over an arbitrarily large file
    /// without loading it: it draws only the lines currently visible (read on demand via
    /// <see cref="LineProvider"/>), tracks its own scroll position in whole-line units, and implements
    /// caret placement, character-level selection, copy, and keyboard/mouse navigation itself. A fixed
    /// monospace font gives a uniform line height and column width, which keeps hit-testing and the
    /// scrollbar math exact.
    /// </summary>
    public sealed class TextView : FrameworkElement
    {
        private const int TabSize = 4;
        private const int MaxCopyChars = 25_000_000;

        private readonly Typeface typeface = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private readonly double emSize = 13.0;
        private double pixelsPerDip = 1.0;
        private double lineHeight;
        private double charWidth;
        private bool metricsValid;

        private readonly Brush backgroundBrush = Brushes.White;
        private readonly Brush textBrush = Brushes.Black;
        private readonly Brush caretBrush = Brushes.Black;
        private readonly Brush selectionActiveBrush = new SolidColorBrush(Color.FromRgb(0xAD, 0xD6, 0xFF));
        private readonly Brush selectionInactiveBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC));

        private sealed record RenderLine(LineColumns Columns, FormattedText Text);
        private readonly Dictionary<int, RenderLine> renderCache = new();

        private LineProvider? provider;
        private int totalLines;
        private int firstVisibleLine;
        private double horizontalOffset;
        private double horizontalExtentPx;

        private TextPosition caret;
        private TextPosition anchor;
        private int desiredColumn = -1;

        private bool hasFocusInternal;
        private bool caretBlinkOn = true;
        private readonly DispatcherTimer caretTimer;
        private readonly DispatcherTimer autoScrollTimer;
        private Point lastMousePosition;

        public event Action? ViewChanged;
        public event Action<TextPosition>? CaretChanged;

        public TextView()
        {
            Focusable = true;
            FocusVisualStyle = null;
            ClipToBounds = true;
            Cursor = Cursors.IBeam; // show the text I-beam over the text area, like a TextBox
            selectionActiveBrush.Freeze();
            selectionInactiveBrush.Freeze();

            caretTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(530) };
            caretTimer.Tick += (_, _) => { caretBlinkOn = !caretBlinkOn; InvalidateVisual(); };

            autoScrollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
            autoScrollTimer.Tick += AutoScrollTimer_Tick;
        }

        #region Public surface for the host

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
        public int FullyVisibleLineCount => lineHeight > 0 ? Math.Max(1, (int)(ActualHeight / lineHeight)) : 1;
        public int MaxFirstLine => Math.Max(0, totalLines - FullyVisibleLineCount);
        public double CharWidthPx => charWidth;
        public double ViewportWidthPx => ActualWidth;
        public double HorizontalExtentPx => Math.Max(horizontalExtentPx, ActualWidth);
        public TextPosition CaretPosition => caret;

        public void SetProvider(LineProvider? newProvider)
        {
            provider = newProvider;
            totalLines = newProvider?.LineCount ?? 0;
            firstVisibleLine = 0;
            horizontalOffset = 0;
            caret = anchor = new TextPosition(0, 0);
            desiredColumn = -1;
            renderCache.Clear();
            RaiseViewChanged();
            InvalidateVisual();
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
            InvalidateVisual();
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
            SetFirstVisibleLine(lineIndex);
            RestartCaretBlink();
            RaiseCaretChanged();
            InvalidateVisual();
        }

        public void SelectMatch(int lineIndex, int startColumn, int length)
        {
            if (provider == null || totalLines == 0)
            {
                return;
            }

            lineIndex = Math.Clamp(lineIndex, 0, totalLines - 1);
            int lineLen = GetRenderLine(lineIndex).Columns.Source.Length;
            int start = Math.Clamp(startColumn, 0, lineLen);
            int end = Math.Clamp(startColumn + length, start, lineLen);
            anchor = new TextPosition(lineIndex, start);
            caret = new TextPosition(lineIndex, end);
            desiredColumn = -1;
            EnsureLineVisibleTop(lineIndex);
            EnsureCaretVisibleHorizontally();
            RestartCaretBlink();
            RaiseCaretChanged();
            InvalidateVisual();
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
            renderCache.Clear();
            RaiseViewChanged();
            InvalidateVisual();
        }

        private void EnsureLineVisibleTop(int lineIndex)
        {
            // Put the target near the top, but never scroll past the last page.
            SetFirstVisibleLine(Math.Min(lineIndex, MaxFirstLine));
        }

        private void SetHorizontalOffset(double value)
        {
            double max = Math.Max(0, HorizontalExtentPx - ActualWidth);
            value = Math.Clamp(value, 0, max);
            if (Math.Abs(value - horizontalOffset) < 0.01)
            {
                return;
            }
            horizontalOffset = value;
            RaiseViewChanged();
            InvalidateVisual();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            int linesPerNotch = SystemParameters.WheelScrollLines;
            if (linesPerNotch <= 0)
            {
                linesPerNotch = 3;
            }
            int delta = -(e.Delta / 120) * linesPerNotch;
            SetFirstVisibleLine(firstVisibleLine + delta);
            e.Handled = true;
        }

        #endregion

        #region Mouse

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            EnsureMetrics();
            lastMousePosition = e.GetPosition(this);
            TextPosition pos = HitTest(lastMousePosition);
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            if (!shift)
            {
                anchor = pos;
            }
            caret = pos;
            desiredColumn = -1;
            CaptureMouse();
            autoScrollTimer.Start();
            RestartCaretBlink();
            RaiseCaretChanged();
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!IsMouseCaptured)
            {
                return;
            }
            lastMousePosition = e.GetPosition(this);
            caret = HitTest(lastMousePosition);
            RaiseCaretChanged();
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
                autoScrollTimer.Stop();
            }
        }

        private void AutoScrollTimer_Tick(object? sender, EventArgs e)
        {
            if (!IsMouseCaptured)
            {
                autoScrollTimer.Stop();
                return;
            }

            if (lastMousePosition.Y < 0)
            {
                SetFirstVisibleLine(firstVisibleLine - 1);
                caret = HitTest(new Point(lastMousePosition.X, 0));
                RaiseCaretChanged();
                InvalidateVisual();
            }
            else if (lastMousePosition.Y > ActualHeight)
            {
                SetFirstVisibleLine(firstVisibleLine + 1);
                caret = HitTest(new Point(lastMousePosition.X, ActualHeight - 1));
                RaiseCaretChanged();
                InvalidateVisual();
            }
        }

        private TextPosition HitTest(Point p)
        {
            EnsureMetrics();
            if (provider == null || totalLines == 0 || lineHeight <= 0)
            {
                return new TextPosition(0, 0);
            }

            int line = firstVisibleLine + (int)Math.Floor(p.Y / lineHeight);
            line = Math.Clamp(line, 0, totalLines - 1);

            LineColumns columns = GetRenderLine(line).Columns;
            double column = (p.X + horizontalOffset) / charWidth;
            int charIndex = columns.CharIndexOfColumn(column);
            return new TextPosition(line, charIndex);
        }

        #endregion

        #region Keyboard

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (provider == null || totalLines == 0)
            {
                return;
            }

            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool handled = true;

            switch (e.Key)
            {
                case Key.Left:
                    MoveCaretByChar(-1, shift);
                    break;
                case Key.Right:
                    MoveCaretByChar(+1, shift);
                    break;
                case Key.Up:
                    MoveCaretByLine(-1, shift);
                    break;
                case Key.Down:
                    MoveCaretByLine(+1, shift);
                    break;
                case Key.PageUp:
                    MoveCaretByLine(-FullyVisibleLineCount, shift);
                    break;
                case Key.PageDown:
                    MoveCaretByLine(+FullyVisibleLineCount, shift);
                    break;
                case Key.Home:
                    MoveCaretTo(ctrl ? new TextPosition(0, 0) : new TextPosition(caret.Line, 0), shift);
                    break;
                case Key.End:
                    if (ctrl)
                    {
                        int last = totalLines - 1;
                        MoveCaretTo(new TextPosition(last, LineLength(last)), shift);
                    }
                    else
                    {
                        MoveCaretTo(new TextPosition(caret.Line, LineLength(caret.Line)), shift);
                    }
                    break;
                case Key.A when ctrl:
                    SelectAll();
                    break;
                case Key.C when ctrl:
                    CopySelection();
                    break;
                default:
                    handled = false;
                    break;
            }

            if (handled)
            {
                e.Handled = true;
            }
        }

        private int LineLength(int line) => GetRenderLine(line).Columns.Source.Length;

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
            pos = new TextPosition(Math.Clamp(pos.Line, 0, totalLines - 1), pos.Column);
            ApplyCaret(new TextPosition(pos.Line, Math.Clamp(pos.Column, 0, LineLength(pos.Line))), shift);
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
            RestartCaretBlink();
            RaiseCaretChanged();
            InvalidateVisual();
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
            InvalidateVisual();
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
            double caretX = GetRenderLine(caret.Line).Columns.ColumnOfCharIndex(caret.Column) * charWidth;
            if (caretX < horizontalOffset)
            {
                SetHorizontalOffset(caretX);
            }
            else if (caretX > horizontalOffset + ActualWidth - charWidth)
            {
                SetHorizontalOffset(caretX - ActualWidth + charWidth);
            }
        }

        #endregion

        #region Copy

        private void CopySelection()
        {
            (TextPosition start, TextPosition end) = NormalizedSelection();
            if (start == end)
            {
                return;
            }

            var sb = new System.Text.StringBuilder();
            if (start.Line == end.Line)
            {
                string line = GetRenderLine(start.Line).Columns.Source;
                sb.Append(Slice(line, start.Column, end.Column));
            }
            else
            {
                string firstLine = GetRenderLine(start.Line).Columns.Source;
                sb.Append(Slice(firstLine, start.Column, firstLine.Length)).Append("\r\n");

                for (int l = start.Line + 1; l < end.Line && sb.Length < MaxCopyChars; l++)
                {
                    sb.Append(GetRenderLine(l).Columns.Source).Append("\r\n");
                }

                if (sb.Length < MaxCopyChars)
                {
                    string lastLine = GetRenderLine(end.Line).Columns.Source;
                    sb.Append(Slice(lastLine, 0, end.Column));
                }
            }

            try
            {
                Clipboard.SetText(sb.ToString());
            }
            catch (Exception)
            {
                // The clipboard can be transiently locked by another process; copying is best-effort.
            }
        }

        private static string Slice(string s, int start, int end)
        {
            start = Math.Clamp(start, 0, s.Length);
            end = Math.Clamp(end, start, s.Length);
            return s.Substring(start, end - start);
        }

        private (TextPosition start, TextPosition end) NormalizedSelection()
            => anchor <= caret ? (anchor, caret) : (caret, anchor);

        #endregion

        #region Focus + metrics

        protected override void OnGotKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            hasFocusInternal = true;
            RestartCaretBlink();
            InvalidateVisual();
        }

        protected override void OnLostKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            hasFocusInternal = false;
            caretTimer.Stop();
            InvalidateVisual();
        }

        private void RestartCaretBlink()
        {
            caretBlinkOn = true;
            if (hasFocusInternal)
            {
                caretTimer.Stop();
                caretTimer.Start();
            }
        }

        private void EnsureMetrics()
        {
            if (metricsValid)
            {
                return;
            }

            try
            {
                pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            }
            catch (Exception)
            {
                pixelsPerDip = 1.0;
            }
            if (pixelsPerDip <= 0)
            {
                pixelsPerDip = 1.0;
            }

            var sample = new FormattedText("0", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, emSize, textBrush, pixelsPerDip);
            charWidth = sample.WidthIncludingTrailingWhitespace;
            if (charWidth <= 0)
            {
                charWidth = emSize * 0.6;
            }
            lineHeight = sample.Height;
            if (lineHeight <= 0)
            {
                lineHeight = emSize * 1.3;
            }
            metricsValid = true;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            metricsValid = false;
            renderCache.Clear();
            InvalidateVisual();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            RaiseViewChanged();
            InvalidateVisual();
        }

        #endregion

        #region Rendering

        private RenderLine GetRenderLine(int lineIndex)
        {
            if (renderCache.TryGetValue(lineIndex, out RenderLine? cached))
            {
                return cached;
            }

            string text = provider!.GetLine(lineIndex);
            LineColumns columns = LineColumns.Build(text, TabSize);
            var formatted = new FormattedText(columns.Display, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, emSize, textBrush, pixelsPerDip);
            var renderLine = new RenderLine(columns, formatted);
            renderCache[lineIndex] = renderLine;
            return renderLine;
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(backgroundBrush, null, new Rect(RenderSize));

            EnsureMetrics();
            if (provider == null || totalLines == 0 || lineHeight <= 0)
            {
                horizontalExtentPx = ActualWidth;
                return;
            }

            int renderCount = (int)Math.Ceiling(ActualHeight / lineHeight) + 1;
            (TextPosition selStart, TextPosition selEnd) = NormalizedSelection();
            bool hasSelection = selStart != selEnd;
            Brush selBrush = hasFocusInternal ? selectionActiveBrush : selectionInactiveBrush;

            dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));

            double maxWidth = 0;
            for (int i = 0; i < renderCount; i++)
            {
                int lineIndex = firstVisibleLine + i;
                if (lineIndex >= totalLines)
                {
                    break;
                }

                double y = i * lineHeight;
                RenderLine rl = GetRenderLine(lineIndex);
                LineColumns columns = rl.Columns;
                maxWidth = Math.Max(maxWidth, columns.TotalColumns * charWidth);

                if (hasSelection && lineIndex >= selStart.Line && lineIndex <= selEnd.Line)
                {
                    int startCol = lineIndex == selStart.Line ? columns.ColumnOfCharIndex(selStart.Column) : 0;
                    int endCol = lineIndex == selEnd.Line ? columns.ColumnOfCharIndex(selEnd.Column) : columns.TotalColumns + 1;
                    double x0 = startCol * charWidth - horizontalOffset;
                    double x1 = endCol * charWidth - horizontalOffset;
                    if (x1 > x0)
                    {
                        dc.DrawRectangle(selBrush, null, new Rect(x0, y, x1 - x0, lineHeight));
                    }
                }

                dc.DrawText(rl.Text, new Point(-horizontalOffset, y));

                if (hasFocusInternal && caretBlinkOn && caret.Line == lineIndex)
                {
                    double caretX = columns.ColumnOfCharIndex(caret.Column) * charWidth - horizontalOffset;
                    dc.DrawRectangle(caretBrush, null, new Rect(caretX, y, 1.0, lineHeight));
                }
            }

            dc.Pop();

            double newExtent = Math.Max(maxWidth, ActualWidth);
            if (Math.Abs(newExtent - horizontalExtentPx) > 0.5)
            {
                horizontalExtentPx = newExtent;
                RaiseViewChanged();
            }
        }

        #endregion

        private void RaiseViewChanged() => ViewChanged?.Invoke();
        private void RaiseCaretChanged() => CaretChanged?.Invoke(caret);
    }
}
