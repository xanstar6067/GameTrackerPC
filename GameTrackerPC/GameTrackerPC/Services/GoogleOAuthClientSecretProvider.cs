using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace GameTrackerPC.Services;

public static class GoogleOAuthClientSecretProvider
{
    private const string ResourceNameSuffix = "Resources.GoogleOAuthClientSecret.dat";
    private static readonly byte[] AdditionalData = Encoding.UTF8.GetBytes("GameVault.GoogleOAuthClientSecret.v1");

    private static readonly byte[] EncryptionKeyMask =
    [
        0x4E, 0x95, 0x62, 0xB4, 0xE9, 0xF7, 0xC5, 0x82, 0x24, 0x0D, 0xAB, 0x3F, 0xEF, 0x2F, 0x5B, 0x0A,
        0x80, 0xBC, 0xEB, 0x40, 0xAC, 0xE6, 0x32, 0xFB, 0x7D, 0xC6, 0x03, 0x49, 0x7A, 0x2B, 0x7F, 0x9A
    ];

    private static readonly byte[] EncryptionKeyShare =
    [
        0x61, 0x96, 0x0C, 0xA0, 0xBB, 0x0A, 0x6F, 0x79, 0x7D, 0x3A, 0x33, 0x93, 0xA5, 0x82, 0x45, 0x62,
        0x00, 0x1C, 0xDF, 0xA9, 0x5E, 0xE5, 0x22, 0x73, 0x2A, 0x84, 0x0A, 0x40, 0xB3, 0x05, 0xF4, 0x5A
    ];

    private static readonly byte[] HmacKeyMask =
    [
        0x89, 0x67, 0xED, 0xBC, 0x73, 0x51, 0x53, 0x14, 0x2B, 0xDD, 0xE8, 0xF5, 0x5B, 0x21, 0x53, 0x47,
        0x50, 0xDA, 0x0A, 0x5B, 0x0F, 0xAB, 0xC4, 0x44, 0xB8, 0xBC, 0xFA, 0x5E, 0xC3, 0xE4, 0xB5, 0x8F
    ];

    private static readonly byte[] HmacKeyShare =
    [
        0x5D, 0x25, 0xD4, 0x7F, 0xE8, 0xF1, 0x6A, 0xF4, 0x6F, 0xDF, 0x0E, 0x30, 0xB9, 0x64, 0xDD, 0xE6,
        0xBA, 0xF9, 0x96, 0x98, 0x3B, 0x49, 0xB1, 0x41, 0x2A, 0xCD, 0x95, 0xDC, 0x58, 0x42, 0x88, 0x58
    ];

    public static MemoryStream OpenRead()
    {
        var encrypted = ReadEncryptedPayload();
        var plain = DecryptPayload(encrypted);
        return new MemoryStream(plain, writable: false);
    }

    private static byte[] ReadEncryptedPayload()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(ResourceNameSuffix, StringComparison.Ordinal));

        if (resourceName is null)
        {
            throw new FileNotFoundException("Encrypted Google OAuth client secret resource was not found.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("Encrypted Google OAuth client secret resource could not be opened.");
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return Convert.FromBase64String(reader.ReadToEnd().Trim());
    }

    private static byte[] DecryptPayload(byte[] payload)
    {
        if (payload.Length <= 48)
        {
            throw new CryptographicException("Encrypted Google OAuth client secret payload is invalid.");
        }

        var iv = payload[..16];
        var expectedTag = payload[16..48];
        var cipherText = payload[48..];
        var hmacInput = AdditionalData.Concat(iv).Concat(cipherText).ToArray();

        using var hmac = new HMACSHA256(CombineKey(HmacKeyMask, HmacKeyShare));
        var actualTag = hmac.ComputeHash(hmacInput);
        if (!CryptographicOperations.FixedTimeEquals(actualTag, expectedTag))
        {
            throw new CryptographicException("Encrypted Google OAuth client secret payload failed integrity validation.");
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = CombineKey(EncryptionKeyMask, EncryptionKeyShare);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
    }

    private static byte[] CombineKey(byte[] mask, byte[] share)
    {
        var key = new byte[mask.Length];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(mask[i] ^ share[i]);
        }

        return key;
    }
}
