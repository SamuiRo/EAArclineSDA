> **Role:** How the system is designed and what must not break · **Audience:** Anyone changing code in this repo

## Шари

```
Arcline.SDA.Desktop (Avalonia, MVVM)     Arcline.SDA.Cli (arcline-sda)     ваші програми
                \                                 |                         /
                 +------------- Arcline.SteamGuard (ядро, net10.0) --------+
                    Storage/   MaFileVault, VaultIndex, SecureFile
                    Crypto/    MaFileEnvelope, MaFileCrypto, SteamGuardCode, ConfirmationSigner
                    Steam/     SteamHttp, SteamTime, SessionService, ConfirmationService,
                               SteamLoginService (SteamKit2), AuthenticatorLinker
                    Legacy/    LegacySdaImporter
                    SteamGuardClient — фасад для застосунків
```

UI та CLI не звертаються до Steam напряму. Усе проходить через `SteamGuardClient`.

## Сховище

- `vault.json` — неcекретний індекс: SteamID, ім'я, файл, прапорець trade protection. У ньому ж налаштування, спільна сіль і `verifier`, за яким перевіряється пароль навіть у порожньому сховищі.
- `<steamid>.maFile` — конверт `arcline-mafile` v1. Усі файли сховища використовують одну сіль, тому ключ виводиться один раз на розблокування. Під час експорту генерується нова сіль.
- `MaFileVault` реалізує `IAccountStateStore`. Через нього сервіси зберігають оновлені токени й прапорець acknowledge.

## Інваріанти (не ламати)

- **Немає plaintext на диску.** Відкритий текст пишуть тільки `Export(..., Plaintext)`, CLI `decrypt` і `--out`. Будь-який інший запис іде через `MaFileCrypto`.
- **Поля maFile сумісні з SDA.** Це snake_case-поля та об'єкт `Session` з PascalCase. Невідомі поля зберігаються через `JsonExtensionData`. Без цього експортований файл не відкриють SDA, ASF та інші інструменти.
- **AAD конверта** — рядок `arcline-mafile|version|kdf|iterations|salt|steamid|account_name`. Будь-яка зміна тут = нова `version` формату (major). Код, специфікацію й `tools/mafile_decrypt.py` міняти разом.
- **Атомарні записи** через `SecureFile.WriteAllText`: тимчасовий файл, потім rename. Права `600` на Unix.
- **Ім'я файлу з `vault.json` проходить через `SafeName`.** Резервні копії приходять ззовні.
- **Секрети ніколи не приймаються аргументом CLI.** Тільки файл, змінна середовища або prompt.
- **Протокол Steam:** TOTP і підпис підтверджень побайтно збігаються з SteamAuth (див. `CodeTests`). Кукі формуються для кожного акаунта окремо, спільного `CookieContainer` немає.

## Потік підтвердження

1. `ConfirmationService.GetAsync`: `EnsureFresh` (refresh, якщо до кінця access token менше 5 хв) → `getlist`. Якщо `needauth`, робиться примусовий refresh і повтор. Якщо й далі `needauth` — `SessionExpiredException`.
2. `RespondAsync`:
   - для трейду, коли acknowledge ще не було, спершу йде `/trade/new/acknowledge`;
   - далі `multiajaxop`;
   - якщо Steam відмовив, ще раз acknowledge і повтор;
   - потім `ajaxop` для кожного підтвердження окремо, щоб повернути результат по кожному.
