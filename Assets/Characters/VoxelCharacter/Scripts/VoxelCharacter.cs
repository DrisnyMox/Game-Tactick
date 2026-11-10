using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VoxelCharacterSystem
{
    /// <summary>
    /// Компонент воксельного персонажа. Отвечает за генерацию воксельной модели,
    /// построение меша с помощью алгоритма Greedy Meshing и прикрепление компонентов рендеринга.
    /// </summary>
    [ExecuteInEditMode]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class VoxelCharacter : MonoBehaviour
    {
        [Header("Настройки вокселей")]
        [Tooltip("Размер одного вокселя в мировых единицах (0.028м = персонаж ростом ~2.0м)")]
        [SerializeField] float voxelSize = 0.028f;

        [Tooltip("Материал персонажа (по умолчанию Survivor_Palette.mat)")]
        [SerializeField] Material characterMaterial;

        [Tooltip("Добавить BoxCollider по размерам сгенерированного персонажа")]
        [SerializeField] bool addCollider = true;

        [Header("Статистика последнего билда")]
        [SerializeField] int solidVoxelsCount;
        [SerializeField] int vertexCount;
        [SerializeField] int triangleCount;

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

        public int SolidVoxelsCount => solidVoxelsCount;
        public int VertexCount => vertexCount;
        public int TriangleCount => triangleCount;

        /// <summary>
        /// Генерирует воксельную модель и строит оптимизированный меш персонажа.
        /// </summary>
        [ContextMenu("Сгенерировать меш")]
        public Mesh GenerateMesh()
        {
            // 1. Построение воксельной модели по референсу Survivor
            VoxelModel model = VoxelSurvivorBuilder.Build();
            solidVoxelsCount = model.CountSolidVoxels();

            // 2. Генерация полигонального меша (Greedy Meshing)
            Mesh mesh = VoxelMeshGenerator.GenerateMesh(model, voxelSize);
            mesh.name = "VoxelSurvivor_Mesh";

            vertexCount = mesh.vertexCount;
            triangleCount = mesh.triangles.Length / 3;

            // 3. Назначение компонентов рендеринга
            var meshFilter = GetComponent<MeshFilter>();
            meshFilter.sharedMesh = mesh;

            var meshRenderer = GetComponent<MeshRenderer>();
            if (characterMaterial == null)
            {
#if UNITY_EDITOR
                characterMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Characters/Survivor/Materials/Survivor_Palette.mat");
#endif
            }
            if (characterMaterial != null)
            {
                meshRenderer.sharedMaterial = characterMaterial;
            }

            // 4. Опциональный коллайдер
            if (addCollider)
            {
                var col = GetComponent<BoxCollider>();
                if (col == null)
                    col = gameObject.AddComponent<BoxCollider>();

                col.center = mesh.bounds.center;
                col.size = mesh.bounds.size;
            }

            Debug.Log($"[VoxelCharacter] Сгенерирован меш: {solidVoxelsCount} вокселей -> {vertexCount} вершин, {triangleCount} треугольников (Greedy Meshing сжал геометрию).");
            return mesh;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Сохраняет сгенерированный меш как отдельный ассет в проекте.
        /// </summary>
        public void SaveMeshToAsset(string assetPath = "Assets/Characters/Survivor/VoxelSurvivor_GeneratedMesh.asset")
        {
            var meshFilter = GetComponent<MeshFilter>();
            if (meshFilter.sharedMesh == null)
            {
                GenerateMesh();
            }

            Mesh meshToSave = Instantiate(meshFilter.sharedMesh);
            meshToSave.name = "VoxelSurvivor_GeneratedMesh";

            // Если ассет уже существует - перезаписываем его
            var existingAsset = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (existingAsset != null)
            {
                EditorUtility.CopySerialized(meshToSave, existingAsset);
                DestroyImmediate(meshToSave);
            }
            else
            {
                AssetDatabase.CreateAsset(meshToSave, assetPath);
            }

            meshFilter.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            Debug.Log($"[VoxelCharacter] Меш успешно сохранён в: {assetPath}");
        }
#endif
    }
}
