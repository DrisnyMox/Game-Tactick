using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Какой меш чанка строить</summary>
public enum ChunkMeshKind : byte
{
    // Меш отрисовки с цветом: сабмеш 0 — суша (всё, кроме воды, с затенением), сабмеш 1 — вода
    Color,
    // Форма суши без цвета, затенения и нормалей: для MeshCollider и NavMesh
    Shape,
    // Форма воды без цвета и нормалей: NavMesh отмечает по ней воду непроходимой
    WaterShape,
}

/// <summary>Один меш одного чанка: начало чанка в вокселях и вид меша</summary>
public struct ChunkMeshTask
{
    public int3 origin;
    public ChunkMeshKind kind;
}

/// <summary>
/// Рабочие списки GreedyMesher одного потока. Живут всё время арены и переиспользуются задачами:
/// без выделения памяти на каждую задачу
/// </summary>
public struct GreedyMeshScratch
{
    public UnsafeList<byte> blocks;
    public UnsafeList<int> mask;
    public UnsafeList<float3> positions;
    public UnsafeList<float3> normals;
    public UnsafeList<Color32> colors;
    public UnsafeList<uint> indices;

    public static NativeArray<GreedyMeshScratch> Create(Allocator allocator) =>
        new(Unity.Jobs.LowLevel.Unsafe.JobsUtility.ThreadIndexCount, allocator);

    public static void Dispose(NativeArray<GreedyMeshScratch> scratch)
    {
        foreach (var s in scratch)
        {
            // Списки потока создаются при первой его задаче
            if (!s.blocks.IsCreated)
                continue;
            s.blocks.Dispose();
            s.mask.Dispose();
            s.positions.Dispose();
            s.normals.Dispose();
            s.colors.Dispose();
            s.indices.Dispose();
        }
        scratch.Dispose();
    }
}

