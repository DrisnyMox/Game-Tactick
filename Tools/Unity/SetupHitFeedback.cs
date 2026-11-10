using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Ощущение от стрельбы:
//  1. генерирует короткие звуки в Assets/Audio/Generated: щелчок попадания, попадание в голову, убийство,
//     удар пули в песок и в дерево (в ассетах Invector таких нет; есть только удар о кирпич и попадание в тело);
//     из звука Invector «удар о кирпич» вырезает сам удар (ImpactHard), без свиста перед ним;
//  2. чинит эффект крови Invector bloodSplash: убирает пустые скрипты Invector, материал переводит на URP;
//  3. на префабе PlayerSurvivor заполняет у SurvivorShooter эффекты и звуки попадания по видам поверхности
//     и звуки отметки попадания;
//  4. в открытую сцену ставит объект «Combat HUD» с лентой убийств (KillFeed) и сохраняет сцену.
// Повторный запуск перезаписывает звуки и настройки.
// Запуск: unity command run_script --file Tools/Unity/SetupHitFeedback.cs --project-path "D:\Game Tactick"
public static class SetupHitFeedback
{
    const string SoundFolder = "Assets/Audio/Generated";
    const string PrefabPath = "Assets/Characters/Survivor/PlayerSurvivor.prefab";
    const string ImpactFolder = "Assets/ThirdParty/Invector/Shooter/Scripts/DecalManager/BulletEffect/Resources/prefabs/";
    const string BloodPrefab = "Assets/ThirdParty/Invector/Basic Locomotion/3D Models/Particles/DamageEffect/prefabs/bloodSplash.prefab";
    const string BloodHitFolder = "Assets/ThirdParty/Invector/Melee Combat/Audio/";
    const string BrickClip = "Assets/ThirdParty/Invector/Shooter/Audio/Weapons/Impact Whoosh House Brick 07.wav";
    const int Rate = 44100;

    static readonly StringBuilder log = new();

    public static string Main()
    {
        log.Clear();

        // 1.
        Directory.CreateDirectory(SoundFolder);
        WriteWav("HitMarker", HitMarker());
        WriteWav("HitMarkerHead", Headshot());
        WriteWav("HitMarkerKill", Kill());
        for (int i = 1; i <= 3; i++)
            WriteWav($"ImpactSoft_{i}", ImpactSoft(i));
        for (int i = 1; i <= 2; i++)
            WriteWav($"ImpactWood_{i}", ImpactWood(i == 1 ? 1f : 1.18f, i));
        // В звуке Invector удар о кирпич звучит на 350 мс после начала, до него - свист: попадание звучало бы
        // с опозданием. Берётся кусок с самого удара
        var brickSource = AssetDatabase.LoadAssetAtPath<AudioClip>(BrickClip);
        WriteWav("ImpactHard", Cut(brickSource, 0.33f, 0.95f), brickSource.frequency);
        AssetDatabase.Refresh();

        // 2.
        var blood = PrefabUtility.LoadPrefabContents(BloodPrefab);
        int removed = blood.GetComponentsInChildren<Transform>(true).Sum(t => GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject));
        foreach (var renderer in blood.GetComponentsInChildren<ParticleSystemRenderer>(true))
            foreach (var material in renderer.sharedMaterials.Where(m => m != null && !m.shader.name.StartsWith("Universal Render Pipeline")))
                ConvertParticleMaterial(material);
        PrefabUtility.SaveAsPrefabAsset(blood, BloodPrefab);
        PrefabUtility.UnloadPrefabContents(blood);
        log.AppendLine($"bloodSplash: removed {removed} missing scripts");

        // 3.
        var brick = Clip("ImpactHard");
        var soft = Enumerable.Range(1, 3).Select(i => Clip($"ImpactSoft_{i}")).ToArray();
        var wood = Enumerable.Range(1, 2).Select(i => Clip($"ImpactWood_{i}")).ToArray();
        var flesh = new[] { "BloodHit_A", "BloodHit_B", "BloodHit_C" }
            .Select(n => AssetDatabase.LoadAssetAtPath<AudioClip>(BloodHitFolder + n + ".wav")).ToArray();

