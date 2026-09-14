using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Zen;

public sealed partial class RequirementReferenceEditor : RichTextBox
{
    private bool _syncing;

    public static readonly DependencyProperty NoteTextProperty = DependencyProperty.Register(
        nameof(NoteText), typeof(string), typeof(RequirementReferenceEditor),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnNoteTextChanged));

    public string NoteText
    {
        get => (string)GetValue(NoteTextProperty);
        set => SetValue(NoteTextProperty, value);
    }

    public RequirementReferenceEditor()
    {
        Document = new FlowDocument(new Paragraph()) { PagePadding = new Thickness(0) };
        TextChanged += (_, _) => SyncFromEditor();
    }

    private static void OnNoteTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not RequirementReferenceEditor editor || editor._syncing) return;
        var value = args.NewValue as string ?? string.Empty;
        if (editor.GetPlainText() == value) { editor.ApplyReferenceColors(); return; }
        editor._syncing = true;
        new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text = value;
        editor._syncing = false;
        editor.ApplyReferenceColors();
    }

    private void SyncFromEditor()
    {
        if (_syncing) return;
        _syncing = true;
        SetCurrentValue(NoteTextProperty, GetPlainText());
        _syncing = false;
        ApplyReferenceColors();
    }

    private string GetPlainText() => new TextRange(Document.ContentStart, Document.ContentEnd).Text.TrimEnd('\r', '\n');

    private void ApplyReferenceColors()
    {
        var all = new TextRange(Document.ContentStart, Document.ContentEnd);
        all.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(244, 246, 250)));
        var text = GetPlainText();
        foreach (Match match in RequirementReferenceRegex().Matches(text))
        {
            var start = PositionAtTextOffset(match.Index);
            var end = PositionAtTextOffset(match.Index + match.Length);
            if (start is null || end is null) continue;
            var range = new TextRange(start, end);
            range.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(255, 96, 112)));
            range.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
        }
    }

    private TextPointer? PositionAtTextOffset(int offset)
    {
        var position = Document.ContentStart;
        var remaining = offset;
        while (position is not null)
        {
            if (position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var run = position.GetTextInRun(LogicalDirection.Forward);
                if (remaining <= run.Length) return position.GetPositionAtOffset(remaining);
                remaining -= run.Length;
            }
            position = position.GetNextContextPosition(LogicalDirection.Forward);
        }
        return Document.ContentEnd;
    }

    [GeneratedRegex(@"\[(?:[1-9]\d*)\]")]
    private static partial Regex RequirementReferenceRegex();
}
