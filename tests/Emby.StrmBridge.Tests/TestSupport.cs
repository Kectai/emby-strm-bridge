using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;

namespace Emby.StrmBridge.Tests;

internal sealed class ManualClock : IClock
{
    private long utcTicks;

    public ManualClock(DateTimeOffset? initial = null) =>
        utcTicks = (initial ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)).UtcDateTime.Ticks;

    public DateTimeOffset UtcNow => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan duration) => Interlocked.Add(ref utcTicks, duration.Ticks);
}

internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        var root = Environment.GetEnvironmentVariable("STRM_BRIDGE_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(root)) root = System.IO.Path.Combine(AppContext.BaseDirectory, "test-work");
        Path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string fileName, string content)
    {
        var path = System.IO.Path.Combine(Path, fileName);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

internal static class TestSources
{
    public static SourceIdentity Create(string suffix = "one") => new(
        new string('a', 63) + "1",
        new string('b', 63) + (suffix == "one" ? "1" : "2"),
        new Uri("https://source.invalid/entry?opaque=source-value"),
        "/library/item.strm",
        48,
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
}

internal class TestDispatchProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null) throw new InvalidOperationException("A proxied method is required.");
        if (Handler is not null) return Handler(targetMethod, args);
        return DefaultValue(targetMethod.ReturnType);
    }

    internal static object? DefaultValue(Type type)
    {
        if (type == typeof(void)) return null;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var valueType = type.GetGenericArguments()[0];
            var value = valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(valueType)
                .Invoke(null, new[] { value });
        }
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}

internal static class TestProxy
{
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, TestDispatchProxy>();
        ((TestDispatchProxy)(object)proxy).Handler = handler;
        return proxy;
    }
}
