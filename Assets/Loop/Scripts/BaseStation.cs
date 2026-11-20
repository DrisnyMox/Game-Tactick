using UnityEngine;

/// <summary>Место на базе, у которого по E открывается окно. Окна рисует BaseHub</summary>
public class BaseStation : MonoBehaviour
{
    public enum StationKind
    {
        Stash,
        Trader,
        Workbench,
        Exit,
    }

    [SerializeField] StationKind kind;
    [Tooltip("С какого расстояния доступно, м")]
    [SerializeField] float reach = 2.8f;

    public StationKind Kind => kind;
    public float Reach => reach;

    public string Title => kind switch
    {
        StationKind.Stash => "Склад",
        StationKind.Trader => "Торговец",
        StationKind.Workbench => "Верстак",
        _ => "Причал: в вылазку",
    };
}
