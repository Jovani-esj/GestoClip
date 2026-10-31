using GestoClip.Core.Actions;
using GestoClip.Core.Protocol;
using GestoClip.Core.Transport;
using GestoClip.Core.Vision;
using Xunit;

namespace GestoClip.Tests;

public class FramingTests
{
    [Fact]
    public async Task RoundTrip_PreservesPayload()
    {
        var p = new Payload(Kinds.Image, "a.png", "image/png", Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray());
        var ms = new MemoryStream();
        await Framing.WriteAsync(ms, p);
        ms.Position = 0;
        var r = await Framing.ReadAsync(ms);
        Assert.Equal(p.Kind, r.Kind);
        Assert.Equal(p.Name, r.Name);
        Assert.Equal(p.Data, r.Data);
    }

    [Fact]
    public async Task CorruptedData_FailsSha()
    {
        var ms = new MemoryStream();
        await Framing.WriteAsync(ms, new Payload(Kinds.Text, "t", "text/plain", "hola mundo"u8.ToArray()));
        var bytes = ms.ToArray();
        bytes[^1] ^= 0xFF;
        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadAsync(new MemoryStream(bytes)));
    }

    [Fact]
    public async Task Truncated_Throws()
    {
        var ms = new MemoryStream();
        await Framing.WriteAsync(ms, new Payload(Kinds.Text, "t", "text/plain", new byte[100]));
        var bytes = ms.ToArray()[..^10];
        await Assert.ThrowsAsync<EndOfStreamException>(() => Framing.ReadAsync(new MemoryStream(bytes)));
    }
}

public class ClassifierTests
{
    // Mano sintética: muñeca en origen, dedos hacia +Y. Extendido => tip lejos; curvado => tip cerca de la muñeca.
    private static List<Landmark> Hand(bool extended)
    {
        var lm = Enumerable.Repeat(new Landmark(0, 0, 0), 21).ToList();
        int[] pips = { 6, 10, 14, 18 }, tips = { 8, 12, 16, 20 };
        for (var i = 0; i < 4; i++)
        {
            lm[pips[i]] = new Landmark(i * 0.02f, 0.5f, 0);
            lm[tips[i]] = new Landmark(i * 0.02f, extended ? 0.9f : 0.2f, 0);
        }
        lm[17] = new Landmark(0.1f, 0.3f, 0);
        lm[3] = new Landmark(-0.1f, 0.3f, 0);
        lm[4] = extended ? new Landmark(-0.3f, 0.5f, 0) : new Landmark(0.05f, 0.25f, 0);
        return lm;
    }

    [Fact] public void OpenHand_IsPalm() => Assert.Equal(HandPose.Palm, PoseClassifier.Classify(Hand(true)));
    [Fact] public void ClosedHand_IsFist() => Assert.Equal(HandPose.Fist, PoseClassifier.Classify(Hand(false)));
    [Fact] public void WrongCount_IsUnknown() => Assert.Equal(HandPose.Unknown, PoseClassifier.Classify(new List<Landmark>()));
}

public class StateMachineTests
{
    private static GestureEvent Feed(GestureStateMachine m, HandPose p, int n)
    {
        var last = GestureEvent.None;
        for (var i = 0; i < n; i++) { var e = m.Push(p); if (e != GestureEvent.None) last = e; }
        return last;
    }

    [Fact]
    public void PalmThenFist_IsCopy()
    {
        var m = new GestureStateMachine(5);
        Assert.Equal(GestureEvent.None, Feed(m, HandPose.Palm, 8));
        Assert.Equal(GestureEvent.Copy, Feed(m, HandPose.Fist, 8));
    }

    [Fact]
    public void FistThenPalm_IsPaste()
    {
        var m = new GestureStateMachine(5);
        Feed(m, HandPose.Fist, 8);
        Assert.Equal(GestureEvent.Paste, Feed(m, HandPose.Palm, 8));
    }

    [Fact]
    public void Jitter_DoesNotTrigger()
    {
        var m = new GestureStateMachine(5);
        Feed(m, HandPose.Palm, 8);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(GestureEvent.None, m.Push(HandPose.Fist));
            Assert.Equal(GestureEvent.None, m.Push(HandPose.Palm));
        }
    }

    [Fact]
    public void HoldingFist_FiresOnce()
    {
        var m = new GestureStateMachine(3);
        Feed(m, HandPose.Palm, 5);
        var count = 0;
        for (var i = 0; i < 20; i++) if (m.Push(HandPose.Fist) == GestureEvent.Copy) count++;
        Assert.Equal(1, count);
    }
}

public class GestureActionsTests
{
    private sealed class FakeClip : IClipboardService
    {
        public uint Seq; public Payload? Content; public Payload? Written;
        public uint SequenceNumber() => Seq;
        public Payload? ReadAny() => Content;
        public void Write(Payload p) { Written = p; }
    }
    private sealed class FakeInput : IInputInjector
    {
        public FakeClip Clip = null!; public bool CopyChangesClipboard; public int Pastes;
        public void CtrlC() { if (CopyChangesClipboard) { Clip.Seq++; Clip.Content = new Payload(Kinds.Text, "sel", "text/plain", "x"u8.ToArray()); } }
        public void CtrlV() => Pastes++;
    }
    private sealed class FakeScreen : IScreenCapture
    {
        public Payload CaptureActiveMonitor() => new(Kinds.Screenshot, "s.png", "image/png", new byte[] { 1, 2, 3 });
    }
    private sealed class FakeSender : ISender
    {
        public List<Payload> Sent = new();
        public Task<int> SendToPeersAsync(Payload p, CancellationToken ct = default) { Sent.Add(p); return Task.FromResult(1); }
    }

    private static (GestureActions a, FakeClip c, FakeInput i, FakeSender s) Make(bool copyChanges)
    {
        var c = new FakeClip(); var i = new FakeInput { Clip = c, CopyChangesClipboard = copyChanges }; var s = new FakeSender();
        return (new GestureActions(c, i, new FakeScreen(), s) { CopyTimeoutMs = 150 }, c, i, s);
    }

    [Fact]
    public async Task Fist_WithSelection_SendsSelection()
    {
        var (a, _, _, s) = Make(true);
        var p = await a.OnFistAsync();
        Assert.Equal(Kinds.Text, p.Kind);
        Assert.Single(s.Sent);
    }

    [Fact]
    public async Task Fist_WithoutSelection_SendsScreenshot()
    {
        var (a, _, _, s) = Make(false);
        var p = await a.OnFistAsync();
        Assert.Equal(Kinds.Screenshot, p.Kind);
        Assert.Single(s.Sent);
    }

    [Fact]
    public void Palm_WithInbox_WritesAndPastes()
    {
        var (a, c, i, _) = Make(false);
        a.Receive(new Payload(Kinds.Text, "t", "text/plain", "hola"u8.ToArray()));
        Assert.True(a.OnPalm());
        Assert.NotNull(c.Written);
        Assert.Equal(1, i.Pastes);
        Assert.False(a.OnPalm()); // ya se consumió
    }

    [Fact]
    public void Palm_WithoutInbox_DoesNothing()
    {
        var (a, c, i, _) = Make(false);
        Assert.False(a.OnPalm());
        Assert.Null(c.Written);
        Assert.Equal(0, i.Pastes);
    }
}