        // Кровь Invector сделана для ближнего боя и закрывает персонажа целиком: у пули она в 0,35 раза меньше
        var impacts = new List<(HitSurface surface, string effect, float scale, AudioClip[] sounds, float volume)>
        {
            (HitSurface.Default, ImpactFolder + "impactConcrete.prefab", 1f, new[] { brick }, 0.5f),
            (HitSurface.Soft, ImpactFolder + "impactDirt.prefab", 1f, soft, 0.8f),
            (HitSurface.Hard, ImpactFolder + "impactConcrete.prefab", 1f, new[] { brick }, 0.5f),
            (HitSurface.Wood, ImpactFolder + "impactWood.prefab", 1f, wood, 0.8f),
            (HitSurface.Body, BloodPrefab, 0.35f, flesh, 0.9f),
        };

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        var so = new SerializedObject(root.GetComponent<SurvivorShooter>());
        var array = so.FindProperty("impacts");
        array.arraySize = impacts.Count;
        for (int i = 0; i < impacts.Count; i++)
        {
            var element = array.GetArrayElementAtIndex(i);
            element.FindPropertyRelative("surface").enumValueIndex = (int)impacts[i].surface;
            element.FindPropertyRelative("effect").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(impacts[i].effect);
            element.FindPropertyRelative("scale").floatValue = impacts[i].scale;
            var sounds = element.FindPropertyRelative("sounds");
            sounds.arraySize = impacts[i].sounds.Length;
            for (int s = 0; s < impacts[i].sounds.Length; s++)
                sounds.GetArrayElementAtIndex(s).objectReferenceValue = impacts[i].sounds[s];
            element.FindPropertyRelative("volume").floatValue = impacts[i].volume;
            log.AppendLine($"impact {impacts[i].surface}: {Path.GetFileNameWithoutExtension(impacts[i].effect)}, " +
                           string.Join(", ", impacts[i].sounds.Select(c => c.name)));
        }
        so.FindProperty("hitMarkerClip").objectReferenceValue = Clip("HitMarker");
        so.FindProperty("headshotMarkerClip").objectReferenceValue = Clip("HitMarkerHead");
        so.FindProperty("killMarkerClip").objectReferenceValue = Clip("HitMarkerKill");
        // Отдача: камера плавно уходит вверх на 0,3° за выстрел и возвращается; при очереди уходит ~на 0,7°
        so.FindProperty("recoilPitch").floatValue = 0.3f;
        so.FindProperty("recoilYaw").floatValue = 0.08f;
        so.FindProperty("recoilRise").floatValue = 30f;
        so.FindProperty("recoilReturn").floatValue = 6f;
        so.FindProperty("kickAngle").floatValue = 2.5f;
        so.ApplyModifiedPropertiesWithoutUndo();
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        PrefabUtility.UnloadPrefabContents(root);

