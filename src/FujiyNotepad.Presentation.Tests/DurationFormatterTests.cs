namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>Tests the compact duration formatter used by the selection timestamp-delta readout (issue #67).</summary>
    public class DurationFormatterTests
    {
        [Theory]
        [InlineData(0, "0ms")]
        [InlineData(250, "250ms")]
        [InlineData(999, "999ms")]
        [InlineData(1000, "1s")]
        [InlineData(1500, "1.500s")]
        [InlineData(30000, "30s")]
        [InlineData(90000, "1m 30s")]
        [InlineData(3600000, "1h")]
        [InlineData(3661000, "1h 1m 1s")]
        [InlineData(172800000, "2d")]
        [InlineData(-90000, "1m 30s")] // magnitude: a negative span formats like its absolute value
        public void Format_ProducesCompactDuration(long milliseconds, string expected)
        {
            Assert.Equal(expected, DurationFormatter.Format(TimeSpan.FromMilliseconds(milliseconds)));
        }
    }
}
