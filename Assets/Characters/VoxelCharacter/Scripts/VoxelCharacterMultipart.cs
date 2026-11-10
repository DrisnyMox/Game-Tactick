using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VoxelCharacterSystem
{
    /// <summary>
    /// Компонент высокодетализированного (HD) воксельного персонажа, собираемого по частям.
    /// Поддерживает:
    /// 1. Иерархическую структуру (Transform-иерархия с отдельными мешами для головы, торса, рук, ног),
    ///    готовую для анимации и риггинга.
    /// 2. Объединение всех частей в единый запечённый меш (Combined Mesh) для максимальной производительности отрисовки.
    /// </summary>
    [ExecuteInEditMode]
    public class VoxelCharacterMultipart : MonoBehaviour
    {
        [Header("Настройки HD вокселей")]
        [Tooltip("Размер одного вокселя в мировых единицах (0.008м = персонаж ростом ~1.95м)")]
        [SerializeField] float voxelSize = 0.008f;

        [Tooltip("Материал персонажа (по умолчанию Survivor_Palette.mat)")]
        [SerializeField] Material characterMaterial;

        [Tooltip("Сглаживать нормали на стыках вокселей (делает руки, ноги и голову круглыми вместо квадратных)")]
        [SerializeField] bool smoothNormals = true;

        [Tooltip("Создавать дочерние объекты для каждой части тела")]
        [SerializeField] bool createHierarchyParts = true;

        [Tooltip("Добавить BoxCollider на персонажа")]
        [SerializeField] bool addCollider = true;

        [Header("Статистика HD билда")]
        [SerializeField] int totalSolidVoxels;
        [SerializeField] int totalVertices;
        [SerializeField] int totalTriangles;

        public float VoxelSize
        {
            get => voxelSize;
            set => voxelSize = value;
        }

        public Material CharacterMaterial
        {
            get => characterMaterial;
            set => characterMaterial = value;
        }

        public int TotalSolidVoxels => totalSolidVoxels;
        public int TotalVertices => totalVertices;
        public int TotalTriangles => totalTriangles;

        /// <summary>
        /// Генерирует высокодетализированного персонажа по частям.
        /// </summary>
        [ContextMenu("Сгенерировать HD персонажа")]
        public void Generate()
        {
            // Убеждаемся в наличии материала
            if (characterMaterial == null)
            {
#if UNITY_EDITOR
                characterMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Characters/Survivor/Materials/Survivor_Palette.mat");
                if (characterMaterial == null)
                    characterMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Characters/PostApocalypticSurvivor/Materials/M_Survivor.mat");
#endif
            }

            // 1. Построение всех анатомических частей в вокселях
            List<VoxelCharacterPart> parts = VoxelSurvivorHDBuilder.BuildAllParts();

            totalSolidVoxels = 0;
            totalVertices = 0;
            totalTriangles = 0;

            var combineInstances = new List<CombineInstance>();

            // 2. Генерация меша для каждой части
            foreach (var part in parts)
            {
                totalSolidVoxels += part.Model.CountSolidVoxels();

                Mesh partMesh = part.GenerateMesh(voxelSize, smoothNormals);
                totalVertices += partMesh.vertexCount;
                totalTriangles += partMesh.triangles.Length / 3;

                // Для Combined Mesh
                var ci = new CombineInstance
                {
                    mesh = partMesh,
                    transform = Matrix4x4.TRS(part.LocalPosition, part.LocalRotation, Vector3.one)
                };
                combineInstances.Add(ci);

                // Создание/обновление дочернего объекта в иерархии
                if (createHierarchyParts)
                {
                    Transform child = transform.Find(part.Name);
                    GameObject childGo;
                    if (child == null)
                    {
                        childGo = new GameObject(part.Name);
                        childGo.transform.SetParent(transform, false);
                    }
                    else
                    {
                        childGo = child.gameObject;
                    }

                    childGo.transform.localPosition = part.LocalPosition;
                    childGo.transform.localRotation = part.LocalRotation;

                    var mf = childGo.GetComponent<MeshFilter>();
                    if (mf == null) mf = childGo.AddComponent<MeshFilter>();
                    mf.sharedMesh = partMesh;

                    var mr = childGo.GetComponent<MeshRenderer>();
                    if (mr == null) mr = childGo.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = characterMaterial;
                }
            }

            // 3. Создание объединённого единого меша на корневом объекте
            Mesh combinedMesh = new Mesh
            {
                name = "VoxelSurvivor_HD_CombinedMesh"
            };
            if (totalVertices > 65535)
                combinedMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            combinedMesh.CombineMeshes(combineInstances.ToArray(), true, true);
            combinedMesh.RecalculateNormals();
            combinedMesh.RecalculateTangents();
            combinedMesh.RecalculateBounds();

            // Если иерархия отключена, выводим объединённый меш на сам корень
            if (!createHierarchyParts)
            {
                var mf = GetComponent<MeshFilter>();
                if (mf == null) mf = gameObject.AddComponent<MeshFilter>();
                mf.sharedMesh = combinedMesh;

                var mr = GetComponent<MeshRenderer>();
                if (mr == null) mr = gameObject.AddComponent<MeshRenderer>();
                mr.sharedMaterial = characterMaterial;
            }

            // 4. Обновление BoxCollider
            if (addCollider)
            {
                var col = GetComponent<BoxCollider>();
                if (col == null) col = gameObject.AddComponent<BoxCollider>();
                col.center = combinedMesh.bounds.center;
                col.size = combinedMesh.bounds.size;
            }

            Debug.Log($"[VoxelCharacterMultipart] Сгенерирован HD персонаж: {parts.Count} частей, {totalSolidVoxels} вокселей -> {totalVertices} вершин, {totalTriangles} треугольников.");
        }

#if UNITY_EDITOR
        /// <summary>
        /// Сохраняет объединённый меш и все части как ассеты в проекте.
        /// </summary>
        public void SaveAssets(string folderPath = null)
        {
            if (string.IsNullOrEmpty(folderPath))
            {
                if (AssetDatabase.IsValidFolder("Assets/Characters/PostApocalypticSurvivor"))
                    folderPath = "Assets/Characters/PostApocalypticSurvivor/HD";
                else if (AssetDatabase.IsValidFolder("Assets/Characters/Survivor"))
                    folderPath = "Assets/Characters/Survivor/HD";
                else
                    folderPath = "Assets/Characters/VoxelCharacter/HD";
            }

            EnsureFolderExists(folderPath);

            // Сохраняем меши дочерних частей
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                var mf = child.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    string partPath = $"{folderPath}/Mesh_{child.name}.asset";
                    SaveOrUpdateMeshAsset(mf.sharedMesh, partPath);
                    mf.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(partPath);
                }
            }

            // Сохраняем префаб
            string prefabPath = $"{folderPath}/VoxelSurvivor_HD.prefab";
            PrefabUtility.SaveAsPrefabAsset(gameObject, prefabPath);
            AssetDatabase.SaveAssets();

            Debug.Log($"[VoxelCharacterMultipart] HD персонаж успешно сохранён в: {folderPath}");
        }

        static void EnsureFolderExists(string folderPath)
        {
            string[] parts = folderPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        static void SaveOrUpdateMeshAsset(Mesh mesh, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                existing.Clear();
                existing.indexFormat = mesh.indexFormat;
                existing.vertices = mesh.vertices;
                existing.normals = mesh.normals;
                existing.uv = mesh.uv;
                existing.colors32 = mesh.colors32;
                existing.triangles = mesh.triangles;
                existing.tangents = mesh.tangents;
                existing.RecalculateBounds();
                EditorUtility.SetDirty(existing);
            }
            else
            {
                Mesh copy = Instantiate(mesh);
                copy.name = mesh.name;
                AssetDatabase.CreateAsset(copy, path);
            }
        }
#endif
    }
}
