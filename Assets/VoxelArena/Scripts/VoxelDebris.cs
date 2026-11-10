using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// Осколки выбитых вокселей. Размер кубика-частицы задаёт pieceSize (доля ребра вокселя), число кубиков на
/// воксель - piecesPerVoxel. Воксель делится на места по размеру кубика, вплотную друг к другу: все вместе они
/// заняли бы ровно объём вокселя. Кубики ставятся в piecesPerVoxel случайных мест из них. Потом кубики
/// разлетаются от точки, откуда воксели
/// выбило, крутятся, отскакивают от земли и в конце жизни сжимаются.
/// Цвет кубика - цвет грани блока из палитры биома: верхний слой берёт цвет верха (у травы - зелёный),
/// остальные - цвет боковины.
/// Частицы рисует одна система частиц на всю арену (кубы, мировые оси). Её, материал и этот компонент
/// собирает Tools/Unity/SetupVoxelDebris.cs.
/// </summary>
[RequireComponent(typeof(ParticleSystem))]
public class VoxelDebris : MonoBehaviour
{
    [SerializeField] VoxelArena arena;

    [Header("Кубики (можно менять прямо в Play)")]
    [Tooltip("Ребро кубика в долях ребра вокселя: 0.25 - четверть вокселя (5,6 см при вокселе 0,226 м). " +
             "Воксель делится на места по этому размеру: на ребро round(1 / pieceSize) мест")]
    [Range(0.05f, 1f)] [SerializeField] float pieceSize = 0.25f;
    [Tooltip("Сколько кубиков выпускать на один выбитый воксель. Кубики встают в случайные места; " +
             "больше, чем мест (при 0.25 это 4³ = 64), не выйдет. Пуля выбивает около 10 вокселей")]
    [Range(1, 512)] [SerializeField] int piecesPerVoxel = 16;

    [Header("Разлёт")]
    [Tooltip("Скорость разлёта от точки попадания, м/с (случайно от x до y)")]
    [SerializeField] Vector2 speed = new(1.5f, 4f);
    [Tooltip("Добавка скорости вверх, м/с (случайно от x до y)")]
    [SerializeField] Vector2 upSpeed = new(0.5f, 2.5f);
    [Tooltip("Случайная добавка скорости в любую сторону, м/с")]
    [SerializeField] float scatter = 0.8f;
    [Tooltip("Скорость вращения кубика, градусы в секунду")]
    [SerializeField] float spin = 540f;
    [Tooltip("Время жизни кубика, с (случайно от x до y)")]
    [SerializeField] Vector2 lifetime = new(0.8f, 1.6f);

    new ParticleSystem particleSystem;
    Color32[] palette;
    // Номера мест внутри вокселя (x + y·n + z·n²): перемешиваются, первые piecesPerVoxel выпускаются
    int[] places = new int[0];

    /// <summary>Сколько частиц выпущено последним выбиванием и за сколько мс (замер)</summary>
    public int LastEmitted { get; private set; }
    public double LastEmitMs { get; private set; }

    void Awake()
    {
        particleSystem = GetComponent<ParticleSystem>();
        arena.VoxelsRemoved += Arena_VoxelsRemoved;
        arena.Generated += Arena_Generated;
    }

    void OnDestroy()
    {
        arena.VoxelsRemoved -= Arena_VoxelsRemoved;
        arena.Generated -= Arena_Generated;
    }

    // Палитра меняется с биомом при каждой генерации
    void Arena_Generated()
    {
        palette = ArenaBiomes.Palette(arena.Biome);
        particleSystem.Clear();
    }

    void Arena_VoxelsRemoved(Vector3 center, List<VoxelArena.RemovedVoxel> removed)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        // Места в вокселе: n на ребро, шаг cell; кубик размером pieceSize вокселя стоит посередине места
        int n = Mathf.Max(1, Mathf.RoundToInt(1f / pieceSize));
        float voxel = arena.VoxelSize;
        float cell = voxel / n;
        var emit = new ParticleSystem.EmitParams
        {
            startSize3D = Vector3.one * (voxel * pieceSize),
            rotation3D = Vector3.zero,
        };

        int total = n * n * n;
        if (places.Length != total)
        {
            places = new int[total];
            for (int i = 0; i < total; i++)
                places[i] = i;
        }
        int count = Mathf.Min(piecesPerVoxel, total);

        foreach (var voxelInfo in removed)
        {
            var top = (Color)palette[VoxelBlocks.FaceSlot(voxelInfo.block, VoxelBlocks.FaceTop)];
            var side = (Color)palette[VoxelBlocks.FaceSlot(voxelInfo.block, VoxelBlocks.FaceSide)];
            var origin = arena.VoxelToWorld((float3)voxelInfo.cell);

            for (int i = 0; i < count; i++)
            {
                // Случайное место из ещё не занятых в этом вокселе (перемешивание первых count номеров)
                int pick = Random.Range(i, total);
                (places[i], places[pick]) = (places[pick], places[i]);
                int place = places[i];
                int x = place % n, y = place / n % n, z = place / (n * n);

                emit.startColor = y == n - 1 ? top : side;
                var position = origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * cell;
                var away = position - center;
                away = away.sqrMagnitude > 1e-6f ? away.normalized : Random.onUnitSphere;
                emit.position = position;
                emit.velocity = away * Random.Range(speed.x, speed.y) +
                                Vector3.up * Random.Range(upSpeed.x, upSpeed.y) +
                                Random.insideUnitSphere * scatter;
                emit.angularVelocity3D = Random.insideUnitSphere * spin;
                emit.startLifetime = Random.Range(lifetime.x, lifetime.y);
                particleSystem.Emit(emit, 1);
            }
        }

        LastEmitted = removed.Count * count;
        LastEmitMs = timer.Elapsed.TotalMilliseconds;
    }
}
