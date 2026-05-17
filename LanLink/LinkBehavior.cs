using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace LanLink;

/// <summary>
/// Attached behavior that parses text for URLs and renders them as clickable
/// Hyperlinks inside a TextBlock.  Non-URL text becomes plain Run elements.
/// </summary>
public static partial class LinkBehavior
{
    // Matches http://, https://, and www. URLs.
    [GeneratedRegex(
        @"(https?://[^\s<>""')\]]+|www\.[^\s<>""')\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    // ------------------------------------------------------------------ attached property

    public static readonly DependencyProperty FormattedTextProperty =
        DependencyProperty.RegisterAttached(
            "FormattedText",
            typeof(string),
            typeof(LinkBehavior),
            new PropertyMetadata(null, OnFormattedTextChanged));

    public static string? GetFormattedText(DependencyObject obj)
        => (string?)obj.GetValue(FormattedTextProperty);

    public static void SetFormattedText(DependencyObject obj, string? value)
        => obj.SetValue(FormattedTextProperty, value);

    // ------------------------------------------------------------------ logic

    private static void OnFormattedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock) return;

        textBlock.Inlines.Clear();

        string? text = e.NewValue as string;
        if (string.IsNullOrEmpty(text))
            return;

        var matches = UrlRegex().Matches(text);

        if (matches.Count == 0)
        {
            textBlock.Inlines.Add(new Run(text));
            return;
        }

        int lastIndex = 0;

        foreach (Match match in matches)
        {
            // Add text before this URL.
            if (match.Index > lastIndex)
            {
                textBlock.Inlines.Add(new Run(text[lastIndex..match.Index]));
            }

            // Build the URI (add scheme if missing for www. links).
            string url = match.Value;
            string navigateUri = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? url
                : "http://" + url;

            var hyperlink = new Hyperlink(new Run(url))
            {
                NavigateUri = new Uri(navigateUri, UriKind.Absolute),
                Foreground = Brushes.DodgerBlue,
                Cursor = Cursors.Hand
            };
            hyperlink.TextDecorations = TextDecorations.Underline;
            hyperlink.RequestNavigate += Hyperlink_RequestNavigate;

            textBlock.Inlines.Add(hyperlink);

            lastIndex = match.Index + match.Length;
        }

        // Add any remaining text after the last URL.
        if (lastIndex < text.Length)
        {
            textBlock.Inlines.Add(new Run(text[lastIndex..]));
        }
    }

    private static void Hyperlink_RequestNavigate(object sender,
        System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch { }

        e.Handled = true;
    }
}
