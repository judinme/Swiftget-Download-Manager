using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SwiftGet.Engine.Server;

public sealed class LoopbackTokenManager
{
    private readonly string _tokenFilePath;
    private string? _cachedToken;

    public LoopbackTokenManager(string? customTokenPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customTokenPath))
        {
            _tokenFilePath = customTokenPath;
        }
        else
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _tokenFilePath = Path.Combine(localAppData, "SwiftGet", "auth.token");
        }
    }

    public string GetOrCreateToken()
    {
        if (!string.IsNullOrEmpty(_cachedToken))
        {
            return _cachedToken;
        }

        if (File.Exists(_tokenFilePath))
        {
            try
            {
                var existing = File.ReadAllText(_tokenFilePath).Trim();
                if (!string.IsNullOrEmpty(existing) && existing.Length >= 32)
                {
                    _cachedToken = existing;
                    return _cachedToken;
                }
            }
            catch
            {
                // Fall back to generating fresh token
            }
        }

        var newToken = GenerateSecureToken();
        var dir = Path.GetDirectoryName(_tokenFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_tokenFilePath, newToken);
        RestrictTokenFileAccess(_tokenFilePath);
        _cachedToken = newToken;
        return _cachedToken;
    }

    private static void RestrictTokenFileAccess(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var fileSecurity = fileInfo.GetAccessControl();
                var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                if (currentUser != null)
                {
                    fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    var rules = fileSecurity.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
                    foreach (System.Security.AccessControl.FileSystemAccessRule rule in rules)
                    {
                        fileSecurity.RemoveAccessRule(rule);
                    }
                    fileSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        currentUser,
                        System.Security.AccessControl.FileSystemRights.FullControl,
                        System.Security.AccessControl.AccessControlType.Allow));
                    fileInfo.SetAccessControl(fileSecurity);
                }
            }
            catch
            {
            }
        }
    }

    public bool ValidateToken(string? providedToken)
    {
        if (string.IsNullOrWhiteSpace(providedToken))
        {
            return false;
        }

        var currentToken = GetOrCreateToken();
        var currentHash = SHA256.HashData(Encoding.UTF8.GetBytes(currentToken));
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedToken.Trim()));

        return CryptographicOperations.FixedTimeEquals(currentHash, providedHash);
    }

    private static string GenerateSecureToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
