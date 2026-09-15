using System;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Subtitles;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Api;

[Route("/StrmBridge/Subtitles/Sessions", "POST")]
public sealed class CreateStrmBridgeSubtitleSession
{
    public string Id { get; set; } = "";
    public long VideoStartTicks { get; set; }
    public bool NativeHlsClock { get; set; } = true;
    public string PlaySessionId { get; set; } = "";
    public string MediaSourceId { get; set; } = "";
    public int Index { get; set; } = -1;
    public string Format => "ass";
    public long StartPositionTicks { get; set; }
    public long? EndPositionTicks { get; set; }
}
[Route("/StrmBridge/Subtitles/Sessions/{SessionId}/Stream", "GET")]
public sealed class GetStrmBridgeSubtitleWindow
{
    public long? MseTimestampOffsetTicks { get; set; }
    public string SessionId { get; set; } = "";
    public long StartPositionTicks { get; set; }
    public long EndPositionTicks { get; set; }
}
[Route("/StrmBridge/Subtitles/Sessions/{SessionId}", "DELETE")]
public sealed class DeleteStrmBridgeSubtitleSession { public string SessionId { get; set; } = ""; }

public sealed class SubtitlePlaybackService : IService, IRequiresRequest
{
    private readonly IAuthorizationContext authorization;
    private readonly IAuthService authService;
    public SubtitlePlaybackService(IAuthorizationContext authorization, IAuthService authService)
    { this.authorization = authorization; this.authService = authService; }
    public IRequest Request { get; set; } = null!;
    public object Post(CreateStrmBridgeSubtitleSession request)
    {
        RequireUser();
        var runtime = Plugin.Runtime;
        if (runtime?.SubtitlePatch?.CanServe != true) return Error(422, "outside-scope");
        try
        {
            var context = runtime.SubtitleRequests?.Resolve(Request, request);
            if (context is null) return Error(422, "outside-scope");
            var session = runtime.Subtitles!.CreateSession(context);
            return new { SessionId = session.Id, WindowSeconds = 60 };
        }
        catch (SubtitleBusyException) { return Error(503, "busy"); }
        catch (SubtitleProblem problem) { return Error(SubtitleRequestProcessor.ProblemStatus(problem.Reason), problem.Reason); }
        catch (ArgumentException) { return Error(400, "invalid-source"); }
    }
    public async Task<object> Get(GetStrmBridgeSubtitleWindow request)
    {
        var user = RequireUser();
        var runtime = Plugin.Runtime;
        var session = runtime?.Subtitles?.FindSession(request.SessionId, user);
        if (session is null) return Error(404, "session-unavailable");
        var bound = session.Context;
        try
        {
            var context = runtime!.SubtitleRequests!.Resolve(Request, new CreateStrmBridgeSubtitleSession
            {
                Id = bound.RequestedItemId.ToString("N"),
                MediaSourceId = bound.MediaSourceId,
                PlaySessionId = bound.PlaySessionId,
                VideoStartTicks = bound.VideoStartTicks,
                NativeHlsClock = bound.NativeHlsClock,
                Index = bound.Index,
                StartPositionTicks = request.StartPositionTicks,
                EndPositionTicks = request.EndPositionTicks,
            });
            if (context is null || context.Generation != bound.Generation || context.StreamFingerprint != bound.StreamFingerprint || !context.Source.HasSameFileVersion(bound.Source))
                return Error(409, "source-changed");
            context.MseTimestampOffsetTicks = request.MseTimestampOffsetTicks;
            return await runtime.SubtitleRequests.RespondAsync(Request, context, session.Token).ConfigureAwait(false);
        }
        catch (SubtitleBusyException) { return Error(503, "busy"); }
        catch (SubtitleProblem problem) { return Error(SubtitleRequestProcessor.ProblemStatus(problem.Reason), problem.Reason); }
        catch (ArgumentException) { return Error(400, "invalid-window"); }
    }
    public object Delete(DeleteStrmBridgeSubtitleSession request)
    {
        var user = RequireUser();
        Plugin.Runtime?.Subtitles?.EndSession(request.SessionId, user);
        Request.Response.StatusCode = 204;
        return string.Empty;
    }
    private Guid RequireUser()
    {
        authService.Authenticate(Request, new AuthenticatedAttribute());
        var user = authorization.GetAuthorizationInfo(Request).User;
        if (user is null || user.Policy.IsDisabled || !user.Policy.EnableMediaPlayback) throw new UnauthorizedAccessException();
        Request.Response.AddHeader("Cache-Control", "private, no-store");
        return user.Id;
    }
    private object Error(int status, string reason)
    { Request.Response.StatusCode = status; return new { ReasonCode = reason }; }
}
