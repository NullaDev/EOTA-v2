using System.Security;
using System.Text.RegularExpressions;

namespace Eota.ContentCli;

public static partial class EmojiArt
{
    public static string Svg(string emoji, string top, string bottom, string style = "card", byte[]? fusionPng = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emoji);
        if (emoji.Length > 64 || !Color().IsMatch(top) || !Color().IsMatch(bottom) || style is not ("card" or "icon"))
        { throw new ArgumentException("Use a short emoji string and #RRGGBB colors."); }
        var artwork = fusionPng is null ? null : FusionImage(fusionPng, style);
        if (style == "icon")
        {
            return $"""
                <svg xmlns="http://www.w3.org/2000/svg" width="512" height="512" viewBox="0 0 512 512">
                  {artwork ?? $"<text x=\"256\" y=\"330\" text-anchor=\"middle\" font-size=\"220\" font-family=\"Segoe UI Emoji,Apple Color Emoji,Noto Color Emoji,sans-serif\">{SecurityElement.Escape(emoji)}</text>"}
                </svg>
                """ + "\n";
        }
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="512" height="512" viewBox="0 0 512 512">
              <defs><linearGradient id="bg" x2="0" y2="1"><stop stop-color="{top}"/><stop offset="1" stop-color="{bottom}"/></linearGradient></defs>
              <rect width="512" height="512" rx="32" fill="url(#bg)"/>
              <circle cx="256" cy="250" r="177" fill="#ffffff" fill-opacity=".07"/>
              <circle cx="256" cy="250" r="187" fill="none" stroke="#ffffff" stroke-opacity=".18" stroke-width="2"/>
              {artwork ?? $"<text x=\"256\" y=\"330\" text-anchor=\"middle\" font-size=\"220\" font-family=\"Segoe UI Emoji,Apple Color Emoji,Noto Color Emoji,sans-serif\">{SecurityElement.Escape(emoji)}</text>"}
            </svg>
            """ + "\n";
    }

    private static string FusionImage(byte[] png, string style)
    {
        if (png.Length < 8 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4e || png[3] != 0x47
            || png[4] != 0x0d || png[5] != 0x0a || png[6] != 0x1a || png[7] != 0x0a)
        { throw new ArgumentException("Fusion artwork must be a PNG image."); }
        var (position, size) = style == "icon" ? ("81", "350") : ("101", "310");
        return $"<image x=\"{position}\" y=\"{position}\" width=\"{size}\" height=\"{size}\" preserveAspectRatio=\"xMidYMid meet\" href=\"data:image/png;base64,{Convert.ToBase64String(png)}\"/>";
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex Color();
}
