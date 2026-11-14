using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace RnxEmailServer;

internal sealed class RelayWorker(MailSettings settings, MailStore store, ILogger<RelayWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Relay.Enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var item = store.ClaimDelivery();
                if (item is not null)
                {
                    await DeliverAsync(item, stoppingToken);
                    continue;
                }
            }
            catch (SqliteException error)
            {
                logger.LogWarning(error, "Ошибка обращения к очереди исходящих");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task DeliverAsync(QueuedMail item, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var message = MailFormat.Parse(item.Raw);
            using var client = new SmtpClient { LocalDomain = settings.HostName };
            var relay = settings.Relay;
            if (relay.CertificateSha256 is { } hex)
            {
                var expected = Convert.FromHexString(hex);
                client.ServerCertificateValidationCallback = (_, certificate, _, errors) =>
                    MatchesRelayCertificate(certificate, errors, expected);
            }
            await client.ConnectAsync(relay.Host!, relay.Port,
                relay.ImplicitTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, timeout.Token);
            if (!string.IsNullOrEmpty(relay.User))
                await client.AuthenticateAsync(relay.User, relay.Password!, timeout.Token);

            await client.SendAsync(message, MailboxAddress.Parse(item.Sender), [MailboxAddress.Parse(item.Recipient)], timeout.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not SqliteException)
        {
            var permanent = error is SmtpCommandException command &&
                            (command.ErrorCode is SmtpErrorCode.SenderNotAccepted or SmtpErrorCode.RecipientNotAccepted or
                                SmtpErrorCode.MessageNotAccepted) &&
                            (int)command.StatusCode is >= 500 and < 600;
            var attempts = item.Attempts + 1;
            var state = permanent || attempts >= 8 ? "failed" : "pending";
            var wait = TimeSpan.FromMinutes(Math.Min(120, Math.Pow(2, item.Attempts)));
            var description = error is SmtpCommandException
                ? MailFormat.Limit(error.Message, 300)
                : error is OperationCanceledException ? "Превышено время ожидания SMTP-релея." :
                    "Ошибка соединения, TLS или входа на SMTP-релей.";
            store.CompleteDelivery(item, state, DateTimeOffset.UtcNow + wait, description);
            logger.LogWarning("SMTP-релей, запись {Id}, попытка {Attempt}, статус {State}, ошибка {Error}",
                item.Id, attempts, state, error.GetType().Name);
            return;
        }

        store.CompleteDelivery(item, "sent", DateTimeOffset.UtcNow, null);
        logger.LogInformation("SMTP-релей принял письмо из очереди {Id}", item.Id);
    }

    private static bool MatchesRelayCertificate(X509Certificate? certificate, SslPolicyErrors errors, byte[] expected)
    {
        if (certificate is not X509Certificate2 leaf || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            return false;

        var now = DateTime.UtcNow;
        return leaf.NotBefore.ToUniversalTime() <= now && now <= leaf.NotAfter.ToUniversalTime() &&
               CryptographicOperations.FixedTimeEquals(leaf.GetCertHash(HashAlgorithmName.SHA256), expected);
    }
}
