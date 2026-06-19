namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Device-pixel snapping for the text surface. Keeping the line height — and therefore every line's top —
    /// on a whole physical pixel is what stops the text jittering up and down by a pixel as the caret-blink
    /// timer repaints the canvas (issues #113, resurfaced while testing #75). The math is pure so the stability
    /// guarantee is unit-tested instead of living only inside the Win2D control.
    /// </summary>
    public static class PixelSnap
    {
        /// <summary>
        /// Rounds <paramref name="valueDip"/> (a length in device-independent pixels) so that it spans a whole
        /// number of physical pixels at the given <paramref name="dpiScale"/> (1.0 = 96 DPI, 1.5 = 144 DPI, …).
        /// The result, multiplied by the scale, is an integer; snapping an already-snapped value is a no-op.
        /// A non-positive scale (or a value that would snap to zero) returns the input unchanged.
        /// </summary>
        public static double SnapToDevicePixels(double valueDip, double dpiScale)
        {
            if (dpiScale <= 0)
            {
                return valueDip;
            }

            double snapped = System.Math.Round(valueDip * dpiScale) / dpiScale;
            return snapped > 0 ? snapped : valueDip;
        }
    }
}
