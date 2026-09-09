using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Media.Model.ProbeModel;
using MediaBrowser.Common;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;

namespace Emby.StrmBridge.Extraction;

internal interface IExtractionProbeFallback
{
    bool IsAvailable { get; }

    Task ProbeAsync(MediaSourceInfo mediaSource, bool isAudio, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the host-configured ffprobe directly when an in-process host probe is intercepted
/// before it opens the Bridge loopback input. The input remains the loopback gateway, so
/// redirect validation, DNS pinning, cancellation and concurrency stay inside the Bridge.
/// </summary>
internal sealed class IndependentFfprobeMediaInfoProbe : IExtractionProbeFallback
{
    private const int MaximumStandardOutputBytes = 32 * 1024 * 1024;
    private const int MaximumStandardErrorBytes = 256 * 1024;
    private const int MaximumProbeStreams = 1024;
    private const string TransportStreamAnalysisDurationMicroseconds = "200000000";
    private const string TransportStreamProbeSizeBytes = "134217728";
    private const string NormalizerTypeName =
        "Emby.Server.MediaEncoding.Probing.ProbeResultNormalizer, Emby.Server.MediaEncoding";
    private readonly IApplicationHost applicationHost;
    private readonly IFfmpegManager ffmpegManager;
    private readonly IStreamInfoManager streamInfoManager;
    private readonly IJsonSerializer jsonSerializer;
    private readonly ILogger logger;
    private readonly Lazy<NormalizerBinding?> normalizer;

    private IndependentFfprobeMediaInfoProbe(
        IApplicationHost applicationHost,
        IFfmpegManager ffmpegManager,
        IStreamInfoManager streamInfoManager,
        IJsonSerializer jsonSerializer,
        ILogger logger)
    {
        this.applicationHost = applicationHost;
        this.ffmpegManager = ffmpegManager;
        this.streamInfoManager = streamInfoManager;
        this.jsonSerializer = jsonSerializer;
        this.logger = logger;
        normalizer = new Lazy<NormalizerBinding?>(CreateNormalizer, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsAvailable => GetProbeExecutable() is not null && normalizer.Value is not null;

    internal static IExtractionProbeFallback? TryCreate(IApplicationHost applicationHost, ILogger logger)
    {
        if (applicationHost is null) throw new ArgumentNullException(nameof(applicationHost));
        if (logger is null) throw new ArgumentNullException(nameof(logger));
        try
        {
            var ffmpegManager = applicationHost.TryResolve<IFfmpegManager>();
            var streamInfoManager = applicationHost.TryResolve<IStreamInfoManager>();
            var jsonSerializer = applicationHost.TryResolve<IJsonSerializer>();
            return ffmpegManager is null || streamInfoManager is null || jsonSerializer is null
                ? null
                : new IndependentFfprobeMediaInfoProbe(
                    applicationHost,
                    ffmpegManager,
                    streamInfoManager,
                    jsonSerializer,
                    logger);
        }
        catch (Exception exception)
        {
            logger.Warn("STRM_BRIDGE_EXTRACTION_FALLBACK_UNAVAILABLE stage=services error=" +
                        exception.GetType().Name);
            return null;
        }
    }

    public async Task ProbeAsync(
        MediaSourceInfo mediaSource,
        bool isAudio,
        CancellationToken cancellationToken)
    {
        if (mediaSource is null) throw new ArgumentNullException(nameof(mediaSource));
        var input = mediaSource.ProbePath ?? mediaSource.Path;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var inputUri) ||
            !inputUri.IsLoopback ||
            inputUri.Scheme != Uri.UriSchemeHttp && inputUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The independent probe input is not a loopback HTTP resource.");
        var executable = GetProbeExecutable() ??
                         throw new InvalidOperationException("The host ffprobe executable is unavailable.");
        var binding = normalizer.Value ??
                      throw new InvalidOperationException("The host probe normalizer is unavailable.");
        var userAgent = GetUserAgent(mediaSource);
        var arguments = new List<string>
        {
            "-user_agent", userAgent,
        };
        if (IsTransportStream(inputUri.AbsolutePath))
        {
            // Some Blu-ray transport streams place usable codec and duration evidence far
            // beyond ffprobe's default analysis window. Keep the deeper scan bounded and
            // apply it only to explicit TS/M2TS inputs.
            arguments.Add("-analyzeduration");
            arguments.Add(TransportStreamAnalysisDurationMicroseconds);
            arguments.Add("-probesize");
            arguments.Add(TransportStreamProbeSizeBytes);
        }
        arguments.AddRange(new[]
        {
            "-i", input,
            "-threads", "0",
            "-v", "info",
            "-print_format", "json",
            "-show_streams",
            "-show_format",
            "-show_chapters",
            "-show_data",
        });
        var json = await RunProcessAsync(
                executable,
                arguments,
                ffmpegManager.FfmpegConfiguration,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var probeResult = jsonSerializer.DeserializeFromString<ProbeResult>(json) ??
                              throw new InvalidDataException("The independent probe returned no result.");
            if (probeResult.error is not null)
                throw new InvalidDataException("The independent probe returned an error result.");
            if ((probeResult.streams?.Length ?? 0) > MaximumProbeStreams)
                throw new InvalidDataException("The independent probe returned too many streams.");
            SanitizeProbeRatios(probeResult);
            var normalized = binding.Normalize(probeResult, isAudio, input, MediaProtocol.Http);
            foreach (var stream in normalized.MediaStreams ?? Enumerable.Empty<MediaBrowser.Model.Entities.MediaStream>())
                if (stream is not null)
                    streamInfoManager.SetDynamicValues(stream);
            CopyTechnicalMediaInfo(normalized, mediaSource);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException))
        {
            throw new IndependentProbeResultException(exception);
        }
    }

    private string? GetProbeExecutable()
    {
        try
        {
            var path = ffmpegManager.FfmpegConfiguration?.ProbePath;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private NormalizerBinding? CreateNormalizer()
    {
        try
        {
            var type = Type.GetType(NormalizerTypeName, throwOnError: false);
            if (type is null) return null;
            var method = type.GetMethod(
                "GetMediaInfo",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(ProbeResult), typeof(bool), typeof(string), typeof(MediaProtocol) },
                modifiers: null);
            if (method is null || method.ReturnType != typeof(MediaInfo)) return null;
            var fileSystem = applicationHost.TryResolve<IFileSystem>() ??
                             throw new InvalidOperationException("The host file-system service is unavailable.");
            var localizationManager = applicationHost.TryResolve<ILocalizationManager>() ??
                                      throw new InvalidOperationException(
                                          "The host localization service is unavailable.");
            var instance = Activator.CreateInstance(type, logger, fileSystem, localizationManager) ??
                           throw new InvalidOperationException("The host did not create the probe normalizer.");
            return new NormalizerBinding(instance, method);
        }
        catch (Exception exception)
        {
            logger.Warn("STRM_BRIDGE_EXTRACTION_FALLBACK_UNAVAILABLE stage=normalizer error=" +
                        exception.GetType().Name);
            return null;
        }
    }

    private static string GetUserAgent(MediaSourceInfo mediaSource)
    {
        if (mediaSource.RequiredHttpHeaders is not null &&
            mediaSource.RequiredHttpHeaders.TryGetValue("User-Agent", out var userAgent) &&
            !string.IsNullOrWhiteSpace(userAgent) && userAgent.Length <= 256 &&
            userAgent.IndexOfAny(new[] { '\r', '\n' }) < 0)
            return userAgent;
        return "Emby.StrmBridge/" +
               (typeof(IndependentFfprobeMediaInfoProbe).Assembly.GetName().Version?.ToString(3) ?? "unknown");
    }

    private static bool IsTransportStream(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".ts", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".m2ts", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".mts", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyTechnicalMediaInfo(MediaInfo source, MediaSourceInfo destination)
    {
        destination.Container = source.Container;
        destination.Size = source.Size;
        destination.Bitrate = source.Bitrate;
        destination.RunTimeTicks = source.RunTimeTicks;
        destination.DefaultAudioStreamIndex = source.DefaultAudioStreamIndex;
        destination.DefaultSubtitleStreamIndex = source.DefaultSubtitleStreamIndex;
        destination.MediaStreams = source.MediaStreams?.ToList() ?? new List<MediaBrowser.Model.Entities.MediaStream>();
        destination.Formats = source.Formats;
        destination.ContainerStartTimeTicks = source.ContainerStartTimeTicks;
        destination.Timestamp = source.Timestamp;
        destination.Video3DFormat = source.Video3DFormat;
        destination.Chapters = source.Chapters;
    }

    private static void SanitizeProbeRatios(ProbeResult result)
    {
        foreach (var stream in result.streams ?? Array.Empty<ProbeStream>())
        {
            if (stream is null) continue;
            if (string.Equals(stream.display_aspect_ratio, "0:1", StringComparison.Ordinal))
                stream.display_aspect_ratio = null;
            if (string.Equals(stream.sample_aspect_ratio, "0:1", StringComparison.Ordinal))
                stream.sample_aspect_ratio = null;
        }
    }

    private static async Task<string> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IFfmpegConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
#pragma warning disable CS0612 // Host compatibility API applies its complete ffmpeg environment.
        configuration.ApplyVariables(startInfo);
#pragma warning restore CS0612

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The host ffprobe process did not start.");
        }
        catch (Exception exception) when (!(exception is OperationCanceledException))
        {
            throw new InvalidOperationException("The host ffprobe process could not be started.", exception);
        }

        using var cancellation = cancellationToken.Register(() => TryKill(process));
        var stdout = ReadBoundedAndKillAsync(
            process,
            process.StandardOutput.BaseStream,
            MaximumStandardOutputBytes,
            cancellationToken);
        var stderr = ReadBoundedAndKillAsync(
            process,
            process.StandardError.BaseStream,
            MaximumStandardErrorBytes,
            cancellationToken);
        var exit = WaitForExitAsync(process, cancellationToken);
        try
        {
            await Task.WhenAll(stdout, stderr, exit).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            TryWaitForExit(process);
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (exit.Result != 0)
            throw new InvalidDataException("The independent media probe failed.");
        var bytes = stdout.Result;
        if (bytes.Length == 0) throw new InvalidDataException("The independent media probe returned no output.");
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> ReadBoundedAndKillAsync(
        Process process,
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadBoundedAsync(stream, maximumBytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                .ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximumBytes)
                throw new InvalidDataException("The independent media probe output exceeded its limit.");
            output.Write(buffer, 0, count);
        }
    }

    private static Task<int> WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Complete(object? sender, EventArgs args)
        {
            try { completion.TrySetResult(process.ExitCode); }
            catch (InvalidOperationException exception) { completion.TrySetException(exception); }
        }
        process.Exited += Complete;
        try
        {
            if (process.HasExited) completion.TrySetResult(process.ExitCode);
        }
        catch (InvalidOperationException exception)
        {
            completion.TrySetException(exception);
        }
        if (!cancellationToken.CanBeCanceled) return completion.Task;
        return AwaitExitWithCancellationAsync(completion.Task, process, cancellationToken);
    }

    private static async Task<int> AwaitExitWithCancellationAsync(
        Task<int> exit,
        Process process,
        CancellationToken cancellationToken)
    {
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => canceled.TrySetResult(true)))
        {
            if (exit != await Task.WhenAny(exit, canceled.Task).ConfigureAwait(false))
            {
                TryKill(process);
                throw new OperationCanceledException(cancellationToken);
            }
        }
        return await exit.ConfigureAwait(false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (NotSupportedException) { }
    }

    private static void TryWaitForExit(Process process)
    {
        try { process.WaitForExit(5000); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (NotSupportedException) { }
    }

    private sealed class NormalizerBinding
    {
        private readonly object instance;
        private readonly MethodInfo method;

        public NormalizerBinding(object instance, MethodInfo method)
        {
            this.instance = instance;
            this.method = method;
        }

        public MediaInfo Normalize(ProbeResult result, bool isAudio, string path, MediaProtocol protocol)
        {
            try
            {
                return method.Invoke(instance, new object[] { result, isAudio, path, protocol }) as MediaInfo ??
                       throw new InvalidDataException("The host probe normalizer returned no media information.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        }
    }
}

internal sealed class IndependentProbeResultException : Exception
{
    public IndependentProbeResultException(Exception innerException)
        : base("The independent probe result could not be normalized.", innerException) { }
}
