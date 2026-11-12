using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenFsso.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Networking.ActiveDirectory;
using Windows.Win32.Security;
using Windows.Win32.Security.Authentication.Identity;

namespace OpenFsso.Windows;

// Asks the domain whether an account is still valid with a Kerberos S4U logon,
// as the receiver's computer account. The domain controller answers for
// disabled, expired and deleted accounts without any credentials of the user.
// A locked-out account keeps its mapping, as it keeps its Windows session:
// anyone can lock an account out with wrong passwords.
// The token is closed at once and never used.
// The name goes as name@dns.domain: with a separate realm, LSA answers later
// requests from its ticket cache instead of asking the domain controller.
public sealed unsafe class LsaAccountStatus : IDisposable
{
    private readonly Dictionary<string, string> dnsDomains = new(StringComparer.OrdinalIgnoreCase);
    private const uint StatusAccountRestriction = 0xC000006E, StatusLogonFailure = 0xC000006D, StatusNoSuchUser = 0xC0000064;
    private const uint StatusAccountDisabled = 0xC0000072, StatusAccountExpired = 0xC0000193;
    // A check that has waited this long means the domain is not answering.
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(1);
    // Start of the oldest check in flight, 0 when none.
    private long oldest;
    private HANDLE lsa;
    private readonly uint package;

    public LsaAccountStatus()
    {
        HANDLE handle;
        Check(PInvoke.LsaConnectUntrusted(&handle));
        lsa = handle;
        fixed (byte* name = "Kerberos\0"u8)
        {
            var kerberos = new LSA_STRING { Length = 8, MaximumLength = 9, Buffer = new PSTR(name) };
            uint id;
            Check(PInvoke.LsaLookupAuthenticationPackage(lsa, &kerberos, &id));
            package = id;
        }
    }

    public AccountCheck Check(AuthenticatedUser user) => Query(user.Name);

    // account is DOMAIN\name, as HTTP.sys authenticated it.
    public AccountCheck Query(string account)
    {
        long running = Interlocked.CompareExchange(ref oldest, Stopwatch.GetTimestamp(), 0);
        // While one check hangs on an unreachable domain, later ones do not queue behind it.
        if (running != 0 && Stopwatch.GetElapsedTime(running) > Slow)
        {
            return new(AccountState.Unknown, "domain-not-answering");
        }

        try
        {
            return Ask(account);
        }
        finally
        {
            if (running == 0)
            {
                Volatile.Write(ref oldest, 0);
            }
        }
    }

    private AccountCheck Ask(string account)
    {
        int slash = account.IndexOf('\\');
        if (slash <= 0 || slash == account.Length - 1)
        {
            return new(AccountState.Unknown, "name-without-domain");
        }

        string? dnsDomain = DnsDomain(account[..slash]);
        if (dnsDomain == null)
        {
            return new(AccountState.Unknown, "domain-not-found");
        }

        byte[] name = Encoding.Unicode.GetBytes(account[(slash + 1)..] + "@" + dnsDomain);
        // LSA requires the strings inside the submitted buffer.
        int header = sizeof(KERB_S4U_LOGON);
        byte* buffer = (byte*)NativeMemory.AllocZeroed((nuint)(header + name.Length));
        try
        {
            name.CopyTo(new Span<byte>(buffer + header, name.Length));
            *(KERB_S4U_LOGON*)buffer = new KERB_S4U_LOGON
            {
                MessageType = KERB_LOGON_SUBMIT_TYPE.KerbS4ULogon,
                Flags = 0,
                ClientUpn = new LSA_UNICODE_STRING { Length = (ushort)name.Length, MaximumLength = (ushort)name.Length, Buffer = new PWSTR((char*)(buffer + header)) },
                ClientRealm = default
            };
            var source = new TOKEN_SOURCE();
            "OpenFsso"u8.CopyTo(new Span<byte>(&source.SourceName, 8));
            NTSTATUS status;
            int sub;
            void* profile;
            uint profileLength;
            LUID logonId;
            HANDLE token;
            QUOTA_LIMITS quotas;
            fixed (byte* origin = "OpenFsso\0"u8)
            {
                var originName = new LSA_STRING { Length = 8, MaximumLength = 9, Buffer = new PSTR(origin) };
                status = PInvoke.LsaLogonUser(lsa, &originName, SECURITY_LOGON_TYPE.Network, package, buffer, (uint)(header + name.Length), null, &source, &profile, &profileLength, &logonId, &token, &quotas, &sub);
            }

            if (profile != null)
            {
                PInvoke.LsaFreeReturnBuffer(profile);
            }

            if (status == 0 && token != default)
            {
                PInvoke.CloseHandle(token);
            }

            return new(Classify((uint)status.Value, (uint)sub), $"0x{(uint)status.Value:X8}/0x{(uint)sub:X8}");
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    // The DNS name of a NetBIOS domain, from a domain controller, cached.
    private string? DnsDomain(string netbios)
    {
        lock (dnsDomains)
        {
            if (dnsDomains.TryGetValue(netbios, out string? known))
            {
                return known;
            }
        }

        DOMAIN_CONTROLLER_INFOW* info;
        if (PInvoke.DsGetDcName(null, netbios, null, null, PInvoke.DS_RETURN_DNS_NAME, out info) != 0)
        {
            return null;
        }

        try
        {
            string? dns = info->DomainName.ToString();
            if (string.IsNullOrEmpty(dns))
            {
                return null;
            }

            lock (dnsDomains)
            {
                dnsDomains[netbios] = dns;
            }

            return dns;
        }
        finally
        {
            PInvoke.NetApiBufferFree(info);
        }
    }

    private static AccountState Classify(uint status, uint sub) => status switch
    {
        0 => AccountState.Valid,
        StatusLogonFailure or StatusNoSuchUser => AccountState.Revoked,
        StatusAccountRestriction when sub is StatusAccountDisabled or StatusAccountExpired => AccountState.Revoked,
        _ => AccountState.Unknown
    };

    private static void Check(NTSTATUS status)
    {
        if (status != 0)
        {
            throw new System.ComponentModel.Win32Exception((int)PInvoke.LsaNtStatusToWinError(status), "LSA connection for account status checks failed.");
        }
    }

    public void Dispose()
    {
        if (lsa != default)
        {
            PInvoke.LsaDeregisterLogonProcess(lsa);
            lsa = default;
        }
    }
}
