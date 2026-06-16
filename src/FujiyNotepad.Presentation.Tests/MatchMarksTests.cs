namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>Tests the constant-memory bucketing of find-match positions for the scrollbar margin.</summary>
    public class MatchMarksTests
    {
        [Fact]
        public void BucketsDistinctPositions()
        {
            var m = new MatchMarks(totalLines: MatchMarks.Resolution); // 1 line per bucket
            m.Add(0);
            m.Add(0);   // same bucket -> deduped
            m.Add(10);

            Assert.Equal(2, m.Count);
            Assert.Contains(0, m.Buckets);
            Assert.Contains(10, m.Buckets);
        }

        [Fact]
        public void IsBoundedByResolution_EvenForManyDistinctLines()
        {
            // A huge file where every line matches must not retain more than Resolution buckets.
            var m = new MatchMarks(totalLines: 10_000_000);
            for (int line = 0; line < 10_000_000; line += 137)
            {
                m.Add(line);
            }

            Assert.True(m.Count <= MatchMarks.Resolution);
            Assert.True(m.IsFull || m.Count > 0);
        }

        [Fact]
        public void IsFull_StopsGrowing()
        {
            var m = new MatchMarks(totalLines: 100); // fewer lines than buckets -> fills at 100 distinct lines
            for (int line = 0; line < 100; line++)
            {
                m.Add(line);
            }
            int before = m.Count;
            m.Add(50); // already present
            Assert.Equal(before, m.Count);
        }

        [Fact]
        public void FirstAndLastLine_BucketAtTopAndBottom()
        {
            var m = new MatchMarks(totalLines: 1000);
            m.Add(0);
            m.Add(999);

            Assert.Contains(0, m.Buckets);
            Assert.Contains(MatchMarks.Resolution - 1, m.Buckets);
        }
    }
}
