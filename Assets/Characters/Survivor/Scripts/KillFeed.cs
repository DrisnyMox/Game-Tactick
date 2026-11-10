using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Лента убийств в правом верхнем углу: «убийца → убитый», цвет имени по команде, «в голову» для выстрела
/// в голову. Строка видна lineTime секунд, строк не больше maxLines. Игрок подписан «вы».
/// Слушает CharacterHealth.Killed. Объект с этим компонентом ставит Tools/Unity/SetupHitFeedback.cs.
/// </summary>
public class KillFeed : MonoBehaviour
{
    [Tooltip("Персонаж игрока: в ленте он подписан «вы»")]
    [SerializeField] CharacterHealth player;
    [SerializeField] float lineTime = 5f;
    [SerializeField] int maxLines = 5;

    static readonly Color BlueTeam = new(0.435f, 0.659f, 1f);
    static readonly Color RedTeam = new(1f, 0.435f, 0.435f);
    static readonly Color Headshot = new(1f, 0.627f, 0.125f);

    struct Line
    {
        public ColoredLine text;
        public float time;
    }

    readonly List<Line> lines = new();
    // Стиль строк: заводится в первом OnGUI (GUI.skin есть только там), чтобы не выделять память на новый в каждом
    GUIStyle style;

    // Лента рисуется только GUI.Label, без GUILayout: без раскладки OnGUI не выделяет под неё память
    void Awake() => useGUILayout = false;

    void OnEnable() => CharacterHealth.Killed += Character_Killed;

    void OnDisable() => CharacterHealth.Killed -= Character_Killed;

    // Строка собирается один раз, при убийстве: куски с цветом команды вместо тегов <color>
    void Character_Killed(CharacterHealth victim, CharacterHealth killer, bool headshot)
    {
        var text = new ColoredLine(headshot ? 4 : 3);
        text.Set(0, Name(killer), TeamColor(killer));
        text.Set(1, "  →  ", Color.white);
        text.Set(2, Name(victim), TeamColor(victim));
        if (headshot)
            text.Set(3, "  в голову", Headshot);
        lines.Add(new Line { text = text, time = Time.time });
        if (lines.Count > maxLines)
            lines.RemoveAt(0);
    }

    string Name(CharacterHealth character) => character == player ? "вы" : character.name;

    static Color TeamColor(CharacterHealth character) => character.Team == 0 ? BlueTeam : RedTeam;

    void OnGUI()
    {
        // Циклом, а не RemoveAll с лямбдой: лямбда, которая читает поле, выделяет память при каждом вызове
        for (int i = lines.Count - 1; i >= 0; i--)
            if (Time.time - lines[i].time > lineTime)
                lines.RemoveAt(i);
        if (lines.Count == 0)
            return;

        int fontSize = Mathf.Clamp(Screen.height / 36, 11, 28);
        style ??= new GUIStyle(GUI.skin.label);
        style.fontSize = fontSize;
        float y = 10f + fontSize * 2f;
        foreach (var line in lines)
        {
            line.text.Draw(Screen.width - 20 - line.text.Width(style), y, fontSize * 1.6f, style);
            y += fontSize * 1.5f;
        }
    }
}
