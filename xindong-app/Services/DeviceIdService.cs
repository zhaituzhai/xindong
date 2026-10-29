using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32;
using XinDong.Models;

namespace XinDong.Services;

/// <summary>
/// 设备指纹生成与身份持久化。
/// 指纹来源：MachineGuid（注册表 Cryptography）+ CPU Identifier（注册表 CentralProcessor），
/// SHA256 取前 16 字节 hex；两者都取不到时回退随机 GUID。零 NuGet，仅 BCL。
/// </summary>
public sealed class DeviceIdService
{
    private readonly string _path;
    private DeviceIdentity? _cache;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public DeviceIdService(string baseDirectory)
        => _path = Path.Combine(baseDirectory, "xindong_device.json");

    /// <summary>加载已有身份，不存在则生成并落盘。</summary>
    public DeviceIdentity LoadOrCreate()
    {
        if (_cache is not null) return _cache;
        _cache = TryLoad() ?? Create();
        return _cache;
    }

    /// <summary>原子写入身份（先 .tmp 再 File.Replace，复刻 StorageService 模式）。</summary>
    public void Save(DeviceIdentity identity)
    {
        _cache = identity;
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(identity, JsonOpts), Encoding.UTF8);
        if (File.Exists(_path)) File.Replace(tmp, _path, null);
        else File.Move(tmp, _path);
    }

    private DeviceIdentity? TryLoad()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            using var fs = File.OpenRead(_path);
            return JsonSerializer.Deserialize<DeviceIdentity>(fs, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private DeviceIdentity Create()
    {
        var (id, src) = GenerateFingerprint();
        var identity = new DeviceIdentity
        {
            DeviceId = id,
            FingerprintSource = src,
            CreatedAt = DateTime.Now.ToString("o")
        };
        Save(identity);
        return identity;
    }

    private static (string id, string source) GenerateFingerprint()
    {
        var parts = new List<string>(2);
        var src = new List<string>(2);

        var machineGuid = ReadRegistry(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid");
        if (!string.IsNullOrEmpty(machineGuid))
        {
            parts.Add(machineGuid);
            src.Add("MachineGuid");
        }

        var cpuId = ReadRegistry(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "Identifier");
        if (!string.IsNullOrEmpty(cpuId))
        {
            parts.Add(cpuId);
            src.Add("Cpu");
        }

        if (parts.Count == 0)
            return (Guid.NewGuid().ToString("N"), "Guid");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        return (Convert.ToHexString(hash, 0, 16).ToLowerInvariant(), string.Join("+", src));
    }

    private static string? ReadRegistry(string path, string value)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(value)?.ToString();
        }
        catch
        {
            return null;
        }
    }
}