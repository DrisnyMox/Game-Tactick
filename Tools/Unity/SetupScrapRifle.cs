using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Ставит модель ScrapRifle (винтовка из металлолома под лор) в префаб SurvivorRifle вместо модели Invector.
// Запускать после SetupSurvivorShooter.cs: тот собирает SurvivorRifle заново из винтовки Invector.
//  1. достаёт вшитые в FBX текстуры и собирает материал URP Lit, как ExtractSurvivorRiggedTextures.cs:
//     металл и шероховатость в FBX отдельными картинками, а URP Lit ждёт одну (металл в красном, гладкость в альфе);
//  2. текстуры в FBX 4096, для WebGL импорт ограничен 1024: оружие на экране мелкое;
//  3. в SurvivorRifle у Model убирает меш Invector и ставит ScrapRifle дочерним объектом.
//     Точки muzzle, leftHandIK, AimReference, частицы и свет остаются прежними, поэтому положение винтовки
//     в руке (SurvivorShooter.weaponLocalPosition) пересчитывать не надо: новая модель подогнана под старую;
//  4. считает хват кистей: точки rightHandIK (новая) и leftHandIK на Model хранят положение и поворот кости кисти,
//     куда SurvivorShooter переносит кисть. Пальцы в позе Aiming@Rifle согнуты вокруг оси - по суставам каждого
//     пальца строится окружность, центры окружностей дают ось кулака. Эта ось ставится на ось пистолетной рукояти
//     (правая) или цевья (левая). Поворот вокруг оси: правая кисть как можно ближе к повороту из анимации,
//     левая - ладонью снизу цевья, запястье снизу слева.
// Запуск через Unity CLI (из корня проекта):
//     unity command run_script --file Tools/Unity/SetupScrapRifle.cs --project-path "D:\Game Tactick"
public static class SetupScrapRifle
{
    const string Folder = "Assets/Weapons/";
    const string ModelPath = Folder + "ScrapRifle.fbx";
    const string TexturesFolder = Folder + "ScrapRifle_Textures";
    const string BaseName = "texture_pbr_20250901";
    const string MaterialPath = Folder + "ScrapRifle.mat";
    const string RiflePath = Folder + "SurvivorRifle.prefab";
    const int MaxTextureSize = 1024;
    const string PlayerPath = "Assets/Characters/Survivor/PlayerSurvivor.prefab";
    const string PosesPath = "Assets/ThirdParty/Invector/Shooter/3DModels/Animations/Shooter_UpperBodyPoses.fbx";

    // Рукоять и цевьё в осях Model, замерены по срезам меша: рукоять от y -0,025 до -0,085, передний край
    // уходит назад на 0,3 м на метр высоты, толщина 0,045 по z; цевьё от z 0,075 до 0,225, высота 0,02..0,095, ширина x -0,029..0,017
    static readonly Vector3 PistolGripCenter = new(-0.005f, -0.055f, -0.165f);
    static readonly Vector3 PistolGripDown = new(0f, -1f, -0.3f);
    // Кулак у заднего края цевья: дальше вперёд левая рука в позе прицела не дотягивается
    static readonly Vector3 HandguardCenter = new(-0.006f, 0.055f, 0.085f);
    // Ладонь под цевьём, большой палец вперёд по левому боку: от указательного к мизинцу - назад
    static readonly Vector3 HandguardBack = new(0f, 0f, -1f);
    static readonly Vector3 LeftWristSide = new(-0.5f, -0.85f, 0f);

    // Подгонка замерена по снимкам сбоку и сверху. В FBX ствол смотрит по -X, верх по +Y, длина 1,19 м.
    // Масштаб 0,73 даёт 0,87 м, как у винтовки Invector (0,86 м), и при нём совпадают расстояния
    // от пистолетной рукояти до дула и до приклада. Сдвиг ставит линию ствола на высоту muzzle
    // (0,055 м в Model), а пистолетную рукоять на место рукояти Invector (0,157 м позади центра Model)
    static readonly Vector3 LocalPosition = new(-0.005f, -0.124f, -0.059f);
    static readonly Vector3 LocalEuler = new(0f, 90f, 0f);
    const float Scale = 0.73f;

