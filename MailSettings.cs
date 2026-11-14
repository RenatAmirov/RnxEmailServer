using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MimeKit;

namespace RnxEmailServer;

internal sealed class MailSettings
{
    public const int MaxMessageBytes = 10 * 1024 * 1024;
    public const int MaxRecipients = 25;

    public string Domain { get; set; } = "example.test";
    public string HostName { get; set; } = "mail.example.test";
    public string DataDirectory { get; set; } = "data";
    public string WebUrl { get; set; } = "http://127.0.0.1:8080";
    public string SmtpBind { get; set; } = "127.0.0.1";
    public int SmtpPort { get; set; } = 8025;
    public string? SmtpCertificatePath { get; set; }
    public string? SmtpCertificatePassword { get; set; }
    public RelaySettings Relay { get; set; } = new();

    public static MailSettings Read(IConfiguration configuration, string root)
    {
        var settings = configuration.GetSection("Mail").Get<MailSettings>() ?? new();
        settings.Domain = settings.Domain.ToLowerInvariant();
        settings.HostName = settings.HostName.ToLowerInvariant();

        if (!IsDnsName(settings.Domain) || !IsDnsName(settings.HostName))
            throw new InvalidOperationException("Mail:Domain и Mail:HostName должны быть полными DNS-именами (ASCII/Punycode).");

        if (!Uri.TryCreate(settings.WebUrl, UriKind.Absolute, out var web) || web.PathAndQuery != "/" ||
            web.Fragment.Length != 0 || web.UserInfo.Length != 0 ||
            !(web.Scheme == "https" || web.Scheme == "http" && web.IsLoopback))
            throw new InvalidOperationException("Mail:WebUrl должен быть HTTPS или локальным HTTP-адресом.");

        if (!IPAddress.TryParse(settings.SmtpBind, out var bind) || bind.AddressFamily != AddressFamily.InterNetwork ||
            settings.SmtpPort is < 1 or > 65535)
            throw new InvalidOperationException("Укажите IPv4-адрес Mail:SmtpBind и допустимый Mail:SmtpPort.");

        if (settings.Relay.Enabled &&
            (Uri.CheckHostName(settings.Relay.Host) is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
             settings.Relay.Port is < 1 or > 65535 ||
             settings.Relay.CertificateSha256 is { } pin && !Regex.IsMatch(pin, @"\A[0-9a-fA-F]{64}\z") ||
             string.IsNullOrEmpty(settings.Relay.User) != string.IsNullOrEmpty(settings.Relay.Password)))
            throw new InvalidOperationException("Проверьте адрес, порт и учётные данные Mail:Relay.");

        settings.DataDirectory = Path.GetFullPath(settings.DataDirectory, root);
        if (!string.IsNullOrWhiteSpace(settings.SmtpCertificatePath))
            settings.SmtpCertificatePath = Path.GetFullPath(settings.SmtpCertificatePath, root);

        return settings;
    }

    private static bool IsDnsName(string value) => value.Contains('.') &&
        Regex.IsMatch(value, @"\A(?=.{1,253}\z)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+\z");

    public bool IsLocal(string address) => address.EndsWith("@" + Domain, StringComparison.OrdinalIgnoreCase);

    public string? AccountAddress(string? value)
    {
        var address = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(address)) return null;
        if (!address.Contains('@')) address += "@" + Domain;

        var localPart = address.Split('@', 2)[0];
        return address.Length <= 254 && IsLocal(address) && localPart.Length <= 64 &&
               Regex.IsMatch(localPart, @"\A[a-z0-9][a-z0-9_+\-]*(?:\.[a-z0-9][a-z0-9_+\-]*)*\z") &&
               MailboxAddress.TryParse(address, out var mailbox) && mailbox.Address == address
            ? address : null;
    }
}

internal sealed class RelaySettings
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool ImplicitTls { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? CertificateSha256 { get; set; }
    public bool Enabled => !string.IsNullOrWhiteSpace(Host);
}
