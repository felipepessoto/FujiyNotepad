namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>Tests the bookmarked-line set: toggling, membership, ordering, and next/previous wrap-around.</summary>
    public class BookmarkSetTests
    {
        [Fact]
        public void Toggle_AddsThenRemoves()
        {
            var b = new BookmarkSet();

            Assert.True(b.Toggle(5));   // added
            Assert.True(b.Contains(5));
            Assert.Equal(1, b.Count);

            Assert.False(b.Toggle(5));  // removed
            Assert.False(b.Contains(5));
            Assert.Equal(0, b.Count);
        }

        [Fact]
        public void Lines_AreAscending()
        {
            var b = new BookmarkSet();
            b.Toggle(7);
            b.Toggle(2);
            b.Toggle(5);

            Assert.Equal(new[] { 2, 5, 7 }, b.Lines);
        }

        [Fact]
        public void Clear_RemovesAll()
        {
            var b = new BookmarkSet();
            b.Toggle(1);
            b.Toggle(2);

            b.Clear();

            Assert.Equal(0, b.Count);
        }

        [Fact]
        public void NextAfter_ReturnsNextHigher()
        {
            var b = new BookmarkSet();
            b.Toggle(2);
            b.Toggle(5);
            b.Toggle(9);

            Assert.Equal(5, b.NextAfter(2)); // strictly greater than current line
            Assert.Equal(9, b.NextAfter(6));
        }

        [Fact]
        public void NextAfter_WrapsToFirst()
        {
            var b = new BookmarkSet();
            b.Toggle(2);
            b.Toggle(5);

            Assert.Equal(2, b.NextAfter(5));  // on/after the last -> wrap to first
            Assert.Equal(2, b.NextAfter(100));
        }

        [Fact]
        public void PreviousBefore_ReturnsPreviousLower()
        {
            var b = new BookmarkSet();
            b.Toggle(2);
            b.Toggle(5);
            b.Toggle(9);

            Assert.Equal(5, b.PreviousBefore(9));
            Assert.Equal(2, b.PreviousBefore(5));
        }

        [Fact]
        public void PreviousBefore_WrapsToLast()
        {
            var b = new BookmarkSet();
            b.Toggle(2);
            b.Toggle(5);

            Assert.Equal(5, b.PreviousBefore(2));  // on/before the first -> wrap to last
            Assert.Equal(5, b.PreviousBefore(0));
        }

        [Fact]
        public void Navigation_EmptySet_ReturnsNull()
        {
            var b = new BookmarkSet();

            Assert.Null(b.NextAfter(3));
            Assert.Null(b.PreviousBefore(3));
        }

        [Fact]
        public void SingleBookmark_NavigationWrapsToItself()
        {
            var b = new BookmarkSet();
            b.Toggle(4);

            Assert.Equal(4, b.NextAfter(4));
            Assert.Equal(4, b.PreviousBefore(4));
        }
    }
}