    public static string Main()
    {
        var log = new StringBuilder();
        var material = BuildMaterial(log);
        var importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
        importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "Material"), material);
        importer.SaveAndReimport();
        PlaceInRifle(log);
        FitHands(log);
        AssetDatabase.SaveAssets();
        return log.ToString();
    }

    static void FitHands(StringBuilder log)
    {
        var player = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath);
        var shooter = new SerializedObject(player.GetComponent<SurvivorShooter>());
        var weaponPosition = shooter.FindProperty("weaponLocalPosition").vector3Value;
        var weaponRotation = Quaternion.Euler(shooter.FindProperty("weaponLocalEuler").vector3Value);

        var body = Object.Instantiate(player);
        var animator = body.GetComponent<Animator>();
        AssetDatabase.LoadAllAssetsAtPath(PosesPath).OfType<AnimationClip>().Single(c => c.name == "Aiming@Rifle").SampleAnimation(body, 0f);

        var root = PrefabUtility.LoadPrefabContents(RiflePath);
        var model = root.transform.Find("Model");
        // Винтовка на кисти из анимации, как её ставит SurvivorShooter: отсюда оси Model в мире
        var rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        var rifleRotation = rightHand.rotation * weaponRotation;
        var rifleOrigin = rightHand.TransformPoint(weaponPosition);
        var modelRotation = rifleRotation * model.localRotation;
        var modelOrigin = rifleOrigin + rifleRotation * model.localPosition;
        Vector3 ToModel(Vector3 world) => Quaternion.Inverse(modelRotation) * (world - modelOrigin);

        // Правая: поворот вокруг рукояти - как у кисти из анимации, чтобы запястье не выкручивалось
        var handInModel = Quaternion.Inverse(modelRotation) * rightHand.rotation;
        var rightFist = Fist(animator, rightHand, true, log);
        var rightRotation = Align(rightFist, PistolGripDown, handInModel * rightFist.wrist);
        SetPoint(model, "rightHandIK", PistolGripCenter - rightRotation * rightFist.center, rightRotation);
        log.AppendLine($"right hand moves {(ToModel(rightHand.position) - (PistolGripCenter - rightRotation * rightFist.center)).magnitude:F3} m, " +
                       $"turns {Quaternion.Angle(handInModel, rightRotation):F0}°");

        var leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        var leftFist = Fist(animator, leftHand, false, log);
        var leftRotation = Align(leftFist, HandguardBack, LeftWristSide);
        SetPoint(model, "leftHandIK", HandguardCenter - leftRotation * leftFist.center, leftRotation);

        var weapon = new SerializedObject(root.GetComponent<ShooterWeapon>());
        weapon.FindProperty("rightHandGrip").objectReferenceValue = model.Find("rightHandIK");
        weapon.ApplyModifiedPropertiesWithoutUndo();
        PrefabUtility.SaveAsPrefabAsset(root, RiflePath);
        PrefabUtility.UnloadPrefabContents(root);
        Object.DestroyImmediate(body);
    }

    // Кулак в осях кости кисти: центр оси, вокруг которой согнуты пальцы, её направление от указательного
    // к мизинцу и направление от оси к запястью
    struct HandFist
    {
        public Vector3 center, axis, wrist;
    }

    static HandFist Fist(Animator animator, Transform hand, bool right, StringBuilder log)
    {
        Vector3 Local(Transform t) => Quaternion.Inverse(hand.rotation) * (t.position - hand.position);
        Transform Bone(int finger, int joint) => animator.GetBoneTransform((HumanBodyBones)((int)(right ? HumanBodyBones.RightThumbProximal : HumanBodyBones.LeftThumbProximal) + finger * 3 + joint));
        // Указательный правой лежит на спуске, рукоять обхватывают остальные
        var fingers = right ? new[] { 2, 3, 4 } : new[] { 1, 2, 3, 4 };
        var center = fingers.Select(f => Circle(Local(Bone(f, 0)), Local(Bone(f, 1)), Local(Bone(f, 2))))
            .Aggregate(Vector3.zero, (a, b) => a + b) / fingers.Length;
        var axis = (Local(Bone(4, 0)) - Local(Bone(1, 0))).normalized;
        log.AppendLine($"{hand.name}: fist center {center:F3}, {center.magnitude:F3} m from wrist");
        return new HandFist { center = center, axis = axis, wrist = Vector3.ProjectOnPlane(-center, axis).normalized };
    }

    // Поворот кисти в осях Model: ось кулака на ось детали, запястье от оси в сторону wristSide
    static Quaternion Align(HandFist fist, Vector3 axis, Vector3 wristSide)
    {
        axis = axis.normalized;
        return Quaternion.LookRotation(axis, Vector3.ProjectOnPlane(wristSide, axis)) * Quaternion.Inverse(Quaternion.LookRotation(fist.axis, fist.wrist));
    }

    // Центр окружности через три точки
    static Vector3 Circle(Vector3 p1, Vector3 p2, Vector3 p3)
    {
        var a = p1 - p3;
        var b = p2 - p3;
        var axb = Vector3.Cross(a, b);
        return p3 + Vector3.Cross(a.sqrMagnitude * b - b.sqrMagnitude * a, axb) / (2f * axb.sqrMagnitude);
    }

    static void SetPoint(Transform model, string name, Vector3 position, Quaternion rotation)
    {
        var point = model.Find(name);
        if (point == null)
        {
            point = new GameObject(name).transform;
            point.SetParent(model, false);
        }
        point.SetLocalPositionAndRotation(position, rotation);
        // Масштаб точки не нужен: она даёт только положение и поворот кисти
        point.localScale = Vector3.one;
    }

    static Material BuildMaterial(StringBuilder log)
    {
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

        SetImport(Tex(""), t => { });
        SetImport(Tex("_normal"), t => t.textureType = TextureImporterType.NormalMap);
        SetImport(metalSmoothPath, t => t.sRGBTexture = false);
        // Исходные металл и шероховатость материал не использует, в сборку они не попадут
        SetImport(Tex("_metallic"), t => { });
        SetImport(Tex("_roughness"), t => { });

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
        log.AppendLine($"material {MaterialPath}");
        return mat;
    }

    static void SetImport(string path, System.Action<TextureImporter> change)
    {
        var t = (TextureImporter)AssetImporter.GetAtPath(path);
        t.maxTextureSize = MaxTextureSize;
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

    static void PlaceInRifle(StringBuilder log)
    {
        var root = PrefabUtility.LoadPrefabContents(RiflePath);
        var model = root.transform.Find("Model");
        // Меш Invector больше не рисуется. Сам объект Model остаётся: на нём точки и частицы выстрела
        Object.DestroyImmediate(model.GetComponent<MeshRenderer>());
        Object.DestroyImmediate(model.GetComponent<MeshFilter>());
        var old = model.Find("ScrapRifle");
        if (old != null)
            Object.DestroyImmediate(old.gameObject);

        var scrap = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath), model);
        scrap.name = "ScrapRifle";
        // Корень FBX уже несёт масштаб 100 (единицы файла - сантиметры)
        float fileScale = scrap.transform.localScale.x;
        scrap.transform.SetLocalPositionAndRotation(LocalPosition, Quaternion.Euler(LocalEuler));
        scrap.transform.localScale = Vector3.one * fileScale * Scale;
        foreach (var r in scrap.GetComponentsInChildren<Renderer>())
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

        PrefabUtility.SaveAsPrefabAsset(root, RiflePath);
        PrefabUtility.UnloadPrefabContents(root);
        var renderer = AssetDatabase.LoadAssetAtPath<GameObject>(RiflePath).GetComponentsInChildren<MeshRenderer>().Single();
        log.AppendLine($"rifle {RiflePath}: {renderer.name} scale {fileScale * Scale:F1}, bounds {renderer.bounds.size:F3}, " +
                       $"material {AssetDatabase.GetAssetPath(renderer.sharedMaterial)}");
    }
}