/// <summary>
/// Строит меши пачки чанков и пишет их прямо в Mesh.MeshDataArray: в главном потоке остаётся
/// только Mesh.ApplyAndDisposeWritableMeshData. Задача index пишет в meshes[index].
/// Чанки без видимых граней сюда не попадают: их отсеивает ChunkFacesJob.
/// Вершины меша отрисовки — три потока: позиция (float3), нормаль (float3), цвет (4 байта:
/// r — индекс палитры, g — затенение угла). У него два сабмеша на общих вершинах: 0 — суша, 1 — вода
/// (у каждого свой материал). У форм — только позиция и один сабмеш.
/// </summary>
[BurstCompile]
public struct GreedyMeshJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<byte> voxels;
    [ReadOnly] public NativeArray<byte> faceColors;
    public int3 dims;
    public int chunkSize;
    [ReadOnly] public NativeArray<ChunkMeshTask> tasks;
    // Раскладка вершин меша отрисовки (позиция, нормаль, цвет) и форм (позиция)
    [ReadOnly] public NativeArray<VertexAttributeDescriptor> colorLayout;
    [ReadOnly] public NativeArray<VertexAttributeDescriptor> shapeLayout;
    public Mesh.MeshDataArray meshes;

    // Списки по номеру потока: задачи одного потока идут друг за другом
    [NativeDisableParallelForRestriction] public NativeArray<GreedyMeshScratch> scratch;
    [NativeSetThreadIndex] int threadIndex;

    public void Execute(int index)
    {
        var task = tasks[index];
        bool plain = task.kind != ChunkMeshKind.Color;

        var lists = scratch[threadIndex];
        if (!lists.blocks.IsCreated)
        {
            int padded = chunkSize + 2;
            lists.blocks = new UnsafeList<byte>(padded * padded * padded, Allocator.Persistent);
            lists.mask = new UnsafeList<int>(chunkSize * chunkSize, Allocator.Persistent);
            lists.positions = new UnsafeList<float3>(1024, Allocator.Persistent);
            lists.normals = new UnsafeList<float3>(1024, Allocator.Persistent);
            lists.colors = new UnsafeList<Color32>(1024, Allocator.Persistent);
            lists.indices = new UnsafeList<uint>(1536, Allocator.Persistent);
        }
        lists.positions.Clear();
        lists.normals.Clear();
        lists.colors.Clear();
        lists.indices.Clear();

        var mesher = new GreedyMesher
        {
            faceColors = faceColors,
            chunkSize = chunkSize,
            water = task.kind == ChunkMeshKind.WaterShape,
            plain = plain,
            blocks = lists.blocks,
            mask = lists.mask,
            positions = lists.positions,
            normals = lists.normals,
            colors = lists.colors,
            indices = lists.indices,
        };
        mesher.CopyBlocks(voxels, dims, task.origin);
        mesher.Build();
        // Меш отрисовки: вода дописывается после суши в те же списки, её индексы — второй сабмеш
        int landIndices = mesher.indices.Length;
        if (!plain)
        {
            mesher.water = true;
            mesher.Build();
        }
        Write(meshes[index], ref mesher, plain, landIndices);

        // Списки могли вырасти и переехать: поток забирает их обратно
        lists.blocks = mesher.blocks;
        lists.mask = mesher.mask;
        lists.positions = mesher.positions;
        lists.normals = mesher.normals;
        lists.colors = mesher.colors;
        lists.indices = mesher.indices;
        scratch[threadIndex] = lists;
    }

    // landIndices — сколько индексов в начале списка у суши (сабмеш 0), остальные — вода (сабмеш 1).
    // У форм сабмеш один
    void Write(Mesh.MeshData data, ref GreedyMesher mesher, bool plain, int landIndices)
    {
        int vertexCount = mesher.positions.Length;
        int indexCount = mesher.indices.Length;

        data.SetVertexBufferParams(vertexCount, plain ? shapeLayout : colorLayout);
        var positions = data.GetVertexData<float3>(0);
        for (int i = 0; i < vertexCount; i++)
            positions[i] = mesher.positions[i];
        if (!plain)
        {
            var normals = data.GetVertexData<float3>(1);
            var colors = data.GetVertexData<Color32>(2);
            for (int i = 0; i < vertexCount; i++)
            {
                normals[i] = mesher.normals[i];
                colors[i] = mesher.colors[i];
            }
        }

        data.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
        var indices = data.GetIndexData<uint>();
        for (int i = 0; i < indexCount; i++)
            indices[i] = mesher.indices[i];

        // Границы — весь чанк: по ним Unity решает, виден ли чанк камере
        var bounds = new Bounds(Vector3.one * (chunkSize * 0.5f), Vector3.one * chunkSize);
        const MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;
        if (plain)
        {
            data.subMeshCount = 1;
            data.SetSubMesh(0, new SubMeshDescriptor(0, indexCount)
            {
                bounds = bounds,
                firstVertex = 0,
                vertexCount = vertexCount,
            }, flags);
            return;
        }

        data.subMeshCount = 2;
        data.SetSubMesh(0, new SubMeshDescriptor(0, landIndices)
        {
            bounds = bounds,
            firstVertex = 0,
            vertexCount = vertexCount,
        }, flags);
        data.SetSubMesh(1, new SubMeshDescriptor(landIndices, indexCount - landIndices)
        {
            bounds = bounds,
            firstVertex = 0,
            vertexCount = vertexCount,
        }, flags);
    }
}

/// <summary>
/// Жадная склейка граней одного чанка. Соседние грани склеиваются в один прямоугольник,
/// только если у них одинаковый цвет и одинаковое затенение во всех четырёх углах.
/// Координаты вершин — в вокселях относительно начала чанка.
/// Режим plain — меш только формы (без цвета, затенения и нормалей): грани склеиваются
/// в крупные прямоугольники. Им пользуются коллайдер и NavMesh.
/// </summary>
struct GreedyMesher
{
    public NativeArray<byte> faceColors;
    public int chunkSize;
    // false — грани суши (всё, кроме воды), true — грани воды
    public bool water;
    public bool plain;

    // Блоки чанка с каймой в один воксель со всех сторон: (chunkSize + 2)³, индекс от угла каймы.
    // Грани и затенение читают только отсюда — без проверок границ арены на каждый воксель
    public UnsafeList<byte> blocks;
    public UnsafeList<int> mask;
    public UnsafeList<float3> positions;
    public UnsafeList<float3> normals;
    // r — индекс палитры, g — затенение угла (AO), 0..255
    public UnsafeList<Color32> colors;
    public UnsafeList<uint> indices;

