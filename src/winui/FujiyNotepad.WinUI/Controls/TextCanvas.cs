using System.Numerics;
using FujiyNotepad.Core;
using FujiyNotepad.WinUI.Logic;
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
    /// <summary>
    /// A from-scratch, read-only text surface that simulates a text box over an arbitrarily large file
    /// without loading it: it draws only the lines currently visible (read on demand via
    /// <see cref="LineProvider"/>) onto a Win2D <see cref="CanvasControl"/>, tracks its own scroll position
    /// in whole-line units, and supports caret placement, character-level selection, copy and
    /// keyboard/mouse navigation. A fixed monospace font gives a uniform line height and column width.
    ///
    /// This control is the WinUI shell: it owns the <see cref="CanvasControl"/>, focus, the caret-blink
    /// timer, clipboard access, the Win2D font metrics and the actual draw calls. All scroll/caret/selection
    /// math and the per-line render model live in the framework-independent <see cref="TextLayoutEngine"/>
    /// (a plain .NET library), so that logic unit-tests on a normal test host.
    /// </summary>
    public sealed class TextCanvas : UserControl
    {
        private readonly TextLayoutEngine engine = new();
        private readonly CanvasControl canvas;
        private readonly CanvasTextFormat textFormat;

        private double charWidth;
        private double lineHeight;
        private bool metricsValid;

        private readonly Color backgroundColor = Colors.White;
        private readonly Color textColor = Colors.Black;
        private readonly Color caretColor = Colors.Black;
        private readonly Color selectionActiveColor = Color.FromArgb(255, 0xAD, 0xD6, 0xFF);
        private readonly Color selectionInactiveColor = Color.FromArgb(255, 0xDC, 0xDC, 0xDC);

        private bool hasFocusInternal;
        private bool caretBlinkOn = true;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer caretTimer;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer autoScrollTimer;

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

            // Repeats the drag while the pointer is held past the top/bottom edge during a selection.
            autoScrollTimer = DispatcherQueue.CreateTimer();
            autoScrollTimer.Interval = TimeSpan.FromMilliseconds(50);
            autoScrollTimer.Tick += AutoScrollTick;

            engine.ViewChanged += () => ViewChanged?.Invoke();
            engine.CaretChanged += pos => CaretChanged?.Invoke(pos);
            engine.RedrawRequested += () => canvas.Invalidate();
            engine.CaretBlinkResetRequested += RestartCaretBlink;

            IsTabStop = true;
            UseSystemFocusVisuals = false;
            IsDoubleTapEnabled = true;

            PointerPressed += OnPointerPressed;
            PointerMoved += OnPointerMoved;
            PointerReleased += OnPointerReleased;
            PointerWheelChanged += OnPointerWheelChanged;
            DoubleTapped += OnDoubleTapped;
            KeyDown += OnKeyDown;
            GotFocus += (_, _) => { hasFocusInternal = true; RestartCaretBlink(); canvas.Invalidate(); };
            LostFocus += (_, _) => { hasFocusInternal = false; caretTimer.Stop(); canvas.Invalidate(); };
            SizeChanged += (_, _) => { SyncViewport(); ViewChanged?.Invoke(); canvas.Invalidate(); };
        }

        // Push the live control size + measured font metrics into the engine before any op that depends on
        // them (clamping, hit-testing, the render model).
        private void Ready()
        {
            EnsureMetrics();
            SyncViewport();
        }

        private void SyncViewport()
        {
            engine.ViewportWidth = ActualWidth;
            engine.ViewportHeight = ActualHeight;
        }

        #region Public surface for the host

        public int FirstVisibleLine
        {
            get => engine.FirstVisibleLine;
            set { Ready(); engine.FirstVisibleLine = value; }
        }

        public double HorizontalOffset
        {
            get => engine.HorizontalOffset;
            set { Ready(); engine.HorizontalOffset = value; }
        }

        public int TotalLines => engine.TotalLines;
        public int FullyVisibleLineCount { get { Ready(); return engine.FullyVisibleLineCount; } }
        public int MaxFirstLine { get { Ready(); return engine.MaxFirstLine; } }
        public double CharWidthPx { get { EnsureMetrics(); return engine.CharWidthPx; } }
        public double ViewportWidthPx => ActualWidth;
        public double HorizontalExtentPx { get { Ready(); return engine.HorizontalExtentPx; } }
        public TextPosition CaretPosition => engine.CaretPosition;

        public int TabSize
        {
            get => engine.TabSize;
            set => engine.TabSize = value;
        }

        public void SetProvider(LineProvider? newProvider)
        {
            Ready();
            engine.SetProvider(newProvider);
        }

        public void UpdateTotalLines(int count)
        {
            Ready();
            engine.UpdateTotalLines(count);
        }

        public void GoToLine(int lineIndex)
        {
            Ready();
            engine.GoToLine(lineIndex);
        }

        public void SelectMatch(int lineIndex, int startColumn, int length)
        {
            Ready();
            engine.SelectMatch(lineIndex, startColumn, length);
        }

        public void FocusCanvas() => Focus(Microsoft.UI.Xaml.FocusState.Programmatic);

        #endregion

        #region Pointer

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            Focus(Microsoft.UI.Xaml.FocusState.Pointer);
            Ready();
            lastPointer = e.GetCurrentPoint(this).Position;
            bool shift = IsDown(VirtualKey.Shift);
            engine.PointerPress(lastPointer.X, lastPointer.Y, shift);
            isSelecting = true;
            CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!isSelecting)
            {
                return;
            }
            Ready();
            lastPointer = e.GetCurrentPoint(this).Position;
            engine.PointerDrag(lastPointer.X, lastPointer.Y);
            UpdateAutoScroll();
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (isSelecting)
            {
                isSelecting = false;
                autoScrollTimer.Stop();
                ReleasePointerCapture(e.Pointer);
            }
        }

        private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            Ready();
            int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            engine.ScrollByWheelDelta(delta);
            e.Handled = true;
        }

        private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            Ready();
            Point p = e.GetPosition(this);
            engine.SelectWordAt(p.X, p.Y);
            e.Handled = true;
        }

        // While the pointer is held past the top/bottom edge during a selection, keep scrolling (and
        // extending the selection) on a timer even if the pointer stops moving.
        private void UpdateAutoScroll()
        {
            if (isSelecting && (lastPointer.Y < 0 || lastPointer.Y > ActualHeight))
            {
                autoScrollTimer.Start();
            }
            else
            {
                autoScrollTimer.Stop();
            }
        }

        private void AutoScrollTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            if (!isSelecting || (lastPointer.Y >= 0 && lastPointer.Y <= ActualHeight))
            {
                autoScrollTimer.Stop();
                return;
            }
            Ready();
            engine.PointerDrag(lastPointer.X, lastPointer.Y);
        }

        #endregion

        #region Keyboard

        private static bool IsDown(VirtualKey k) =>
            (InputKeyboardSource.GetKeyStateForCurrentThread(k) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

        // Translates a physical key (plus the Ctrl state) into a UI-agnostic engine command, or null when
        // the key isn't a navigation/selection/copy shortcut the canvas handles.
        private static NavKey? MapKey(VirtualKey key, bool ctrl) => key switch
        {
            VirtualKey.Left => NavKey.Left,
            VirtualKey.Right => NavKey.Right,
            VirtualKey.Up => NavKey.Up,
            VirtualKey.Down => NavKey.Down,
            VirtualKey.PageUp => NavKey.PageUp,
            VirtualKey.PageDown => NavKey.PageDown,
            VirtualKey.Home => ctrl ? NavKey.DocumentStart : NavKey.LineStart,
            VirtualKey.End => ctrl ? NavKey.DocumentEnd : NavKey.LineEnd,
            VirtualKey.A when ctrl => NavKey.SelectAll,
            VirtualKey.C when ctrl => NavKey.Copy,
            _ => null,
        };

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            bool ctrl = IsDown(VirtualKey.Control);
            NavKey? nav = MapKey(e.Key, ctrl);
            if (nav is null)
            {
                return;
            }

            Ready();
            bool shift = IsDown(VirtualKey.Shift);
            KeyResult result = engine.HandleKey(nav.Value, shift);
            if (result.CopyText != null)
            {
                CopyToClipboard(result.CopyText);
            }
            if (result.Handled)
            {
                e.Handled = true;
            }
        }

        #endregion

        #region Copy

        private static void CopyToClipboard(string text)
        {
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
            }
            catch (Exception)
            {
                // The clipboard can be transiently locked by another process; copying is best-effort.
            }
        }

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
                // Only latch the metrics once a real probe succeeds, so a transient device failure on
                // the first call falls back for this draw but is recomputed on the next.
                if (charWidth > 0 && lineHeight > 0)
                {
                    metricsValid = true;
                }
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

            engine.SetMetrics(charWidth, lineHeight);
        }

        private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            CanvasDrawingSession ds = args.DrawingSession;
            ds.Clear(backgroundColor);

            EnsureMetrics();
            SyncViewport();

            Color selectionColor = hasFocusInternal ? selectionActiveColor : selectionInactiveColor;
            foreach (VisibleLine line in engine.GetVisibleLines(hasFocusInternal, caretBlinkOn))
            {
                if (line.HasSelection)
                {
                    ds.FillRectangle((float)line.SelectionX, (float)line.Y, (float)line.SelectionWidth, (float)lineHeight, selectionColor);
                }

                ds.DrawText(line.Display, new Vector2((float)line.TextX, (float)line.Y), textColor, textFormat);

                if (line.HasCaret)
                {
                    ds.FillRectangle((float)line.CaretX, (float)line.Y, 1.0f, (float)lineHeight, caretColor);
                }
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
    }
}
