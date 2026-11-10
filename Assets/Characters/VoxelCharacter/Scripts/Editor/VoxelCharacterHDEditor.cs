using UnityEngine;
using UnityEditor;

namespace VoxelCharacterSystem.Editor
{
    [CustomEditor(typeof(VoxelCharacterMultipart))]
    public class VoxelCharacterHDEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var character = (VoxelCharacterMultipart)target;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Управление HD генерацией (по частям)", EditorStyles.boldLabel);

            if (GUILayout.Button("1. Сгенерировать HD персонажа", GUILayout.Height(32)))
            {
                Undo.RecordObject(character, "Generate HD Voxel Character");
                character.Generate();
                EditorUtility.SetDirty(character);
            }

            if (GUILayout.Button("2. Сохранить меши и префаб в Assets", GUILayout.Height(28)))
            {
                character.SaveAssets();
            }
        }

        [MenuItem("Tools/Voxel Arena/Generate Voxel Character HD (Multipart)", priority = 21)]
        public static void GenerateHDCharacterInScene()
        {
            var existing = Object.FindFirstObjectByType<VoxelCharacterMultipart>();
            GameObject go;
            if (existing != null)
            {
                go = existing.gameObject;
            }
            else
            {
                go = new GameObject("Voxel Survivor HD");
                // Ставим рядом с базовым вариантом для наглядного сравнения (смещение по X = 1.5м)
                go.transform.position = new Vector3(1.5f, 0f, 0f);
            }

            var multipart = go.GetComponent<VoxelCharacterMultipart>();
            if (multipart == null)
                multipart = go.AddComponent<VoxelCharacterMultipart>();

            multipart.CharacterMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Characters/Survivor/Materials/Survivor_Palette.mat");
            multipart.Generate();
            multipart.SaveAssets();

            Selection.activeGameObject = go;
            Debug.Log("[VoxelCharacterMultipart] HD персонаж успешно сгенерирован и размещен в сцене!");
        }
    }
}
