using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Граната:
//  1. генерирует звук взрыва Assets/Audio/Generated/GrenadeExplosion.wav (в ассетах Invector взрыва нет):
//     треск в начале, гул шума через фильтр низких частот и низкий удар;
//  2. собирает префаб Assets/Weapons/Grenade.prefab: тело (Rigidbody, шар-коллайдер с отскоком) на слое
//     Ignore Raycast, чтобы пули, взгляд ботов и проверка земли его не видели; модель — вытянутый шар;
//     огонь — раскадровка 5×4 explosion.png Invector (материал fire.mat), пыль — DustCloud.mat, вспышка — точечный свет;
//     стук об поверхность — звуки попадания пули ImpactSoft, ImpactWood, ImpactHard (нужен SetupHitFeedback.cs);
//  3. на префаб PlayerSurvivor ставит GrenadeThrower со ссылкой на гранату. Сцены берут его из префаба.
// Повторный запуск перезаписывает звук и настройки, ссылки на префаб гранаты сохраняются.
// Запуск: unity command run_script --file Tools/Unity/SetupGrenade.cs --project-path "D:\Game Tactick"
public static class SetupGrenade
{
    const string SoundFolder = "Assets/Audio/Generated";
    const string SoundPath = SoundFolder + "/GrenadeExplosion.wav";
    const string Folder = "Assets/Weapons/";
    const string PrefabPath = Folder + "Grenade.prefab";
    const string MaterialPath = Folder + "Grenade.mat";
    // .asset: файл .physicsMaterial AssetDatabase.CreateAsset создавать не даёт
    const string PhysicsPath = Folder + "GrenadePhysics.asset";
    const string PlayerPath = "Assets/Characters/Survivor/PlayerSurvivor.prefab";
    const string FireMaterial = "Assets/ThirdParty/Invector/Shooter/3DModels/Particles/Materials/fire.mat";
    const string DustMaterial = "Assets/ThirdParty/Invector/Shooter/Scripts/DecalManager/BulletEffect/Resources/materials/DustCloud.mat";
    const int Rate = 44100;
    const int IgnoreRaycastLayer = 2;

    static readonly StringBuilder log = new();

    public static string Main()
    {
        log.Clear();

        // 1.
        Directory.CreateDirectory(SoundFolder);
        WriteWav(SoundPath, Explosion());
        AssetDatabase.ImportAsset(SoundPath);

        // 2.
        bool exists = File.Exists(PrefabPath);
        var root = exists ? PrefabUtility.LoadPrefabContents(PrefabPath) : new GameObject("Grenade");
        BuildGrenade(root);
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        if (exists)
            PrefabUtility.UnloadPrefabContents(root);
        else
            Object.DestroyImmediate(root);
        log.AppendLine($"prefab {PrefabPath} ({(exists ? "updated" : "created")})");

        // 3.
        var grenade = AssetDatabase.LoadAssetAtPath<Grenade>(PrefabPath);
        var player = PrefabUtility.LoadPrefabContents(PlayerPath);
        var thrower = GetOrAdd<GrenadeThrower>(player);
        var so = new SerializedObject(thrower);
        so.FindProperty("grenadePrefab").objectReferenceValue = grenade;
        so.ApplyModifiedPropertiesWithoutUndo();
        PrefabUtility.SaveAsPrefabAsset(player, PlayerPath);
        PrefabUtility.UnloadPrefabContents(player);
        log.AppendLine($"{PlayerPath}: GrenadeThrower -> {PrefabPath}");

        AssetDatabase.SaveAssets();
        return log.ToString();
    }

