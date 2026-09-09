using System.Net;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class TestNetworkEnvironment
{
    private static IWebProxy? originalProxy;

    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        originalProxy = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy();
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        if (originalProxy is not null) HttpClient.DefaultProxy = originalProxy;
    }
}
