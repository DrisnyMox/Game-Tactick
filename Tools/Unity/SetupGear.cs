using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Надевает части снаряжения из Assets/Models/Gear на игрока (вариант PlayerSurvivor8b8745d5).
// Части готовят Tools/Blender/fit_gear.py (подгонка), cut_gear.py (вырезание из тела), place_gear.py (жёсткая часть
// без смены формы), place_pants.py или place_chest.py (меш, вырезанный из модели генератора): <часть>.fbx (меш на скелете тела), <часть>_Textures, <часть>_BodyMask.json (вершины тела под частью, координаты Blender).
//  1. материал из текстур части на шейдере Game Tactick/Gear (URP Lit с покраской в цвет качества); металл
//     с гладкостью fit_gear.py уже свёл в одну картинку. Маска цвета качества <часть>_quality.png
//     (Tools/Blender/quality_mask.py) — если есть, и средняя яркость текстуры под ней;
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
    // Маска плавная, на экране полоса узкая: полный размер не нужен
    const int MaskTextureSize = 1024;
    const string ShaderName = "Game Tactick/Gear";
    // На вариант ставятся все части из Folder: что надеть, в игре решает профиль (Gear.Dress, окно снаряжения
    // EquipmentScreen). ClothChest подгоняет fit_gear.py, ClothGloves вырезает из тела cut_gear.py, ClothBoots
    // NomadBoots и SunShoes ставит без смены формы place_gear.py, ClothPantsRope, NomadPants и SunPants - place_pants.py,
    // NomadChest - place_chest.py, SunChest - place_chest_fitted.py, SunHead - place_head_fitted.py, NomadGloves -
    // place_gloves.py: файлы у всех одинаковые. Parts - части, включённые в префабе: так игрок одет в сценах без профиля
    // (SampleScene, SurvivorPlayground) и в пробах снаряжения; остальные стоят выключенными
    static readonly string[] Parts = { "SunHead", "SunChest", "NomadGloves", "SunShoes", "SunPants" };
    // Части из одного слоя ткани с открытыми краями: у NomadChest между поясом-обмоткой и подолом, у горловины
    // и под накидкой видна изнанка, у NomadPants - внутри широкой штанины шорт и с изнанки лент обмоток, у NomadGloves -
    // в отверстиях для пальцев и в манжете, у SunPants - внутри пояса сверху, у SunChest - в проймах, в разрезе подола
    // спереди и под наплечником, у SunHead - снизу под колпаком (полоса через макушку - один слой). С одной стороной
    // граней в эти щели был виден фон (тело под частью спрятано)
    static readonly string[] DoubleSided = { "NomadChest", "NomadPants", "NomadGloves", "SunPants", "SunChest", "SunHead" };

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

        var all = AssetDatabase.FindAssets("t:Model", new[] { Folder.TrimEnd('/') })
            .Select(g => System.IO.Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(g))).OrderBy(n => n).ToArray();
        foreach (var name in Parts)
            if (!all.Contains(name))
                throw new System.InvalidOperationException($"{name}: нет {Folder}{name}.fbx");

        foreach (var part in all)
        {
            var material = BuildMaterial(part, log);
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

            piece.gameObject.SetActive(Parts.Contains(part));

            var hidden = HiddenVertices(part, body.sharedMesh, bodyImporter.globalScale, log);
            pieces.Add((smr, hidden));
            log.AppendLine($"{part}: verts {source.sharedMesh.vertexCount}, hidden body triangles " +
                           $"{HiddenTriangleCount(body.sharedMesh, hidden)}" + (Parts.Contains(part) ? ", надета" : ""));
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
            var verts = item.FindPropertyRelative("hiddenVertices");
            verts.arraySize = pieces[i].hidden.Length;
            for (int k = 0; k < pieces[i].hidden.Length; k++)
                verts.GetArrayElementAtIndex(k).intValue = pieces[i].hidden[k];
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

    // Вершины тела под частью (номера в меше тела). Треугольники прячет WornGear по всем надетым частям вместе:
    // треугольник кожи у щиколотки (4 см) одной вершиной под штанами, другой под кедами - по отдельности его не
    // прятала ни одна часть, и кожа проступала между низом штанин и голенищем
    static int[] HiddenVertices(string part, Mesh body, float scale, StringBuilder log)
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
        log.AppendLine($"{part}: mask {mask.hidden.Length / 3} positions -> {hiddenVerts.Count} body vertices");
        return hiddenVerts.OrderBy(i => i).ToArray();
    }

    // Для отчёта: сколько треугольников тела часть прячет одна
    static int HiddenTriangleCount(Mesh body, int[] hiddenVerts)
    {
        var set = new HashSet<int>(hiddenVerts);
        int count = 0;
        for (int sub = 0; sub < body.subMeshCount; sub++)
        {
            var tris = body.GetTriangles(sub);
            for (int t = 0; t < tris.Length / 3; t++)
                if (set.Contains(tris[t * 3]) && set.Contains(tris[t * 3 + 1]) && set.Contains(tris[t * 3 + 2]))
                    count++;
        }
        return count;
    }

    static Material BuildMaterial(string part, StringBuilder log)
    {
        string Tex(string suffix) => $"{Folder}{part}_Textures/{part}{suffix}.png";
        SetImport(Tex(""), MaxTextureSize, t => { });
        SetImport(Tex("_normal"), MaxTextureSize, t => t.textureType = TextureImporterType.NormalMap);
        SetImport(Tex("_metallic_smoothness"), MaxTextureSize, t => t.sRGBTexture = false);
        // Маска цвета качества (Tools/Blender/quality_mask.py) есть не у всех: базовый набор серый, его не красят
        bool hasMask = File.Exists(Tex("_quality"));
        if (hasMask)
            SetImport(Tex("_quality"), MaskTextureSize, t =>
            {
                t.sRGBTexture = false;
                t.textureCompression = TextureImporterCompression.CompressedHQ;
            });

        var path = Folder + part + ".mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find(ShaderName));
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = Shader.Find(ShaderName);
        mat.SetTexture("_QualityMask", hasMask ? AssetDatabase.LoadAssetAtPath<Texture2D>(Tex("_quality")) : null);
        mat.SetColor("_QualityColor", Color.white);
        float luma = hasMask ? MaskedLuma(Tex(""), Tex("_quality")) : 0.5f;
        mat.SetFloat("_QualityLuma", luma);
        if (hasMask)
            log.AppendLine($"{part}: маска цвета качества, средняя яркость полосы {luma:F3}");
        mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Tex("")));
        mat.SetColor("_BaseColor", Color.white);
        mat.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Tex("_normal")));
        mat.EnableKeyword("_NORMALMAP");
        mat.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Tex("_metallic_smoothness")));
        mat.EnableKeyword("_METALLICSPECGLOSSMAP");
        mat.SetFloat("_Smoothness", 1f);
        bool twoSided = DoubleSided.Contains(part);
        mat.SetFloat("_Cull", (float)(twoSided ? UnityEngine.Rendering.CullMode.Off : UnityEngine.Rendering.CullMode.Back));
        mat.doubleSidedGI = twoSided;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static void SetImport(string path, int maxSize, System.Action<TextureImporter> change)
    {
        AssetDatabase.ImportAsset(path);
        var t = (TextureImporter)AssetImporter.GetAtPath(path);
        t.maxTextureSize = maxSize;
        change(t);
        t.SaveAndReimport();
    }

    // Средняя линейная яркость текстуры под маской: шейдер делит на неё яркость пикселя, и середина полосы
    // становится ровно цветом качества. Файлы читаются с диска: импортированные текстуры сжаты и не читаются
    static float MaskedLuma(string colorPath, string maskPath)
    {
        var color = new Texture2D(2, 2);
        var mask = new Texture2D(2, 2);
        color.LoadImage(File.ReadAllBytes(colorPath));
        mask.LoadImage(File.ReadAllBytes(maskPath));
        if (color.width != mask.width || color.height != mask.height)
            throw new System.InvalidOperationException($"{maskPath}: {mask.width}x{mask.height}, а у текстуры {color.width}x{color.height}");
        var c = color.GetPixels32();
        var m = mask.GetPixels32();
        double sum = 0, weight = 0;
        for (int i = 0; i < c.Length; i++)
        {
            float w = m[i].r / 255f;
            if (w < 0.5f)
                continue;
            var linear = ((Color)c[i]).linear;
            sum += w * (0.2126 * linear.r + 0.7152 * linear.g + 0.0722 * linear.b);
            weight += w;
        }
        Object.DestroyImmediate(color);
        Object.DestroyImmediate(mask);
        if (weight == 0)
            throw new System.InvalidOperationException($"{maskPath}: маска пустая");
        return (float)(sum / weight);
    }
}
