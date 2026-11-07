namespace Reshare.Core.Capture;

/// <summary>Capture performed by ffmpeg itself (x11grab, avfoundation, gdigrab...).</summary>
public sealed class FfmpegNativeCapture(IReadOnlyList<string> inputArgs) : ICaptureSource
{
    public IReadOnlyList<string>? FfmpegInputArgs { get; } = inputArgs;
    public int Width => 0;
    public int Height => 0;
    public void Run(FrameHandler onFrame, CancellationToken ct) => throw new NotSupportedException();
    public void Dispose() { }
}
