using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Zen;

public partial class ColorPickerWindow : Window
{
    private bool _updating;
    private Color _selectedColor;

    public string SelectedHex => _selectedColor.A == byte.MaxValue
        ? $"#{_selectedColor.R:X2}{_selectedColor.G:X2}{_selectedColor.B:X2}"
        : $"#{_selectedColor.A:X2}{_selectedColor.R:X2}{_selectedColor.G:X2}{_selectedColor.B:X2}";

    public ColorPickerWindow(string initialColor)
    {
        InitializeComponent();
        _selectedColor = TryParseColor(initialColor, out var color) ? color : Color.FromRgb(29, 34, 44);
        ApplyColor(_selectedColor);
    }

    private void ApplyColor(Color color)
    {
        _updating = true;
        _selectedColor = color;
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        RedValue.Text = color.R.ToString();
        GreenValue.Text = color.G.ToString();
        BlueValue.Text = color.B.ToString();
        HexInput.Text = SelectedHex;
        Preview.Background = new SolidColorBrush(color);
        _updating = false;
    }

    private void RgbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || RedSlider is null || GreenSlider is null || BlueSlider is null) return;
        ApplyColor(Color.FromArgb(_selectedColor.A, (byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value));
    }

    private void HexInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !TryParseColor(HexInput.Text, out var color)) return;
        ApplyColor(color);
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && TryParseColor(value, out var color)) ApplyColor(color);
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value) || value[0] != '#' || value.Length is not (4 or 5 or 7 or 9) || !value[1..].All(Uri.IsHexDigit))
            {
                color = default;
                return false;
            }
            color = (Color)ColorConverter.ConvertFromString(value)!;
            return true;
        }
        catch
        {
            color = default;
            return false;
        }
    }

    private void UseColor_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
