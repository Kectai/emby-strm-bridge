using System.Reflection;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;

var roots = new[] { typeof(BasePlugin).Assembly, typeof(IServerApplicationPaths).Assembly };
var filters = args.Length == 0 ? new[] { "IMediaSourceProvider", "IScheduledTask" } : args;
var assemblies = roots.SelectMany(LoadClosure).DistinctBy(x => x.FullName).ToArray();

foreach (var type in assemblies.SelectMany(GetLoadableTypes)
             .Where(type => filters.Any(filter => type.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
             .OrderBy(type => type.FullName, StringComparer.Ordinal))
{
    Console.WriteLine($"TYPE {type.FullName} [{type.Assembly.GetName().Name}]");
    if (type.IsEnum) Console.WriteLine($"  VALUES {string.Join(", ", Enum.GetNames(type))}");
    foreach (var constructor in type.GetConstructors()) Console.WriteLine($"  CTOR {Format(constructor)}");
    foreach (var property in type.GetProperties().OrderBy(x => x.Name)) Console.WriteLine($"  PROP {FormatType(property.PropertyType)} {property.Name}");
    foreach (var method in type.GetMethods().Where(x => !x.IsSpecialName).OrderBy(x => x.Name).ThenBy(x => x.GetParameters().Length)) Console.WriteLine($"  METHOD {Format(method)}");
    Console.WriteLine();
}

static IEnumerable<Assembly> LoadClosure(Assembly root)
{
    var pending = new Queue<Assembly>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    pending.Enqueue(root);
    while (pending.Count > 0)
    {
        var assembly = pending.Dequeue();
        if (!seen.Add(assembly.FullName ?? assembly.GetName().Name ?? string.Empty)) continue;
        yield return assembly;
        foreach (var reference in assembly.GetReferencedAssemblies())
        {
            try { pending.Enqueue(Assembly.Load(reference)); } catch { }
        }
    }
}

static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
{
    try { return assembly.GetTypes(); }
    catch (ReflectionTypeLoadException exception) { return exception.Types.OfType<Type>(); }
}

static string Format(MethodBase method)
{
    var result = method is MethodInfo info ? FormatType(info.ReturnType) + " " : string.Empty;
    return result + method.Name + "(" + string.Join(", ", method.GetParameters().Select(x => FormatType(x.ParameterType) + " " + x.Name)) + ")";
}

static string FormatType(Type type)
{
    if (!type.IsGenericType) return type.FullName ?? type.Name;
    var name = type.GetGenericTypeDefinition().FullName ?? type.Name;
    return name[..name.IndexOf('`')] + "<" + string.Join(", ", type.GetGenericArguments().Select(FormatType)) + ">";
}
