using System.Text;
using System.Text.RegularExpressions;

namespace PlayerokBridge.Core;

/// <summary>Приведение текста к единому виду для сравнения (порт norm_text из bot.py).</summary>
public static class TextNorm
{
    private static readonly Regex ZeroWidth = new("[​‌‍⁠﻿­]", RegexOptions.Compiled);

    // Латинские буквы, похожие на русские: в названиях лотов их часто смешивают.
    private const string LookFrom = "abcehkmoptxy";
    private const string LookTo = "авсенкмортху";

    public static string Normalize(string? value)
    {
        var text = (value ?? "").Normalize(NormalizationForm.FormKC);
        text = ZeroWidth.Replace(text, "").ToLowerInvariant().Replace('ё', 'е');
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var i = LookFrom.IndexOf(ch);
            sb.Append(i >= 0 ? LookTo[i] : ch);
        }
        return sb.ToString();
    }
}
