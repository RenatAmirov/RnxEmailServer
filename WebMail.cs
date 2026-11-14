using System.Net.Mime;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using MimeKit;

namespace RnxEmailServer;

internal static class WebMail
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/login", (HttpContext context, MailSettings settings, IAntiforgery forms) =>
            context.User.Identity?.IsAuthenticated == true ? Results.Redirect("/") :
                LoginPage(context, forms, settings));
        app.MapPost("/login", LoginAsync).RequireRateLimiting("login");
        app.MapPost("/logout", async (HttpContext context, IAntiforgery forms) =>
        {
            if (!await forms.IsRequestValidAsync(context)) return Results.BadRequest();
            await context.SignOutAsync();
            return Results.Redirect("/login");
        }).RequireAuthorization();

        var mail = app.MapGroup("").RequireAuthorization();
        mail.MapGet("/", (HttpContext context, IAntiforgery forms, MailStore store, long? before) =>
            Mailbox(context, forms, store, "inbox", before, null));
        mail.MapGet("/sent", (HttpContext context, IAntiforgery forms, MailStore store, long? before) =>
            Mailbox(context, forms, store, "sent", before, null));
        mail.MapGet("/mail/{folder}/{id:long}", (HttpContext context, IAntiforgery forms, MailStore store, string folder, long id) =>
            Mailbox(context, forms, store, folder, null, id));
        mail.MapGet("/mail/{folder}/{id:long}/download", (HttpContext context, MailStore store, string folder, long id) =>
        {
            var message = ValidFolder(folder) ? store.FindMail(Owner(context), folder, id) : null;
            return message is null ? Results.NotFound() :
                Results.File(message.Raw, MediaTypeNames.Application.Octet, $"message-{id}.eml");
        });
        mail.MapGet("/compose", (HttpContext context, IAntiforgery forms, MailStore store, MailSettings settings) =>
            ComposePage(context, forms, store, settings));
        mail.MapPost("/compose", ComposeAsync);
    }

    private static async Task<IResult> LoginAsync(HttpContext context, IAntiforgery forms, MailSettings settings, MailStore store)
    {
        if (!await forms.IsRequestValidAsync(context)) return Results.BadRequest();
        var form = await context.Request.ReadFormAsync();
        var login = form["address"].ToString();
        var address = settings.AccountAddress(login);
        if (!store.CheckPassword(address, form["password"].ToString()))
            return LoginPage(context, forms, settings, MailFormat.Limit(login, 254), "Проверьте адрес и пароль.", 401);

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, address!)], CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(new ClaimsPrincipal(identity));
        return Results.Redirect("/");
    }

    private static async Task<IResult> ComposeAsync(HttpContext context, IAntiforgery forms, MailStore store, MailSettings settings)
    {
        if (!await forms.IsRequestValidAsync(context)) return Results.BadRequest();
        var form = await context.Request.ReadFormAsync();
        var to = form["to"].ToString().Trim();
        var subject = form["subject"].ToString().Trim();
        var body = form["body"].ToString();

        IResult Error(string message) => ComposePage(context, forms, store, settings,
            MailFormat.Limit(to, 4000), MailFormat.Limit(subject, 250), MailFormat.Limit(body, 100_000), message);

        if (subject.Length > 250 || subject.Any(char.IsControl) || body.Length > 100_000 || string.IsNullOrWhiteSpace(body))
            return Error("Укажите текст письма. Тема может содержать до 250 символов, текст — до 100 000.");
        if (to.Length is 0 or > 4000 || to.Any(char.IsControl) ||
            !InternetAddressList.TryParse(to, out var parsed) || parsed.Count == 0 ||
            parsed.Count > MailSettings.MaxRecipients || parsed.Any(address => address is not MailboxAddress))
            return Error("Введите до 25 адресов через запятую.");

        var local = new List<string>();
        var external = new List<string>();
        var recipients = new List<MailboxAddress>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var address in parsed.Mailboxes)
        {
            if (address.Address.Length > 254 || !address.Address.Contains('@'))
                return Error("Укажите полные адреса, включая домен.");

            var localAddress = settings.IsLocal(address.Address) ? settings.AccountAddress(address.Address) : null;
            if (settings.IsLocal(address.Address) && (localAddress is null || !store.AccountExists(localAddress)))
                return Error($"Ящик {address.Address} не найден.");
            if (localAddress is null && !settings.Relay.Enabled)
                return Error("Внешняя отправка недоступна. Укажите адрес в домене " + settings.Domain + ".");

            var normalized = localAddress ?? address.Address;
            if (!seen.Add(normalized)) continue;
            (localAddress is null ? external : local).Add(normalized);
            recipients.Add(address);
        }

        var owner = Owner(context);
        var raw = MailFormat.Compose(settings, owner, recipients, subject, body);
        var id = store.SaveMessage(owner, string.Join(", ", recipients), local, external, subject, raw, owner);
        return Results.Redirect($"/mail/sent/{id}");
    }

    private static IResult LoginPage(HttpContext context, IAntiforgery forms, MailSettings settings,
        string address = "", string? error = null, int status = 200) =>
        Html(MailViews.Login(Token(context, forms), settings.Domain, address, error), status);

    private static IResult ComposePage(HttpContext context, IAntiforgery forms, MailStore store, MailSettings settings,
        string to = "", string subject = "", string body = "", string? error = null)
    {
        var owner = Owner(context);
        return Html(MailViews.Compose(owner, store.CountUnread(owner), Token(context, forms), settings,
            to, subject, body, error), error is null ? 200 : 400);
    }

    private static IResult Mailbox(HttpContext context, IAntiforgery forms, MailStore store, string folder, long? before, long? id)
    {
        if (!ValidFolder(folder)) return Results.NotFound();
        var owner = Owner(context);
        StoredMail? selected = null;
        var body = "";
        IReadOnlyList<DeliveryState> states = [];
        if (id is long messageId)
        {
            selected = store.FindMail(owner, folder, messageId);
            if (selected is null) return Results.NotFound();
            if (folder == "inbox") store.MarkRead(owner, messageId);
            else states = store.DeliveryStates(messageId);
            try
            {
                body = MailFormat.ReadBody(MailFormat.Parse(selected.Raw));
            }
            catch (FormatException)
            {
                body = "Не удалось разобрать письмо. Скачайте оригинал, чтобы открыть его в почтовой программе.";
            }
        }

        var list = store.ListMail(owner, folder, before);
        return Html(MailViews.Mailbox(owner, store.CountUnread(owner), Token(context, forms), folder,
            list, selected?.Row, body, states, before));
    }

    private static string Token(HttpContext context, IAntiforgery forms) => forms.GetAndStoreTokens(context).RequestToken!;
    private static bool ValidFolder(string folder) => folder is "inbox" or "sent";
    private static string Owner(HttpContext context) => context.User.Identity!.Name!;
    private static IResult Html(string html, int status = 200) => Results.Content(html, "text/html; charset=utf-8", statusCode: status);
}
