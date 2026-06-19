namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the device-pixel snapping that keeps the text surface vertically stable — a fractional line height
    /// makes line tops land on sub-pixels, so Win2D re-rasterizes the text a pixel up/down between redraws
    /// (the caret-blink jitter, #113, resurfaced while testing #75). These lock in that the snapped value spans
    /// a whole number of physical pixels and is stable under repeated snapping.
    /// </summary>
    public class PixelSnapTests
    {
        [Theory]
        [InlineData(18.6, 1.0)]   // 100% DPI
        [InlineData(18.6, 1.25)]  // 125%
        [InlineData(18.6, 1.5)]   // 150%
        [InlineData(18.6, 2.0)]   // 200%
        [InlineData(23.3333, 1.5)]
        public void SnapToDevicePixels_ResultIsAWholeNumberOfPhysicalPixels(double valueDip, double scale)
        {
            double snapped = PixelSnap.SnapToDevicePixels(valueDip, scale);

            double physical = snapped * scale;
            Assert.Equal(System.Math.Round(physical), physical, precision: 6);
        }

        [Fact]
        public void SnapToDevicePixels_AtScaleOne_RoundsToWholeDip()
        {
            Assert.Equal(19.0, PixelSnap.SnapToDevicePixels(18.6, 1.0));
            Assert.Equal(18.0, PixelSnap.SnapToDevicePixels(18.4, 1.0));
        }

        [Theory]
        [InlineData(18.6, 1.5)]
        [InlineData(20.0, 1.25)]
        [InlineData(17.3, 2.0)]
        public void SnapToDevicePixels_IsIdempotent(double valueDip, double scale)
        {
            double once = PixelSnap.SnapToDevicePixels(valueDip, scale);
            double twice = PixelSnap.SnapToDevicePixels(once, scale);

            Assert.Equal(once, twice, precision: 9);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        public void SnapToDevicePixels_NonPositiveScale_ReturnsInput(double scale)
        {
            Assert.Equal(18.6, PixelSnap.SnapToDevicePixels(18.6, scale));
        }

        [Fact]
        public void SnapToDevicePixels_StaysPositive()
        {
            // A tiny value that would round to zero keeps the (positive) input rather than collapsing to 0.
            double snapped = PixelSnap.SnapToDevicePixels(0.2, 1.0);

            Assert.True(snapped > 0);
        }
    }
}
