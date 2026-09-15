using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Subtitles;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class SubtitleTests
{
    [TestMethod]
    public void NativeOrUnspecifiedClockCannotResolvePluginSubtitles()
    {
        using var workspace = new TestWorkspace();
        using var runtime = new Emby.StrmBridge.Runtime.PluginRuntime();
        runtime.Initialize(workspace.Path);
        runtime.UpdateOptions(new PluginConfiguration { Enabled = true, EnableSubtitles = true }, false);
        // Null services make any accidental media/auth access fail this check.
        var processor = new SubtitleRequestProcessor(runtime, null!, null!, null!, null!, null!, null!, null!);
        Assert.IsNull(processor.Resolve(null!, new Emby.StrmBridge.Api.CreateStrmBridgeSubtitleSession()));
        Assert.IsNull(processor.Resolve(null!, new { NativeHlsClock = true }));
        Assert.IsNull(processor.Resolve(null!, new { Id = "missing-clock" }));
        Assert.AreEqual(0, runtime.Tickets.Count);
    }

    [TestMethod]
    public void Configuration_SubtitlesAreOptInAndSurviveSnapshot()
    {
        var options = new PluginConfiguration();
        Assert.IsFalse(options.EnableSubtitles);
        options.EnableSubtitles = true;
        Assert.IsTrue(options.Snapshot().EnableSubtitles);
    }

    [TestMethod]
    [DataRow(true, PlaybackRoutingMode.Adaptive, false)]
    [DataRow(true, PlaybackRoutingMode.Adaptive, true)]
    [DataRow(false, PlaybackRoutingMode.Adaptive, false)]
    [DataRow(true, PlaybackRoutingMode.Native, false)]
    public void RequestPolicy_HostDenialAndDisabledFeatureNeverResolveMedia(bool enabled, PlaybackRoutingMode mode, bool disabledUser)
    {
        using var workspace = new TestWorkspace();
        using var runtime = new Emby.StrmBridge.Runtime.PluginRuntime();
        runtime.Initialize(workspace.Path, new ManualClock());
        var folder = new MediaBrowser.Controller.Entities.Folder { Id = Guid.NewGuid() };
        var item = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Id = Guid.NewGuid(),
            Path = workspace.Write("fixture.strm", "https://source.invalid/fixture.mkv"),
        };
        runtime.UpdateOptions(new PluginConfiguration
        {
            Enabled = true,
            EnableSubtitles = enabled,
            PlaybackMode = mode,
            IncludedLibraryIds = new[] { folder.Id.ToString("N") },
        }, false);
        var user = new MediaBrowser.Controller.Entities.User();
        var policy = user.GetType().GetProperty("Policy")!;
        policy.SetValue(user, Activator.CreateInstance(policy.PropertyType));
        user.Policy.EnableMediaPlayback = true;
        user.Policy.IsDisabled = disabledUser;
        var authorizationReads = 0;
        var authenticationCalls = 0;
        var mediaReads = 0;
        var authorization = TestProxy.Create<MediaBrowser.Controller.Net.IAuthorizationContext>((method, _) =>
        {
            authorizationReads++;
            var info = Activator.CreateInstance(method.ReturnType)!;
            info.GetType().GetProperty("User")!.SetValue(info, user);
            return info;
        });
        var auth = TestProxy.Create<MediaBrowser.Controller.Net.IAuthService>((_, _) =>
        {
            authenticationCalls++;
            throw new UnauthorizedAccessException("synthetic host policy denial");
        });
        var library = TestProxy.Create<MediaBrowser.Controller.Library.ILibraryManager>((method, _) => method.Name switch
        {
            "GetItemById" => item,
            "GetCollectionFolders" => new[] { folder },
            _ => TestDispatchProxy.DefaultValue(method.ReturnType),
        });
        var sources = TestProxy.Create<MediaBrowser.Controller.Library.IMediaSourceManager>((method, _) =>
        {
            mediaReads++;
            return TestDispatchProxy.DefaultValue(method.ReturnType);
        });
        var processor = new SubtitleRequestProcessor(runtime, library, sources, authorization, auth, null!, null!, null!);
        var http = TestProxy.Create<MediaBrowser.Model.Services.IRequest>((method, _) => TestDispatchProxy.DefaultValue(method.ReturnType));
        var request = new Emby.StrmBridge.Api.CreateStrmBridgeSubtitleSession { Id = item.Id.ToString("N"), MediaSourceId = "exact", Index = 3, NativeHlsClock = false };
        if (!enabled || mode == PlaybackRoutingMode.Native)
        {
            Assert.IsNull(processor.Resolve(http, request));
            Assert.AreEqual(0, authorizationReads);
        }
        else
        {
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => processor.Resolve(http, request));
            Assert.AreEqual(disabledUser ? 0 : 1, authenticationCalls);
        }
        Assert.AreEqual(0, mediaReads);
        Assert.AreEqual(0, runtime.Tickets.Count);
    }

}
