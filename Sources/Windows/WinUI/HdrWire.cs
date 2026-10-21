using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace PersonalTools;

internal static class HdrWire
{
    public const string RegistryPath = @"Software\PersonalTools\HdrWidget";
    public static string PipeName => $"PersonalTools.Hdr.v1.{WindowsIdentity.GetCurrent().User!.Value}.{Process.GetCurrentProcess().SessionId}";
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > 1048576) throw new IOException("HDR 메시지 크기가 올바르지 않습니다.");
        var body = new byte[size];
        await stream.ReadExactlyAsync(body, token);
        return JsonSerializer.Deserialize<T>(body) ?? throw new IOException("HDR 메시지가 비어 있습니다.");
    }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }
}