    int padded;

    /// <summary>
    /// Копирует блоки чанка с каймой. Ниже арены — сплошная порода (нижние грани не строятся),
    /// за краями и выше — воздух (края карты видны как срез)
    /// </summary>
    public void CopyBlocks(NativeArray<byte> voxels, int3 dims, int3 origin)
    {
        padded = chunkSize + 2;
        blocks.Resize(padded * padded * padded);
        mask.Resize(chunkSize * chunkSize);

        int k = 0;
        for (int ly = 0; ly < padded; ly++)
        {
            int y = origin.y + ly - 1;
            for (int lz = 0; lz < padded; lz++)
            {
                int z = origin.z + lz - 1;
                if (y < 0 || y >= dims.y || z < 0 || z >= dims.z)
                {
                    byte fill = y < 0 ? VoxelBlocks.Bedrock : VoxelBlocks.Air;
                    for (int lx = 0; lx < padded; lx++)
                        blocks[k++] = fill;
                    continue;
                }

                int row = (y * dims.z + z) * dims.x;
                for (int lx = 0; lx < padded; lx++)
                {
                    int x = origin.x + lx - 1;
                    blocks[k++] = x >= 0 && x < dims.x ? voxels[row + x] : VoxelBlocks.Air;
                }
            }
        }
    }

    public void Build()
    {
        for (int d = 0; d < 3; d++)
        {
            int3 ed = Axis(d);
            int3 eu = Axis((d + 1) % 3);
            int3 ev = Axis((d + 2) % 3);

            for (int dir = -1; dir <= 1; dir += 2)
            {
                int face = d == 1
                    ? (dir > 0 ? VoxelBlocks.FaceTop : VoxelBlocks.FaceBottom)
                    : VoxelBlocks.FaceSide;

                for (int s = 0; s < chunkSize; s++)
                {
                    BuildMask(ed, eu, ev, dir, face, s);
                    MergeMask(ed, eu, ev, dir, s);
                }
            }
        }
    }

    // Ключ грани: бит 0 — грань есть, биты 1-8 — цвет, биты 9-16 — AO четырёх углов
    void BuildMask(int3 ed, int3 eu, int3 ev, int dir, int face, int s)
    {
        // Шаги по осям в индексе blocks
        int stepD = Step(ed), stepU = Step(eu), stepV = Step(ev);
        int next = stepD * dir;

        for (int j = 0; j < chunkSize; j++)
        {
            int p = Index(ed * s + ev * j);
            for (int i = 0; i < chunkSize; i++, p += stepU)
            {
                int key = 0;

                byte block = blocks[p];
                bool isWater = block == VoxelBlocks.Water;
                if (block != VoxelBlocks.Air && isWater == water)
                {
                    // Суша видна сквозь воду, вода — только на границе с воздухом
                    int q = p + next;
                    byte nextBlock = blocks[q];
                    bool visible = nextBlock == VoxelBlocks.Air || (!water && nextBlock == VoxelBlocks.Water);
                    if (visible && plain)
                    {
                        key = 1;
                    }
                    else if (visible)
                    {
                        int color = faceColors[block * 3 + face];
                        int ao0 = 3, ao1 = 3, ao2 = 3, ao3 = 3;
                        if (!water)
                        {
                            ao0 = Ao(q, -stepU, -stepV);
                            ao1 = Ao(q, stepU, -stepV);
                            ao2 = Ao(q, stepU, stepV);
                            ao3 = Ao(q, -stepU, stepV);
                        }
                        key = 1 | (color << 1) | (ao0 << 9) | (ao1 << 11) | (ao2 << 13) | (ao3 << 15);
                    }
                }

                mask[i + j * chunkSize] = key;
            }
        }
    }

