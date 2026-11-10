using System;
using UnityEngine;

namespace VoxelCharacterSystem
{
    /// <summary>
    /// Трёхмерная воксельная сетка для персонажей и моделей.
    /// Хранит байтовые индексы палитры (SurvivorPalette.Air = пустота).
    /// </summary>
    public class VoxelModel
    {
        public readonly int SizeX;
        public readonly int SizeY;
        public readonly int SizeZ;
        public readonly byte[] Voxels;

        public VoxelModel(int sizeX, int sizeY, int sizeZ)
        {
            if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0)
                throw new ArgumentException("Размеры воксельной модели должны быть положительными числами.");

            SizeX = sizeX;
            SizeY = sizeY;
            SizeZ = sizeZ;
            Voxels = new byte[sizeX * sizeY * sizeZ];
            Clear(SurvivorPalette.Air);
        }

        public void Clear(byte value = SurvivorPalette.Air)
        {
            Array.Fill(Voxels, value);
        }

        public bool InBounds(int x, int y, int z) =>
            x >= 0 && x < SizeX && y >= 0 && y < SizeY && z >= 0 && z < SizeZ;

        public int GetIndex(int x, int y, int z) =>
            x + y * SizeX + z * SizeX * SizeY;

        public byte Get(int x, int y, int z)
        {
            if (!InBounds(x, y, z)) return SurvivorPalette.Air;
            return Voxels[x + y * SizeX + z * SizeX * SizeY];
        }

        public void Set(int x, int y, int z, byte value)
        {
            if (InBounds(x, y, z))
                Voxels[x + y * SizeX + z * SizeX * SizeY] = value;
        }

