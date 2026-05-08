using System;
using System.Text;

namespace BackstabTheTrade;

internal static class ClientTextMap
{
    private static readonly string[] ContextMenuTradeLabels =
    {
        "Trade",
        "\u4EA4\u6613",
        "\u30C8\u30EC\u30FC\u30C9\u306B\u51FA\u3059",
        "\u30C8\u30EC\u30FC\u30C9",
    };

    private static readonly string[] TooFarAwayMessages =
    {
        "Too far away.",
        "\u8DDD\u96E2\u592A\u9060\u3002",
        "\u8DDD\u79BB\u592A\u8FDC\u3002",
        "\u8DDD\u96E2\u304C\u9060\u3059\u304E\u307E\u3059\u3002",
    };

    private static readonly string[] SpecifyQuantityPrompts =
    {
        "Specify quantity.",
        "\u8ACB\u8A2D\u7F6E\u6578\u91CF\u3002",
        "\u8BF7\u8BBE\u7F6E\u6570\u91CF\u3002",
        "\u53D7\u3051\u6E21\u3059\u6570\u3092\u6307\u5B9A\u3057\u3066\u304F\u3060\u3055\u3044\u3002",
    };

    public static bool IsTradeContextMenuLabel(string label)
    {
        return MatchesAny(label, ContextMenuTradeLabels);
    }

    public static bool IsTooFarAwayMessage(string text)
    {
        return MatchesAny(text, TooFarAwayMessages);
    }

    public static bool IsSpecifyQuantityPrompt(string text)
    {
        return MatchesAny(text, SpecifyQuantityPrompts);
    }

    private static bool MatchesAny(string value, string[] candidates)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalizedValue = NormalizeForMatch(value);

        foreach (var candidate in candidates)
        {
            var normalizedCandidate = NormalizeForMatch(candidate);
            if (normalizedValue.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string NormalizeForMatch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (!char.IsControl(ch))
                sb.Append(ch);
        }

        return sb.ToString();
    }
}
