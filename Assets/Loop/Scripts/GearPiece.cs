using System;

/// <summary>
/// Одна часть снаряжения в профиле: отдельная запись, а не число в ItemCounts — у неё свой цвет качества, позже
/// и баффы. Надетая часть уходит в вылазку
/// </summary>
[Serializable]
public sealed class GearPiece
{
    public GearKind kind;
    public bool worn;
    // В сохранениях до цвета качества поля нет: JsonUtility даёт 0 (серое), Profile.Load делает такие части белыми
    public GearQuality quality;
}