    static void BuildGrenade(GameObject root)
    {
        root.layer = IgnoreRaycastLayer;

        var body = GetOrAdd<Rigidbody>(root);
        body.mass = 0.4f;
        body.linearDamping = 0.05f;
        body.angularDamping = 0.8f;
        // Граната маленькая и быстрая, а стены арены тонкие
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.interpolation = RigidbodyInterpolation.Interpolate;

        var sphere = GetOrAdd<SphereCollider>(root);
        sphere.radius = 0.05f;
        sphere.sharedMaterial = PhysicsMaterialAsset();

        var audio = GetOrAdd<AudioSource>(root);
        audio.clip = AssetDatabase.LoadAssetAtPath<AudioClip>(SoundPath);
        audio.playOnAwake = false;
        audio.spatialBlend = 1f;
        audio.rolloffMode = AudioRolloffMode.Logarithmic;
        audio.minDistance = 10f;
        audio.maxDistance = 250f;
        audio.volume = 1f;

        // Модель: вытянутый шар 7,5×10 см
        var model = Child(root, "Model");
        var primitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        GetOrAdd<MeshFilter>(model).sharedMesh = primitive.GetComponent<MeshFilter>().sharedMesh;
        Object.DestroyImmediate(primitive);
        var renderer = GetOrAdd<MeshRenderer>(model);
        renderer.sharedMaterial = BodyMaterial();
        model.transform.localScale = new Vector3(0.075f, 0.1f, 0.075f);

        var fire = Particles(root, "Fire");
        SetupFire(fire);
        var dust = Particles(root, "Dust");
        SetupDust(dust);

        var flashObject = Child(root, "Flash");
        var flash = GetOrAdd<Light>(flashObject);
        flash.type = LightType.Point;
        flash.range = 14f;
        flash.intensity = 6f;
        flash.color = new Color(1f, 0.7f, 0.4f);
        flash.shadows = LightShadows.None;
        flash.enabled = false;

        var grenade = GetOrAdd<Grenade>(root);
        var so = new SerializedObject(grenade);
        so.FindProperty("body").objectReferenceValue = renderer;
        var particles = so.FindProperty("explosionParticles");
        particles.arraySize = 2;
        particles.GetArrayElementAtIndex(0).objectReferenceValue = fire;
        particles.GetArrayElementAtIndex(1).objectReferenceValue = dust;
        so.FindProperty("flash").objectReferenceValue = flash;
        so.FindProperty("explosionAudio").objectReferenceValue = audio;
        // Стук об поверхность — те же звуки, что у попадания пули (их генерирует SetupHitFeedback.cs)
        SetClips(so.FindProperty("softImpactClips"), "ImpactSoft_1", "ImpactSoft_2", "ImpactSoft_3");
        SetClips(so.FindProperty("woodImpactClips"), "ImpactWood_1", "ImpactWood_2");
        SetClips(so.FindProperty("hardImpactClips"), "ImpactHard");
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetClips(SerializedProperty array, params string[] names)
    {
        array.arraySize = names.Length;
        for (int i = 0; i < names.Length; i++)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>($"{SoundFolder}/{names[i]}.wav");
            if (clip == null)
                throw new System.Exception($"нет звука {SoundFolder}/{names[i]}.wav: сначала Tools/Unity/SetupHitFeedback.cs");
            array.GetArrayElementAtIndex(i).objectReferenceValue = clip;
        }
    }

