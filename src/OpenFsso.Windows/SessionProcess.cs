using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace OpenFsso.Windows;

public static class SessionProcess
{
    public static unsafe uint Start(uint sessionId, string executable)
    {
        using var user = SessionToken.Open(sessionId);
        SessionToken.RequireUser(user);
        using var limited = LimitedToken(user);
        var token = (HANDLE)(limited ?? user).DangerousGetHandle();
        void* environment;
        if (!PInvoke.CreateEnvironmentBlock(&environment, token, false))
        {
            throw new Win32Exception();
        }

        try
        {
            fixed (char* desktop = @"winsta0\default")
            {
                var startup = new STARTUPINFOW
                {
                    cb = (uint)sizeof(STARTUPINFOW),
                    lpDesktop = new PWSTR(desktop)
                };
                Span<char> commandLine = $"\"{executable}\"\0".ToCharArray();
                if (!PInvoke.CreateProcessAsUser(limited ?? user, executable, ref commandLine, null, null, false,
                    PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT, environment,
                    Path.GetDirectoryName(executable), startup, out var process))
                {
                    throw new Win32Exception();
                }

                PInvoke.CloseHandle(process.hThread);
                PInvoke.CloseHandle(process.hProcess);
                return process.dwProcessId;
            }
        }
        finally
        {
            PInvoke.DestroyEnvironmentBlock(environment);
        }
    }

    private static unsafe SafeAccessTokenHandle? LimitedToken(SafeAccessTokenHandle user)
    {
        var token = (HANDLE)user.DangerousGetHandle();
        TOKEN_ELEVATION_TYPE elevation;
        uint returned;
        if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevationType,
            &elevation, (uint)sizeof(TOKEN_ELEVATION_TYPE), &returned))
        {
            throw new Win32Exception();
        }

        if (elevation != TOKEN_ELEVATION_TYPE.TokenElevationTypeFull)
        {
            return null;
        }

        TOKEN_LINKED_TOKEN linked;
        if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenLinkedToken,
            &linked, (uint)sizeof(TOKEN_LINKED_TOKEN), &returned))
        {
            throw new Win32Exception();
        }

        return new SafeAccessTokenHandle((nint)linked.LinkedToken.Value);
    }
}