        /// <summary>
        /// Заполняет прямоугольный параллелепипед вокселями указанного цвета.
        /// </summary>
        public void FillBox(int minX, int minY, int minZ, int maxX, int maxY, int maxZ, byte color)
        {
            minX = Math.Max(0, minX);
            minY = Math.Max(0, minY);
            minZ = Math.Max(0, minZ);
            maxX = Math.Min(SizeX - 1, maxX);
            maxY = Math.Min(SizeY - 1, maxY);
            maxZ = Math.Min(SizeZ - 1, maxZ);

            if (minX > maxX || minY > maxY || minZ > maxZ) return;

            int count = maxX - minX + 1;
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    int startIdx = minX + y * SizeX + z * SizeX * SizeY;
                    Array.Fill(Voxels, color, startIdx, count);
                }
            }
        }

        /// <summary>
        /// Заполняет сферу вокселями.
        /// </summary>
        public void FillSphere(float cx, float cy, float cz, float radius, byte color)
        {
            int minX = Math.Max(0, (int)Mathf.Floor(cx - radius));
            int maxX = Math.Min(SizeX - 1, (int)Mathf.Ceil(cx + radius));
            int minY = Math.Max(0, (int)Mathf.Floor(cy - radius));
            int maxY = Math.Min(SizeY - 1, (int)Mathf.Ceil(cy + radius));
            int minZ = Math.Max(0, (int)Mathf.Floor(cz - radius));
            int maxZ = Math.Min(SizeZ - 1, (int)Mathf.Ceil(cz + radius));

            float rSq = radius * radius;
            for (int z = minZ; z <= maxZ; z++)
            {
                float dz = z - cz;
                for (int y = minY; y <= maxY; y++)
                {
                    float dy = y - cy;
                    for (int x = minX; x <= maxX; x++)
                    {
                        float dx = x - cx;
                        if (dx * dx + dy * dy + dz * dz <= rSq)
                            Set(x, y, z, color);
                    }
                }
            }
        }

        /// <summary>
        /// Заполняет вертикальный эллипсоид.
        /// </summary>
        public void FillEllipsoid(float cx, float cy, float cz, float rx, float ry, float rz, byte color)
        {
            int minX = Math.Max(0, (int)Mathf.Floor(cx - rx));
            int maxX = Math.Min(SizeX - 1, (int)Mathf.Ceil(cx + rx));
            int minY = Math.Max(0, (int)Mathf.Floor(cy - ry));
            int maxY = Math.Min(SizeY - 1, (int)Mathf.Ceil(cy + ry));
            int minZ = Math.Max(0, (int)Mathf.Floor(cz - rz));
            int maxZ = Math.Min(SizeZ - 1, (int)Mathf.Ceil(cz + rz));

            for (int z = minZ; z <= maxZ; z++)
            {
                float nz = (z - cz) / rz;
                float nzSq = nz * nz;
                if (nzSq > 1f) continue;

                for (int y = minY; y <= maxY; y++)
                {
                    float ny = (y - cy) / ry;
                    float nySq = ny * ny;
                    if (nzSq + nySq > 1f) continue;

                    for (int x = minX; x <= maxX; x++)
                    {
                        float nx = (x - cx) / rx;
                        if (nx * nx + nySq + nzSq <= 1f)
                            Set(x, y, z, color);
                    }
                }
            }
        }

        /// <summary>
        /// Заполняет конический эллиптический цилиндр по оси Y (идеально для анатомического торса с V-конусом).
        /// </summary>
        public void FillTaperedEllipticY(int minY, int maxY, float cx, float cz, float rxBottom, float rxTop, float rzBottom, float rzTop, byte color)
        {
            minY = Math.Max(0, minY);
            maxY = Math.Min(SizeY - 1, maxY);
            if (minY > maxY) return;

            float height = Math.Max(1f, maxY - minY);
            for (int y = minY; y <= maxY; y++)
            {
                float t = (y - minY) / height;
                float rx = Mathf.Lerp(rxBottom, rxTop, t);
                float rz = Mathf.Lerp(rzBottom, rzTop, t);
                if (rx <= 0.01f || rz <= 0.01f) continue;

                int minX = Math.Max(0, (int)Mathf.Floor(cx - rx));
                int maxX = Math.Min(SizeX - 1, (int)Mathf.Ceil(cx + rx));
                int minZ = Math.Max(0, (int)Mathf.Floor(cz - rz));
                int maxZ = Math.Min(SizeZ - 1, (int)Mathf.Ceil(cz + rz));

                for (int z = minZ; z <= maxZ; z++)
                {
                    float nz = (z - cz) / rz;
                    float nzSq = nz * nz;
                    if (nzSq > 1f) continue;

                    for (int x = minX; x <= maxX; x++)
                    {
                        float nx = (x - cx) / rx;
                        if (nx * nx + nzSq <= 1f)
                            Set(x, y, z, color);
                    }
                }
            }
        }

        /// <summary>
        /// Заполняет конический круглый цилиндр по оси Y (для анатомических ног и шеи).
        /// </summary>
        public void FillTaperedCylinderY(int minY, int maxY, float cx, float cz, float rBottom, float rTop, byte color)
        {
            FillTaperedEllipticY(minY, maxY, cx, cz, rBottom, rTop, rBottom, rTop, color);
        }

        /// <summary>
        /// Заполняет конический круглый цилиндр по оси X (для анатомических рук со сужением к запястью).
        /// </summary>
        public void FillTaperedCylinderX(int minX, int maxX, float cy, float cz, float rStart, float rEnd, byte color)
        {
            minX = Math.Max(0, minX);
            maxX = Math.Min(SizeX - 1, maxX);
            if (minX > maxX) return;

            float length = Math.Max(1f, maxX - minX);
            for (int x = minX; x <= maxX; x++)
            {
                float t = (x - minX) / length;
                float r = Mathf.Lerp(rStart, rEnd, t);
                if (r <= 0.01f) continue;

                int minY = Math.Max(0, (int)Mathf.Floor(cy - r));
                int maxY = Math.Min(SizeY - 1, (int)Mathf.Ceil(cy + r));
                int minZ = Math.Max(0, (int)Mathf.Floor(cz - r));
                int maxZ = Math.Min(SizeZ - 1, (int)Mathf.Ceil(cz + r));
                float rSq = r * r;

                for (int z = minZ; z <= maxZ; z++)
                {
                    float dz = z - cz;
                    for (int y = minY; y <= maxY; y++)
                    {
                        float dy = y - cy;
                        if (dy * dy + dz * dz <= rSq)
                            Set(x, y, z, color);
                    }
                }
            }
        }

        /// <summary>
        /// Заполняет капсулу (3D отрезок с радиусом скругления) - сглаживает изгибы и суставы.
        /// </summary>
        public void FillCapsule(Vector3 p0, Vector3 p1, float radius, byte color)
        {
            Vector3 min = Vector3.Min(p0, p1) - Vector3.one * radius;
            Vector3 max = Vector3.Max(p0, p1) + Vector3.one * radius;

            int minX = Math.Max(0, (int)Mathf.Floor(min.x));
            int maxX = Math.Min(SizeX - 1, (int)Mathf.Ceil(max.x));
            int minY = Math.Max(0, (int)Mathf.Floor(min.y));
            int maxY = Math.Min(SizeY - 1, (int)Mathf.Ceil(max.y));
            int minZ = Math.Max(0, (int)Mathf.Floor(min.z));
            int maxZ = Math.Min(SizeZ - 1, (int)Mathf.Ceil(max.z));

            Vector3 line = p1 - p0;
            float lineLenSq = line.sqrMagnitude;
            float rSq = radius * radius;

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        Vector3 p = new Vector3(x, y, z);
                        float t = lineLenSq > 0.0001f ? Mathf.Clamp01(Vector3.Dot(p - p0, line) / lineLenSq) : 0f;
                        Vector3 closest = p0 + line * t;
                        if ((p - closest).sqrMagnitude <= rSq)
                            Set(x, y, z, color);
                    }
                }
            }
        }

        /// <summary>
        /// Добавляет выступающий шип указанной длины.
        /// </summary>
        public void AddSpike(int startX, int startY, int startZ, int dirX, int dirY, int dirZ, int length, byte color)
        {
            for (int step = 0; step < length; step++)
            {
                Set(startX + dirX * step, startY + dirY * step, startZ + dirZ * step, color);
            }
        }

        /// <summary>
        /// Количество занятых вокселей.
        /// </summary>
        public int CountSolidVoxels()
        {
            int count = 0;
            for (int i = 0; i < Voxels.Length; i++)
            {
                if (Voxels[i] != SurvivorPalette.Air)
                    count++;
            }
            return count;
        }
    }
}
