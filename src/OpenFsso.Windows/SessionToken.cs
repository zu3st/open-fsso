using System.ComponentModel;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace OpenFsso.Windows;

public static class SessionToken
{
    public static unsafe SafeAccessTokenHandle Open(uint sessionId)
    {
        HANDLE token;
        if (!PInvoke.WTSQueryUserToken(sessionId, &token))
        {
            throw new Win32Exception();
        }

        return new SafeAccessTokenHandle((nint)token.Value);
    }

    public static void RequireUser(SafeAccessTokenHandle token)
    {
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        if (identity.IsSystem || identity.IsAnonymous || identity.User == null || identity.Name.EndsWith('$'))
        {
            throw new InvalidOperationException("An interactive user token is required.");
        }
    }
}
