using System.Reflection;
using System.Runtime.Loader;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;

var parsed = ParseArguments(args);
var assemblies = parsed.AssemblyDirectory is null
    ? new[] { typeof(BasePlugin).Assembly, typeof(IServerApplicationPaths).Assembly }
        .SelectMany(LoadClosure)
        .DistinctBy(assembly => assembly.FullName)
        .ToArray()
    : LoadDirectory(parsed.AssemblyDirectory);

foreach (var assembly in assemblies.OrderBy(value => value.GetName().Name, StringComparer.Ordinal))
{
    var matches = GetLoadableTypes(assembly)
        .Where(type => parsed.Filters.Any(filter =>
            (type.FullName ?? type.Name).Contains(filter, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(type => type.FullName, StringComparer.Ordinal)
        .ToArray();
    if (matches.Length == 0) continue;
    Console.WriteLine($"ASSEMBLY {assembly.GetName().Name} {assembly.GetName().Version}");
    foreach (var type in matches) PrintType(type);
}

static ProbeArguments ParseArguments(string[] values)
{
    string? directory = null;
    var filters = new List<string>();
    for (var index = 0; index < values.Length; index++)
    {
        if (string.Equals(values[index], "--assembly-directory", StringComparison.Ordinal))
        {
            if (++index >= values.Length) throw new ArgumentException("An assembly directory is required.");
            directory = Path.GetFullPath(values[index]);
            continue;
        }
        filters.Add(values[index]);
    }
    if (filters.Count == 0) filters.AddRange(new[] { "IMediaSourceProvider", "IScheduledTask" });
    return new ProbeArguments(directory, filters.ToArray());
}

static Assembly[] LoadDirectory(string directory)
{
    if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
    var context = new AssemblyDirectoryLoadContext(directory);
    var loaded = new List<Assembly>();
    foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
    {
        try { loaded.Add(context.LoadFromAssemblyPath(Path.GetFullPath(path))); }
        catch (Exception exception) when (
            exception is BadImageFormatException ||
            exception is FileLoadException ||
            exception is FileNotFoundException)
        {
        }
    }
    return loaded.DistinctBy(assembly => assembly.FullName).ToArray();
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
    catch { return Array.Empty<Type>(); }
}

static void PrintType(Type type)
{
    Console.WriteLine($"TYPE {type.FullName}");
    Console.WriteLine($"  BASE {FormatType(type.BaseType)}");
    if (type.IsEnum) Console.WriteLine($"  VALUES {string.Join(", ", Enum.GetNames(type))}");
    foreach (var constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        Console.WriteLine($"  CTOR {Format(constructor)}");
    foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                 .OrderBy(value => value.Name))
        Console.WriteLine($"  PROP {FormatType(property.PropertyType)} {property.Name} " +
                          $"read={property.CanRead} write={property.CanWrite}");
    foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                 .Where(value => !value.IsSpecialName && value.DeclaringType == type)
                 .OrderBy(value => value.Name)
                 .ThenBy(value => value.GetParameters().Length))
        Console.WriteLine($"  METHOD {Format(method)}");
    Console.WriteLine();
}

static string Format(MethodBase method)
{
    var result = method is MethodInfo info ? FormatType(info.ReturnType) + " " : string.Empty;
    return result + method.Name + "(" + string.Join(", ", method.GetParameters().Select(parameter =>
        FormatType(parameter.ParameterType) + " " + parameter.Name)) + ")";
}

static string FormatType(Type? type)
{
    if (type is null) return "-";
    if (!type.IsGenericType) return type.FullName ?? type.Name;
    var name = type.GetGenericTypeDefinition().FullName ?? type.Name;
    return name[..name.IndexOf('`')] + "<" + string.Join(", ", type.GetGenericArguments().Select(FormatType)) + ">";
}

internal sealed record ProbeArguments(string? AssemblyDirectory, string[] Filters);

internal sealed class AssemblyDirectoryLoadContext : AssemblyLoadContext
{
    private readonly string directory;

    public AssemblyDirectoryLoadContext(string directory) : base(isCollectible: false)
    {
        this.directory = directory;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = Path.Combine(directory, assemblyName.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
}
