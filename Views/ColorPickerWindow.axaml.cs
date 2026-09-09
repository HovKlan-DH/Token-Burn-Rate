using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace TokenBurnRate.Views;

/// <summary>
/// A small modal for picking one panel's accent color, opened from the widget's
/// right-click "Panel colors" submenu (see MainWindow.axaml's context menu).
///
/// Wraps Avalonia.Controls.ColorPicker rather than building a custom swatch grid: the
/// built-in control already gives a palette plus a custom hex/RGB entry, which is the
/// "pick another color for any of the 3 panels" ask in one control instead of two.
/// </summary>
public partial class ColorPickerWindow : Window
{
    /// <summary>Result of the dialog: null for cancel, "default" for reset, else #RRGGBB.</summary>
    private string? _result;

    public ColorPickerWindow()
    {
        InitializeComponent();

        var okButton = this.FindControl<Button>("OkButton")!;
        var cancelButton = this.FindControl<Button>("CancelButton")!;
        var resetButton = this.FindControl<Button>("ResetButton")!;

        okButton.Click += (_, _) =>
        {
            var color = this.FindControl<ColorPicker>("Picker")!.Color;
            _result = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            Close();
        };

        cancelButton.Click += (_, _) => Close();

        resetButton.Click += (_, _) =>
        {
            _result = "default";
            Close();
        };
    }

    /// <summary>
    /// Opens the picker preset to <paramref name="currentHex"/> and waits for a choice.
    /// Returns null if the user cancelled or closed the window without choosing, "default"
    /// if they asked to clear the override, or a #RRGGBB string otherwise.
    /// </summary>
    public static async Task<string?> PickAsync(Window owner, string panelLabel, string currentHex)
    {
        var window = new ColorPickerWindow();
        window.FindControl<TextBlock>("TitleText")!.Text = $"{panelLabel} accent color";

        var picker = window.FindControl<ColorPicker>("Picker")!;
        picker.Color = TryParse(currentHex) ?? Colors.Gray;

        await window.ShowDialog(owner);
        return window._result;
    }

    private static Color? TryParse(string hex)
    {
        try { return Color.Parse(hex); }
        catch (Exception) { return null; }
    }
}
