namespace RnxEmailServer;

internal static class AccountCommands
{
    public static int Run(string[] arguments, MailSettings settings, MailStore store)
    {
        if (arguments is ["account", "list"])
        {
            foreach (var address in store.ListAccounts()) Console.WriteLine(address);
            return 0;
        }

        if (arguments is not ["account", "add" or "password", _])
        {
            Console.Error.WriteLine("Использование: dotnet run -- account add <адрес> | account password <адрес> | account list");
            return 2;
        }

        var addressToAdd = settings.AccountAddress(arguments[2]);
        if (addressToAdd is null)
        {
            Console.Error.WriteLine($"Укажите допустимый адрес в домене {settings.Domain}.");
            return 2;
        }

        var reset = arguments[1] == "password";
        if (store.AccountExists(addressToAdd) != reset)
        {
            Console.Error.WriteLine(reset ? "Ящик не найден." : "Такой ящик уже существует.");
            return 2;
        }

        var password = ReadPassword("Пароль (от 12 до 256 символов): ");
        if (password is null || password.Length is < 12 or > 256 || password != ReadPassword("Повторите пароль: "))
        {
            Console.Error.WriteLine("Длина пароля вне диапазона или пароли не совпадают.");
            return 2;
        }

        var saved = reset ? store.SetPassword(addressToAdd, password) : store.AddAccount(addressToAdd, password);
        if (!saved)
        {
            Console.Error.WriteLine(reset ? "Ящик не найден." : "Такой ящик уже существует.");
            return 2;
        }

        Console.WriteLine(reset ? $"Пароль обновлён для {addressToAdd}" : $"Создан ящик {addressToAdd}");
        return 0;
    }

    private static string? ReadPassword(string prompt)
    {
        Console.Error.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine();

        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && password.Length > 0) password.Length--;
            else if (!char.IsControl(key.KeyChar) && password.Length < 257) password.Append(key.KeyChar);
        }

        Console.Error.WriteLine();
        return password.ToString();
    }
}
