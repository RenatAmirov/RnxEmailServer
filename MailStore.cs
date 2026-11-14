using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace RnxEmailServer;

internal sealed record MailRow(long Id, string Sender, string Recipients, string Subject, long ReceivedAt, bool IsRead, string? Delivery);
internal sealed record StoredMail(MailRow Row, byte[] Raw);
internal sealed record QueuedMail(long Id, string Sender, string Recipient, byte[] Raw, int Attempts, string Lease);
internal sealed record DeliveryState(string Recipient, string State, string? Error);

internal sealed class MailStore
{
    private const int PasswordIterations = 600_000;
    public const int PageSize = 30;
    private readonly string _connectionString;

    public MailStore(MailSettings settings)
    {
        Directory.CreateDirectory(settings.DataDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(settings.DataDirectory, "mail.db"),
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();
    }

    public void Initialize()
    {
        using var connection = Open();
        using var command = Command(connection, "PRAGMA journal_mode=WAL");
        command.ExecuteScalar();
        using var transaction = connection.BeginTransaction();
        using var schema = Command(connection, """
            CREATE TABLE IF NOT EXISTS accounts (
                address TEXT PRIMARY KEY,
                salt BLOB NOT NULL,
                password_hash BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY,
                sender TEXT NOT NULL,
                recipients TEXT NOT NULL,
                subject TEXT NOT NULL,
                received_at INTEGER NOT NULL,
                raw BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS mailbox (
                owner TEXT NOT NULL REFERENCES accounts(address),
                folder TEXT NOT NULL CHECK (folder IN ('inbox', 'sent')),
                message_id INTEGER NOT NULL REFERENCES messages(id),
                is_read INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (owner, folder, message_id)
            );
            CREATE TABLE IF NOT EXISTS outbox (
                id INTEGER PRIMARY KEY,
                message_id INTEGER NOT NULL REFERENCES messages(id),
                recipient TEXT NOT NULL,
                state TEXT NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'sent', 'failed')),
                attempts INTEGER NOT NULL DEFAULT 0,
                next_attempt INTEGER NOT NULL,
                lease TEXT,
                last_error TEXT
            );
            CREATE INDEX IF NOT EXISTS outbox_due ON outbox(state, next_attempt);
            CREATE INDEX IF NOT EXISTS outbox_message ON outbox(message_id);
            """, transaction);
        schema.ExecuteNonQuery();
        transaction.Commit();
    }

    public bool AccountExists(string address)
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT 1 FROM accounts WHERE address = $address", null, ("$address", address));
        return command.ExecuteScalar() is not null;
    }

    public bool AddAccount(string address, string password)
        => SavePassword(address, password,
            "INSERT INTO accounts(address, salt, password_hash) VALUES ($address, $salt, $hash) ON CONFLICT(address) DO NOTHING");

    public bool SetPassword(string address, string password)
        => SavePassword(address, password, "UPDATE accounts SET salt = $salt, password_hash = $hash WHERE address = $address");