    // Огонь: несколько больших кадров раскадровки 5×4 explosion.png, растут и гаснут за ~0,8 с
    static void SetupFire(ParticleSystem ps)
    {
        var main = ps.main;
        main.duration = 1f;
        main.loop = false;
        main.playOnAwake = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 0.9f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 2.5f);
        main.startSize = new ParticleSystem.MinMaxCurve(2.5f, 4f);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 16;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 7) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.4f;

        var sheet = ps.textureSheetAnimation;
        sheet.enabled = true;
        sheet.mode = ParticleSystemAnimationMode.Grid;
        sheet.numTilesX = 5;
        sheet.numTilesY = 4;
        sheet.animation = ParticleSystemAnimationType.WholeSheet;

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.6f, 1f, 1.3f));

        var renderer = ps.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(FireMaterial);
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
    }

    // Пыль: серо-песочные клубы разлетаются, медленно поднимаются и тают за ~2 с
    static void SetupDust(ParticleSystem ps)
    {
        var main = ps.main;
        main.duration = 1f;
        main.loop = false;
        main.playOnAwake = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.4f, 2.4f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 6f);
        main.startSize = new ParticleSystem.MinMaxCurve(1.5f, 3.5f);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.startColor = new Color(0.62f, 0.56f, 0.46f, 0.7f);
        main.gravityModifier = -0.05f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 24;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 14) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Hemisphere;
        shape.radius = 0.5f;
        // Полусфера Unity смотрит по +z: повернуть куполом вверх
        shape.rotation = new Vector3(-90f, 0f, 0f);

        var velocity = ps.limitVelocityOverLifetime;
        velocity.enabled = true;
        velocity.limit = 0.6f;
        velocity.dampen = 0.15f;

        var color = ps.colorOverLifetime;
        color.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.7f, 0.4f), new GradientAlphaKey(0f, 1f) });
        color.color = gradient;

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.7f, 1f, 1.6f));

        var renderer = ps.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(DustMaterial);
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
    }

    static Material BodyMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(material, MaterialPath);
        }
        // Оливковый металл с потёртостями
        material.SetColor("_BaseColor", new Color(0.26f, 0.3f, 0.17f));
        material.SetFloat("_Metallic", 0.45f);
        material.SetFloat("_Smoothness", 0.35f);
        EditorUtility.SetDirty(material);
        return material;
    }

    static PhysicsMaterial PhysicsMaterialAsset()
    {
        var material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(PhysicsPath);
        if (material == null)
        {
            material = new PhysicsMaterial("Grenade");
            AssetDatabase.CreateAsset(material, PhysicsPath);
        }
        // Чуть отскакивает: Minimum берёт меньший отскок из двух, поэтому земля гранату не подбрасывает выше.
        // Трение — большее из двух. Остальное гасит Grenade при первом ударе
        material.bounciness = 0.15f;
        material.dynamicFriction = 0.6f;
        material.staticFriction = 0.7f;
        material.bounceCombine = PhysicsMaterialCombine.Minimum;
        material.frictionCombine = PhysicsMaterialCombine.Maximum;
        EditorUtility.SetDirty(material);
        return material;
    }

    static ParticleSystem Particles(GameObject root, string name)
    {
        var child = Child(root, name);
        return GetOrAdd<ParticleSystem>(child);
    }

    static GameObject Child(GameObject root, string name)
    {
        var found = root.transform.Find(name);
        if (found != null)
            return found.gameObject;
        var child = new GameObject(name);
        child.transform.SetParent(root.transform, false);
        child.layer = IgnoreRaycastLayer;
        return child;
    }

    // Через == null, а не ??: недостающий компонент Unity отдаёт «пустышкой», которая для ?? не null
    static T GetOrAdd<T>(GameObject target) where T : Component
    {
        var component = target.GetComponent<T>();
        return component != null ? component : target.AddComponent<T>();
    }

    // Взрыв ~2,2 с: треск в первые миллисекунды, гул шума через фильтр низких частот, низкий удар с падающим тоном
    static float[] Explosion()
    {
        var rnd = new System.Random(7);
        var s = new float[Mathf.RoundToInt(Rate * 2.2f)];
        float low = 0f, lower = 0f;
        for (int i = 0; i < s.Length; i++)
        {
            float t = (float)i / Rate;
            float white = (float)rnd.NextDouble() * 2f - 1f;
            low += 0.08f * (white - low);
            lower += 0.02f * (low - lower);
            float attack = Mathf.Clamp01(t / 0.003f);
            float crack = white * Mathf.Exp(-t / 0.025f);
            float rumble = (low * 1.5f * Mathf.Exp(-t / 0.25f) + lower * 6f * Mathf.Exp(-t / 0.7f));
            float thumpHz = 38f + 70f * Mathf.Exp(-t * 12f);
            float thump = Mathf.Sin(2f * Mathf.PI * thumpHz * t) * Mathf.Exp(-t / 0.35f);
            s[i] = attack * (0.35f * crack + rumble + 0.9f * thump);
        }
        return s;
    }

    static void WriteWav(string path, float[] samples)
    {
        float peak = samples.Max(Mathf.Abs);
        float gain = peak > 0f ? 0.95f / peak : 1f;
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + samples.Length * 2);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(Rate);
            writer.Write(Rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(samples.Length * 2);
            foreach (var sample in samples)
                writer.Write((short)Mathf.RoundToInt(Mathf.Clamp(sample * gain, -1f, 1f) * short.MaxValue));
        }
        log.AppendLine($"sound {path}: {samples.Length * 1000 / Rate} ms");
    }
}
