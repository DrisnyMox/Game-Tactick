using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using StarterAssets;
using UnityEditor;
using UnityEngine;

// Записывает позы игрока с надетым снаряжением (SetupGear.cs) в Play mode для замера в Blender.
// Сцена - SurvivorPlayground (только игрок, без ботов). Первый запуск в Play ставит ввод игрока по сценарию
// States (стоит, идёт, бежит, целится прямо, вверх и вниз, идёт боком и назад с прицелом, перезаряжает, прыгает)
// и каждые Step секунд пишет кадр в Tools/Blender/gear_poses.json:
//  * кости игрока после анимации, IK рук на винтовке и наклона корпуса за прицелом (SurvivorShooter.LateUpdate):
//    голова кости и поворот от T-позы, в осях Blender;
//  * каждую SampleStep-ю вершину запечённых мешей тела и частей: по ним measure_gear.py проверяет, что его поза
//    даёт те же меши, что игра (кожу в игре считают две кости на вершину, это повторяет gear_poses.SKIN_BONES).
// Сам замер (кожа сквозь часть, рука в части, дыры от спрятанной зоны) - Tools/Blender/measure_gear.py по ближайшей
// точке поверхности: лучи Unity по нормали на этом теле дают ложные попадания.
// Повторные запуски печатают ход записи, в конце - итог.
// Перед запуском: editor_play, editor_focus, set_autotick --enable true (CLAUDE.md, «Проверка после правки кода»).
// Запуск: unity command run_script --file Tools/Unity/ProbeGear.cs
public static class ProbeGear
{
    const string ResultPath = "Temp/ProbeGear.txt";
    const string PosesPath = "Tools/Blender/gear_poses.json";
    const string BodyModelPath = "Assets/Models/20261121003938_8b8745d5.fbx";
    const string VariantPath = "Assets/Characters/Survivor/PlayerSurvivor8b8745d5.prefab";
    const float Step = 0.2f, Settle = 0.4f;
    const int SampleStep = 40;
    // Простор перед игроком: бег за состояние run (2.4 с) с запасом на прицел в стену
    const float ClearAhead = 25f;
    const int HitboxLayer = 6;  // кости рэгдолла самого игрока
    // run_script каждый раз собирает новую сборку и её статика начинается заново, поэтому ход записи - в SessionState
    const string Running = "ProbeGear.running", ProgressKey = "ProbeGear.progress";

    // Сценарий: имя, длительность, ввод. Pitch - поворот камеры вверх (<0) или вниз (>0) в начале состояния
    static readonly (string name, float time, Vector2 move, bool sprint, bool aim, bool jump, bool reload, float pitch)[] States =
    {
        ("idle", 2.4f, Vector2.zero, false, false, false, false, 0f),
        ("walk", 2.4f, Vector2.up, false, false, false, false, 0f),
        ("run", 2.4f, Vector2.up, true, false, false, false, 0f),
        ("aim", 2.0f, Vector2.zero, false, true, false, false, 0f),
        ("aim_up", 2.0f, Vector2.zero, false, true, false, false, -60f),
        ("aim_down", 2.0f, Vector2.zero, false, true, false, false, 85f),
        ("aim_strafe", 2.4f, Vector2.right, false, true, false, false, -25f),
        ("aim_back", 2.4f, Vector2.down, false, true, false, false, 0f),
        ("reload", 2.6f, Vector2.zero, false, false, false, true, 0f),
        ("jump", 2.4f, Vector2.up, false, false, true, false, 0f),
    };

    public static string Main()
    {
        Application.runInBackground = true;
        if (!Application.isPlaying)
            return "нужен Play mode (SurvivorPlayground)";
        if (!SessionState.GetBool(Running, false))
        {
            // Готовый итог отдаётся один раз, следующий запуск начинает новую запись
            if (File.Exists(ResultPath))
            {
                var result = File.ReadAllText(ResultPath);
                File.Delete(ResultPath);
                return result;
            }
            var recorder = new Recorder();
            SessionState.SetBool(Running, true);
            EditorApplication.update += recorder.Tick;
            return "запись начата";
        }
        return $"идёт: {SessionState.GetString(ProgressKey, "")}";
    }

    class Recorder
    {
        static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        readonly SurvivorShooter shooter;
        readonly StarterAssetsInputs input;
        readonly ThirdPersonController controller;
        readonly Transform root;
        readonly SkinnedMeshRenderer body;
        readonly List<SkinnedMeshRenderer> parts = new();
        readonly Quaternion[] restRotation;
        readonly float scale;
        readonly List<string> poses = new();
        readonly Mesh baked = new();
        readonly Vector3 start;
        int state = -1;
        float stateStart, nextSample;

