# RnxEmailServer

Свой почтовый сервер для одного домена на .NET 10. Принимает письма по SMTP, доставляет их в локальные ящики и хранит оригиналы в SQLite. В веб-почте можно читать и отправлять письма, скачивать оригиналы `.eml` вместе с вложениями и смотреть результат отправки для каждого внешнего получателя.

## Запуск на Windows

Установите .NET 10 SDK. В PowerShell из каталога проекта выполните:

```powershell
dotnet restore --locked-mode
dotnet run -- account add alice
dotnet run -- account add bob
dotnet run
```

Команда добавления ящика запросит пароль длиной от 12 символов и его повтор. Короткие имена `alice` и `bob` станут адресами `alice@example.test` и `bob@example.test`. Список ящиков можно получить командой `dotnet run -- account list`. Чтобы поменять пароль, выполните `dotnet run -- account password alice`.

Откройте [веб-почту](http://127.0.0.1:8080/login), войдите как Alice и отправьте письмо на `bob@example.test`. Затем войдите как Bob, чтобы проверить входящие. Локальный SMTP слушает `127.0.0.1:8025` и принимает письма для существующих ящиков. Для проверки SMTP из другого окна PowerShell можно использовать Python:

```powershell
@'
from email.message import EmailMessage
from smtplib import SMTP

message = EmailMessage()
message["From"] = "sender@example.org"
message["To"] = "alice@example.test"
message["Subject"] = "SMTP test"
message.set_content("Hello from SMTP.")
with SMTP("127.0.0.1", 8025, timeout=10) as smtp:
    smtp.send_message(message)
'@ | py -
```

## Настройки

Значения по умолчанию находятся в `appsettings.json`. Переопределяйте их через переменные среды или файл `appsettings.Local.json`.

| Ключ | Назначение |
| --- | --- |
| `Mail:Domain` | Домен ящиков. Задайте его до создания учётных записей. |
| `Mail:HostName` | Полное DNS-имя сервера для SMTP. |
| `Mail:DataDirectory` | Каталог базы `mail.db` и ключей веб-сессий. |
| `Mail:WebUrl` | Адрес веб-почты. HTTP доступен на loopback, для внешнего адреса используется HTTPS. |
| `Mail:SmtpBind`, `Mail:SmtpPort` | IPv4-адрес и порт входящего SMTP. |
| `Mail:SmtpCertificatePath`, `Mail:SmtpCertificatePassword` | Сертификат PFX с закрытым ключом и пароль для SMTP STARTTLS. |
| `Mail:Relay:Host`, `Mail:Relay:Port` | Адрес и порт внешнего SMTP-релея. |
| `Mail:Relay:ImplicitTls` | `false` для обязательного STARTTLS на порту 587, `true` для TLS с момента подключения, обычно на порту 465. |
| `Mail:Relay:User`, `Mail:Relay:Password` | Учётные данные SMTP-релея, если он требует авторизацию. |
| `Mail:Relay:CertificateSha256` | Необязательный SHA-256 отпечаток сертификата частного релея (64 шестнадцатеричных символа). При заданном отпечатке сервер проверяет имя, срок действия и совпадение сертификата. |

### Внешняя отправка

Задайте параметры SMTP-релея и перезапустите сервер. Например, для PowerShell:

```powershell
$env:Mail__Relay__Host = 'smtp.provider.example'
$env:Mail__Relay__Port = '587'
$env:Mail__Relay__User = 'relay-user'
$env:Mail__Relay__Password = [Net.NetworkCredential]::new('', (Read-Host 'Пароль SMTP-релея' -AsSecureString)).Password
dotnet run
```

Подтвердите отправляющий домен у провайдера релея и добавьте его записи SPF и DKIM в DNS. Письмо и очередь сохраняются одной транзакцией. Очередь переживает перезапуск, временные ошибки повторяются с задержкой до восьми попыток. Статус «Принято релеем» означает, что релей подтвердил приём письма. Если сервер завершится между этим подтверждением и записью статуса в SQLite, повтор может доставить дубликат.

### Приём почты из интернета

Для собственного домена задайте `Mail:Domain` и `Mail:HostName`, создайте ящики, укажите сетевой IPv4-адрес в `Mail:SmtpBind` и порт 25 в `Mail:SmtpPort`. Настройте A-запись для имени сервера, MX-запись домена на это имя и доступ к порту 25 снаружи. Установите доверенный TLS-сертификат для SMTP. Для HTTPS веб-почты задайте `Mail:WebUrl` и настройте сертификат Kestrel через `Kestrel__Certificates__Default__Path` и `Kestrel__Certificates__Default__Password`.

Храните каталог данных с правами доступа только для учётной записи сервера. Для резервной копии остановите сервер и скопируйте каталог целиком. На Windows ключи сессий защищены DPAPI текущего пользователя. При переносе на другой компьютер перенесите базу, а веб-почта создаст новые ключи для входа. В текущей версии веб-почта служит почтовым клиентом. IMAP, POP3, прямой исходящий SMTP по MX и фильтрация спама не реализованы. Входящий SMTP принимает до 10 МиБ на письмо и до 25 получателей за транзакцию.

Документация по развёртыванию почтовых серверов и требованиям к DNS доступна у [Red Hat](https://docs.redhat.com/en/documentation/red_hat_enterprise_linux/9/html/deploying_mail_servers/assembly_configuring-and-maintaining-a-dovecot-imap-and-pop3-server_deploying-mail-servers) и в [правилах для отправителей Gmail](https://support.google.com/a/answer/81126).
