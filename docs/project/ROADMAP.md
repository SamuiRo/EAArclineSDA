> **Role:** Backlog: open items, planned work, known debt · **Audience:** Anyone picking the next piece of work

## Перевірка

- Прогін на реальному акаунті Steam: логін, підтвердження, acknowledge, прив'язка, видалення автентифікатора.
- Ручна перевірка GUI на Linux: трей (потрібен StatusNotifier/DBus), діалоги файлів, права файлів.

## Можливості

- Self-contained збірки й пакування: Windows zip, Linux AppImage/tar.gz. CI на GitHub Actions для двох ОС.
- Автоблокування сховища після N хвилин неактивності.
- Іконки й деталі трейду (предмети) в картці підтвердження.
- Опційний Argon2id як KDF для формату v2. Це major-зміна формату, див. [decisions/ENCRYPTION_FORMAT.md](../decisions/ENCRYPTION_FORMAT.md).

## Борг

- Секрети в пам'яті розблокованого процесу лежать як `string`. У .NET їх неможливо надійно затерти.
- `SteamLoginService` не має тестів, бо SteamKit2 погано мокається. Покривається лише ручною перевіркою.
- Немає перевірки, що підтвердження типу `Purchase` (12) приймаються. За коментарями до #1111 там може бути окрема проблема з боку Steam.
