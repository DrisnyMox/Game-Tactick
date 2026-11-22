using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using StarterAssets;
using UnityEditor;
using UnityEngine;

// Снимки обуви и штанов вблизи в Play (сцена SurvivorPlayground): стоя, при ходьбе, беге, в прыжке и в прицеле. Числам замера
// place_gear.py одним верить нельзя, вид проверяется глазами. Первый запуск ставит ввод игрока по сценарию States
// (как ProbeGear.cs: каждое состояние - с точки старта, в сторону, где простор) и в заданные моменты ставит
// временные камеры у каждой ступни: снаружи, изнутри, спереди, сзади, две камеры на обе ноги и четыре на пояс. Снимки -
// Temp/GearShots/feet_<состояние>_<время>_*.png. В тот же момент пишет, насколько подошва каждого ботинка
// ниже земли под ним (луч вниз по слою Default). Повторные запуски печатают ход, в конце - итог.
// Перед запуском: editor_play, editor_focus, set_autotick --enable true (CLAUDE.md, «Проверка после правки кода»).
// Запуск: unity command run_script --file Tools/Unity/ProbeGearFeetShots.cs
public static class ProbeGearFeetShots
{
    const string Folder = "Temp/GearShots";
    const string ResultPath = "Temp/ProbeGearFeetShots.txt";
    const string Part = "ClothBoots";
    const int Size = 640;
    const float ClearAhead = 25f;
    const int HitboxLayer = 6;  // кости рэгдолла самого игрока
    // run_script каждый раз собирает новую сборку и её статика начинается заново, поэтому ход - в SessionState
    const string Running = "ProbeGearFeetShots.running", ProgressKey = "ProbeGearFeetShots.progress";

    // Сценарий: имя, длительность, ввод, прицел, моменты снимков от начала состояния
    static readonly (string name, float time, Vector2 move, bool sprint, bool jump, bool aim, float[] shots)[] States =
    {
        ("idle", 1.6f, Vector2.zero, false, false, false, new[] { 1.2f }),
        ("walk", 2.0f, Vector2.up, false, false, false, new[] { 1.2f, 1.5f }),
        ("run", 2.0f, Vector2.up, true, false, false, new[] { 1.2f, 1.35f }),
        ("jump", 2.0f, Vector2.zero, false, true, false, new[] { 0.35f, 0.7f, 1.6f }),
        ("aim", 2.0f, Vector2.zero, false, false, true, new[] { 1.4f }),
        ("aim_walk", 2.0f, Vector2.right, false, false, true, new[] { 1.2f, 1.5f }),
    };

    public static string Main()
    {
        Application.runInBackground = true;
        if (!Application.isPlaying)
            return "нужен Play mode (SurvivorPlayground)";
        if (!SessionState.GetBool(Running, false))
        {
            if (File.Exists(ResultPath))
            {
                var result = File.ReadAllText(ResultPath);
                File.Delete(ResultPath);
                return result;
            }
            Directory.CreateDirectory(Folder);
            var shooter = new Shooter();
            SessionState.SetBool(Running, true);
            EditorApplication.update += shooter.Tick;
            return "снимки начаты";
        }
        return $"идёт: {SessionState.GetString(ProgressKey, "")}";
    }

    class Shooter
    {
        readonly SurvivorShooter shooter;
        readonly StarterAssetsInputs input;
        readonly ThirdPersonController controller;
        readonly Transform root;
        readonly SkinnedMeshRenderer boots;
        readonly Dictionary<string, Transform> bones;
        readonly Vector3 start;
        readonly Mesh baked = new();
        readonly StringBuilder log = new();
        readonly List<float> sink = new();
        int state = -1, shot;
        float stateStart;
        int count;

        public Shooter()
        {
            shooter = Object.FindObjectsByType<SurvivorShooter>().Single(s => !s.BotControlled);
            root = shooter.transform;
            input = shooter.GetComponent<StarterAssetsInputs>();
            controller = shooter.GetComponent<ThirdPersonController>();
            boots = root.Find(Part).GetComponent<SkinnedMeshRenderer>();
            bones = root.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            start = root.position;
            float yaw = Enumerable.Range(0, 24).Select(k => k * 15f).First(a => IsClear(Quaternion.Euler(0f, a, 0f) * root.forward));
            controller.AddCameraRotation(0f, yaw);
        }

        bool IsClear(Vector3 direction) =>
            !Physics.Raycast(start + Vector3.up + direction * 0.5f, direction, ClearAhead, ~(1 << HitboxLayer));

