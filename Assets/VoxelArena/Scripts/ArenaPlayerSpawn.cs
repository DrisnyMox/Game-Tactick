using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Ставит команды на базы после каждой генерации карты: команду 0 (игрок и союзники) - на VoxelArena.BaseOne,
/// команду 1 - на BaseTwo, лицом к своим воротам, в две шеренги. До генерации земли под ними нет,
/// и без этого компонента персонажи падают сквозь пустоту.
/// Убитого возрождает на базе его команды через respawnDelay секунд и считает убийства команд.
/// Над союзниками игрока рисует синюю метку, чтобы в бою их можно было отличить от врагов.
/// Команды берёт из CharacterHealth.All, номер команды - CharacterHealth.Team.
/// </summary>
[RequireComponent(typeof(VoxelArena))]
public class ArenaPlayerSpawn : MonoBehaviour
{
    [Tooltip("Персонаж игрока: по нему показывается надпись «убит» и считаются союзники")]
    [SerializeField] CharacterHealth player;
    [Tooltip("Насколько выше поверхности ставить персонажа, м")]
    [SerializeField] float lift = 0.1f;
    [SerializeField] float respawnDelay = 4f;
    [Tooltip("Расстояние между персонажами в шеренге, м")]
    [SerializeField] float spacing = 2.5f;

    VoxelArena arena;
    readonly Dictionary<CharacterHealth, float> respawnTimes = new();
    readonly List<CharacterHealth> due = new();
    readonly int[] teamKills = new int[2];
    Camera cam;

    void Awake()
    {
        // Надписи рисуются только GUI.Label, без GUILayout: без раскладки OnGUI не выделяет под неё память
        useGUILayout = false;
        arena = GetComponent<VoxelArena>();
        arena.Generated += Arena_Generated;
    }

    void Start()
    {
        cam = Camera.main;
        foreach (var character in CharacterHealth.All)
            character.Died += Character_Died;
    }

    void OnDestroy()
    {
        arena.Generated -= Arena_Generated;
        foreach (var character in CharacterHealth.All)
            character.Died -= Character_Died;
    }

    void Arena_Generated()
    {
        respawnTimes.Clear();
        foreach (var character in CharacterHealth.All)
            Place(character);
    }

    void Character_Died(CharacterHealth dead)
    {
        teamKills[1 - dead.Team]++;
        respawnTimes[dead] = Time.time + respawnDelay;
    }

    void Update()
    {
        due.Clear();
        foreach (var pair in respawnTimes)
            if (Time.time >= pair.Value)
                due.Add(pair.Key);
        foreach (var character in due)
        {
            respawnTimes.Remove(character);
            Place(character);
        }
    }

    // Место в шеренге: номер персонажа среди своей команды, по 5 в ряд, ряды назад от ворот
    void Place(CharacterHealth character)
    {
        int slot = 0;
        foreach (var other in CharacterHealth.All)
        {
            if (other == character)
                break;
            if (other.Team == character.Team)
                slot++;
        }

        bool home = character.Team == 0;
        var spawn = home ? arena.BaseOne : arena.BaseTwo;
        // Лицом к своим воротам: это выход с базы
        var look = (home ? arena.GateOne : arena.GateTwo) - spawn;
        look.y = 0f;
        var rotation = Quaternion.LookRotation(look);
        float side = (slot % 5 - 2) * spacing;
        float back = slot / 5 * spacing;
        var point = spawn + rotation * new Vector3(side, 0f, -back);

        // Высота земли под местом в шеренге: база ровная, но в ней бывают постройки
        if (Physics.Raycast(point + Vector3.up * 10f, Vector3.down, out var ground, 30f, 1, QueryTriggerInteraction.Ignore))
            point = ground.point;
        character.Revive(point + Vector3.up * lift, rotation);
    }

    // Стиль надписей заводится в первом OnGUI (GUI.skin есть только там). Счёт и строка отсчёта до возрождения
    // собираются заново, только когда меняется число: OnGUI идёт по нескольку раз за кадр, и новые стиль и строки
    // в каждом вызове выделяли бы память
    GUIStyle style;
    static readonly Color AllyMarkColor = new(0.435f, 0.659f, 1f);
    static readonly Color RedTeamColor = new(1f, 0.435f, 0.435f);
    // Счёт команд: куски с цветом команды вместо тегов <color>
    readonly ColoredLine score = new(3);
    string respawnText;
    int shownBlue = -1, shownRed = -1, shownSeconds = -1;

    void OnGUI()
    {
        int fontSize = Mathf.Clamp(Screen.height / 30, 12, 32);
        style ??= new GUIStyle(GUI.skin.label);
        style.fontSize = fontSize;
        if (teamKills[0] != shownBlue || teamKills[1] != shownRed)
        {
            shownBlue = teamKills[0];
            shownRed = teamKills[1];
            score.Set(0, $"синие {shownBlue}", AllyMarkColor);
            score.Set(1, " : ", Color.white);
            score.Set(2, $"{shownRed} красные", RedTeamColor);
        }
        score.Draw((Screen.width - score.Width(style)) * 0.5f, 10, fontSize * 2, style);

        // Синяя метка над головой живых союзников. Цвет - цветом стиля, а не тегом <color>: разбор тегов в каждой
        // надписи выделяет память
        style.fontSize = fontSize;
        style.alignment = TextAnchor.LowerCenter;
        var textColor = style.normal.textColor;
        style.normal.textColor = AllyMarkColor;
        foreach (var character in CharacterHealth.All)
        {
            if (character == player || character.Team != player.Team || character.IsDead)
                continue;
            var screen = cam.WorldToScreenPoint(character.Chest.position + Vector3.up * 0.9f);
            if (screen.z <= 0f)
                continue;
            GUI.Label(new Rect(screen.x - 50, Screen.height - screen.y - fontSize * 1.5f, 100, fontSize * 1.5f), "▼", style);
        }
        style.normal.textColor = textColor;

        if (respawnTimes.TryGetValue(player, out float respawnTime))
        {
            style.alignment = TextAnchor.MiddleCenter;
            style.fontSize = fontSize * 2;
            int seconds = Mathf.CeilToInt(respawnTime - Time.time);
            if (seconds != shownSeconds)
            {
                shownSeconds = seconds;
                respawnText = $"убит, возрождение через {seconds}";
            }
            GUI.Label(new Rect(0, 0, Screen.width, Screen.height), respawnText, style);
        }
    }
}
