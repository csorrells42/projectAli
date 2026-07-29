using Ali.Modules.Internet;

namespace Ali.Framework.Tests;

public sealed class GoogleBillingSettingsGuardTests
{
    [Fact]
    public void StoresOnlySaltedHashAndVerifiesCorrectPassword()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliGoogleBillingGuardTests", Guid.NewGuid().ToString("N"));
        try
        {
            var guard = new GoogleBillingSettingsGuard(root);
            const string password = "warm-and-fuzzy-42";

            guard.SetPassword(password);

            Assert.True(guard.IsConfigured);
            Assert.True(guard.Verify(password));
            Assert.False(guard.Verify("wrong-password"));
            var stored = File.ReadAllText(guard.Path);
            Assert.DoesNotContain(password, stored, StringComparison.Ordinal);
            Assert.Contains("PBKDF2-SHA256", stored, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ChangeRequiresCurrentPasswordAndInvalidatesOldPassword()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliGoogleBillingGuardTests", Guid.NewGuid().ToString("N"));
        try
        {
            var guard = new GoogleBillingSettingsGuard(root);
            guard.SetPassword("first-password");

            Assert.Throws<UnauthorizedAccessException>(() =>
                guard.ChangePassword("not-the-password", "second-password"));

            guard.ChangePassword("first-password", "second-password");
            Assert.False(guard.Verify("first-password"));
            Assert.True(guard.Verify("second-password"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsShortPassword()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliGoogleBillingGuardTests", Guid.NewGuid().ToString("N"));
        var guard = new GoogleBillingSettingsGuard(root);
        Assert.Throws<ArgumentException>(() => guard.SetPassword("short"));
    }
}
