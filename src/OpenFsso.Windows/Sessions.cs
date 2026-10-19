using System.ComponentModel;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.RemoteDesktop;

namespace OpenFsso.Windows;

// Attached: the session is connected to the console or an RDP client, locked or not.
public sealed record SessionIdentity(uint SessionId, string Sid, string Name, string LogonId, long LogonTime, bool Attached, ushort Protocol);
public static class Sessions
{
    public static bool Eligible(ushort protocol, bool allowRdp) =>
        protocol == PInvoke.WTS_PROTOCOL_TYPE_CONSOLE || allowRdp && protocol == PInvoke.WTS_PROTOCOL_TYPE_RDP;

    public static unsafe ushort Protocol(uint id)
    {
        PWSTR data;
        uint length;
        if (!PInvoke.WTSQuerySessionInformation(default, id, WTS_INFO_CLASS.WTSClientProtocolType, &data, &length))
        {
            throw new Win32Exception();
        }

        try
        {
            return *(ushort*)data.Value;
        }
        finally
        {
            PInvoke.WTSFreeMemory(data.Value);
        }
    }

    public static unsafe SessionIdentity Read(uint id, SafeAccessTokenHandle token)
    {
        SessionToken.RequireUser(token);
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        TOKEN_STATISTICS statistics;
        uint returned;
        if (!PInvoke.GetTokenInformation((HANDLE)token.DangerousGetHandle(), TOKEN_INFORMATION_CLASS.TokenStatistics, &statistics, (uint)sizeof(TOKEN_STATISTICS), &returned))
        {
            throw new Win32Exception();
        }

        string luid = $"{statistics.AuthenticationId.HighPart:X8}{statistics.AuthenticationId.LowPart:X8}";
        ushort protocol = Protocol(id);
        PWSTR data;
        uint length;
        if (!PInvoke.WTSQuerySessionInformation(default, id, WTS_INFO_CLASS.WTSSessionInfoEx, &data, &length))
        {
            throw new Win32Exception();
        }

        try
        {
            var info = *(WTSINFOEXW*)data.Value;
            if (info.Level != 1)
            {
                throw new InvalidOperationException("Unsupported WTS session information.");
            }

            var detail = info.Data.WTSInfoExLevel1;
            return new(id, identity.User!.Value, identity.Name, luid, detail.LogonTime, detail.SessionState == WTS_CONNECTSTATE_CLASS.WTSActive, protocol);
        }
        finally
        {
            PInvoke.WTSFreeMemory(data.Value);
        }
    }

    public static unsafe IReadOnlyList<SessionIdentity> Eligible(bool allowRdp)
    {
        var result = new List<SessionIdentity>();
        WTS_SESSION_INFOW* sessions;
        uint count;
        if (!PInvoke.WTSEnumerateSessions(default, Reserved: 0, Version: 1, &sessions, &count))
        {
            throw new Win32Exception();
        }

        try
        {
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    using var token = SessionToken.Open(sessions[i].SessionId);
                    var identity = Read(sessions[i].SessionId, token);
                    if (Eligible(identity.Protocol, allowRdp))
                    {
                        result.Add(identity);
                    }
                }
                catch (Win32Exception)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        finally
        {
            PInvoke.WTSFreeMemory(sessions);
        }

        return result;
    }
}
