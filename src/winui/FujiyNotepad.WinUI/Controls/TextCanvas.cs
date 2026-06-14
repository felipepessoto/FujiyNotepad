using System.Numerics;
using FujiyNotepad.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace FujiyNotepad.WinUI.Controls
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
    /// A from-scratch, read-only text surface that simulates a text box over an arbitrarily large file
    /// without loading it: it draws only the lines currently visible (read on demand via
    /// <see cref="LineProvider"/>) onto a Win2D <see cref="CanvasControl"/>, tracks its own scroll
    /// position in whole-line units, and implements caret placement, character-level selection, copy,
    /// and keyboard/mouse navigation itself. A fixed monospace font gives a uniform line height and
    /// column width, which keeps hit-testing and the scrollbar math exact.
    /// </summary>
    public sealed class TextCanvas : UserControl
    {
        private const int TabSize = 4;
        private const int MaxCopyChars = 25_000_000;

        private readonly CanvasControl canvas;
        private readonly CanvasTextFormat textFormat;
        private double lineHeight;
        private double charWidth;
        private bool metricsValid;

        private readonly Color backgroundColor = Colors.White;
        private readonly Color textColor = Colors.Black;
        private readonly Color caretColor = Colors.Black;
        private readonly Color selectionActiveColor = Color.FromArgb(255, 0xAD, 0xD6, 0xFF);
        private readonly Color selectionInactiveColor = Color.FromArgb(255, 0xDC, 0xDC, 0xDC);

        private readonly Dictionary<int, LineColumns> columnsCache = new();

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
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer caretTimer;

        private bool isSelecting;
        private Point lastPointer;

        public event Action? ViewChanged;
        public event Action<TextPosition>? CaretChanged;

        public TextCanvas()
        {
            textFormat = new CanvasTextFormat
            {
                FontFamily = "Consolas",
                FontSize = 14,
                WordWrapping = CanvasWordWrapping.NoWrap,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
            };

            canvas = new CanvasControl();
            canvas.Draw += OnDraw;
            Content = canvas;

            caretTimer = DispatcherQueue.CreateTimer();
            caretTimer.Interval = TimeSpan.FromMilliseconds(530);
            caretTimer.Tick += (_, _) => { caretBlinkOn = !caretBlinkOn; canvas.Invalidate(); };

            IsTabStop = true;
            UseSystemFocusVisuals = false;

            PointerPressed += OnPointerPressed;
            PointerMoved += OnPointerMoved;
            PointerReleased += OnPointerReleased;
            PointerWheelChanged += OnPointerWheelChanged;
            KeyDown += OnKeyDown;
            GotFocus += (_, _) => { hasFocusInternal = true; RestartCaretBlink(); canvas.Invalidate(); };
            LostFocus += (_, _) => { hasFocusInternal = false; caretTimer.Stop(); canvas.Invalidate(); };
            SizeChanged += (_, _) => { RaiseViewChanged(); canvas.Invalidate(); };
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
            columnsCache.Clear();
            RaiseViewChanged();
            RaiseCaretChanged();
            canvas.Invalidate();
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
            canvas.Invalidate();
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
            RestartCaretBlink();
            RaiseCaretChanged();
            canvas.Invalidate();
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
            SetFirstVisibleLine(Math.Min(lineIndex, MaxFirstLine));
            EnsureCaretVisibleHorizontally();
            RestartCaretBlink();
            RaiseCaretChanged();
            canvas.Invalidate();
        }

        public void FocusCanvas() => Focus(Microsoft.UI.Xaml.FocusState.Programmatic);

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
            canvas.Invalidate();
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
            canvas.Invalidate();
        }

        private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            int linesPerNotch = 3;
            int lines = -(delta / 120) * linesPerNotch;
            SetFirstVisibleLine(firstVisibleLine + lines);
            e.Handled = true;
        }

        #endregion

        #region Pointer

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            Focus(Microsoft.UI.Xaml.FocusState.Pointer);
            EnsureMetrics();
            lastPointer = e.GetCurrentPoint(this).Position;
            TextPosition pos = HitTest(lastPointer);
            bool shift = IsDown(VirtualKey.Shift);
            if (!shift)
            {
                anchor = pos;
            }
            caret = pos;
            desiredColumn = -1;
            isSelecting = true;
            CapturePointer(e.Pointer);
            RestartCaretBlink();
            RaiseCaretChanged();
            canvas.Invalidate();
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!isSelecting)
            {
                return;
            }
            lastPointer = e.GetCurrentPoint(this).Position;

            // Auto-scroll when dragging past the top/bottom edge.
            if (lastPointer.Y < 0)
            {
                SetFirstVisibleLine(firstVisibleLine - 1);
            }
            else if (lastPointer.Y > ActualHeight)
            {
                SetFirstVisibleLine(firstVisibleLine + 1);
            }

            caret = HitTest(lastPointer);
            RaiseCaretChanged();
            canvas.Invalidate();
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (isSelecting)
            {
                isSelecting = false;
                ReleasePointerCapture(e.Pointer);
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

            LineColumns columns = GetColumns(line);
            double column = (p.X + horizontalOffset) / charWidth;
            int charIndex = columns.CharIndexOfColumn(column);
            return new TextPosition(line, charIndex);
        }

        #endregion

        #region Keyboard

        private static bool IsDown(VirtualKey k) =>
            (InputKeyboardSource.GetKeyStateForCurrentThread(k) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (provider == null || totalLines == 0)
            {
                return;
            }

            bool shift = IsDown(VirtualKey.Shift);
            bool ctrl = IsDown(VirtualKey.Control);
            bool handled = true;

            switch (e.Key)
            {
                case VirtualKey.Left:
                    MoveCaretByChar(-1, shift);
                    break;
                case VirtualKey.Right:
                    MoveCaretByChar(+1, shift);
                    break;
                case VirtualKey.Up:
                    MoveCaretByLine(-1, shift);
                    break;
                case VirtualKey.Down:
                    MoveCaretByLine(+1, shift);
                    break;
                case VirtualKey.PageUp:
                    MoveCaretByLine(-FullyVisibleLineCount, shift);
                    break;
                case VirtualKey.PageDown:
                    MoveCaretByLine(+FullyVisibleLineCount, shift);
                    break;
                case VirtualKey.Home:
                    MoveCaretTo(ctrl ? new TextPosition(0, 0) : new TextPosition(caret.Line, 0), shift);
                    break;
                case VirtualKey.End:
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
                case VirtualKey.A when ctrl:
                    SelectAll();
                    break;
                case VirtualKey.C when ctrl:
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
            RestartCaretBlink();
            RaiseCaretChanged();
            canvas.Invalidate();
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
            canvas.Invalidate();
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

            try
            {
                var package = new DataPackage();
                package.SetText(sb.ToString());
                Clipboard.SetContent(package);
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

        #region Metrics + rendering

        private void EnsureMetrics()
        {
            if (metricsValid)
            {
                return;
            }

            try
            {
                CanvasDevice device = CanvasDevice.GetSharedDevice();
                using var probe = new CanvasTextLayout(device, new string('0', 10), textFormat, 0, 0);
                charWidth = probe.LayoutBounds.Width / 10.0;
                lineHeight = probe.LayoutBounds.Height;
            }
            catch (Exception)
            {
                charWidth = 0;
                lineHeight = 0;
            }

            if (charWidth <= 0)
            {
                charWidth = 8.0;
            }
            if (lineHeight <= 0)
            {
                lineHeight = 18.0;
            }
            metricsValid = true;
        }

        private LineColumns GetColumns(int lineIndex)
        {
            if (columnsCache.TryGetValue(lineIndex, out LineColumns? cached))
            {
                return cached;
            }

            string text = provider!.GetLine(lineIndex);
            LineColumns columns = LineColumns.Build(text, TabSize);
            if (columnsCache.Count >= 4096)
            {
                columnsCache.Clear();
            }
            columnsCache[lineIndex] = columns;
            return columns;
        }

        private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            CanvasDrawingSession ds = args.DrawingSession;
            ds.Clear(backgroundColor);

            EnsureMetrics();
            if (provider == null || totalLines == 0 || lineHeight <= 0)
            {
                horizontalExtentPx = ActualWidth;
                return;
            }

            int renderCount = (int)Math.Ceiling(ActualHeight / lineHeight) + 1;
            (TextPosition selStart, TextPosition selEnd) = NormalizedSelection();
            bool hasSelection = selStart != selEnd;
            Color selColor = hasFocusInternal ? selectionActiveColor : selectionInactiveColor;

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

                if (hasSelection && lineIndex >= selStart.Line && lineIndex <= selEnd.Line)
                {
                    int startCol = lineIndex == selStart.Line ? columns.ColumnOfCharIndex(selStart.Column) : 0;
                    int endCol = lineIndex == selEnd.Line ? columns.ColumnOfCharIndex(selEnd.Column) : columns.TotalColumns + 1;
                    double x0 = startCol * charWidth - horizontalOffset;
                    double x1 = endCol * charWidth - horizontalOffset;
                    if (x1 > x0)
                    {
                        ds.FillRectangle((float)x0, (float)y, (float)(x1 - x0), (float)lineHeight, selColor);
                    }
                }

                ds.DrawText(columns.Display, new Vector2((float)(-horizontalOffset), (float)y), textColor, textFormat);

                if (hasFocusInternal && caretBlinkOn && caret.Line == lineIndex)
                {
                    double caretX = columns.ColumnOfCharIndex(caret.Column) * charWidth - horizontalOffset;
                    ds.FillRectangle((float)caretX, (float)y, 1.0f, (float)lineHeight, caretColor);
                }
            }

            double newExtent = Math.Max(maxWidth, ActualWidth);
            if (Math.Abs(newExtent - horizontalExtentPx) > 0.5)
            {
                horizontalExtentPx = newExtent;
                RaiseViewChanged();
            }
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

        #endregion

        private void RaiseViewChanged() => ViewChanged?.Invoke();
        private void RaiseCaretChanged() => CaretChanged?.Invoke(caret);
    }
}
