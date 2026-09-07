using System.Reflection;
using System.Runtime.InteropServices;

var dll = @"C:\Users\manue\.nuget\packages\ndilibdotnet6\1.0.11\lib\net6.0\NDILibDotNet6.dll";
var a = Assembly.LoadFrom(dll);
foreach (var t in a.GetTypes().Where(t => t.Name.Contains("video_frame") || t.Name.Contains("FourCC")))
{
    Console.WriteLine("TYPE " + t.FullName);
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
        Console.WriteLine($"  FIELD {f.Name} {f.FieldType.FullName}");
    if (t.IsEnum)
        foreach (var n in Enum.GetNames(t).Take(40))
            Console.WriteLine($"  ENUM {n}={(int)Enum.Parse(t, n)}");
}
