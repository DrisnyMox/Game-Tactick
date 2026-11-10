using System.Text;
using UnityEditor;
using UnityEngine;

// Печатает громкость звука по кускам в 50 мс: где в нём удар, где тишина.
// Запуск: unity command run_script --file Tools/Unity/InspectClip.cs --project-path "D:\Game Tactick" -- --args "[\"Assets/....wav\"]"
public static class InspectClip
{
    public static string Main(string path)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        var data = new float[clip.samples * clip.channels];
        clip.GetData(data, 0);
        int step = clip.frequency * clip.channels / 20;
        var log = new StringBuilder($"{clip.name}: {clip.length:0.00} s, {clip.frequency} Hz, {clip.channels} ch, load {clip.loadType}\n");
        for (int start = 0; start < data.Length; start += step)
        {
            float peak = 0f;
            for (int i = start; i < Mathf.Min(start + step, data.Length); i++)
                peak = Mathf.Max(peak, Mathf.Abs(data[i]));
            log.Append($"{start / clip.channels * 1000 / clip.frequency}ms:{peak:0.00} ");
        }
        return log.ToString();
    }
}
