using System.Globalization;
using System.Net;
using System.Text;

namespace RnxEmailServer;

internal static class MailViews
{
    private const string Brand = """<span class="brand"><i class="brand-icon" aria-hidden="true"></i> Rnx<span>Mail</span></span>""";

    public static string Login(string token, string domain, string address, string? error) => Page("Вход", $$"""
        <main class="login">
            <section class="login-card">
                <div class="login-head">
                    {{Brand}}
                    <span class="domain">{{E(domain)}}</span>
                </div>
                <h1>Вход в почту</h1>
                {{Error(error)}}
                <form method="post" class="fields">
                    {{Token(token)}}
                    <label>Почтовый адрес
                        <input name="address" autocomplete="username" value="{{E(address)}}" placeholder="имя@{{E(domain)}}" maxlength="254" required>
                    </label>
                    <label>Пароль
                        <input type="password" name="password" autocomplete="current-password" maxlength="256" required>
                    </label>
                    <button class="button primary" type="submit">Войти</button>
                </form>
            </section>
        </main>
        """);

    public static string Compose(string owner, int unread, string token, MailSettings settings,
        string to, string subject, string body, string? error)
    {
        var hint = settings.Relay.Enabled ? "Несколько адресов можно указать через запятую." :
            $"Письма можно отправлять на адреса домена {E(settings.Domain)}.";
        return Shell("Новое письмо", owner, unread, token, "compose", $$"""
            <main class="compose-panel">
                <header class="panel-head"><h1>Новое письмо</h1></header>
                <div class="compose-content">
                    {{Error(error)}}
                    <form action="/compose" method="post" class="fields">
                        {{Token(token)}}
                        <label>Кому
                            <input name="to" value="{{E(to)}}" maxlength="4000" required aria-describedby="address-hint">
                            <span id="address-hint" class="muted">{{hint}}</span>
                        </label>
                        <label>Тема
                            <input name="subject" value="{{E(subject)}}" maxlength="250">
                        </label>
                        <label>Сообщение
                            <textarea name="body" rows="10" maxlength="100000" required>{{E(body)}}</textarea>
                        </label>
                        <div class="form-actions">
                            <button class="button primary" type="submit">Отправить</button>
                            <a class="button subtle" href="/">Отмена</a>
                        </div>
                    </form>
                </div>
            </main>
            """);
    }

    public static string Mailbox(string owner, int unread, string token, string folder, IReadOnlyList<MailRow> rows,
        MailRow? selected, string body, IReadOnlyList<DeliveryState> states, long? before)
    {
        var inbox = folder == "inbox";
        var title = inbox ? "Входящие" : "Отправленные";
        var baseUrl = inbox ? "/" : "/sent";
        var items = new StringBuilder();
        foreach (var item in rows.Take(MailStore.PageSize))
        {
            var active = item.Id == selected?.Id;
            var rowClass = !item.IsRead && !active ? " unread" : "";
            var current = active ? """ aria-current="true" """ : "";
            var party = inbox ? DisplaySender(item.Sender) : item.Recipients;
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(item.ReceivedAt);
            var date = timestamp.ToLocalTime().Date == DateTime.Today ? FormatDate(timestamp, "HH:mm") : FormatDate(timestamp, "d MMM");
            var status = item.Delivery is null ? "" : $"""<small class="state {E(item.Delivery)}">{StateLabel(item.Delivery)}</small>""";
            items.Append($$"""
                <li><a class="mail-row{{rowClass}}" href="/mail/{{folder}}/{{item.Id}}"{{current}}>
                    <i class="unread-dot" aria-hidden="true"></i>
                    <span class="party">{{E(party)}}</span><time datetime="{{timestamp:O}}">{{E(date)}}</time>
                    <span class="subject">{{E(Subject(item.Subject))}}</span>{{status}}
                </a></li>
                """);
        }

        var empty = rows.Count == 0 ? $"""<p class="list-empty">{(inbox ? "Входящих писем пока нет." : "Отправленных писем пока нет.")}</p>""" : "";
        var topPage = before.HasValue ? $"""<a href="{baseUrl}">К началу</a>""" : "";
        var nextPage = rows.Count > MailStore.PageSize ?
            $"""<a href="{baseUrl}?before={rows[MailStore.PageSize - 1].Id}">Более ранние <span aria-hidden="true">→</span></a>""" : "";
        var pagination = topPage.Length + nextPage.Length > 0 ? $"""<nav class="pagination" aria-label="Страницы писем">{topPage}{nextPage}</nav>""" : "";
        var preview = selected is null ? $$"""
            <section class="empty-preview" aria-label="Просмотр письма">
                <i class="empty-icon" aria-hidden="true"></i>
                <h2>{{(rows.Count == 0 ? "Здесь будет письмо" : "Выберите письмо")}}</h2>
            </section>
            """ : ReadMessage(folder, selected, body, states);

        return Shell(title, owner, unread, token, folder, $$"""
            <main class="mailbox{{(selected is null ? "" : " has-selection")}}">
                <section class="message-list" aria-label="{{title}}">
                    <header class="panel-head">
                        <h1>{{title}}</h1>
                        <a class="refresh" href="{{baseUrl}}" aria-label="Обновить список" title="Обновить список">↻</a>
                    </header>
                    {{empty}}
                    <ul>{{items}}</ul>
                    {{pagination}}
                </section>
                {{preview}}
            </main>
            """);
    }

