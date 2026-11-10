using System;
using System.Reflection;
using System.Text;

// Ищет класс мастера рэгдолла Unity (UnityEditor.RagdollBuilder) по загруженным сборкам и печатает его поля и методы.
// Запуск: unity command run_script --file Tools/Unity/FindRagdollBuilder.cs --project-path "D:\Game Tactick"
public static class FindRagdollBuilder
{
    public static string Main()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType("UnityEditor.RagdollBuilder");
            if (type == null)
                continue;
            var log = new StringBuilder(type.AssemblyQualifiedName).AppendLine();
            foreach (var field in type.GetFields(flags))
                log.Append(field.Name).Append(' ');
            log.AppendLine();
            foreach (var method in type.GetMethods(flags))
                log.Append(method.Name).Append(' ');
            return log.ToString();
        }
        return "not found";
    }
}
