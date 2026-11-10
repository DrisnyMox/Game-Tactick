using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VoxelCharacterSystem
{
    /// <summary>
    /// Генератор полигонального меша из воксельной модели с использованием алгоритма Greedy Meshing.
    /// Склеивает компланарные грани одинакового цвета в крупные квады, генерирует нормали,
    /// UV-координаты по палитре Survivor_Palette.png и вершинные цвета.
    /// </summary>
    public static class VoxelMeshGenerator
    {
        public static Mesh GenerateMesh(VoxelModel model, float voxelSize = 0.03f, Vector3? customPivotOffset = null, bool smoothNormals = true)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color32>();
            var indices = new List<int>();

            // Точка привязки (по умолчанию - центр по X/Z и подошва ног на Y=0)
            Vector3 pivotOffset = customPivotOffset ?? new Vector3(-model.SizeX * 0.5f, 0f, -model.SizeZ * 0.5f);

            int[] dims = { model.SizeX, model.SizeY, model.SizeZ };

            // 3 оси: 0=X, 1=Y, 2=Z
            for (int d = 0; d < 3; d++)
            {
                int u = (d + 1) % 3;
                int v = (d + 2) % 3;

                int dimD = dims[d];
                int dimU = dims[u];
                int dimV = dims[v];

                Vector3Int ed = GetAxisVector(d);
                Vector3Int eu = GetAxisVector(u);
                Vector3Int ev = GetAxisVector(v);

                byte[] mask = new byte[dimU * dimV];

                // 2 направления нормали: -1 (назад/влево/вниз) и +1 (вперёд/вправо/вверх)
                for (int dir = -1; dir <= 1; dir += 2)
                {
                    Vector3 normal = (Vector3)ed * dir;

                    for (int s = 0; s < dimD; s++)
                    {
                        // 1. Построение маски видимых граней для текущего среза
                        for (int j = 0; j < dimV; j++)
                        {
                            for (int i = 0; i < dimU; i++)
                            {
                                int px = ed.x * s + eu.x * i + ev.x * j;
                                int py = ed.y * s + eu.y * i + ev.y * j;
                                int pz = ed.z * s + eu.z * i + ev.z * j;

                                byte current = model.Get(px, py, pz);
                                int npx = px + ed.x * dir;
                                int npy = py + ed.y * dir;
                                int npz = pz + ed.z * dir;
                                byte neighbor = model.Get(npx, npy, npz);

                                bool isVisible = current != SurvivorPalette.Air && neighbor == SurvivorPalette.Air;
                                mask[i + j * dimU] = isVisible ? current : SurvivorPalette.Air;
                            }
                        }

                        // 2. Жадное объединение (Greedy Meshing)
                        for (int j = 0; j < dimV; j++)
                        {
                            for (int i = 0; i < dimU;)
                            {
                                byte col = mask[i + j * dimU];
                                if (col == SurvivorPalette.Air)
                                {
                                    i++;
                                    continue;
                                }

                                // Находим максимальную ширину с таким же цветом
                                int w = 1;
                                while (i + w < dimU && mask[(i + w) + j * dimU] == col)
                                {
                                    w++;
                                }

                                // Находим максимальную высоту
                                int h = 1;
                                bool canExpand = true;
                                while (j + h < dimV)
                                {
                                    for (int k = 0; k < w; k++)
                                    {
                                        if (mask[(i + k) + (j + h) * dimU] != col)
                                        {
                                            canExpand = false;
                                            break;
                                        }
                                    }
                                    if (!canExpand) break;
                                    h++;
                                }

                                // 3. Генерация вершин квада
                                int sliceOffset = dir > 0 ? (s + 1) : s;
                                Vector3 origin = (Vector3)ed * sliceOffset + (Vector3)eu * i + (Vector3)ev * j;
                                Vector3 du = (Vector3)eu * w;
                                Vector3 dv = (Vector3)ev * h;

                                Vector3 p0 = (origin + pivotOffset) * voxelSize;
                                Vector3 p1 = (origin + du + pivotOffset) * voxelSize;
                                Vector3 p2 = (origin + du + dv + pivotOffset) * voxelSize;
                                Vector3 p3 = (origin + dv + pivotOffset) * voxelSize;

                                int baseVertex = positions.Count;
                                positions.Add(p0);
                                positions.Add(p1);
                                positions.Add(p2);
                                positions.Add(p3);

                                normals.Add(normal);
                                normals.Add(normal);
                                normals.Add(normal);
                                normals.Add(normal);

                                Vector2 uv = SurvivorPalette.GetUV(col);
                                uvs.Add(uv);
                                uvs.Add(uv);
                                uvs.Add(uv);
                                uvs.Add(uv);

                                Color32 vertexColor = SurvivorPalette.GetColor(col);
                                colors.Add(vertexColor);
                                colors.Add(vertexColor);
                                colors.Add(vertexColor);
                                colors.Add(vertexColor);

                                // Правильный порядок обхода (Winding Order) для нормали наружу
                                if (dir > 0)
                                {
                                    indices.Add(baseVertex + 0);
                                    indices.Add(baseVertex + 1);
                                    indices.Add(baseVertex + 2);

                                    indices.Add(baseVertex + 0);
                                    indices.Add(baseVertex + 2);
                                    indices.Add(baseVertex + 3);
                                }
                                else
                                {
                                    indices.Add(baseVertex + 0);
                                    indices.Add(baseVertex + 2);
                                    indices.Add(baseVertex + 1);

                                    indices.Add(baseVertex + 0);
                                    indices.Add(baseVertex + 3);
                                    indices.Add(baseVertex + 2);
                                }

                                // Очищаем объединённую область в маске
                                for (int dy = 0; dy < h; dy++)
                                {
                                    for (int dx = 0; dx < w; dx++)
                                    {
                                        mask[(i + dx) + (j + dy) * dimU] = SurvivorPalette.Air;
                                    }
                                }

                                i += w;
                            }
                        }
                    }
                }
            }

            // Сглаживание нормалей на общих рёбрах (убирает эффект рубленых коробок, делая круглые руки, ноги и голову)
            if (smoothNormals && positions.Count > 0)
            {
                var normalAccum = new Dictionary<Vector3Int, Vector3>();
                for (int k = 0; k < positions.Count; k++)
                {
                    var key = new Vector3Int(
                        Mathf.RoundToInt(positions[k].x * 10000f),
                        Mathf.RoundToInt(positions[k].y * 10000f),
                        Mathf.RoundToInt(positions[k].z * 10000f)
                    );
                    if (!normalAccum.TryGetValue(key, out var acc)) acc = Vector3.zero;
                    normalAccum[key] = acc + normals[k];
                }

                for (int k = 0; k < positions.Count; k++)
                {
                    var key = new Vector3Int(
                        Mathf.RoundToInt(positions[k].x * 10000f),
                        Mathf.RoundToInt(positions[k].y * 10000f),
                        Mathf.RoundToInt(positions[k].z * 10000f)
                    );
                    Vector3 blended = normalAccum[key];
                    if (blended.sqrMagnitude > 0.0001f)
                        normals[k] = blended.normalized;
                }
            }

            var mesh = new Mesh
            {
                name = "VoxelCharacterMesh"
            };

            if (positions.Count > 65535)
                mesh.indexFormat = IndexFormat.UInt32;

            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(indices, 0);

            mesh.RecalculateTangents();
            mesh.RecalculateBounds();

            return mesh;
        }

        static Vector3Int GetAxisVector(int axis) => axis switch
        {
            0 => new Vector3Int(1, 0, 0),
            1 => new Vector3Int(0, 1, 0),
            _ => new Vector3Int(0, 0, 1),
        };
    }
}
