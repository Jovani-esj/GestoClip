using GestoClip.Core.Vision;
using OpenCvSharp;

namespace GestoClip.Vision;

/// <summary>Abre la webcam solo durante la ventana de activación y la libera al terminar.</summary>
public sealed class CameraSession
{
    private static readonly (int A, int B)[] Bones =
    {
        (0,1),(1,2),(2,3),(3,4),
        (0,5),(5,6),(6,7),(7,8),
        (0,9),(9,10),(10,11),(11,12),
        (0,13),(13,14),(14,15),(15,16),
        (0,17),(17,18),(18,19),(19,20),
        (5,9),(9,13),(13,17)
    };

    private readonly HandTracker _tracker;
    public CameraSession(HandTracker tracker) => _tracker = tracker;

    public event Action<HandPose, int>? Frame; // pose, frame #
    public event Action<byte[], HandPose, int>? FramePreview; // jpeg bytes, pose, frame #

    /// <summary>
    /// Ejecuta hasta que ocurra un gesto o se agote el tiempo. Devuelve el gesto detectado (None si hubo timeout).
    /// </summary>
    public Task<GestureEvent> RunAsync(TimeSpan window, CancellationToken ct = default) => Task.Run(() =>
    {
        using var cap = new VideoCapture(0, VideoCaptureAPIs.DSHOW);
        if (!cap.IsOpened()) throw new InvalidOperationException("No se pudo abrir la webcam (¿otra app la usa o falta permiso de cámara?)");
        cap.Set(VideoCaptureProperties.FrameWidth, 640);
        cap.Set(VideoCaptureProperties.FrameHeight, 480);

        var machine = new GestureStateMachine(4);
        using var frame = new Mat();
        var end = long.MaxValue;
        var n = 0;
        while (Environment.TickCount64 < end && !ct.IsCancellationRequested)
        {
            if (!cap.Read(frame) || frame.Empty()) { Thread.Sleep(30); continue; }
            if (end == long.MaxValue) end = Environment.TickCount64 + (long)window.TotalMilliseconds;

            var pose = HandPose.Unknown;
            var lm = _tracker.Detect(frame);
            if (lm is not null) pose = PoseClassifier.Classify(lm);
            ++n;
            Frame?.Invoke(pose, n);

            if (FramePreview is not null)
            {
                using var vis = frame.Clone();
                if (lm is not null) DrawHand(vis, lm, pose);
                Cv2.Flip(vis, vis, FlipMode.Y);
                Cv2.ImEncode(".jpg", vis, out var jpg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 80));
                FramePreview.Invoke(jpg, pose, n);
            }

            var ev = machine.Push(pose);
            if (ev != GestureEvent.None) return ev;
        }
        return GestureEvent.None;
    }, ct);

    private static void DrawHand(Mat img, IReadOnlyList<Landmark> lm, HandPose pose)
    {
        var color = pose switch
        {
            HandPose.Palm => new Scalar(80, 220, 100),
            HandPose.Fist => new Scalar(70, 140, 255),
            _ => new Scalar(200, 200, 200)
        };
        foreach (var (a, b) in Bones)
        {
            Cv2.Line(img, new Point((int)lm[a].X, (int)lm[a].Y), new Point((int)lm[b].X, (int)lm[b].Y), color, 2, LineTypes.AntiAlias);
        }
        for (var i = 0; i < lm.Count; i++)
        {
            Cv2.Circle(img, new Point((int)lm[i].X, (int)lm[i].Y), i % 4 == 0 ? 5 : 3, new Scalar(255, 255, 255), -1, LineTypes.AntiAlias);
        }
    }
}
