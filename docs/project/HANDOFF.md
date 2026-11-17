> **Role:** Current state and next steps · **Audience:** Anyone starting a session on this project, human or AI

## Поточний стан

Версія **2.1.0** на `master`. Remote-и: `starbase` (Forgejo, upstream) і `github`. Звичайний `git push` відправляє в обидва, бо в `starbase` два pushurl, а `push.followTags=true`. Це переписаний Steam Desktop Authenticator. Оригінал лежить поруч у `S:\Daedalus\Aureum\SteamDesktopAuthenticator` і використовується лише як референс.

Що є:
- ядро `Arcline.SteamGuard`, CLI `arcline-sda`, GUI на Avalonia 11;
- платформа .NET 10, цілі — Windows і Linux;
- виправлення SDA #1111 (trade protection);
- maFiles зашифровані за замовчуванням;
- експорт і імпорт, міграція сховища між ПК, імпорт старої папки SDA.

Тести: 34 юніт-тести ядра (включно з міграцією) і 4 headless UI-тести (з них 2 на вибір, фільтр і масові дії з підтвердженнями проти фейкового Steam). Усі проходять.

SDK встановлено локально: `.dotnet\dotnet.exe` (у git не потрапляє). Збірка й тести: `.\.dotnet\dotnet.exe test EAArclineSDA.slnx`.

**Не перевірено на реальному акаунті Steam:** логін, прив'язка, підтвердження, acknowledge і видалення автентифікатора. GUI на Linux не запускався, перевірено лише збірку під linux-x64.

## Наступні кроки

1. Перший прогін на одному реальному акаунті: імпорт його maFile → список підтвердження → accept трейду. Перевірити, що acknowledge спрацював, а токени зберігаються після рестарту.
2. Запустити GUI на Linux і перевірити трей, діалоги файлів та права `600` на файли сховища.
3. Розібрати пункти з [ROADMAP.md](ROADMAP.md).

## Правила, які легко порушити

- GUI не тестувати через SendKeys чи глобальні натискання клавіш, бо вони потрапляють у чужі вікна. Тільки `tests/Arcline.SDA.Desktop.Tests` (Avalonia.Headless).
- Нові NuGet-пакети брати лише у версіях, старших за 2 тижні.

## Журнал сесій

### 2026-11-17

- Проаналізовано старий SDA, переписано з нуля (ядро, CLI, GUI), виправлено #1111, введено формат `arcline-mafile` v1.
- Додано резервну копію, відновлення та злиття сховищ для переїзду між ПК. Ініціалізовано git на `master`, задокументовано 2.0.0.
- Налаштовано push у Forgejo (`starbase`) і GitHub одночасно, ключ `id_ed25519_starlith` через SSH-аліаси. Вийшла 2.1.0: вибір, фільтр, масові дії та режим «All accounts» для підтверджень.
