namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the device-free tail decision logic (issue #28): the grow / shrink / unchanged transition over
    /// successive lengths, and the sticky-bottom follow decision.
    /// </summary>
    public class TailControllerTests
    {
        [Fact]
        public void Observe_DetectsGrowShrinkAndUnchanged_AndTracksLength()
        {
            var c = new TailController(100);

            Assert.Equal(TailChange.None, c.Observe(100));
            Assert.Equal(TailChange.Grew, c.Observe(150));
            Assert.Equal(150, c.LastLength);
            Assert.Equal(TailChange.None, c.Observe(150));
            Assert.Equal(TailChange.Shrunk, c.Observe(20)); // truncation / rotation
            Assert.Equal(20, c.LastLength);
            Assert.Equal(TailChange.Grew, c.Observe(30));   // grows again from the new baseline
        }

        [Theory]
        [InlineData(10, 10, true)]   // exactly at the bottom -> stick to the new end
        [InlineData(12, 10, true)]   // clamped past the bottom -> stick
        [InlineData(3, 10, false)]   // scrolled up to read history -> leave the user alone
        [InlineData(0, 0, true)]     // file shorter than the viewport -> at the bottom
        public void ShouldStickToBottom(int firstVisibleLine, int maxFirstLine, bool expected)
        {
            Assert.Equal(expected, TailController.ShouldStickToBottom(firstVisibleLine, maxFirstLine));
        }
    }
}
