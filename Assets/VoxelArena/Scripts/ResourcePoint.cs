using Unity.Mathematics;

public struct ResourcePoint
{
    public int2 cell;
    public ResourceKind kind;
    /// <summary>0 — база один, 1 — база два, -1 — ничья (точка захвата)</summary>
    public int team;
    /// <summary>Остров: шаг клетки в сторону моря (причал и остов смотрят в море)</summary>
    public int2 facing;
}
