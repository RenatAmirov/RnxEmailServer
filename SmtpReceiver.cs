using System.Buffers;
using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Data.Sqlite;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace RnxEmailServer;

internal sealed class SmtpReceiver(MailSettings settings, MailStore store, ILogger<SmtpReceiver> logger)
    : BackgroundService
{
    private static readonly SmtpResponse UnsupportedCommand = new(SmtpReplyCode.CommandNotImplemented, "Command not supported");
    private SmtpServer.SmtpServer? _server;
    private Task? _serverTask;
    private X509Certificate2? _certificate;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.SmtpCertificatePath))
            _certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.SmtpCertificatePath,
                settings.SmtpCertificatePassword,
                OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
        if (_certificate is { HasPrivateKey: false })
            throw new InvalidOperationException("SMTP-сертификат должен содержать закрытый ключ.");

        var address = new IPEndPoint(IPAddress.Parse(settings.SmtpBind), settings.SmtpPort);
        var options = new SmtpServerOptionsBuilder()
            .ServerName(settings.HostName)
            .CustomSmtpGreeting(_ => $"220 {settings.HostName} ESMTP ready")
            .MaxMessageSize(MailSettings.MaxMessageBytes, MaxMessageSizeHandling.Strict)
            .CommandWaitTimeout(TimeSpan.FromSeconds(60))
            .Endpoint(endpoint =>
            {
                endpoint.Endpoint(address).SessionTimeout(TimeSpan.FromMinutes(5))
                    .SupportedSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13);
                if (_certificate is not null) endpoint.Certificate(_certificate);
            }).Build();
        var services = new SmtpServer.ComponentModel.ServiceProvider();
        var mailbox = new LocalMailbox(settings, store, logger);
        services.Add((IMailboxFilter)mailbox);
        services.Add((IMessageStore)mailbox);
        services.Add(new IncomingCommands());
        _server = new SmtpServer.SmtpServer(options, services);
        _server.SessionFaulted += (_, session) => logger.LogDebug(session.Exception, "Сессия SMTP завершилась с ошибкой");
        _server.SessionCreated += (_, session) => session.Context.CommandExecuting += (_, command) =>
        {
            if (command.Command is AuthCommand or ProxyCommand) throw new SmtpResponseException(UnsupportedCommand);
        };
        _serverTask = _server.StartAsync(CancellationToken.None);
        if (_serverTask.IsCompleted) await _serverTask;
        logger.LogInformation("SMTP принимает письма на {Endpoint}", address);
        await base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _serverTask!;

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _server?.Shutdown();
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _server?.Shutdown();
        _certificate?.Dispose();
        base.Dispose();
    }

    private sealed class IncomingCommands : SmtpCommandFactory
    {
        public override SmtpCommand CreateEhlo(string domainOrAddress) => new IncomingEhlo(domainOrAddress);
    }

    private sealed class IncomingEhlo(string domainOrAddress) : EhloCommand(domainOrAddress)
    {
        protected override IEnumerable<string> GetExtensions(ISessionContext context) =>
            base.GetExtensions(context).Where(extension => !extension.StartsWith("AUTH ", StringComparison.Ordinal));
    }

    private sealed class LocalMailbox(MailSettings settings, MailStore store, ILogger<SmtpReceiver> logger)
        : IMailboxFilter, IMessageStore
    {
        private const string RecipientCount = "rnx.rcpt-count";
        private static readonly SmtpResponse StorageError = new(SmtpReplyCode.Aborted, "4.3.0 Temporary storage failure");
        private static readonly SmtpResponse TooManyRecipients = new(SmtpReplyCode.InsufficientStorage, "4.5.3 Too many recipients");

        public Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
        {
            context.Properties[RecipientCount] = 0;
            return Task.FromResult(true);
        }

        public Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox from, CancellationToken cancellationToken)
        {
            var count = (int)context.Properties[RecipientCount];
            if (count >= MailSettings.MaxRecipients) throw new SmtpResponseException(TooManyRecipients);

            var address = settings.AccountAddress(to.AsAddress());
            try
            {
                if (address is null || !store.AccountExists(address)) return Task.FromResult(false);
            }
            catch (SqliteException error)
            {
                logger.LogWarning(error, "Не удалось проверить получателя SMTP");
                throw new SmtpResponseException(StorageError);
            }

            context.Properties[RecipientCount] = count + 1;
            return Task.FromResult(true);
        }

        public Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction,
            ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length > MailSettings.MaxMessageBytes) return Task.FromResult(SmtpResponse.SizeLimitExceeded);

            var receivedAt = DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture);
            var header = Encoding.ASCII.GetBytes($"Received: by {settings.HostName} with ESMTP; {receivedAt}\r\n");
            var raw = new byte[header.Length + (int)buffer.Length + 2];
            header.CopyTo(raw, 0);
            buffer.CopyTo(raw.AsSpan(header.Length));
            raw[^2] = 13;
            raw[^1] = 10;

            var sender = transaction.From.AsAddress() ?? "";
            var recipients = transaction.To.Select(mailbox => settings.AccountAddress(mailbox.AsAddress())!)
                .Distinct(StringComparer.Ordinal).ToArray();
            try
            {
                var (subject, visibleRecipients) = MailFormat.ReadHeaders(raw);
                var id = store.SaveMessage(sender, visibleRecipients, recipients, [], subject, raw);
                logger.LogInformation("Принято письмо {Id}, получателей: {Count}", id, recipients.Length);
                return Task.FromResult(new SmtpResponse(SmtpReplyCode.Ok, $"2.0.0 Stored as {id}"));
            }
            catch (SqliteException error)
            {
                logger.LogError(error, "Не удалось сохранить входящее письмо");
                return Task.FromResult(StorageError);
            }
        }
    }
}