        public Recorder()
        {
            shooter = Object.FindObjectsByType<SurvivorShooter>().Single(s => !s.BotControlled);
            root = shooter.transform;
            input = shooter.GetComponent<StarterAssetsInputs>();
            controller = shooter.GetComponent<ThirdPersonController>();
            var worn = new SerializedObject(shooter.GetComponent<WornGear>());
            body = (SkinnedMeshRenderer)worn.FindProperty("body").objectReferenceValue;
            var pieces = worn.FindProperty("pieces");
            // На варианте стоят все части, замеряются надетые (включённые)
            for (int i = 0; i < pieces.arraySize; i++)
            {
                var part = (SkinnedMeshRenderer)pieces.GetArrayElementAtIndex(i).FindPropertyRelative("renderer").objectReferenceValue;
                if (part.gameObject.activeSelf)
                    parts.Add(part);
            }
            scale = ((ModelImporter)AssetImporter.GetAtPath(BodyModelPath)).globalScale;
            // T-поза - кости ассета варианта: в Play связь экземпляра с префабом не видна
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
            var prefabBones = prefab.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            restRotation = body.bones.Select(b => Quaternion.Inverse(prefab.transform.rotation) * prefabBones[b.name].rotation).ToArray();
            // Каждое состояние начинается с точки старта: иначе после бега игрок упирается в стену, точка прицела
            // оказывается за стволом, и доворот груди к прицелу (SurvivorShooter.LateUpdate) разворачивает грудь
            // почти на 180°. Камера (а за ней бег и прицел) поворачивается туда, где простор на весь бег
            start = root.position;
            float yaw = Enumerable.Range(0, 24).Select(k => k * 15f).First(a => IsClear(Quaternion.Euler(0f, a, 0f) * root.forward));
            controller.AddCameraRotation(0f, yaw);
        }

        bool IsClear(Vector3 direction) =>
            !Physics.Raycast(start + Vector3.up + direction * 0.5f, direction, ClearAhead, ~(1 << HitboxLayer));

        void ReturnToStart()
        {
            var character = shooter.GetComponent<CharacterController>();
            character.enabled = false;
            root.position = start;
            character.enabled = true;
        }

        public void Tick()
        {
            if (!Application.isPlaying)
            {
                Finish("Play mode закончился раньше записи");
                return;
            }
            float now = Time.time;
            if (state < 0 || now - stateStart >= States[state].time)
            {
                state++;
                if (state == States.Length)
                {
                    SetInput(default);
                    Finish(null);
                    return;
                }
                stateStart = now;
                nextSample = now + Settle;
                ReturnToStart();
                SetInput(States[state]);
                if (States[state].pitch != 0f)
                    controller.AddCameraRotation(States[state].pitch, 0f);
            }
            // прыжок - одним нажатием
            input.jump = States[state].jump && now - stateStart < 0.1f;
            if (now >= nextSample)
            {
                nextSample = now + Step;
                Record($"{States[state].name}@{(now - stateStart).ToString("F1", Ci)}");
                SessionState.SetString(ProgressKey, $"{States[state].name}, кадров {poses.Count}");
            }
        }

        void SetInput((string name, float time, Vector2 move, bool sprint, bool aim, bool jump, bool reload, float pitch) s)
        {
            input.move = s.move;
            input.sprint = s.sprint;
            input.look = Vector2.zero;
            shooter.AimInput = s.aim;
            if (s.reload)
                shooter.ReloadInput = true;
        }

        // Точка в осях Blender: Blender = (-x, -z, y) Unity в осях игрока / масштаб импорта модели
        Vector3 ToBlender(Vector3 world)
        {
            var p = Quaternion.Inverse(root.rotation) * (world - root.position) / scale;
            return new Vector3(-p.x, -p.z, p.y);
        }

        static string Floats(IEnumerable<float> values) => string.Join(", ", values.Select(x => x.ToString("0.######", Ci)));

        static IEnumerable<float> Xyz(Vector3 v)
        {
            yield return v.x;
            yield return v.y;
            yield return v.z;
        }

        void Record(string name)
        {
            // Кость: голова и поворот от T-позы. Поворот переводится в оси Blender той же заменой осей, что точки
            var data = new List<float>();
            int[] axis = { 0, 2, 1 };
            float[] sign = { -1f, -1f, 1f };
            for (int i = 0; i < body.bones.Length; i++)
            {
                var bone = body.bones[i];
                data.AddRange(Xyz(ToBlender(bone.position)));
                var delta = Matrix4x4.Rotate(Quaternion.Inverse(root.rotation) * bone.rotation * Quaternion.Inverse(restRotation[i]));
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                        data.Add(sign[r] * sign[c] * delta[axis[r], axis[c]]);
            }
            var json = new StringBuilder($"{{\"name\": \"{name}\", \"data\": [{Floats(data)}]");
            foreach (var smr in new[] { body }.Concat(parts))
            {
                smr.BakeMesh(baked, true);
                var vertices = baked.vertices;
                var sample = Enumerable.Range(0, vertices.Length / SampleStep).SelectMany(k => Xyz(ToBlender(smr.transform.TransformPoint(vertices[k * SampleStep]))));
                json.Append($", \"{smr.name}\": [{Floats(sample)}]");
            }
            poses.Add(json.Append('}').ToString());
        }

        void Finish(string error)
        {
            EditorApplication.update -= Tick;
            string result;
            if (error == null)
            {
                var json = new StringBuilder("{\"bones\": [");
                json.Append(string.Join(", ", body.bones.Select(b => $"\"{b.name}\"")));
                json.Append($"],\n\"body\": \"{body.name}\", \"parts\": [{string.Join(", ", parts.Select(p => $"\"{p.name}\""))}],\n\"poses\": [\n");
                json.Append(string.Join(",\n", poses)).Append("]}\n");
                File.WriteAllText(PosesPath, json.ToString());
                result = $"записано кадров {poses.Count} -> {PosesPath}; замер: Tools/Blender/measure_gear.py";
            }
            else
                result = error;
            File.WriteAllText(ResultPath, result);
            SessionState.SetBool(Running, false);
        }
    }
}
