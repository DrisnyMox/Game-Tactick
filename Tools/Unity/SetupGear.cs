using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Надевает части снаряжения из Assets/Models/Gear на игрока (вариант PlayerSurvivor8b8745d5).
// Части готовят Tools/Blender/fit_gear.py (подгонка) или cut_gear.py (вырезание из тела): <часть>.fbx (меш
// на скелете тела), <часть>_Textures, <часть>_BodyMask.json (вершины тела под частью, координаты Blender).
//  1. материал URP Lit из текстур части; металл с гладкостью fit_gear.py уже свёл в одну картинку;
//  2. импорт FBX части с масштабом модели тела: тогда позы покоя костей части (bindposes) совпадают с телом,
//     и кости привязываются по именам без пересчёта. Совпадение проверяется;
//  3. в варианте каждая часть - дочерний объект со своим SkinnedMeshRenderer, кости - скелет варианта;
//  4. зона тела под частью: вершины тела находятся по положениям из маски (Unity = (-x, z, -y) Blender,
//     умножить на масштаб импорта), прячутся треугольники тела, у которых спрятаны все три вершины.
//     Номера треугольников пишутся в WornGear на корне варианта.
// Скрипт можно запускать повторно. SetupPlayerModel.cs берёт первый SkinnedMeshRenderer варианта как тело,
// поэтому части ставятся после тела.
// Запуск через Unity CLI (из корня проекта):
//     unity command run_script --file Tools/Unity/SetupGear.cs
public static class SetupGear
{
    const string Folder = "Assets/Models/Gear/";
    const string BodyModelPath = "Assets/Models/20261121003938_8b8745d5.fbx";
    const string VariantPath = "Assets/Characters/Survivor/PlayerSurvivor8b8745d5.prefab";
    const string BodyName = "Survivor_Body";
    const int MaxTextureSize = 2048;
    // ClothChest подгоняет fit_gear.py, ClothGloves вырезает из тела cut_gear.py: файлы у обоих одинаковые
    static readonly string[] Parts = { "ClothChest", "ClothGloves" };

    // Заполняет JsonUtility
#pragma warning disable CS0649
    [System.Serializable]
    class BodyMask
    {
        public float[] hidden;
    }
#pragma warning restore CS0649

