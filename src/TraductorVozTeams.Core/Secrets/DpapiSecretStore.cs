using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace TraductorVozTeams.Core.Secrets;

public interface ISecretStore
{
    string? Get(string name);

    void Set(string name, string value);

    bool Delete(string name);

    bool Exists(string name);
}

/// <summary>
/// Guarda cada secreto cifrado con DPAPI (ámbito del usuario actual de Windows):
/// solo ese usuario en ese equipo puede descifrarlo. Nunca se guarda junto al ejecutable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string directory) : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TraductorVozTeams.secrets.v1");

    public string? Get(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }

        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Directory.CreateDirectory(directory);
        var plain = Encoding.UTF8.GetBytes(value);
        try
        {
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var path = PathFor(name);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public bool Delete(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    private string PathFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
        {
            throw new ArgumentException("Nombre de secreto no válido.", nameof(name));
        }

        return Path.Combine(directory, name + ".bin");
    }
}