        // 4.
        var scene = EditorSceneManager.GetActiveScene();
        var player = UnityEngine.Object.FindObjectsByType<SurvivorShooter>(FindObjectsInactive.Exclude).Single(s => !s.BotControlled);
        var hud = GameObject.Find("Combat HUD") ?? new GameObject("Combat HUD");
        var feed = hud.GetComponent<KillFeed>() ?? hud.AddComponent<KillFeed>();
        var feedSo = new SerializedObject(feed);
        feedSo.FindProperty("player").objectReferenceValue = player.GetComponent<CharacterHealth>();
        feedSo.ApplyModifiedPropertiesWithoutUndo();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        log.AppendLine($"Combat HUD with KillFeed in {scene.path}, player {player.name}");
        return log.ToString();
    }

    static AudioClip Clip(string name) => AssetDatabase.LoadAssetAtPath<AudioClip>($"{SoundFolder}/{name}.wav");

    // Старые шейдеры частиц URP не рисует: Particles/Unlit, прозрачный, цвет и текстура как были
    static void ConvertParticleMaterial(Material material)
    {
        string before = material.shader.name;
        var texture = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
        var tint = material.HasProperty("_TintColor") ? material.GetColor("_TintColor") : Color.white;
        material.shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        material.SetTexture("_BaseMap", texture);
        material.SetColor("_BaseColor", tint * 2f);
        material.SetFloat("_Surface", 1f);
        material.SetFloat("_Blend", 0f);
        material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_ZWrite", 0f);
        material.SetOverrideTag("RenderType", "Transparent");
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        EditorUtility.SetDirty(material);
        log.AppendLine($"material {material.name}: {before} -> {material.shader.name}");
    }

    // ---------- Звуки ----------

    static float[] Buffer(float seconds) => new float[Mathf.CeilToInt(seconds * Rate)];

    static float Sine(float hz, float t) => Mathf.Sin(2f * Mathf.PI * hz * t);

    // Щелчок: короткий высокий тон с шумом, гаснет за ~10 мс
    static float[] HitMarker()
    {
        var rnd = new System.Random(7);
        var s = Buffer(0.05f);
        for (int i = 0; i < s.Length; i++)
        {
            float t = (float)i / Rate;
            float env = Mathf.Exp(-t / 0.009f) * Mathf.Clamp01(t / 0.0008f);
            s[i] = env * (0.6f * Sine(3200f, t) + 0.35f * ((float)rnd.NextDouble() * 2f - 1f));
        }
        return s;
    }

    // В голову: щелчок и звонкий двойной тон
    static float[] Headshot()
    {
        var tick = HitMarker();
        var s = Buffer(0.3f);
        for (int i = 0; i < s.Length; i++)
        {
            float t = (float)i / Rate;
            float env = Mathf.Exp(-t / 0.075f) * Mathf.Clamp01(t / 0.002f);
            s[i] = env * (0.5f * Sine(1900f, t) + 0.3f * Sine(2850f, t)) + (i < tick.Length ? tick[i] * 0.7f : 0f);
        }
        return s;
    }

    // Убийство: щелчок и два тона вверх
    static float[] Kill()
    {
        var tick = HitMarker();
        var s = Buffer(0.4f);
        for (int i = 0; i < s.Length; i++)
        {
            float t = (float)i / Rate;
            float first = Mathf.Exp(-t / 0.08f) * Mathf.Clamp01(t / 0.002f) * Sine(1100f, t);
            float t2 = t - 0.07f;
            float second = t2 > 0f ? Mathf.Exp(-t2 / 0.1f) * Mathf.Clamp01(t2 / 0.002f) * Sine(1650f, t2) : 0f;
            s[i] = 0.5f * first + 0.5f * second + (i < tick.Length ? tick[i] * 0.7f : 0f);
        }
        return s;
    }

    // Пуля в песок: глухой шум (через простой фильтр низких частот) и низкий удар
    static float[] ImpactSoft(int seed)
    {
        var rnd = new System.Random(seed * 13);
        var s = Buffer(0.2f);
        float low = 0f;
        float a = 0.06f + seed * 0.015f;
        for (int i = 0; i < s.Length; i++)
        {
            float t = (float)i / Rate;
            low += a * (((float)rnd.NextDouble() * 2f - 1f) - low);
            float noise = low * 3f * Mathf.Exp(-t / 0.035f) * Mathf.Clamp01(t / 0.001f);
            float thumpHz = 70f + 90f * Mathf.Exp(-t * 25f);
            float thump = 0.6f * Sine(thumpHz, t) * Mathf.Exp(-t / 0.045f);
            s[i] = noise + thump;
        }
        return s;
    }

    // Пуля в дерево: короткий стук из трёх тонов и щелчок шума
    static float[] ImpactWood(float pitch, int seed)
    {
        var rnd = new System.Random(seed * 31);
        var s = Buffer(0.2f);
        for (int i = 0; i < s.Length; i++)
        {
            float t = (float)i / Rate;
            float env = Mathf.Exp(-t / 0.04f) * Mathf.Clamp01(t / 0.0008f);
            float tone = 0.5f * Sine(480f * pitch, t) + 0.3f * Sine(760f * pitch, t) + 0.2f * Sine(1180f * pitch, t);
            float click = ((float)rnd.NextDouble() * 2f - 1f) * Mathf.Exp(-t / 0.004f) * 0.5f;
            s[i] = env * tone + click;
        }
        return s;
    }

    // Кусок звука от from до to секунд, каналы сведены в моно, края плавно нарастают и гаснут (без щелчков)
    static float[] Cut(AudioClip clip, float from, float to)
    {
        var data = new float[clip.samples * clip.channels];
        clip.GetData(data, 0);
        int start = Mathf.RoundToInt(from * clip.frequency), end = Mathf.Min(clip.samples, Mathf.RoundToInt(to * clip.frequency));
        var s = new float[end - start];
        int fade = clip.frequency / 200;
        for (int i = 0; i < s.Length; i++)
        {
            float sum = 0f;
            for (int c = 0; c < clip.channels; c++)
                sum += data[(start + i) * clip.channels + c];
            float edge = Mathf.Clamp01(Mathf.Min(i, s.Length - 1 - i) / (float)fade);
            s[i] = sum / clip.channels * edge;
        }
        return s;
    }

    // 16 бит, моно; громкость приводится к пику 0.9
    static void WriteWav(string name, float[] samples, int rate = Rate)
    {
        float peak = samples.Max(Mathf.Abs);
        float gain = peak > 0f ? 0.9f / peak : 1f;
        string path = $"{SoundFolder}/{name}.wav";
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + samples.Length * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(samples.Length * 2);
        foreach (var sample in samples)
            writer.Write((short)Mathf.RoundToInt(Mathf.Clamp(sample * gain, -1f, 1f) * short.MaxValue));
        log.AppendLine($"sound {path}: {samples.Length * 1000 / rate} ms");
    }
}