    private bool SavePassword(string address, string password, string sql)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Hash(password, salt);
        using var connection = Open();
        using var command = Command(connection, sql, null, ("$address", address), ("$salt", salt), ("$hash", hash));
        return command.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<string> ListAccounts()
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT address FROM accounts ORDER BY address");
        using var reader = command.ExecuteReader();
        var addresses = new List<string>();
        while (reader.Read()) addresses.Add(reader.GetString(0));
        return addresses;
    }

    public bool CheckPassword(string? address, string password)
    {
        if (password.Length is < 12 or > 256) return false;
        using var connection = Open();
        using var command = Command(connection,
            "SELECT salt, password_hash FROM accounts WHERE address = $address", null, ("$address", address));
        using var reader = command.ExecuteReader();
        var found = reader.Read();
        var salt = found ? (byte[])reader[0] : new byte[16];
        var expected = found ? (byte[])reader[1] : new byte[32];
        var actual = Hash(password, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected) && found;
    }

    public long SaveMessage(string sender, string visibleRecipients, IReadOnlyList<string> local,
        IReadOnlyList<string> external, string subject, byte[] raw, string? author = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var message = Command(connection, """
            INSERT INTO messages(sender, recipients, subject, received_at, raw)
            VALUES ($sender, $recipients, $subject, $time, $raw)
            RETURNING id
            """, transaction, ("$sender", sender), ("$recipients", visibleRecipients),
            ("$subject", subject), ("$time", now), ("$raw", raw));
        var id = (long)message.ExecuteScalar()!;

        foreach (var owner in local)
            AddMailboxEntry(connection, transaction, id, owner, "inbox");
        if (author is not null)
            AddMailboxEntry(connection, transaction, id, author, "sent");

        foreach (var recipient in external)
        {
            using var item = Command(connection, """
                INSERT INTO outbox(message_id, recipient, next_attempt) VALUES ($id, $recipient, $time)
                """, transaction, ("$id", id), ("$recipient", recipient), ("$time", now));
            item.ExecuteNonQuery();
        }

        transaction.Commit();
        return id;
    }

    public IReadOnlyList<MailRow> ListMail(string owner, string folder, long? before)
    {
        using var connection = Open();
        using var command = Command(connection, """
            SELECT m.id, m.sender, m.recipients, m.subject, m.received_at, b.is_read,
                CASE WHEN b.folder != 'sent' THEN NULL
                    WHEN EXISTS (SELECT 1 FROM outbox WHERE message_id = m.id AND state = 'pending') THEN 'pending'
                    WHEN EXISTS (SELECT 1 FROM outbox WHERE message_id = m.id AND state = 'failed') THEN 'failed'
                    WHEN EXISTS (SELECT 1 FROM outbox WHERE message_id = m.id) THEN 'sent'
                    ELSE 'local' END
            FROM mailbox b JOIN messages m ON m.id = b.message_id
            WHERE b.owner = $owner AND b.folder = $folder AND ($before IS NULL OR b.message_id < $before)
            ORDER BY b.message_id DESC LIMIT $limit
            """, null, ("$owner", owner), ("$folder", folder), ("$before", before), ("$limit", PageSize + 1));
        using var reader = command.ExecuteReader();
        var items = new List<MailRow>();
        while (reader.Read()) items.Add(ReadRow(reader));
        return items;
    }

    public int CountUnread(string owner)
    {
        using var connection = Open();
        using var command = Command(connection, """
            SELECT COUNT(*) FROM mailbox WHERE owner = $owner AND folder = 'inbox' AND is_read = 0
            """, null, ("$owner", owner));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public StoredMail? FindMail(string owner, string folder, long id)
    {
        using var connection = Open();
        using var command = Command(connection, """
            SELECT m.id, m.sender, m.recipients, m.subject, m.received_at, b.is_read, NULL, m.raw
            FROM mailbox b JOIN messages m ON m.id = b.message_id
            WHERE b.owner = $owner AND b.folder = $folder AND b.message_id = $id
            """, null, ("$owner", owner), ("$folder", folder), ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new StoredMail(ReadRow(reader), (byte[])reader[7]) : null;
    }

    public void MarkRead(string owner, long id)
    {
        using var connection = Open();
        using var command = Command(connection, """
            UPDATE mailbox SET is_read = 1 WHERE owner = $owner AND folder = 'inbox' AND message_id = $id AND is_read = 0
            """, null, ("$owner", owner), ("$id", id));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<DeliveryState> DeliveryStates(long id)
    {
        using var connection = Open();
        using var command = Command(connection,
            "SELECT recipient, state, last_error FROM outbox WHERE message_id = $id ORDER BY id", null, ("$id", id));
        using var reader = command.ExecuteReader();
        var items = new List<DeliveryState>();
        while (reader.Read())
            items.Add(new DeliveryState(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return items;
    }

    public QueuedMail? ClaimDelivery()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var lease = Guid.NewGuid().ToString("N");
        using var claim = Command(connection, """
            UPDATE outbox SET next_attempt = $until, lease = $lease
            WHERE id = (SELECT id FROM outbox WHERE state = 'pending' AND next_attempt <= $now ORDER BY next_attempt, id LIMIT 1)
            RETURNING id
            """, transaction, ("$until", now + 120), ("$lease", lease), ("$now", now));
        if (claim.ExecuteScalar() is not long id) return null;

        using var load = Command(connection, """
            SELECT m.sender, q.recipient, m.raw, q.attempts
            FROM outbox q JOIN messages m ON m.id = q.message_id WHERE q.id = $id
            """, transaction, ("$id", id));
        QueuedMail item;
        using (var reader = load.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Исходящее письмо отсутствует в базе.");
            item = new QueuedMail(id, reader.GetString(0), reader.GetString(1), (byte[])reader[2], reader.GetInt32(3), lease);
        }

        transaction.Commit();
        return item;
    }

    public void CompleteDelivery(QueuedMail item, string state, DateTimeOffset nextAttempt, string? error)
    {
        using var connection = Open();
        using var command = Command(connection, """
            UPDATE outbox SET state = $state, attempts = attempts + 1, next_attempt = $time, lease = NULL, last_error = $error
            WHERE id = $id AND lease = $lease
            """, null, ("$state", state), ("$time", nextAttempt.ToUnixTimeSeconds()), ("$error", error),
            ("$id", item.Id), ("$lease", item.Lease));
        command.ExecuteNonQuery();
    }

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);

    private static MailRow ReadRow(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4), reader.GetBoolean(5), reader.IsDBNull(6) ? null : reader.GetString(6));

    private static void AddMailboxEntry(SqliteConnection connection, SqliteTransaction transaction, long id, string owner, string folder)
    {
        using var command = Command(connection, """
            INSERT INTO mailbox(owner, folder, message_id, is_read) VALUES ($owner, $folder, $id, $read)
            """, transaction, ("$owner", owner), ("$folder", folder), ("$id", id), ("$read", folder == "sent"));
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
