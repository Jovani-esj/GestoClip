namespace GestoClip.Core.Vision;

public readonly record struct Landmark(float X, float Y, float Z);

public enum HandPose { Unknown, Palm, Fist }

public enum GestureEvent { None, Copy, Paste }

/// <summary>Clasificador geométrico sobre los 21 landmarks de MediaPipe Hands.</summary>
public static class PoseClassifier
{
    // Índices MediaPipe: 0 muñeca; dedos (PIP, TIP): índice 6/8, medio 10/12, anular 14/16, meñique 18/20
    private static readonly (int Pip, int Tip)[] Fingers = { (6, 8), (10, 12), (14, 16), (18, 20) };

    public static HandPose Classify(IReadOnlyList<Landmark> lm)
    {
        if (lm.Count != 21) return HandPose.Unknown;
        var wrist = lm[0];
        var extended = 0;
        foreach (var (pip, tip) in Fingers)
            if (Dist(lm[tip], wrist) > Dist(lm[pip], wrist) * 1.05f) extended++;

        // Pulgar: punta más lejos del nudillo del meñique (17) que su articulación IP (3)
        if (Dist(lm[4], lm[17]) > Dist(lm[3], lm[17]) * 1.05f) extended++;

        return extended >= 4 ? HandPose.Palm : extended <= 1 ? HandPose.Fist : HandPose.Unknown;
    }

    private static float Dist(Landmark a, Landmark b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>
/// Máquina de estados temporal: exige una pose estable N cuadros y luego la transición.
/// Palm→Fist = Copy ("agarrar"); Fist→Palm = Paste ("soltar").
/// </summary>
public sealed class GestureStateMachine
{
    private readonly int _stableFrames;
    private HandPose _candidate = HandPose.Unknown;
    private int _count;
    private HandPose _stable = HandPose.Unknown;

    public GestureStateMachine(int stableFrames = 5) => _stableFrames = stableFrames;

    public GestureEvent Push(HandPose pose)
    {
        if (pose == HandPose.Unknown) { _candidate = HandPose.Unknown; _count = 0; return GestureEvent.None; }
        if (pose == _candidate) _count++; else { _candidate = pose; _count = 1; }
        if (_count < _stableFrames || _candidate == _stable) return GestureEvent.None;

        var prev = _stable;
        _stable = _candidate;
        if (prev == HandPose.Palm && _stable == HandPose.Fist) return GestureEvent.Copy;
        if (prev == HandPose.Fist && _stable == HandPose.Palm) return GestureEvent.Paste;
        return GestureEvent.None;
    }

    public void Reset() { _candidate = HandPose.Unknown; _stable = HandPose.Unknown; _count = 0; }
}
