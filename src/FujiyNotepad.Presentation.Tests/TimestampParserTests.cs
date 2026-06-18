namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the leading-timestamp parser (issue #67): ISO 8601 / "yyyy-MM-dd HH:mm:ss[.fff]", syslog, bare
    /// time, bracketed variants, leading whitespace, and rejection of non-timestamp lines.
    /// </summary>
    public class TimestampParserTests
    {
        [Theory]
        [InlineData("2024-01-02T15:04:05Z trailing")]
        [InlineData("2024-01-02 15:04:05 trailing")]
        [InlineData("[2024-01-02 15:04:05] trailing")]
        [InlineData("   2024-01-02 15:04:05 indented")]
        public void Parses_IsoDateTime_AsUtc(string line)
        {
            Assert.True(TimestampParser.TryParseLeading(line, out DateTimeOffset value));
            Assert.Equal(new DateTimeOffset(2024, 1, 2, 15, 4, 5, TimeSpan.Zero), value);
        }

        [Fact]
        public void Parses_FractionalSeconds()
        {
            Assert.True(TimestampParser.TryParseLeading("2024-01-02T15:04:05.250Z x", out DateTimeOffset value));
            Assert.Equal(new DateTimeOffset(2024, 1, 2, 15, 4, 5, 250, TimeSpan.Zero), value);
        }

        [Fact]
        public void Parses_ExplicitTimezoneOffset()
        {
            Assert.True(TimestampParser.TryParseLeading("2024-01-02T15:04:05+02:00 x", out DateTimeOffset value));
            // Equal as an instant to 13:04:05 UTC.
            Assert.Equal(new DateTimeOffset(2024, 1, 2, 13, 4, 5, TimeSpan.Zero), value.ToUniversalTime());
        }

        [Theory]
        // log4j / Python logging emit the fractional seconds after a comma (yyyy-MM-dd HH:mm:ss,SSS).
        [InlineData("2026-06-17 22:05:51,119 INFO SnapshotManager [Thread-33]: msg")]
        [InlineData("[2026-06-17 22:05:51,119] INFO")]
        [InlineData("2026-06-17T22:05:51,119 INFO")]
        public void Parses_CommaFractionalSeconds(string line)
        {
            Assert.True(TimestampParser.TryParseLeading(line, out DateTimeOffset value));
            Assert.Equal(new DateTimeOffset(2026, 6, 17, 22, 5, 51, 119, TimeSpan.Zero), value);
        }

        [Fact]
        public void Parses_BareTime_WithCommaFraction()
        {
            Assert.True(TimestampParser.TryParseLeading("15:04:05,250 message", out DateTimeOffset value));
            Assert.Equal(250, value.Millisecond);
        }

        [Theory]
        [InlineData("Jan 2 15:04:05 host app: msg")]
        [InlineData("Jan  2 15:04:05 host app: msg")] // syslog pads a single-digit day with two spaces
        public void Parses_Syslog_WithCurrentYearAssumed(string line)
        {
            Assert.True(TimestampParser.TryParseLeading(line, out DateTimeOffset value));
            Assert.Equal(1, value.Month);
            Assert.Equal(2, value.Day);
            Assert.Equal(15, value.Hour);
            Assert.Equal(4, value.Minute);
            Assert.Equal(5, value.Second);
        }

        [Theory]
        [InlineData("15:04:05.250 message")]
        [InlineData("[15:04:05.250] message")]
        public void Parses_BareTime_WithFraction(string line)
        {
            Assert.True(TimestampParser.TryParseLeading(line, out DateTimeOffset value));
            Assert.Equal(15, value.Hour);
            Assert.Equal(4, value.Minute);
            Assert.Equal(5, value.Second);
            Assert.Equal(250, value.Millisecond);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("INFO  application starting up")]
        [InlineData("12345 not a timestamp")]
        [InlineData("99:99:99 invalid time")]
        [InlineData("[2024-01-02 15:04:05 unclosed bracket")]
        public void Rejects_NonTimestampLines(string? line)
        {
            Assert.False(TimestampParser.TryParseLeading(line, out _));
        }

        [Fact]
        public void Delta_BetweenTwoParsedLines_IsTheTimeDifference()
        {
            Assert.True(TimestampParser.TryParseLeading("2024-01-02 15:04:05 start", out DateTimeOffset a));
            Assert.True(TimestampParser.TryParseLeading("2024-01-02 15:06:35 end", out DateTimeOffset b));

            Assert.Equal(TimeSpan.FromSeconds(150), b - a);
        }

        [Fact]
        public void Delta_BetweenCommaMillisecondLines_KeepsSubSecondPrecision()
        {
            // Two events in the same second differing only by their comma-milliseconds: the delta must keep the
            // sub-second precision instead of collapsing to 0 (the regression this fixes).
            Assert.True(TimestampParser.TryParseLeading("2026-06-17 22:05:51,119 a", out DateTimeOffset a));
            Assert.True(TimestampParser.TryParseLeading("2026-06-17 22:05:51,500 b", out DateTimeOffset b));

            Assert.Equal(TimeSpan.FromMilliseconds(381), b - a);
        }

        [Fact]
        public void Delta_BetweenTwoBareTimes_CancelsTheSyntheticDate()
        {
            Assert.True(TimestampParser.TryParseLeading("15:04:05 a", out DateTimeOffset a));
            Assert.True(TimestampParser.TryParseLeading("15:04:10 b", out DateTimeOffset b));

            Assert.Equal(TimeSpan.FromSeconds(5), b - a);
        }
    }
}
