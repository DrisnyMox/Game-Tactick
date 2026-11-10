using UnityEngine;

/// <summary>
/// Надпись IMGUI из кусков разного цвета без тегов &lt;color&gt;: IMGUI разбирает теги в каждом вызове GUI.Label
/// и выделяет на это память каждый кадр. Куски рисуются подряд слева направо, у каждого свой цвет.
/// Ширины кусков меряются один раз и заново, только когда меняется размер шрифта стиля.
/// </summary>
public class ColoredLine
{
    readonly string[] parts;
    readonly Color[] colors;
    readonly float[] widths;
    int measuredFontSize = -1;
    float totalWidth;
    static readonly GUIContent Content = new();

    public ColoredLine(int count)
    {
        parts = new string[count];
        colors = new Color[count];
        widths = new float[count];
    }

    public void Set(int index, string text, Color color)
    {
        parts[index] = text;
        colors[index] = color;
        measuredFontSize = -1;
    }

    /// <summary>Ширина всей надписи этим стилем. Звать только внутри OnGUI: CalcSize работает только там</summary>
    public float Width(GUIStyle style)
    {
        if (style.fontSize != measuredFontSize)
        {
            measuredFontSize = style.fontSize;
            totalWidth = 0f;
            for (int i = 0; i < parts.Length; i++)
            {
                Content.text = parts[i];
                widths[i] = style.CalcSize(Content).x;
                totalWidth += widths[i];
            }
        }
        return totalWidth;
    }

    /// <summary>Рисует надпись с левого края x. Стиль после вызова тот же, что до</summary>
    public void Draw(float x, float y, float height, GUIStyle style)
    {
        Width(style);
        var color = style.normal.textColor;
        var alignment = style.alignment;
        style.alignment = TextAnchor.UpperLeft;
        for (int i = 0; i < parts.Length; i++)
        {
            style.normal.textColor = colors[i];
            GUI.Label(new Rect(x, y, widths[i] + 1f, height), parts[i], style);
            x += widths[i];
        }
        style.normal.textColor = color;
        style.alignment = alignment;
    }

    public override string ToString() => string.Concat(parts);
}