    public static string Main()
    {
        var log = new StringBuilder();
        // fit_gear.py пишет файлы мимо Editor: без обновления Unity взял бы прежние FBX и маску
        AssetDatabase.Refresh();
        var bodyImporter = (ModelImporter)AssetImporter.GetAtPath(BodyModelPath);
        // WornGear в игре копирует меш тела и убирает из копии треугольники под частями: без Read/Write
        // Unity не даёт читать и менять треугольники ни в Editor, ни в сборке
        if (!bodyImporter.isReadable)
        {
            bodyImporter.isReadable = true;
            bodyImporter.SaveAndReimport();
            log.AppendLine($"{BodyModelPath}: включён Read/Write");
        }
        var bodyModelSmr = AssetDatabase.LoadAssetAtPath<GameObject>(BodyModelPath).GetComponentInChildren<SkinnedMeshRenderer>();

        var root = PrefabUtility.LoadPrefabContents(VariantPath);
        var body = root.transform.Find(BodyName).GetComponent<SkinnedMeshRenderer>();
        if (body.sharedMesh != bodyModelSmr.sharedMesh)
            throw new System.InvalidOperationException($"{VariantPath}: тело {body.sharedMesh.name}, а не меш {BodyModelPath}");
        var own = root.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
        var pieces = new List<(SkinnedMeshRenderer renderer, int[] hidden)>();

        foreach (var part in Parts)
        {
            var material = BuildMaterial(part);
            var modelPath = Folder + part + ".fbx";
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            importer.globalScale = bodyImporter.globalScale;
            importer.useFileScale = bodyImporter.useFileScale;
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.NoAvatar;
            importer.importAnimation = false;
            importer.importBlendShapes = false;
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "Material"), material);
            importer.SaveAndReimport();

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath).GetComponentInChildren<SkinnedMeshRenderer>();
            CheckBindposes(source, bodyModelSmr);

            var piece = root.transform.Find(part);
            if (piece == null)
            {
                piece = new GameObject(part).transform;
                piece.SetParent(root.transform, false);
            }
            piece.SetAsLastSibling();
            piece.SetLocalPositionAndRotation(source.transform.localPosition, source.transform.localRotation);
            piece.localScale = source.transform.localScale;
            var smr = piece.GetComponent<SkinnedMeshRenderer>();
            if (smr == null)
                smr = piece.gameObject.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = source.sharedMesh;
            smr.sharedMaterials = new[] { material };
            smr.bones = source.bones.Select(b => own[b.name]).ToArray();
            smr.rootBone = body.rootBone;
            // Границы тела: часть всегда внутри них, отдельный пересчёт не нужен
            smr.localBounds = body.localBounds;

            var hidden = HiddenTriangles(part, body.sharedMesh, bodyImporter.globalScale, log);
            pieces.Add((smr, hidden));
            log.AppendLine($"{part}: verts {source.sharedMesh.vertexCount}, hidden body triangles {hidden.Length}");
        }

        var worn = root.GetComponent<WornGear>();
        if (worn == null)
            worn = root.AddComponent<WornGear>();
        var so = new SerializedObject(worn);
        so.FindProperty("body").objectReferenceValue = body;
        var list = so.FindProperty("pieces");
        list.arraySize = pieces.Count;
        for (int i = 0; i < pieces.Count; i++)
        {
            var item = list.GetArrayElementAtIndex(i);
            item.FindPropertyRelative("renderer").objectReferenceValue = pieces[i].renderer;
            var tris = item.FindPropertyRelative("hiddenTriangles");
            tris.arraySize = pieces[i].hidden.Length;
            for (int k = 0; k < pieces[i].hidden.Length; k++)
                tris.GetArrayElementAtIndex(k).intValue = pieces[i].hidden[k];
        }
        so.ApplyModifiedPropertiesWithoutUndo();

        PrefabUtility.SaveAsPrefabAsset(root, VariantPath);
        PrefabUtility.UnloadPrefabContents(root);
        AssetDatabase.SaveAssets();
        return log.ToString();
    }

    // Кости части привязываются к скелету тела по именам, поэтому позы покоя должны совпасть. Проверяются кости
    // с весами части: концевые кости пальцев Blender при импорте поворачивает по-своему, но часть они не двигают
    static void CheckBindposes(SkinnedMeshRenderer part, SkinnedMeshRenderer body)
    {
        var bodyBind = body.bones.Select((b, i) => (b.name, body.sharedMesh.bindposes[i])).ToDictionary(x => x.name, x => x.Item2);
        var used = new HashSet<int>(part.sharedMesh.boneWeights.SelectMany(w => new[]
        {
            (w.boneIndex0, w.weight0), (w.boneIndex1, w.weight1), (w.boneIndex2, w.weight2), (w.boneIndex3, w.weight3),
        }).Where(x => x.Item2 > 0f).Select(x => x.Item1));
        foreach (int i in used)
        {
            var a = part.sharedMesh.bindposes[i];
            var b = bodyBind[part.bones[i].name];
            for (int k = 0; k < 16; k++)
                if (Mathf.Abs(a[k] - b[k]) > 1e-4f)
                    throw new System.InvalidOperationException(
                        $"{part.sharedMesh.name}: поза покоя кости {part.bones[i].name} не как у тела ({a[k]} и {b[k]})");
        }
    }

    static int[] HiddenTriangles(string part, Mesh body, float scale, StringBuilder log)
    {
        var mask = JsonUtility.FromJson<BodyMask>(AssetDatabase.LoadAssetAtPath<TextAsset>(Folder + part + "_BodyMask.json").text);
        var vertices = body.vertices;
        var hiddenVerts = new HashSet<int>();
        const float eps = 1e-4f;
        for (int k = 0; k < mask.hidden.Length; k += 3)
        {
            var p = new Vector3(-mask.hidden[k], mask.hidden[k + 2], -mask.hidden[k + 1]) * scale;
            int found = 0;
            float nearest = float.MaxValue;
            for (int i = 0; i < vertices.Length; i++)
            {
                float d = (vertices[i] - p).sqrMagnitude;
                nearest = Mathf.Min(nearest, d);
                if (d < eps * eps)
                {
                    hiddenVerts.Add(i);
                    found++;
                }
            }
            if (found == 0)
                throw new System.InvalidOperationException(
                    $"{part}: вершина маски {p} не найдена на теле, ближайшая в {Mathf.Sqrt(nearest):F5} м");
        }
        var result = new List<int>();
        int first = 0;
        for (int sub = 0; sub < body.subMeshCount; sub++)
        {
            var tris = body.GetTriangles(sub);
            for (int t = 0; t < tris.Length / 3; t++)
                if (hiddenVerts.Contains(tris[t * 3]) && hiddenVerts.Contains(tris[t * 3 + 1]) && hiddenVerts.Contains(tris[t * 3 + 2]))
                    result.Add(first + t);
            first += tris.Length / 3;
        }
        log.AppendLine($"{part}: mask {mask.hidden.Length / 3} positions -> {hiddenVerts.Count} body vertices");
        return result.ToArray();
    }

    static Material BuildMaterial(string part)
    {
        string Tex(string suffix) => $"{Folder}{part}_Textures/{part}{suffix}.png";
        SetImport(Tex(""), t => { });
        SetImport(Tex("_normal"), t => t.textureType = TextureImporterType.NormalMap);
        SetImport(Tex("_metallic_smoothness"), t => t.sRGBTexture = false);

        var path = Folder + part + ".mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Tex("")));
        mat.SetColor("_BaseColor", Color.white);
        mat.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Tex("_normal")));
        mat.EnableKeyword("_NORMALMAP");
        mat.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Tex("_metallic_smoothness")));
        mat.EnableKeyword("_METALLICSPECGLOSSMAP");
        mat.SetFloat("_Smoothness", 1f);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static void SetImport(string path, System.Action<TextureImporter> change)
    {
        AssetDatabase.ImportAsset(path);
        var t = (TextureImporter)AssetImporter.GetAtPath(path);
        t.maxTextureSize = MaxTextureSize;
        change(t);
        t.SaveAndReimport();
    }
}
