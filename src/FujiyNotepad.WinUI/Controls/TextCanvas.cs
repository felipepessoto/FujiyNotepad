using System.Numerics;
using FujiyNotepad.Core;
using FujiyNotepad.Presentation;
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
using Windows.UI.ViewManagement;

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
    public sealed partial class TextCanvas : UserControl
    {
        private readonly TextLayoutEngine engine = new();
        private readonly CanvasControl canvas;
        private readonly CanvasTextFormat textFormat;

        private double charWidth;
        private double lineHeight;
        private double lineHeightRaw;     // unsnapped line height straight from the font probe
        private double lastMetricsDpi;    // DPI used for the last pixel-snap; re-snap when it changes
        private bool metricsValid;

        // Theme-dependent colors for the Win2D surface (which paints its own pixels instead of using XAML
        // brushes). Updated by ApplyTheme() from the control's resolved ActualTheme; default to light.
        private Color backgroundColor = Colors.White;
        private Color textColor = Colors.Black;
        private Color caretColor = Colors.Black;
        private Color selectionColor = Color.FromArgb(255, 0xAD, 0xD6, 0xFF);
        private Color matchHighlightColor = Color.FromArgb(255, 0xFF, 0xD5, 0x4F);
        private Color gutterTextColor = Color.FromArgb(255, 0x88, 0x88, 0x88);
        private Color gutterSeparatorColor = Color.FromArgb(255, 0xE0, 0xE0, 0xE0);
        private Color bookmarkColor = Color.FromArgb(255, 0x1A, 0x7F, 0xD6);
        private Color whitespaceColor = Color.FromArgb(210, 0x5A, 0x5A, 0x5A);
        private Color trailingWhitespaceColor = Color.FromArgb(235, 0xD0, 0x30, 0x30);
        private Color controlCharColor = Color.FromArgb(235, 0xD0, 0x30, 0x30);

        // Detects Windows High Contrast so ApplyTheme can switch the surface to the system HC palette. Kept
        // alive as a field so its HighContrastChanged event keeps firing.
        private AccessibilitySettings? accessibility;

        // Right margin (px) between a line number and the gutter's edge; mirrors the engine's gutter padding.
        private const double GutterRightMargin = 6d;

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

            // Show the text (I-beam) cursor while hovering the surface, like a real text control.
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);

            PointerPressed += OnPointerPressed;
            PointerMoved += OnPointerMoved;
            PointerReleased += OnPointerReleased;
            PointerWheelChanged += OnPointerWheelChanged;
            DoubleTapped += OnDoubleTapped;
            KeyDown += OnKeyDown;
            GotFocus += (_, _) => { hasFocusInternal = true; RestartCaretBlink(); canvas.Invalidate(); };
            LostFocus += (_, _) => { hasFocusInternal = false; caretTimer.Stop(); canvas.Invalidate(); };
            SizeChanged += (_, _) => { SyncViewport(); ViewChanged?.Invoke(); canvas.Invalidate(); };

            // The Win2D surface paints its own colors, so re-theme it whenever the app theme changes.
            ApplyTheme();
            ActualThemeChanged += (_, _) => ApplyTheme();
            Loaded += (_, _) => ApplyTheme();

            // Re-theme when the user toggles Windows High Contrast or switches HC scheme (the event also
            // covers scheme changes). It can fire off the UI thread, so marshal back before repainting.
            // Construction can throw if the thread has no view, so guard it and fall back to the palette.
            try
            {
                accessibility = new AccessibilitySettings();
                accessibility.HighContrastChanged += (_, _) => DispatcherQueue?.TryEnqueue(ApplyTheme);
            }
            catch
            {
                // High Contrast detection unavailable; ApplyTheme falls back to the curated light/dark palette.
            }

            BuildContextMenu();
        }

        // A right-click / long-press / Shift+F10 context menu for the read-only surface.
        private void BuildContextMenu()
        {
            var copyItem = new MenuFlyoutItem { Text = "Copy", KeyboardAcceleratorTextOverride = "Ctrl+C" };
            copyItem.Click += (_, _) => CopySelection();

            var selectAllItem = new MenuFlyoutItem { Text = "Select All", KeyboardAcceleratorTextOverride = "Ctrl+A" };
            selectAllItem.Click += (_, _) => SelectAllText();

            var flyout = new MenuFlyout();
            flyout.Items.Add(copyItem);
            flyout.Items.Add(selectAllItem);
            // Copy is only meaningful when something is selected.
            flyout.Opening += (_, _) => copyItem.IsEnabled = engine.HasSelection;

            ContextFlyout = flyout;
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

        public TextPosition SelectionStart => engine.SelectionStart;

        /// <summary>The size (characters and lines) of the current selection, for the status bar.</summary>
        public SelectionStats GetSelectionStats() => engine.GetSelectionStats();

        /// <summary>The time delta between the first and last selected lines' leading timestamps, or null.</summary>
        public TimeSpan? GetSelectionTimestampDelta() => engine.GetSelectionTimestampDelta();

        public int TabSize
        {
            get => engine.TabSize;
            set => engine.TabSize = value;
        }

        // Font size bounds and the "100% zoom" baseline (the default size). Zoom changes the font size.
        private const double BaseFontSize = 14;
        private const double MinFontSize = 6;
        private const double MaxFontSize = 72;
        private const double FontSizeStep = 1;

        /// <summary>The monospace font family used to render text. Changing it re-measures the cell metrics.</summary>
        public string FontFamilyName
        {
            get => textFormat.FontFamily;
            set
            {
                if (!string.IsNullOrEmpty(value) && !string.Equals(value, textFormat.FontFamily, StringComparison.Ordinal))
                {
                    textFormat.FontFamily = value;
                    RefreshFont();
                }
            }
        }

        /// <summary>The font size in points (clamped). Zoom is expressed through this. Re-measures the metrics.</summary>
        public double FontSizePoints
        {
            get => textFormat.FontSize;
            set
            {
                float clamped = (float)Math.Clamp(value, MinFontSize, MaxFontSize);
                if (Math.Abs(clamped - textFormat.FontSize) > 0.01f)
                {
                    textFormat.FontSize = clamped;
                    RefreshFont();
                }
            }
        }

        /// <summary>The current zoom as a percentage of the default font size (100% = the baseline size).</summary>
        public int ZoomPercent => (int)Math.Round(textFormat.FontSize / BaseFontSize * 100);

        public void ZoomIn() => FontSizePoints = textFormat.FontSize + FontSizeStep;

        public void ZoomOut() => FontSizePoints = textFormat.FontSize - FontSizeStep;

        public void ResetZoom() => FontSizePoints = BaseFontSize;

        /// <summary>Raised when the font family or size (zoom) changes, so the host can persist and update status.</summary>
        public event Action? FontChanged;

        // Re-measure the monospace cell, push the new metrics, refresh the scrollbars, and repaint.
        private void RefreshFont()
        {
            metricsValid = false;
            EnsureMetrics();
            ViewChanged?.Invoke();
            canvas?.Invalidate();
            FontChanged?.Invoke();
        }

        public void SetProvider(ILineSource? newProvider)
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

        public void GoToLineColumn(int lineIndex, int column)
        {
            Ready();
            engine.GoToLineColumn(lineIndex, column);
        }

        public void SelectMatch(int lineIndex, int startColumn, int length)
        {
            Ready();
            engine.SelectMatch(lineIndex, startColumn, length);
        }

        /// <summary>Sets (or clears, with null) the highlighter that paints every Find match in the viewport.</summary>
        public void SetHighlighter(ILineHighlighter? highlighter) => engine.SetHighlighter(highlighter);

        /// <summary>Sets (or clears, with null) the persistent highlight rules painted across the whole file.</summary>
        public void SetHighlightRules(HighlightRuleSet? rules) => engine.SetHighlightRules(rules);

        /// <summary>Toggles a bookmark on the caret line; returns true if it is now bookmarked.</summary>
        public bool ToggleBookmark() => engine.ToggleBookmarkAtCaret();

        /// <summary>Moves the caret to the next bookmarked line (wrapping).</summary>
        public void GoToNextBookmark() => engine.GoToNextBookmark();

        /// <summary>Moves the caret to the previous bookmarked line (wrapping).</summary>
        public void GoToPreviousBookmark() => engine.GoToPreviousBookmark();

        /// <summary>Removes every bookmark.</summary>
        public void ClearBookmarks() => engine.ClearBookmarks();

        /// <summary>Whether any line is bookmarked.</summary>
        public bool HasBookmarks => engine.HasBookmarks;

        /// <summary>The bookmarked line indices, for the scrollbar marker margin.</summary>
        public IReadOnlyCollection<int> BookmarkLines => engine.BookmarkLines;

        /// <summary>Whether the line-number gutter is shown.</summary>
        public bool ShowLineNumbers
        {
            get => engine.ShowLineNumbers;
            set => engine.ShowLineNumbers = value;
        }

        public bool ShowWhitespace
        {
            get => engine.ShowWhitespace;
            set => engine.ShowWhitespace = value;
        }

        public void FocusCanvas() => Focus(Microsoft.UI.Xaml.FocusState.Programmatic);

        /// <summary>Copies the current selection to the clipboard (no-op when nothing is selected).</summary>
        public void CopySelection()
        {
            string? text = engine.BuildCopyText();
            if (text != null)
            {
                CopyToClipboard(text);
            }
        }

        /// <summary>Copies the selected lines (or the caret's line) prefixed with their 1-based line numbers.</summary>
        public void CopySelectionWithLineNumbers()
        {
            string? text = engine.BuildCopyTextWithLineNumbers();
            if (text != null)
            {
                CopyToClipboard(text);
            }
        }

        /// <summary>Selects the whole document.</summary>
        public void SelectAllText()
        {
            Ready();
            engine.HandleKey(NavKey.SelectAll, false);
            FocusCanvas();
        }

        #endregion

        #region Pointer

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // Let non-left buttons through (e.g. right-click, which raises the context menu).
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

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
            if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
            {
                // Ctrl + wheel zooms (changes the font size) instead of scrolling.
                if (delta > 0)
                {
                    ZoomIn();
                }
                else if (delta < 0)
                {
                    ZoomOut();
                }
            }
            else
            {
                engine.ScrollByWheelDelta(delta);
            }
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
            double dpi = canvas.Dpi > 0 ? canvas.Dpi : 96.0;
            if (metricsValid && dpi == lastMetricsDpi)
            {
                return;
            }

            if (!metricsValid)
            {
                try
                {
                    CanvasDevice device = CanvasDevice.GetSharedDevice();
                    using var probe = new CanvasTextLayout(device, new string('0', 10), textFormat, 0, 0);
                    charWidth = probe.LayoutBounds.Width / 10.0;
                    lineHeightRaw = probe.LayoutBounds.Height;
                    // Only latch the metrics once a real probe succeeds, so a transient device failure on
                    // the first call falls back for this draw but is recomputed on the next.
                    if (charWidth > 0 && lineHeightRaw > 0)
                    {
                        metricsValid = true;
                    }
                }
                catch (Exception)
                {
                    charWidth = 0;
                    lineHeightRaw = 0;
                }

                if (charWidth <= 0)
                {
                    charWidth = 8.0;
                }
                if (lineHeightRaw <= 0)
                {
                    lineHeightRaw = 18.0;
                }
            }

            // Snap the line height to a whole *device* pixel so every line's top lands on the physical pixel
            // grid. With a fractional line height, line.Y falls on sub-pixels and Win2D re-rasterizes the text
            // a pixel up or down between swap-chain buffers on each redraw — a subtle vertical jitter that is
            // visible while the caret-blink timer repaints the canvas every 530 ms.
            double dpiScale = dpi / 96.0;
            double snapped = Math.Round(lineHeightRaw * dpiScale) / dpiScale;
            lineHeight = snapped > 0 ? snapped : lineHeightRaw;
            lastMetricsDpi = dpi;

            engine.SetMetrics(charWidth, lineHeight);
        }

        private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            CanvasDrawingSession ds = args.DrawingSession;
            ds.Clear(backgroundColor);

            EnsureMetrics();
            SyncViewport();

            // The selection keeps its colour even when the canvas isn't focused (e.g. the menu has focus), so
            // it stays clearly visible; only the caret is hidden while unfocused.
            IReadOnlyList<VisibleLine> lines = engine.GetVisibleLines(hasFocusInternal, caretBlinkOn);
            foreach (VisibleLine line in lines)
            {
                // Persistent highlight-rule backgrounds (under everything), each in its own colour. Painted
                // before the Find highlight so an active search still stands out over a coloured line.
                if (line.RuleHighlights is { Count: > 0 })
                {
                    foreach (RuleHighlightRect r in line.RuleHighlights)
                    {
                        ds.FillRectangle((float)r.X, (float)line.Y, (float)r.Width, (float)lineHeight, ToColor(r.Argb));
                    }
                }

                // Highlight every Find match (under the text); the selected match is drawn over its highlight
                // by the selection layer below, so the current match reads as the selection colour.
                if (line.Matches is { Count: > 0 })
                {
                    foreach (HighlightRect h in line.Matches)
                    {
                        ds.FillRectangle((float)h.X, (float)line.Y, (float)h.Width, (float)lineHeight, matchHighlightColor);
                    }
                }

                if (line.HasSelection)
                {
                    ds.FillRectangle((float)line.SelectionX, (float)line.Y, (float)line.SelectionWidth, (float)lineHeight, selectionColor);
                }

                ds.DrawText(line.Display, new Vector2((float)line.TextX, (float)line.Y), textColor, textFormat);

                if (line.Whitespace is { Count: > 0 })
                {
                    DrawWhitespaceMarkers(ds, line);
                }

                if (line.HasCaret)
                {
                    ds.FillRectangle((float)line.CaretX, (float)line.Y, 1.0f, (float)lineHeight, caretColor);
                }
            }

            DrawGutter(ds, sender, lines);
        }

        // Overlays markers for spaces (dot), tabs (arrow) and other control chars (box) when "Show Whitespace"
        // is on; trailing space/tab runs use a stronger (reddish) colour so they stand out.
        private void DrawWhitespaceMarkers(CanvasDrawingSession ds, VisibleLine line)
        {
            float cw = (float)charWidth;
            float h = (float)lineHeight;
            float midY = (float)line.Y + h / 2f;

            foreach (WhitespaceMarker m in line.Whitespace)
            {
                float x = (float)line.TextX + m.Column * cw;
                Windows.UI.Color color = m.Trailing ? trailingWhitespaceColor : whitespaceColor;

                switch (m.Kind)
                {
                    case WhitespaceKind.Space:
                        float r = Math.Max(1.5f, cw * 0.14f);
                        ds.FillEllipse(x + cw / 2f, midY, r, r, color);
                        break;

                    case WhitespaceKind.Tab:
                        float w = m.Width * cw;
                        float x0 = x + 2f;
                        float x1 = x + w - 2f;
                        if (x1 > x0)
                        {
                            ds.DrawLine(x0, midY, x1, midY, color, 1.5f);
                            ds.DrawLine(x1 - 4f, midY - 4f, x1, midY, color, 1.5f); // arrowhead
                            ds.DrawLine(x1 - 4f, midY + 4f, x1, midY, color, 1.5f);
                        }
                        break;

                    case WhitespaceKind.Control:
                        ds.DrawRectangle(x + 1f, (float)line.Y + 2f, cw - 2f, h - 4f, controlCharColor, 1.5f);
                        break;

                    case WhitespaceKind.Lf:
                        DrawReturnMark(ds, x, (float)line.Y, cw, h, crlf: false);
                        break;

                    case WhitespaceKind.CrLf:
                        DrawReturnMark(ds, x, (float)line.Y, cw, h, crlf: true);
                        break;
                }
            }
        }

        // Draws a faint "return" mark at the end of a line for its terminator: a down-then-left arrow for LF,
        // with a small leading CR tick for CRLF so the two endings are distinguishable (e.g. in a Mixed file).
        private void DrawReturnMark(CanvasDrawingSession ds, float x, float yTop, float cw, float h, bool crlf)
        {
            Windows.UI.Color color = whitespaceColor;
            float baseX = x;
            if (crlf)
            {
                // CR tick: a short vertical bar just before the LF arrow.
                ds.FillRectangle(baseX + 1f, yTop + h * 0.30f, 2f, h * 0.40f, color);
                baseX += 4f;
            }

            float riserX = baseX + cw * 0.55f;
            float topY = yTop + h * 0.28f;
            float botY = yTop + h * 0.60f;
            float leftX = baseX + cw * 0.18f;

            ds.DrawLine(riserX, topY, riserX, botY, color, 1.5f);          // vertical riser
            ds.DrawLine(riserX, botY, leftX, botY, color, 1.5f);           // base, leftward
            ds.DrawLine(leftX, botY, leftX + 4f, botY - 4f, color, 1.5f);  // arrowhead
            ds.DrawLine(leftX, botY, leftX + 4f, botY + 4f, color, 1.5f);
        }

        // Draws the line-number gutter on top of the text (its opaque background masks any text scrolled under
        // it, since the gutter itself stays fixed while the text scrolls horizontally).
        private void DrawGutter(CanvasDrawingSession ds, CanvasControl sender, IReadOnlyList<VisibleLine> lines)
        {
            double gutterWidth = engine.GutterWidthPx;
            if (gutterWidth <= 0)
            {
                return;
            }

            float w = (float)gutterWidth;
            float h = (float)sender.Size.Height;
            ds.FillRectangle(0, 0, w, h, backgroundColor);
            ds.DrawLine(w - 0.5f, 0, w - 0.5f, h, gutterSeparatorColor, 1f);

            foreach (VisibleLine line in lines)
            {
                if (line.IsBookmarked)
                {
                    // A small dot near the gutter's left edge (numbers are right-aligned, so this stays clear).
                    float r = (float)Math.Min(lineHeight * 0.18, 4.0);
                    float cx = r + 1.5f;
                    float cy = (float)(line.Y + lineHeight / 2);
                    ds.FillEllipse(cx, cy, r, r, bookmarkColor);
                }

                string number = (line.LineIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                float x = (float)(gutterWidth - GutterRightMargin - number.Length * charWidth);
                ds.DrawText(number, new Vector2(x, (float)line.Y), gutterTextColor, textFormat);
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

        // Unpacks a 0xAARRGGBB highlight-rule colour into a Win2D Color.
        private static Color ToColor(uint argb) =>
            Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        // Resolves the surface palette from the control's resolved light/dark and the live High-Contrast
        // state, then converts each packed colour to a Win2D Color. The curated palettes and the HC mapping
        // live in the device-free CanvasPalette, which is unit-tested without a graphics device (issue #76).
        private void ApplyTheme()
        {
            bool isDark = ActualTheme == Microsoft.UI.Xaml.ElementTheme.Dark;

            bool isHighContrast = false;
            try { isHighContrast = accessibility?.HighContrast == true; } catch { /* fall back to non-HC */ }

            HighContrastColors hc = isHighContrast ? ReadHighContrastColors() : default;
            CanvasColors c = CanvasPalette.Resolve(isDark, isHighContrast, hc);

            backgroundColor = ToColor(c.Background);
            textColor = ToColor(c.Text);
            caretColor = ToColor(c.Caret);
            selectionColor = ToColor(c.Selection);
            matchHighlightColor = ToColor(c.MatchHighlight);
            gutterTextColor = ToColor(c.GutterText);
            gutterSeparatorColor = ToColor(c.GutterSeparator);
            bookmarkColor = ToColor(c.Bookmark);
            whitespaceColor = ToColor(c.Whitespace);
            trailingWhitespaceColor = ToColor(c.TrailingWhitespace);
            controlCharColor = ToColor(c.ControlChar);

            canvas?.Invalidate();
        }

        // Reads the live Windows High-Contrast system colours for CanvasPalette to map onto the surface.
        private static HighContrastColors ReadHighContrastColors() => new(
            Window: SysColor(COLOR_WINDOW),
            WindowText: SysColor(COLOR_WINDOWTEXT),
            Highlight: SysColor(COLOR_HIGHLIGHT),
            GrayText: SysColor(COLOR_GRAYTEXT),
            Hotlight: SysColor(COLOR_HOTLIGHT));

        // Reads a Win32 system colour (which Windows swaps to the High-Contrast scheme when HC is on) and
        // packs its 0x00BBGGRR COLORREF into an opaque 0xAARRGGBB value.
        private static uint SysColor(int index)
        {
            uint colorRef = GetSysColor(index);
            uint r = colorRef & 0xFF, g = (colorRef >> 8) & 0xFF, b = (colorRef >> 16) & 0xFF;
            return 0xFF000000u | (r << 16) | (g << 8) | b;
        }

        private const int COLOR_WINDOW = 5;
        private const int COLOR_WINDOWTEXT = 8;
        private const int COLOR_HIGHLIGHT = 13;
        private const int COLOR_GRAYTEXT = 17;
        private const int COLOR_HOTLIGHT = 26;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetSysColor(int nIndex);

        #endregion
    }
}
