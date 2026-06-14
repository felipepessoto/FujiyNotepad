using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using FujiyNotepad.UI.Controls;
using FujiyNotepad.Core;
using Xunit;

namespace FujiyNotepad.UI.Tests
{
    /// <summary>
    /// Exercises the real <see cref="TextView"/> rendering path off-screen (no display required) via
    /// <see cref="RenderTargetBitmap"/>. This catches exceptions in OnRender/measure/arrange and verifies
    /// content is actually drawn — the parts unit tests on the pure helpers cannot cover.
    /// </summary>
    public class TextViewRenderTests
    {
        private static void RunSta(Action action)
        {
            Exception? captured = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { captured = ex; }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (captured != null)
            {
                throw new InvalidOperationException("STA render action failed: " + captured.Message, captured);
            }
        }

        private static async Task<LineProvider> BuildProviderAsync(string content)
        {
            var source = new InMemoryByteSource(content);
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            return new LineProvider(source, indexer);
        }

        private static int Render(TextView view, int width, int height)
        {
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            view.UpdateLayout();

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(view);

            int stride = width * 4;
            var pixels = new byte[height * stride];
            rtb.CopyPixels(pixels, stride, 0);

            int nonWhite = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                // Pbgra32: B, G, R, A. Background is white, drawn text/selection is darker.
                if (pixels[i] < 250 || pixels[i + 1] < 250 || pixels[i + 2] < 250)
                {
                    nonWhite++;
                }
            }
            return nonWhite;
        }

        private static string ManyLines(int count)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                sb.Append("Line ").Append(i).Append(" : the quick brown fox\n");
            }
            return sb.ToString();
        }

        [Fact]
        public async Task RendersContent_WithoutThrowing_AndDrawsPixels()
        {
            LineProvider provider = await BuildProviderAsync(ManyLines(100));

            RunSta(() =>
            {
                var view = new TextView();
                view.SetProvider(provider);

                int nonWhite = Render(view, 800, 600);

                Assert.True(view.CharWidthPx > 0, "metrics should be computed after render");
                Assert.True(view.FullyVisibleLineCount > 1, "viewport should span multiple lines");
                Assert.True(nonWhite > 200, $"expected drawn text pixels, got {nonWhite}");
            });
        }

        [Fact]
        public void TextView_UsesIBeamCursor()
        {
            RunSta(() => Assert.Equal(Cursors.IBeam, new TextView().Cursor));
        }

        [Fact]
        public void EmptyAndNullProvider_RenderWithoutThrowing()
        {
            RunSta(() =>
            {
                var view = new TextView();
                Render(view, 400, 300); // no provider set

                view.SetProvider(null);
                Render(view, 400, 300);
            });
        }

        [Fact]
        public async Task GoToLine_SetsFirstVisibleLineAndCaret()
        {
            LineProvider provider = await BuildProviderAsync(ManyLines(500));

            RunSta(() =>
            {
                var view = new TextView();
                view.SetProvider(provider);
                Render(view, 800, 600); // establish metrics/viewport

                view.GoToLine(250);
                Assert.Equal(250, view.FirstVisibleLine);
                Assert.Equal(250, view.CaretPosition.Line);

                Render(view, 800, 600); // re-render at the new position must not throw
            });
        }

        [Fact]
        public async Task SelectMatch_SetsSelectionRange_AndRenders()
        {
            LineProvider provider = await BuildProviderAsync(ManyLines(100));

            RunSta(() =>
            {
                var view = new TextView();
                view.SetProvider(provider);
                Render(view, 800, 600);

                view.SelectMatch(40, 5, 3);
                Assert.Equal(40, view.CaretPosition.Line);
                Assert.Equal(8, view.CaretPosition.Column); // start 5 + length 3

                int nonWhite = Render(view, 800, 600);
                Assert.True(nonWhite > 200);
            });
        }
    }
}
