namespace OpenFsso.Core;

public static class SyslogFormat
{
    public static string Render(string template, MappingChange change) => template.Replace("{ip}", change.Ip, StringComparison.Ordinal).Replace("{user}", change.User.Name, StringComparison.Ordinal);
}
