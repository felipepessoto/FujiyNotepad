using FujiyNotepad.Presentation;

namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>Tests the pure rule that decides whether a selection highlights its occurrences (issue #130).</summary>
    public class SelectionHighlightPolicyTests
    {
        [Fact]
        public void Word_OnSingleLine_ReturnsTerm()
        {
            Assert.Equal("error", SelectionHighlightPolicy.TermFor("error", isSingleLine: true));
        }

        [Fact]
        public void MultiLineSelection_ReturnsNull()
        {
            Assert.Null(SelectionHighlightPolicy.TermFor("error", isSingleLine: false));
        }

        [Fact]
        public void NullSelection_ReturnsNull()
        {
            Assert.Null(SelectionHighlightPolicy.TermFor(null, isSingleLine: true));
        }

        [Fact]
        public void EmptySelection_ReturnsNull()
        {
            Assert.Null(SelectionHighlightPolicy.TermFor("", isSingleLine: true));
        }

        [Theory]
        [InlineData("   ")]
        [InlineData("\t")]
        [InlineData(" \t ")]
        public void WhitespaceOnlySelection_ReturnsNull(string text)
        {
            Assert.Null(SelectionHighlightPolicy.TermFor(text, isSingleLine: true));
        }

        [Fact]
        public void TermWithSurroundingSpaces_IsKept()
        {
            // A selection that contains non-whitespace plus spaces is still a valid term (highlight it verbatim).
            Assert.Equal(" err ", SelectionHighlightPolicy.TermFor(" err ", isSingleLine: true));
        }

        [Fact]
        public void SingleChar_AtMinLength_ReturnsTerm()
        {
            Assert.Equal("x", SelectionHighlightPolicy.TermFor("x", isSingleLine: true));
        }

        [Fact]
        public void TermAtMaxLength_ReturnsTerm()
        {
            string term = new string('a', SelectionHighlightPolicy.MaxLength);
            Assert.Equal(term, SelectionHighlightPolicy.TermFor(term, isSingleLine: true));
        }

        [Fact]
        public void TermOverMaxLength_ReturnsNull()
        {
            string term = new string('a', SelectionHighlightPolicy.MaxLength + 1);
            Assert.Null(SelectionHighlightPolicy.TermFor(term, isSingleLine: true));
        }
    }
}