        public void Tick()
        {
            if (!Application.isPlaying)
            {
                Finish("Play mode закончился раньше снимков");
                return;
            }
            float now = Time.time;
            if (state < 0 || now - stateStart >= States[state].time)
            {
                state++;
                if (state == States.Length)
                {
                    Finish(null);
                    return;
                }
                stateStart = now;
                shot = 0;
                var character = shooter.GetComponent<CharacterController>();
                character.enabled = false;
                root.position = start;
                character.enabled = true;
                input.move = States[state].move;
                input.sprint = States[state].sprint;
                input.look = Vector2.zero;
                shooter.AimInput = States[state].aim;
            }
            var s = States[state];
            input.jump = s.jump && now - stateStart < 0.1f;
            if (shot < s.shots.Length && now - stateStart >= s.shots[shot])
            {
                Shoot($"feet_{s.name}_{s.shots[shot]:F2}");
                shot++;
                SessionState.SetString(ProgressKey, $"{s.name}, снимков {count}");
            }
        }

        void Shoot(string prefix)
        {
            // Подошва против земли: самая низкая вершина каждого ботинка и земля под ней
            boots.BakeMesh(baked, true);
            var points = baked.vertices.Select(v => boots.transform.TransformPoint(v)).ToArray();
            var line = new StringBuilder($"{prefix}:");
            foreach (var side in new[] { "Left", "Right" })
            {
                float sign = side == "Left" ? -1f : 1f;  // левая сторона персонажа - -X в осях игрока
                var own = points.Where(p => Mathf.Sign(Vector3.Dot(p - root.position, root.right)) == sign).ToArray();
                var low = own.OrderBy(p => p.y).First();
                if (Physics.Raycast(low + Vector3.up * 0.3f, Vector3.down, out var hit, 2f, 1))
                {
                    float below = (hit.point.y - low.y) * 1000f;
                    sink.Add(below);
                    line.Append($" {side} подошва {below:F0} мм ниже земли;");
                }
                else
                    line.Append($" {side} земли нет;");
                var target = bones[side + "Foot"].position;
                target.y = (target.y + low.y) * 0.5f;
                var outside = root.right * -sign;
                ShootAt($"{prefix}_{side}_out", target, outside, 0.75f);
                ShootAt($"{prefix}_{side}_in", target, -outside, 0.75f);
                ShootAt($"{prefix}_{side}_front", target, root.forward, 0.75f);
                ShootAt($"{prefix}_{side}_back", target, -root.forward, 0.75f);
            }
            var legs = (bones["LeftLowerLeg"].position + bones["RightLowerLeg"].position) * 0.5f;
            legs.y = root.position.y + 0.3f;
            ShootAt($"{prefix}_legs_side", legs, root.right + root.forward * 0.3f, 1.8f);
            ShootAt($"{prefix}_legs_back_top", legs, -root.forward + Vector3.up * 0.8f, 1.8f);
            // Пояс: нет ли голой кожи между штанами и нагрудником, в том числе снизу
            var hips = bones["Hips"].position;
            ShootAt($"{prefix}_waist_front", hips, root.forward, 1f);
            ShootAt($"{prefix}_waist_back", hips, -root.forward + Vector3.up * 0.3f, 1f);
            ShootAt($"{prefix}_waist_side", hips, -root.right, 1f);
            ShootAt($"{prefix}_waist_below_back", hips, -root.forward - Vector3.up * 0.5f, 1f);
            log.AppendLine(line.ToString());
        }

        void ShootAt(string name, Vector3 target, Vector3 from, float distance)
        {
            var go = new GameObject("ProbeGearFeetShotsCamera");
            var cam = go.AddComponent<Camera>();
            cam.transform.SetPositionAndRotation(target + from.normalized * distance, Quaternion.LookRotation(-from.normalized, Vector3.up));
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.02f;
            var rt = new RenderTexture(Size, Size, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(Size, Size, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(Folder, name + ".png"), tex.EncodeToPNG());
            RenderTexture.active = null;
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(go);
            count++;
        }

        void Finish(string error)
        {
            EditorApplication.update -= Tick;
            input.move = Vector2.zero;
            input.sprint = false;
            input.jump = false;
            shooter.AimInput = false;
            string result = error ?? $"{count} снимков в {Folder}\n{log}" +
                (sink.Count > 0 ? $"подошва ниже земли: худшее {sink.Max():F0} мм\n" : "");
            File.WriteAllText(ResultPath, result);
            SessionState.SetBool(Running, false);
        }
    }
}
