using System.IO;
using ProtonProfiles.App.Services;
using ProtonProfiles.Core.Storage;

internal static class WindowsCredentialSmoke
{
    public static void Run(string root)
    {
        var store = new WindowsCredentialStore();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        try
        {
            var old = store.Write(a, new("fixture-user", "synthetic-old-secret"));
            var current = store.Write(a, new("fixture-user", "synthetic-current-secret"));
            var other = store.Write(b, new("fixture-other", "synthetic-other-secret"));
            if (old == current || !store.Exists(old) || store.Read(current)?.Password != "synthetic-current-secret"
                || !store.ListManagedProfileIds().Contains(a)) throw new InvalidOperationException("Windows Credential Manager roundtrip failed.");
            var paths = new ManagedPaths(Path.Combine(root, "uninstall-credential-fixture")); paths.EnsureBaseDirectories();
            Directory.CreateDirectory(paths.UserDataFolder(a));
            File.WriteAllText(Path.Combine(paths.UserDataFolder(a), "session"), "synthetic");
            if (!ManagedDataRemoval.Remove(paths, store, [a]).IsComplete || Directory.Exists(paths.Root)
                || store.Exists(old) || store.Exists(current) || store.Read(other)?.Password != "synthetic-other-secret")
                throw new InvalidOperationException("Managed uninstall cleanup lost isolation or retained profile credentials.");
            Console.WriteLine("PASS: real Windows Credential Manager: write/read/fresh secret references/enumeration; managed removal deletes A data and every A secret, B secret retained.");
        }
        finally { store.DeleteAllForProfile(a); store.DeleteAllForProfile(b); }
    }
}
