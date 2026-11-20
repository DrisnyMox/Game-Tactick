/// <summary>Во что попала пуля: от этого зависят эффект и звук попадания</summary>
public enum HitSurface
{
    /// <summary>Всё, что не сказало о себе (не IShootable)</summary>
    Default,
    /// <summary>Песок, земля, трава, снег</summary>
    Soft,
    /// <summary>Камень, стены, руда</summary>
    Hard,
    /// <summary>Дерево, листва, кактусы</summary>
    Wood,
    /// <summary>Персонаж</summary>
    Body,
}