    private static string ReadMessage(string folder, MailRow mail, string body, IReadOnlyList<DeliveryState> states)
    {
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(mail.ReceivedAt);
        var results = new StringBuilder();
        foreach (var status in states)
        {
            var error = status.Error is null ? "" : $"""<span class="delivery-error">{E(status.Error)}</span>""";
            results.Append($"""<li><span>{E(status.Recipient)}</span> <span class="state {E(status.State)}">{StateLabel(status.State)}</span>{error}</li>""");
        }
        var delivery = results.Length == 0 ? "" : $"""<ul class="delivery" aria-label="Доставка по получателям">{results}</ul>""";
        var from = DisplaySender(mail.Sender);
        return $$"""
            <article class="preview" aria-label="Просмотр письма">
                <header class="panel-head preview-bar">
                    <a class="back-link" href="{{(folder == "inbox" ? "/" : "/sent")}}">← К списку</a>
                    <a class="button subtle" href="/mail/{{folder}}/{{mail.Id}}/download">Скачать .eml</a>
                </header>
                <div class="preview-content">
                    <h2>{{E(Subject(mail.Subject))}}</h2>
                    <div class="message-meta">
                        <span class="avatar" aria-hidden="true">{{E(Initial(from))}}</span>
                        <div class="addresses">
                            <strong>{{E(from)}}</strong>
                            <span>Кому: {{E(mail.Recipients.Length == 0 ? "Адрес скрыт" : mail.Recipients)}}</span>
                        </div>
                        <time datetime="{{timestamp:O}}">{{E(FormatDate(timestamp, "d MMM yyyy, HH:mm"))}}</time>
                    </div>
                    {{delivery}}
                    <div class="message-body">{{E(string.IsNullOrWhiteSpace(body) ? "В письме нет текста." : body)}}</div>
                </div>
            </article>
            """;
    }

    private static string Shell(string title, string owner, int unread, string token, string active, string content)
    {
        var count = unread == 0 ? "" : $"""<span class="count" aria-label="{unread} непрочитанных">{unread}</span>""";
        return Page(title, $$"""
            <header class="top">
                <a href="/" class="home" aria-label="Rnx Mail — входящие">{{Brand}}</a>
                <div class="account">
                    <span class="avatar" aria-hidden="true">{{E(Initial(owner))}}</span>
                    <span class="owner">{{E(owner)}}</span>
                    <form action="/logout" method="post">{{Token(token)}}<button class="button subtle" type="submit">Выйти</button></form>
                </div>
            </header>
            <div class="workspace">
                <aside class="sidebar">
                    <a href="/compose" class="button primary compose-button"><span aria-hidden="true">+</span> Написать</a>
                    <nav class="folders" aria-label="Папки">
                        <a href="/"{{Current(active == "inbox")}}><i class="folder-icon inbox-icon" aria-hidden="true"></i>Входящие{{count}}</a>
                        <a href="/sent"{{Current(active == "sent")}}><i class="folder-icon sent-icon" aria-hidden="true">↗</i>Отправленные</a>
                    </nav>
                </aside>
                {{content}}
            </div>
            """);
    }

    private static string Page(string title, string content) => $$"""
        <!doctype html>
        <html lang="ru">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{E(title)}} · Rnx Mail</title>
            <link rel="stylesheet" href="/mail.css">
        </head>
        <body>{{content}}</body>
        </html>
        """;

    private static string Current(bool current) => current ? """ aria-current="page" """ : "";
    private static string Token(string token) => $"""<input type="hidden" name="__RequestVerificationToken" value="{E(token)}">""";
    private static string Error(string? error) => error is null ? "" : $"""<p class="error" role="alert">{E(error)}</p>""";
    private static string FormatDate(DateTimeOffset date, string format) => date.ToLocalTime().ToString(format, CultureInfo.GetCultureInfo("ru-RU"));
    private static string DisplaySender(string sender) => sender.Length == 0 ? "Системное уведомление" : sender;
    private static string Subject(string subject) => string.IsNullOrWhiteSpace(subject) ? "(Без темы)" : subject;
    private static string Initial(string address) => StringInfo.GetNextTextElement(address).ToUpperInvariant();
    private static string E(string value) => WebUtility.HtmlEncode(value);

    private static string StateLabel(string state) => state switch
    {
        "pending" => "В очереди",
        "sent" => "Принято релеем",
        "failed" => "Ошибка доставки",
        _ => "Доставлено локально"
    };
}
