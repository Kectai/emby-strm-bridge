using Emby.StrmBridge.Runtime;
using Emby.StrmBridge.Subtitles;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Events;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class SubtitleCompatibilityTests
{
    [TestMethod]
    public void UnknownWebResourcesAreNeverRewritten() => Assert.IsNull(SubtitlePatchHost.Transform("function renderAssSsa(){}"));

    [TestMethod]
    public void MissingPlaybackInputs_NeverNegotiatesSharedHls()
    {
        using var runtime = new PluginRuntime();
        var library = TestProxy.Create<ILibraryManager>((_, _) => throw new AssertFailedException("Must reject before accessing media."));
        var sources = TestProxy.Create<IMediaSourceManager>((_, _) => throw new AssertFailedException("Must reject before accessing media."));
        Assert.IsFalse(runtime.SubtitleInputsReady);
        var negotiation = new SharedSubtitleNegotiation(runtime, library, sources);
        Assert.IsNull(negotiation.Prepare(1, SharedSubtitleNegotiationTests.Source(), SharedSubtitleNegotiationTests.Profile(), null!, true));
    }

    [TestMethod]
    public void RunnerAbi_RequiresStringTaskAndLifecycleMembers()
    {
        Assert.IsNotNull(SubtitlePatchHost.FindRunnerTarget(typeof(CompleteRunner)));
        Assert.IsNull(SubtitlePatchHost.FindRunnerTarget(typeof(MissingRunnerState)));
        Assert.IsNull(SubtitlePatchHost.FindRunnerTarget(typeof(WrongRunnerReturn)));
        Assert.IsNull(SubtitlePatchHost.FindRunnerTarget(typeof(WrongRunnerEvent)));
    }

#pragma warning disable CS0169, CS0067
    private sealed class CompleteRunner
    {
        private object? command;
        private object? jobState;
        public event EventHandler<GenericEventArgs<int>>? Exited;
        public Task<bool> Start(string command, CancellationToken token) => Task.FromResult(true);
    }
    private sealed class MissingRunnerState
    {
        public Task<bool> Start(string command, CancellationToken token) => Task.FromResult(true);
    }
    private sealed class WrongRunnerReturn
    {
        private object? command;
        private object? jobState;
        public event EventHandler<GenericEventArgs<int>>? Exited;
        public bool Start(string command, CancellationToken token) => true;
    }
    private sealed class WrongRunnerEvent
    {
        private object? command;
        private object? jobState;
        public event EventHandler? Exited;
        public Task<bool> Start(string command, CancellationToken token) => Task.FromResult(true);
    }
#pragma warning restore CS0169, CS0067
}
