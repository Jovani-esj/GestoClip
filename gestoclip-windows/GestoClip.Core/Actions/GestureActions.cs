using GestoClip.Core.Protocol;

namespace GestoClip.Core.Actions;

/// <summary>Abstracciones de plataforma para poder probar la lógica sin Windows real.</summary>
public interface IClipboardService
{
    uint SequenceNumber();
    Payload? ReadAny();
    void Write(Payload p);
}

public interface IInputInjector
{
    void CtrlC();
    void CtrlV();
}

public interface IScreenCapture
{
    Payload CaptureActiveMonitor();
}

public interface ISender
{
    Task<int> SendToPeersAsync(Payload p, CancellationToken ct = default);
}

public sealed class GestureActions
{
    private readonly IClipboardService _clip;
    private readonly IInputInjector _input;
    private readonly IScreenCapture _screen;
    private readonly ISender _sender;
    private Payload? _inbox;
    private readonly object _lock = new();

    public int CopyTimeoutMs { get; init; } = 400;

    public GestureActions(IClipboardService clip, IInputInjector input, IScreenCapture screen, ISender sender)
        => (_clip, _input, _screen, _sender) = (clip, input, screen, sender);

    public void Receive(Payload p) { lock (_lock) _inbox = p; }

    public bool HasPending { get { lock (_lock) return _inbox is not null; } }

    /// <summary>Puño: copia la selección; si no cambió el portapapeles, captura pantalla. Devuelve el payload enviado.</summary>
    public async Task<Payload> OnFistAsync(CancellationToken ct = default)
    {
        var seq = _clip.SequenceNumber();
        _input.CtrlC();

        Payload? payload = null;
        var deadline = Environment.TickCount64 + CopyTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_clip.SequenceNumber() != seq) { payload = _clip.ReadAny(); break; }
            await Task.Delay(25, ct);
        }
        payload ??= _screen.CaptureActiveMonitor();
        await _sender.SendToPeersAsync(payload, ct);
        return payload;
    }

    /// <summary>Palma: pone lo recibido en el portapapeles y pega. False si no había nada pendiente.</summary>
    public bool OnPalm()
    {
        Payload? p;
        lock (_lock) { p = _inbox; _inbox = null; }
        if (p is null) return false;
        _clip.Write(p);
        _input.CtrlV();
        return true;
    }
}
