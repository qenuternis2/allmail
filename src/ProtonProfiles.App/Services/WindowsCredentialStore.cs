using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ProtonProfiles.Core.Credentials;

namespace ProtonProfiles.App.Services;

/// <summary>
/// Proxy secrets in Windows Credential Manager (generic credentials, CurrentUser, local-machine persistence).
/// Nothing is written to SQLite, URIs, process arguments or logs (spec §6.1).
/// </summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public bool Exists(string credentialRef)
    {
        if (!CredRead(credentialRef, CRED_TYPE_GENERIC, 0, out var ptr)) return false;
        CredFree(ptr);
        return true;
    }

    public ProxyCredential? Read(string credentialRef)
    {
        if (!CredRead(credentialRef, CRED_TYPE_GENERIC, 0, out var ptr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND) return null;
            throw new Win32Exception(err);
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            var password = cred.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2);
            return new ProxyCredential(cred.UserName, password);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public string Write(Guid profileId, ProxyCredential credential)
    {
        var target = CredentialRefs.Create(profileId);
        var blob = Encoding.Unicode.GetBytes(credential.Password);
        var handle = Marshal.AllocCoTaskMem(Math.Max(blob.Length, 1));
        try
        {
            Marshal.Copy(blob, 0, handle, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = credential.UserName,
                CredentialBlob = handle,
                CredentialBlobSize = blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                Comment = "All Mails: учётные данные прокси",
            };
            if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return target;
        }
        finally
        {
            Array.Clear(blob);
            Marshal.FreeCoTaskMem(handle);
        }
    }

    public void Delete(string credentialRef)
    {
        if (!CredDelete(credentialRef, CRED_TYPE_GENERIC, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ERROR_NOT_FOUND) throw new Win32Exception(err);
        }
    }

    public void DeleteAllForProfile(Guid profileId)
    {
        if (!CredEnumerate(CredentialRefs.Prefix(profileId) + "*", 0, out var count, out var list)) return;
        var targets = new List<string>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                targets.Add(Marshal.PtrToStructure<CREDENTIAL>(item).TargetName);
            }
        }
        finally
        {
            CredFree(list);
        }
        foreach (var t in targets) Delete(t);
    }
}
