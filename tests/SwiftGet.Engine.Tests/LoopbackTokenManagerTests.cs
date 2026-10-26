using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using SwiftGet.Engine.Server;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class LoopbackTokenManagerTests
{
    [Fact]
    public void TokenFile_IsUserOnlyAce()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "swiftget_token_acl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tokenFile = Path.Combine(tempDir, "auth.token");

        try
        {
            var manager = new LoopbackTokenManager(tokenFile);
            var token = manager.GetOrCreateToken();

            token.Should().NotBeNullOrWhiteSpace();
            File.Exists(tokenFile).Should().BeTrue();

            if (OperatingSystem.IsWindows())
            {
                var fileInfo = new FileInfo(tokenFile);
                var accessControl = fileInfo.GetAccessControl();
                accessControl.AreAccessRulesProtected.Should().BeTrue("inheritance must be blocked so only explicit user rules apply");

                var rules = accessControl.GetAccessRules(true, false, typeof(SecurityIdentifier));
                rules.Count.Should().BeGreaterThan(0);

                var currentUser = WindowsIdentity.GetCurrent().User;
                currentUser.Should().NotBeNull();

                bool foundCurrentUserRule = false;
                foreach (FileSystemAccessRule rule in rules)
                {
                    if (rule.IdentityReference == currentUser && rule.AccessControlType == AccessControlType.Allow)
                    {
                        foundCurrentUserRule = true;
                        rule.FileSystemRights.Should().HaveFlag(FileSystemRights.FullControl);
                    }
                }

                foundCurrentUserRule.Should().BeTrue("the token file must have an allow rule for the current user");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void ValidateToken_FixedTime_ValidatesCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "swiftget_token_val_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tokenFile = Path.Combine(tempDir, "auth.token");

        try
        {
            var manager = new LoopbackTokenManager(tokenFile);
            var token = manager.GetOrCreateToken();

            // Correct token
            manager.ValidateToken(token).Should().BeTrue();

            // Wrong tokens of same length
            var wrongSameLength = new string('a', token.Length);
            manager.ValidateToken(wrongSameLength).Should().BeFalse();

            // Wrong tokens of different lengths (must not throw or leak)
            manager.ValidateToken("short").Should().BeFalse();
            manager.ValidateToken(new string('b', token.Length * 2)).Should().BeFalse();
            manager.ValidateToken("").Should().BeFalse();
            manager.ValidateToken(null).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
