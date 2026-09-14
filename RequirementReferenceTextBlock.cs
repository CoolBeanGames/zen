using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Zen;

public sealed partial class RequirementReferenceTextBlock : TextBlock
{
    public static readonly DependencyProperty NoteTextProperty = DependencyProperty.Register(
        nameof(NoteText), typeof(string), typeof(RequirementReferenceTextBlock),
        new FrameworkPropertyMetadata(string.Empty, OnNoteTextChanged));

    public string NoteText
    {
        get => (string)GetValue(NoteTextProperty);
        set => SetValue(NoteTextProperty, value);
    }

    private static void OnNoteTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not RequirementReferenceTextBlock block) return;
        block.Inlines.Clear();
        var text = args.NewValue as string ?? string.Empty;
        var cursor = 0;
        foreach (Match match in RequirementReferenceRegex().Matches(text))
        {
            if (match.Index > cursor) block.Inlines.Add(new Run(text[cursor..match.Index]));
            block.Inlines.Add(new Run(match.Value) { Foreground = new SolidColorBrush(Color.FromRgb(255, 96, 112)), FontWeight = FontWeights.SemiBold });
            cursor = match.Index + match.Length;
        }
        if (cursor < text.Length) block.Inlines.Add(new Run(text[cursor..]));
    }

    [GeneratedRegex(@"\[(?:[1-9]\d*)\]")]
    private static partial Regex RequirementReferenceRegex();
}