    void MergeMask(int3 ed, int3 eu, int3 ev, int dir, int s)
    {
        for (int j = 0; j < chunkSize; j++)
        {
            int i = 0;
            while (i < chunkSize)
            {
                int key = mask[i + j * chunkSize];
                if (key == 0)
                {
                    i++;
                    continue;
                }

                int w = 1;
                while (i + w < chunkSize && mask[i + w + j * chunkSize] == key)
                    w++;

                int h = 1;
                while (j + h < chunkSize && RowMatches(key, i, j + h, w))
                    h++;

                EmitQuad(key, ed, eu, ev, dir, s, i, j, w, h);

                for (int l = 0; l < h; l++)
                    for (int k = 0; k < w; k++)
                        mask[i + k + (j + l) * chunkSize] = 0;

                i += w;
            }
        }
    }

    bool RowMatches(int key, int i, int row, int w)
    {
        for (int k = 0; k < w; k++)
        {
            if (mask[i + k + row * chunkSize] != key)
                return false;
        }
        return true;
    }

    void EmitQuad(int key, int3 ed, int3 eu, int3 ev, int dir, int s, int i, int j, int w, int h)
    {
        var origin = (float3)(ed * (s + (dir > 0 ? 1 : 0)) + eu * i + ev * j);
        var du = (float3)(eu * w);
        var dv = (float3)(ev * h);
        var normal = (float3)(ed * dir);

        byte color = (byte)((key >> 1) & 0xFF);
        int ao0 = (key >> 9) & 3;
        int ao1 = (key >> 11) & 3;
        int ao2 = (key >> 13) & 3;
        int ao3 = (key >> 15) & 3;

        uint start = (uint)positions.Length;
        AddVertex(origin, normal, color, ao0);
        AddVertex(origin + du, normal, color, ao1);
        AddVertex(origin + du + dv, normal, color, ao2);
        AddVertex(origin + dv, normal, color, ao3);

        // cross(eu, ev) == ed, поэтому порядок 0-1-2 смотрит в +ed.
        // Диагональ выбираем так, чтобы затенение не растекалось по всему квадрату.
        bool flip = ao0 + ao2 < ao1 + ao3;
        if (dir > 0)
        {
            if (!flip) { Tri(start, 0, 1, 2); Tri(start, 0, 2, 3); }
            else       { Tri(start, 1, 2, 3); Tri(start, 1, 3, 0); }
        }
        else
        {
            if (!flip) { Tri(start, 0, 2, 1); Tri(start, 0, 3, 2); }
            else       { Tri(start, 1, 3, 2); Tri(start, 1, 0, 3); }
        }
    }

    void Tri(uint start, uint a, uint b, uint c)
    {
        indices.Add(start + a);
        indices.Add(start + b);
        indices.Add(start + c);
    }

    void AddVertex(float3 position, float3 normal, byte color, int ao)
    {
        positions.Add(position);
        if (plain)
            return;
        normals.Add(normal);
        colors.Add(new Color32(color, (byte)(ao * 85), 0, 255));
    }

    // Затенение угла: 3 — открыт, 0 — зажат с двух сторон. Вода не затеняет.
    // q — индекс вокселя перед гранью, du / dv — шаги в blocks к соседям вдоль граней
    int Ao(int q, int du, int dv)
    {
        int side1 = IsOpaque(blocks[q + du]) ? 1 : 0;
        int side2 = IsOpaque(blocks[q + dv]) ? 1 : 0;
        int corner = IsOpaque(blocks[q + du + dv]) ? 1 : 0;

        if (side1 == 1 && side2 == 1)
            return 0;

        return 3 - (side1 + side2 + corner);
    }

    static bool IsOpaque(byte block) => block != VoxelBlocks.Air && block != VoxelBlocks.Water;

    // Индекс в blocks вокселя чанка (координаты от начала чанка, от -1 до chunkSize)
    int Index(int3 p) => ((p.y + 1) * padded + p.z + 1) * padded + p.x + 1;

    int Step(int3 axis) => (axis.y * padded + axis.z) * padded + axis.x;

    static int3 Axis(int d)
    {
        if (d == 0) return new int3(1, 0, 0);
        if (d == 1) return new int3(0, 1, 0);
        return new int3(0, 0, 1);
    }
}
