using StarterAssets;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Общее для экранов вылазки и базы: окно поверх игры (ModalOpen) с выключенным вводом игрока и свободным курсором,
/// размер шрифта по высоте экрана, надпись с тенью.
/// </summary>
public static class LoopGui
{
    /// <summary>Открыто окно (база, итог вылазки): меню настроек по Esc не открывается</summary>
    public static bool ModalOpen { get; private set; }

    /// <summary>
    /// Кадр, в котором окно закрылось. Esc закрывает окно базы, и меню настроек в том же кадре не должно
    /// открыться от того же нажатия (порядок Update у них не задан)
    /// </summary>
    public static int ClosedFrame { get; private set; } = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        ModalOpen = false;
        ClosedFrame = -1;
    }

    public static int FontSize => Mathf.Clamp(Screen.height / 34, 12, 30);

    /// <summary>
    /// Открыть или закрыть окно. Пока окно открыто, ввод игрока выключен, зажатые кнопки сброшены, курсор свободен
    /// (как в SettingsMenu)
    /// </summary>
    public static void SetModal(bool open, GameObject player)
    {
        ModalOpen = open;
        var playerInput = player.GetComponent<PlayerInput>();
        var inputs = player.GetComponent<StarterAssetsInputs>();
        var shooter = player.GetComponent<SurvivorShooter>();
        if (open)
        {
            playerInput.DeactivateInput();
            inputs.MoveInput(Vector2.zero);
            inputs.LookInput(Vector2.zero);
            inputs.JumpInput(false);
            inputs.SprintInput(false);
            shooter.AimInput = false;
            shooter.FireInput = false;
            inputs.cursorLocked = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else
        {
            ClosedFrame = Time.frameCount;
            playerInput.ActivateInput();
            inputs.cursorLocked = true;
            Cursor.lockState = CursorLockMode.Locked;
        }
    }

    static GUIStyle label;

    /// <summary>Стиль надписи: один на все экраны, размер и выравнивание ставятся перед каждой надписью</summary>
    public static GUIStyle Label(int fontSize, TextAnchor anchor)
    {
        label ??= new GUIStyle(GUI.skin.label) { richText = false, wordWrap = false };
        label.fontSize = fontSize;
        label.alignment = anchor;
        return label;
    }

    /// <summary>Надпись с тёмной тенью: читается и на светлом песке, и на небе</summary>
    public static void Shadowed(Rect rect, string text, GUIStyle style, Color color)
    {
        var old = style.normal.textColor;
        style.normal.textColor = new Color(0f, 0f, 0f, 0.75f * color.a);
        GUI.Label(new Rect(rect.x + 1.5f, rect.y + 1.5f, rect.width, rect.height), text, style);
        style.normal.textColor = color;
        GUI.Label(rect, text, style);
        style.normal.textColor = old;
    }

    /// <summary>Тёмная подложка окна</summary>
    public static void Panel(Rect rect, float alpha = 0.8f)
    {
        var old = GUI.color;
        GUI.color = new Color(0.05f, 0.07f, 0.09f, alpha);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = old;
    }
}
