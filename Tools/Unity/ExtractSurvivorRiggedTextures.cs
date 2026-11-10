using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Достаёт вшитые текстуры из SurvivorRigged.fbx, собирает материал URP Lit и подставляет его модели
// вместо встроенного материала "Material", у которого Unity текстуры не подхватывает.
// Металл и шероховатость лежат в FBX отдельными картинками, а URP Lit ждёт одну:
// металл в красном канале, гладкость (1 - шероховатость) в альфе. Скрипт собирает её сам.
// Запуск через Unity CLI (из корня проекта):
//     unity command run_script --file Tools/Unity/ExtractSurvivorRiggedTextures.cs --project-path "D:\Game Tactick"
public static class ExtractSurvivorRiggedTextures
{
    const string ModelPath = "Assets/Characters/Survivor/SurvivorRigged.fbx";
    const string TexturesFolder = "Assets/Characters/Survivor/SurvivorRigged_Textures";
    const string BaseName = "texture_pbr_20250901";
    const string MaterialPath = "Assets/Characters/Survivor/Materials/SurvivorRigged.mat";

    public static string Main()
    {
        var log = new StringBuilder();
        var importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);

        if (!AssetDatabase.IsValidFolder(TexturesFolder))
        {
            AssetDatabase.CreateFolder(Path.GetDirectoryName(TexturesFolder).Replace('\\', '/'), Path.GetFileName(TexturesFolder));
            log.AppendLine($"ExtractTextures: {importer.ExtractTextures(TexturesFolder)}");
            AssetDatabase.Refresh();
        }

        string Tex(string suffix) => $"{TexturesFolder}/{BaseName}{suffix}.png";
        var metalSmoothPath = Tex("_metallic_smoothness");
        WriteMetallicSmoothness(Tex("_metallic"), Tex("_roughness"), metalSmoothPath);
        AssetDatabase.ImportAsset(metalSmoothPath);

        SetImport(Tex("_normal"), t => t.textureType = TextureImporterType.NormalMap);
        SetImport(metalSmoothPath, t => t.sRGBTexture = false);

        var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(mat, MaterialPath);
        }
        mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Tex("")));
        mat.SetColor("_BaseColor", Color.white);
        mat.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Tex("_normal")));
        mat.EnableKeyword("_NORMALMAP");
        mat.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>(metalSmoothPath));
        mat.EnableKeyword("_METALLICSPECGLOSSMAP");
        mat.SetFloat("_Smoothness", 1f);
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();

        importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "Material"), mat);
        importer.SaveAndReimport();

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            log.AppendLine($"renderer {r.name}: {string.Join(", ", r.sharedMaterials.Select(m => AssetDatabase.GetAssetPath(m)))}");
        foreach (var prop in mat.GetTexturePropertyNames())
        {
            var tex = mat.GetTexture(prop);
            if (tex != null)
                log.AppendLine($"  {prop} = {AssetDatabase.GetAssetPath(tex)}");
        }
        return log.ToString();
    }

    static void SetImport(string path, System.Action<TextureImporter> change)
    {
        var t = (TextureImporter)AssetImporter.GetAtPath(path);
        change(t);
        t.SaveAndReimport();
    }

    static void WriteMetallicSmoothness(string metallicPath, string roughnessPath, string outPath)
    {
        var metallic = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        var roughness = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        metallic.LoadImage(File.ReadAllBytes(metallicPath));
        roughness.LoadImage(File.ReadAllBytes(roughnessPath));
        var m = metallic.GetPixels32();
        var r = roughness.GetPixels32();
        var result = new Color32[m.Length];
        for (int i = 0; i < m.Length; i++)
            result[i] = new Color32(m[i].r, m[i].r, m[i].r, (byte)(255 - r[i].r));
        var output = new Texture2D(metallic.width, metallic.height, TextureFormat.RGBA32, false, true);
        output.SetPixels32(result);
        File.WriteAllBytes(outPath, output.EncodeToPNG());
        Object.DestroyImmediate(metallic);
        Object.DestroyImmediate(roughness);
        Object.DestroyImmediate(output);
    }
}
