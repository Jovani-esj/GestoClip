using GestoClip.Vision;
using OpenCvSharp;
using Xunit;

namespace GestoClip.Tests;

public class VisionSmokeTests
{
    private static string Assets => Path.Combine(AppContext.BaseDirectory, "Assets");

    [Fact]
    public void Models_Load_And_BlankFrame_ReturnsNoHand()
    {
        using var tracker = new HandTracker(Assets);
        using var blank = new Mat(480, 640, MatType.CV_8UC3, Scalar.Gray);
        Assert.Null(tracker.Detect(blank)); // ejercita palm-detect completo y verifica formas de salida
    }

    [Fact]
    public void Noise_Frame_DoesNotThrow()
    {
        using var tracker = new HandTracker(Assets);
        using var noise = new Mat(480, 640, MatType.CV_8UC3);
        Cv2.Randu(noise, Scalar.All(0), Scalar.All(255));
        var r = tracker.Detect(noise);
        Assert.True(r is null || r.Count == 21);
    }
}
