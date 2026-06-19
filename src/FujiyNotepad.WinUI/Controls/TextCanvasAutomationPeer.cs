using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FujiyNotepad.WinUI.Controls
{
    /// <summary>
    /// UI Automation peer that exposes the <see cref="TextCanvas"/>'s file content to screen readers
    /// (Narrator / NVDA) — issue #75. The canvas paints text directly onto a Win2D surface, so without a peer
    /// assistive tech can focus the viewer but read nothing.
    ///
    /// Files can be huge, so rather than the whole file the peer exposes a bounded window — the raw text of the
    /// line the caret is on — through the read-only UIA Value pattern, and raises a value-changed event as the
    /// caret moves, so the user hears each line while navigating. The position is offered as the peer's name.
    /// </summary>
    internal sealed partial class TextCanvasAutomationPeer : FrameworkElementAutomationPeer, IValueProvider
    {
        private readonly TextCanvas owner;
        private string lastValue = string.Empty;

        public TextCanvasAutomationPeer(TextCanvas owner) : base(owner) => this.owner = owner;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(TextCanvas);

        // Prefer any AutomationProperties.Name set in XAML; otherwise describe the surface and the caret line.
        protected override string GetNameCore()
        {
            string name = base.GetNameCore();
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            (int line, int column) = owner.GetAccessibleCaretPosition();
            return $"File contents, line {line}, column {column}";
        }

        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);

        // ----- IValueProvider: the caret line's text, read-only (the viewer never edits) -----

        public bool IsReadOnly => true;

        public string Value => owner.GetAccessibleText();

        public void SetValue(string value) =>
            throw new System.InvalidOperationException("The text viewer is read-only.");

        // Raises a UIA value-changed event so a screen reader reads the new caret line when the caret moves to
        // a different line. Called by the canvas only while a peer exists (an automation client is attached).
        internal void NotifyCaretLineChanged()
        {
            string newValue = owner.GetAccessibleText();
            RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, lastValue, newValue);
            lastValue = newValue;
        }
    }
}
