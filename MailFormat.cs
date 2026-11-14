using System.Text;
using MimeKit;
using MimeKit.Text;
using MimeKit.Utils;

namespace RnxEmailServer;

internal static class MailFormat
{
    private static readonly ParserOptions Options = new() { MaxMimeDepth = 24 };

    public static MimeMessage Parse(byte[] raw)
    {
        using var stream = new MemoryStream(raw, writable: false);
        return MimeMessage.Load(Options, stream);
    }

    public static (string Subject, string Recipients) ReadHeaders(byte[] raw)
    {
        try
        {
            var message = Parse(raw);
            var recipients = string.Join(", ", message.To.Concat(message.Cc));
            return (Limit(message.Subject ?? "", 500), Limit(recipients, 4000));
        }
        catch (FormatException)
        {
            return ("", "");
        }
    }

    public static string ReadBody(MimeMessage message)
    {
        var text = message.TextBody;
        if (string.IsNullOrWhiteSpace(text) && message.HtmlBody is { } html)
            text = ExtractHtmlText(Limit(html, 100_000));
        return Limit(text ?? "", 100_000);
    }

    private static string ExtractHtmlText(string html)
    {
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        var text = new StringBuilder();
        var hidden = new HashSet<HtmlTagId>();
        while (tokenizer.ReadNextToken(out var token))
        {
            if (token is HtmlTagToken tag)
            {
                if (tag.Id is HtmlTagId.Head or HtmlTagId.Style or HtmlTagId.Script or HtmlTagId.Title)
                {
                    if (tag.IsEndTag) hidden.Remove(tag.Id);
                    else hidden.Add(tag.Id);
                }
                if (hidden.Count == 0 && (tag.Id is HtmlTagId.Br or HtmlTagId.P or HtmlTagId.Div || tag.Name is "li" or "tr"))
                    text.AppendLine();
            }
            else if (hidden.Count == 0 && token is HtmlDataToken data && token.Kind == HtmlTokenKind.Data)
            {
                text.Append(data.Data);
            }
        }

        return text.ToString().Trim();
    }

    public static string Limit(string value, int length)
    {
        if (value.Length <= length) return value;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }

    public static byte[] Compose(MailSettings settings, string sender, IReadOnlyList<MailboxAddress> recipients,
        string subject, string body)
    {
        var message = new MimeMessage
        {
            Subject = subject,
            Date = DateTimeOffset.Now,
            MessageId = MimeUtils.GenerateMessageId(settings.HostName),
            Body = new TextPart("plain") { Text = body, ContentTransferEncoding = ContentEncoding.QuotedPrintable }
        };
        message.From.Add(MailboxAddress.Parse(sender));
        message.To.AddRange(recipients);
        using var stream = new MemoryStream();
        var format = FormatOptions.Default.Clone();
        format.NewLineFormat = NewLineFormat.Dos;
        message.WriteTo(format, stream);
        return stream.ToArray();
    }
}
