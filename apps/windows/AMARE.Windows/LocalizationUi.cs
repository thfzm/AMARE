using Amare.Windows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Amare.Windows;

/// <summary>
/// Translates static XAML/dialog text using the English source string as the
/// resource key. Dynamic status messages should use <see cref="L10n.T"/> so
/// their placeholders can be localized safely.
/// </summary>
internal static class LocalizationUi
{
    public static void Apply(DependencyObject root)
    {
        ApplyElement(root);
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            Apply(VisualTreeHelper.GetChild(root, index));
        }
    }

    public static void ApplyDialog(ContentDialog dialog)
    {
        if (dialog.Title is string title)
        {
            dialog.Title = L10n.TranslateStatic(title);
        }
        if (dialog.PrimaryButtonText is { Length: > 0 } primary)
        {
            dialog.PrimaryButtonText = L10n.TranslateStatic(primary);
        }
        if (dialog.SecondaryButtonText is { Length: > 0 } secondary)
        {
            dialog.SecondaryButtonText = L10n.TranslateStatic(secondary);
        }
        if (dialog.CloseButtonText is { Length: > 0 } close)
        {
            dialog.CloseButtonText = L10n.TranslateStatic(close);
        }
        if (dialog.Content is DependencyObject content)
        {
            Apply(content);
        }
    }

    private static void ApplyElement(DependencyObject element)
    {
        switch (element)
        {
            case TextBlock textBlock when !string.IsNullOrEmpty(textBlock.Text):
                textBlock.Text = L10n.TranslateStatic(textBlock.Text);
                break;
            case Button button when button.Content is string text:
                button.Content = L10n.TranslateStatic(text);
                break;
            case CheckBox checkBox when checkBox.Content is string text:
                checkBox.Content = L10n.TranslateStatic(text);
                break;
            case RadioButton radio when radio.Content is string text:
                radio.Content = L10n.TranslateStatic(text);
                break;
            case TextBox textBox when !string.IsNullOrEmpty(textBox.PlaceholderText):
                textBox.PlaceholderText = L10n.TranslateStatic(textBox.PlaceholderText);
                break;
            case PasswordBox passwordBox when !string.IsNullOrEmpty(passwordBox.PlaceholderText):
                passwordBox.PlaceholderText = L10n.TranslateStatic(passwordBox.PlaceholderText);
                break;
            case ComboBox comboBox when !string.IsNullOrEmpty(comboBox.PlaceholderText):
                comboBox.PlaceholderText = L10n.TranslateStatic(comboBox.PlaceholderText);
                break;
        }

        if (ToolTipService.GetToolTip(element) is string tooltip)
        {
            ToolTipService.SetToolTip(element, L10n.TranslateStatic(tooltip));
        }
        var automationName = AutomationProperties.GetName(element);
        if (!string.IsNullOrEmpty(automationName))
        {
            AutomationProperties.SetName(element, L10n.TranslateStatic(automationName));
        }
    }
}
