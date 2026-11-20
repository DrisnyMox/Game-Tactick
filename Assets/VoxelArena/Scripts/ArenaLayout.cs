using System;
using Unity.Collections;
using Unity.Mathematics;

/// <summary>
/// Раскладка карты по столбцам (x, z): высоты, вода, рампы, базы, ресурсы, деревья, камни.
/// Индекс столбца: z * sizeX + x. Все данные в нативной памяти — освобождаются Dispose.
/// </summary>
public sealed class ArenaLayout : IDisposable
{
    public const byte FlagSand = 1;   // сверху песок (берег, дно озера)
    public const byte FlagCliff = 2;  // край обрыва: под верхним блоком сразу камень
    public const byte FlagRamp = 4;
    public const byte FlagRock = 8;       // горы по краю: камень до самого верха
    public const byte FlagSoftTop = 16;   // пологий склон горы: сверху трава

    public readonly int sizeX;
    public readonly int sizeZ;
    public NativeArray<int> height;    // число сплошных блоков в столбце
    public NativeArray<int> waterTop;  // 0 — нет воды, иначе вода стоит до этого y (не включая)
    public NativeArray<byte> flags;

    public int2 baseOne;
    public int2 baseTwo;
    /// <summary>Сторона ворот базы один: 0 +z, 1 +x, 2 -z, 3 -x (у базы два — противоположная)</summary>
    public int gateSide;
    /// <summary>
    /// Внутри стен какого уровня лежит клетка: 0 — ни в каких, 1..3 — база один, 5..7 — база два (4 + уровень).
    /// Хранится наименьший уровень, в который клетка попадает.
    /// </summary>
    public NativeArray<byte> baseZone;
    /// <summary>Ровная площадка базы (под стены 3-го уровня и отступ): 0 — нет, 1 — база один, 2 — база два</summary>
    public NativeArray<byte> baseArea;
    /// <summary>Направление от базы один к базе два (единичный вектор в плоскости x, z)</summary>
    public float2 baseAxis;
    /// <summary>Стены 1-го уровня обеих баз</summary>
    public NativeList<WallCell> walls;
    /// <summary>Клетка сразу снаружи ворот: [0] — база один, [1] — база два</summary>
    public NativeList<int2> gates;
    public NativeList<ResourcePoint> resources;
    public NativeList<int2> trees;
    public NativeList<int2> rocks;

    // Для лога
    public int regionCount;
    public int rampCount;
    public int flattenedRegions;
    public bool basesConnected;
    public bool capturePointsReachable;
    /// <summary>Остров: вода стоит до этой высоты (клетки раскладки); 0 — карта без моря</summary>
    public int seaLevel;
    /// <summary>На сколько вокселей вода ниже верха клетки waterTop: на острове 1, чтобы глубина была 1–3 вокселя, а не 2–4</summary>
    public int waterDropVoxels;
    /// <summary>По воде можно ходить (мелководье архипелага): NavMesh не помечает воду непроходимой</summary>
    public bool wadeableWater;
    /// <summary>
    /// На сколько вокселей трава с землёй выше песка: на острове 1 (трава лежит слоем над песком), на арене 0.
    /// Трава — клетка суши без песка и камня (см. IsRaisedGrass)
    /// </summary>
    public int grassRaiseVoxels;

    /// <summary>Клетка поднята на grassRaiseVoxels: сверху трава, а не песок и не камень</summary>
    public static bool IsRaisedGrass(byte flags, int grassRaise) => grassRaise > 0 && (flags & (FlagSand | FlagRock)) == 0;

    /// <summary>
    /// Остров: высота поверхности посередине клетки в вокселях, дробная. Между серединами клеток ExpandLayoutJob
    /// меняет её плавно, поэтому берег поднимается на воксель через несколько вокселей, а не лесенкой.
    /// Используется, только если fineSurface; height при этом — та же высота в клетках с округлением вниз
    /// </summary>
    public NativeArray<float> surface;
    public bool fineSurface;

    public ArenaLayout(int sizeX, int sizeZ)
    {
        this.sizeX = sizeX;
        this.sizeZ = sizeZ;
        int n = sizeX * sizeZ;
        height = new NativeArray<int>(n, Allocator.Persistent);
        waterTop = new NativeArray<int>(n, Allocator.Persistent);
        flags = new NativeArray<byte>(n, Allocator.Persistent);
        surface = new NativeArray<float>(n, Allocator.Persistent);
        baseZone = new NativeArray<byte>(n, Allocator.Persistent);
        baseArea = new NativeArray<byte>(n, Allocator.Persistent);
        walls = new NativeList<WallCell>(Allocator.Persistent);
        gates = new NativeList<int2>(2, Allocator.Persistent);
        resources = new NativeList<ResourcePoint>(Allocator.Persistent);
        trees = new NativeList<int2>(Allocator.Persistent);
        rocks = new NativeList<int2>(Allocator.Persistent);
    }

    public void Dispose()
    {
        height.Dispose();
        waterTop.Dispose();
        flags.Dispose();
        surface.Dispose();
        baseZone.Dispose();
        baseArea.Dispose();
        walls.Dispose();
        gates.Dispose();
        resources.Dispose();
        trees.Dispose();
        rocks.Dispose();
    }
}
