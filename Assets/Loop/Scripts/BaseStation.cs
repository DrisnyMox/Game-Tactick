using UnityEngine;

/// <summary>Место на базе, у которого по E открывается окно. Окна рисует BaseHub</summary>
public class BaseStation : MonoBehaviour
{
    public enum Kind
    {
        Stash,
        Trader,
        Workbench,
        Exit,
    }

    public Kind kind;
    [Tooltip("С какого расстояния доступно, м")]
    public float reach = 2.8f;

    public string Title => kind switch
    {
        Kind.Stash => "Склад",
        Kind.Trader => "Торговец",
        Kind.Workbench => "Верстак",
        _ => "Причал: в вылазку",
    };
}
