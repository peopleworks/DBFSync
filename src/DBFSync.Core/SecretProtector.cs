using System.Security.Cryptography;
using System.Text;

namespace PeopleWorks.DBFSync;

public interface ISecretProtector
{
    string Protect(string value, SecretScope scope);
    string Unprotect(string value);
}

public sealed class DpapiSecretProtector : ISecretProtector
{
    private const string UserPrefix = "dpapi:u:";
    private const string MachinePrefix = "dpapi:m:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PeopleWorks.DBFSync.v1");

    public string Protect(string value, SecretScope scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(L10n.T("DpapiWindowsOnly"));
        byte[] protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            Entropy,
            scope == SecretScope.LocalMachine
                ? DataProtectionScope.LocalMachine
                : DataProtectionScope.CurrentUser);
        return (scope == SecretScope.LocalMachine ? MachinePrefix : UserPrefix) +
               Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(L10n.T("DpapiWindowsOnly"));

        DataProtectionScope scope;
        string payload;
        if (value.StartsWith(UserPrefix, StringComparison.Ordinal))
        {
            scope = DataProtectionScope.CurrentUser;
            payload = value[UserPrefix.Length..];
        }
        else if (value.StartsWith(MachinePrefix, StringComparison.Ordinal))
        {
            scope = DataProtectionScope.LocalMachine;
            payload = value[MachinePrefix.Length..];
        }
        else
        {
            throw new FormatException(L10n.T("DpapiFormatInvalid"));
        }

        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(payload), Entropy, scope));
        }
        catch (CryptographicException ex)
        {
            string hint = scope == DataProtectionScope.CurrentUser
                ? L10n.T("DpapiCurrentUserHint")
                : L10n.T("DpapiMachineHint");
            throw new InvalidOperationException(L10n.T("DpapiDecryptFailed", hint), ex);
        }
    }

}
