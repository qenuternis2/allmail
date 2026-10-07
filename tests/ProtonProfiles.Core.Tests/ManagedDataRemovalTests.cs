using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.Core.Tests;

public class ManagedDataRemovalTests
{
    [Fact]
    public void Multiple_instances_can_share_lease_but_removal_requires_exclusive_access()
    {
        using var env = new TestEnv();
        using var a = ManagedDataRemoval.AcquireLease(env.Paths, exclusive: false);
        using var b = ManagedDataRemoval.AcquireLease(env.Paths, exclusive: false);
        Assert.Throws<IOException>(() => ManagedDataRemoval.Remove(env.Paths, env.Credentials, []));
        Assert.True(File.Exists(env.Paths.DatabasePath));
    }

    [Fact]
    public void Active_profile_blocks_removal_without_deleting_data_or_secrets()
    {
        using var env = new TestEnv();
        var profile = env.AddProfile();
        var secret = env.Credentials.Write(profile.Id, new("user", "secret"));
        Assert.True(ProfileLock.TryAcquire(env.Paths, profile.Id, out var held));
        using (held)
        {
            Assert.Equal(CleanupOutcome.Pending, ManagedDataRemoval.Remove(env.Paths, env.Credentials, [profile.Id]).Outcome);
            Assert.True(File.Exists(env.Paths.DatabasePath));
            Assert.True(env.Credentials.Exists(secret));
        }
    }

    [Fact]
    public void Confirmed_removal_deletes_managed_data_and_secrets_but_preserves_external_attachment()
    {
        using var env = new TestEnv();
        var profile = env.AddProfile();
        var secret = env.Credentials.Write(profile.Id, new("user", "secret"));
        Directory.CreateDirectory(env.Paths.UserDataFolder(profile.Id));
        File.WriteAllText(Path.Combine(env.Paths.UserDataFolder(profile.Id), "cookie"), "synthetic");
        var attachment = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".attachment");
        try
        {
            File.WriteAllText(attachment, "preserved");
            env.Repository.Update(profile with { DownloadDirectory = Path.GetDirectoryName(attachment) });
            Assert.True(ManagedDataRemoval.Remove(env.Paths, env.Credentials, [profile.Id]).IsComplete);
            Assert.False(Directory.Exists(env.Paths.Root));
            Assert.False(env.Credentials.Exists(secret));
            Assert.Equal("preserved", File.ReadAllText(attachment));
        }
        finally { File.Delete(attachment); }
    }

    [Fact]
    public void A_link_to_external_data_is_removed_without_following_it()
    {
        using var env = new TestEnv();
        var external = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(external);
        try
        {
            File.WriteAllText(Path.Combine(external, "keep"), "preserved");
            var link = Path.Combine(env.Paths.Root, "external-link");
            if (OperatingSystem.IsWindows())
            {
                using var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{external}\"") { UseShellExecute = false, CreateNoWindow = true })!;
                junction.WaitForExit(); Assert.Equal(0, junction.ExitCode);
            }
            else Directory.CreateSymbolicLink(link, external);
            Assert.True(ManagedDataRemoval.Remove(env.Paths, env.Credentials, []).IsComplete);
            Assert.Equal("preserved", File.ReadAllText(Path.Combine(external, "keep")));
        }
        finally { Directory.Delete(external, recursive: true); }
    }
}
