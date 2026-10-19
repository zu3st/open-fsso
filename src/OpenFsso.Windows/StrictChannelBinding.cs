using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Networking.HttpServer;

namespace OpenFsso.Windows;

public static class StrictChannelBinding
{
    // Called after HTTP.sys creates its queue, before the managed pipeline is enabled.
    // Query the native policy back; the framework CBT switch only enables Medium.
    public static unsafe void Enable(string queueName, string prefix)
    {
        Check(PInvoke.HttpCreateRequestQueue(new HTTPAPI_VERSION { HttpApiMajorVersion = 2 }, queueName, null, 1, out var handle));
        using (handle)
        {
            var queue = (HANDLE)handle.DangerousGetHandle();
            ulong group;
            fixed (char* url = prefix)
            {
                Check(PInvoke.HttpFindUrlGroupId(url, queue, &group));
            }

            // Configure native Kerberos directly so the framework does not add
            // its default Windows challenges to the Bearer-only health route.
            var authentication = new HTTP_SERVER_AUTHENTICATION_INFO
            {
                Flags = new()
                {
                    Present = true
                },
                AuthSchemes = PInvoke.HTTP_AUTH_ENABLE_KERBEROS
            };
            Check(PInvoke.HttpSetUrlGroupProperty(group, HTTP_SERVER_PROPERTY.HttpServerAuthenticationProperty, &authentication, (uint)sizeof(HTTP_SERVER_AUTHENTICATION_INFO)));
            HTTP_SERVER_AUTHENTICATION_INFO actualAuthentication;
            uint authenticationSize;
            Check(PInvoke.HttpQueryUrlGroupProperty(group, HTTP_SERVER_PROPERTY.HttpServerAuthenticationProperty, &actualAuthentication, (uint)sizeof(HTTP_SERVER_AUTHENTICATION_INFO), &authenticationSize));
            if (!actualAuthentication.Flags.Present || actualAuthentication.AuthSchemes != authentication.AuthSchemes || actualAuthentication.ExFlags != 0 || actualAuthentication.ReceiveMutualAuth != 0 || actualAuthentication.ReceiveContextHandle != 0)
            {
                throw new InvalidOperationException("Native Kerberos-only HTTP.sys authentication was not applied.");
            }

            var info = new HTTP_CHANNEL_BIND_INFO
            {
                Flags = 0, // No proxy, cohosting or service-name-check bypass flags.
                Hardening = HTTP_AUTHENTICATION_HARDENING_LEVELS.HttpAuthenticationHardeningStrict
            };
            Check(PInvoke.HttpSetUrlGroupProperty(group, HTTP_SERVER_PROPERTY.HttpServerChannelBindProperty, &info, (uint)sizeof(HTTP_CHANNEL_BIND_INFO)));
            HTTP_CHANNEL_BIND_INFO actual;
            uint size;
            Check(PInvoke.HttpQueryUrlGroupProperty(group, HTTP_SERVER_PROPERTY.HttpServerChannelBindProperty, &actual, (uint)sizeof(HTTP_CHANNEL_BIND_INFO), &size));
            if (actual.Hardening != info.Hardening || actual.Flags != 0 || actual.NumberOfServiceNames != 0)
            {
                throw new InvalidOperationException("Strict HTTP.sys channel binding was not applied.");
            }
        }
    }

    private static void Check(uint result)
    {
        if (result != 0)
        {
            throw new Win32Exception((int)result, "HTTP.sys channel-binding configuration failed.");
        }
    }
}
