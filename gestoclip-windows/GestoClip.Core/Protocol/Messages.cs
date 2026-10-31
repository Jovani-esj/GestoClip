using System.Text.Json;
using System.Text.Json.Serialization;

namespace GestoClip.Core.Protocol;

public static class Kinds
{
    public const string Text = "TEXT";
    public const string Image = "IMAGE";
    public const string File = "FILE";
    public const string Screenshot = "SCREENSHOT";
    public const string PairRequest = "PAIR_REQUEST";
    public const string PairAck = "PAIR_ACK";
}

public record Beacon(int V, string DeviceId, string Name, string Platform, int Port, string PubKeyFp, long SentAt);

public record ClipMessage(int V, string Kind, string Name, string Mime, long Size, string Sha256);

public record PairRequestMessage(string Token, string DeviceId, string Name, string Platform);

public record PairAckMessage(bool Accepted, string ServerDeviceId, string ServerName, string ServerPlatform, string Reason = "");

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string s) => JsonSerializer.Deserialize<T>(s, Options);
}

/// <summary>Contenido listo para viajar o pegarse.</summary>
public record Payload(string Kind, string Name, string Mime, byte[] Data);
