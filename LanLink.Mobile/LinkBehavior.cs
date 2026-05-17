using System.Text.RegularExpressions;

namespace LanLink;

/// <summary>
/// Attached behavior that parses text for URLs and renders them as tappable
/// Spans inside a Label's FormattedText. Non-URL text becomes plain spans.
/// </summary>
public static partial class LinkBehavior
{
    // Matches http://, https://, and www. URLs.
    [GeneratedRegex(
        @"(https?://[^\s<>""')\]]+|www\.[^\s<>""')\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    // ------------------------------------------------------------------ attached property

    public static readonly BindableProperty FormattedTextProperty =
        BindableProperty.CreateAttached(
            "FormattedText",
            typeof(string),
            typeof(LinkBehavior),
            null,
            propertyChanged: OnFormattedTextChanged);

    public static string? GetFormattedText(BindableObject obj)
        => (string?)obj.GetValue(FormattedTextProperty);

    public static void SetFormattedText(BindableObject obj, string? value)
        => obj.SetValue(FormattedTextProperty, value);

    // ------------------------------------------------------------------ attached color property

    public static readonly BindableProperty TextColorProperty =
        BindableProperty.CreateAttached(
            "TextColor",
            typeof(Color),
            typeof(LinkBehavior),
            Colors.Gray,
            propertyChanged: OnTextColorChanged);

    public static Color GetTextColor(BindableObject obj)
        => (Color)obj.GetValue(TextColorProperty);

    public static void SetTextColor(BindableObject obj, Color value)
        => obj.SetValue(TextColorProperty, value);

    // ------------------------------------------------------------------ logic

    private static void OnTextColorChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not Label label) return;
        // Re-apply formatted text with new color.
        string? text = GetFormattedText(label);
        if (text is not null)
            ApplyFormattedText(label, text);
    }

    private static void OnFormattedTextChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not Label label) return;
        string? text = newValue as string;
        ApplyFormattedText(label, text);
    }

    private static void ApplyFormattedText(Label label, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            label.FormattedText = null;
            label.Text = "";
            return;
        }

        var matches = UrlRegex().Matches(text);

        if (matches.Count == 0)
        {
            // No URLs — use plain text (faster).
            label.FormattedText = null;
            label.Text = text;
            return;
        }

        var formatted = new FormattedString();
        var baseColor = GetTextColor(label);
        int lastIndex = 0;

        foreach (Match match in matches)
        {
            // Add text before this URL.
            if (match.Index > lastIndex)
            {
                formatted.Spans.Add(new Span
                {
                    Text = text[lastIndex..match.Index],
                    TextColor = baseColor
                });
            }

            // Build the URI (add scheme if missing for www. links).
            string url = match.Value;
            string navigateUri = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? url
                : "http://" + url;

            var linkSpan = new Span
            {
                Text = url,
                TextColor = Colors.DodgerBlue,
                TextDecorations = TextDecorations.Underline
            };

            var tap = new TapGestureRecognizer();
            string capturedUri = navigateUri; // capture for closure
            tap.Tapped += async (_, _) =>
            {
                try
                {
                    await Launcher.OpenAsync(new Uri(capturedUri));
                }
                catch { /* best effort */ }
            };
            linkSpan.GestureRecognizers.Add(tap);

            formatted.Spans.Add(linkSpan);
            lastIndex = match.Index + match.Length;
        }

        // Add any remaining text after the last URL.
        if (lastIndex < text.Length)
        {
            formatted.Spans.Add(new Span
            {
                Text = text[lastIndex..],
                TextColor = baseColor
            });
        }

        label.FormattedText = formatted;
    }
}
