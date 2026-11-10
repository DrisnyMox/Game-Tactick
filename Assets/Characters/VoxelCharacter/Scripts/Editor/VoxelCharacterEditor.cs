using UnityEngine;
using UnityEditor;

namespace VoxelCharacterSystem.Editor
{
    [CustomEditor(typeof(VoxelCharacter))]
    public class VoxelCharacterEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var character = (VoxelCharacter)target;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Управление воксельной генерацией", EditorStyles.boldLabel);

            if (GUILayout.Button("1. Сгенерировать воксельный меш", GUILayout.Height(32)))
            {
                Undo.RecordObject(character, "Generate Voxel Character Mesh");
                character.GenerateMesh();
                EditorUtility.SetDirty(character);
            }

            if (GUILayout.Button("2. Сохранить меш как ассет (.asset)", GUILayout.Height(28)))
            {
                character.SaveMeshToAsset();
            }

            if (GUILayout.Button("3. Создать / Обновить префаб", GUILayout.Height(28)))
            {
                CreatePrefab(character.gameObject);
            }
        }

        [MenuItem("Tools/Voxel Arena/Generate Voxel Character (Survivor)", priority = 20)]
        public static void GenerateCharacterInScene()
        {
            var existing = Object.FindFirstObjectByType<VoxelCharacter>();
            GameObject go;
            if (existing != null)
            {
                go = existing.gameObject;
            }
            else
            {
                go = new GameObject("Voxel Survivor");
                go.transform.position = new Vector3(0, 0, 0);
            }

            var voxelChar = go.GetComponent<VoxelCharacter>();
            if (voxelChar == null)
                voxelChar = go.AddComponent<VoxelCharacter>();

            voxelChar.CharacterMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Characters/Survivor/Materials/Survivor_Palette.mat");
            voxelChar.GenerateMesh();
            voxelChar.SaveMeshToAsset();

            CreatePrefab(go);

            Selection.activeGameObject = go;
            Debug.Log("[VoxelCharacter] Персонаж успешно сгенерирован и выделен в сцене!");
        }

        static void CreatePrefab(GameObject characterGo)
        {
            string prefabPath = "Assets/Characters/Survivor/VoxelSurvivor_Generated.prefab";
            PrefabUtility.SaveAsPrefabAsset(characterGo, prefabPath);
            Debug.Log($"[VoxelCharacter] Префаб успешно создан: {prefabPath}");
        }
    }
}
