using System.Security.Cryptography;
using System.Text;
using Google.Apis.Util.Store;
using Newtonsoft.Json;

namespace GameTrackerPC.Services;

public sealed class ProtectedFileDataStore : IDataStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GameVault.GoogleDriveToken.v1");
    private readonly string _folderPath;

    public ProtectedFileDataStore(string folderPath)
    {
        _folderPath = folderPath;
        Directory.CreateDirectory(_folderPath);
        DeleteLegacyPlainTextTokenFiles();
    }

    public Task StoreAsync<T>(string key, T value)
    {
        var json = JsonConvert.SerializeObject(value);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GetFilePath<T>(key), protectedBytes);
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        var filePath = GetFilePath<T>(key);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return Task.CompletedTask;
    }

    public Task<T?> GetAsync<T>(string key)
    {
        var filePath = GetFilePath<T>(key);
        if (!File.Exists(filePath))
        {
            return Task.FromResult<T?>(default);
        }

        var protectedBytes = File.ReadAllBytes(filePath);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        var json = Encoding.UTF8.GetString(plainBytes);
        return Task.FromResult(JsonConvert.DeserializeObject<T>(json));
    }

    public Task ClearAsync()
    {
        if (Directory.Exists(_folderPath))
        {
            Directory.Delete(_folderPath, recursive: true);
        }

        Directory.CreateDirectory(_folderPath);
        return Task.CompletedTask;
    }

    private string GetFilePath<T>(string key)
    {
        var material = $"{typeof(T).FullName}:{key}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        var fileName = Convert.ToHexString(hash).ToLowerInvariant() + ".bin";
        return Path.Combine(_folderPath, fileName);
    }

    private void DeleteLegacyPlainTextTokenFiles()
    {
        foreach (var filePath in Directory.EnumerateFiles(_folderPath, "*", SearchOption.AllDirectories))
        {
            if (!filePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(filePath);
            }
        }
    }
}
