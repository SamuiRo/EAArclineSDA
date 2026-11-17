# Карта документації

Кожен файл у `docs/` має одну роль. Шукайте рядок, що відповідає вашому питанню, замість пошуку по всьому.

| Питання | Де |
|---|---|
| Де ми зараз, що далі | [project/HANDOFF.md](project/HANDOFF.md) — читати повністю |
| Що ще зробити, відомий борг | [project/ROADMAP.md](project/ROADMAP.md) |
| Що змінилося в кожній версії і чому | [project/CHANGELOG.md](project/CHANGELOG.md) |
| Як влаштована система, що не можна ламати | [project/ARCHITECTURE.md](project/ARCHITECTURE.md) |
| Формат зашифрованого maFile для інших програм (контракт) | [features/MAFILE_FORMAT.md](features/MAFILE_FORMAT.md) |
| Чому обрано саме такий формат шифрування | [decisions/ENCRYPTION_FORMAT.md](decisions/ENCRYPTION_FORMAT.md) |
| Чому trade protection підтверджується автоматично | [decisions/TRADE_PROTECTION_ACK.md](decisions/TRADE_PROTECTION_ACK.md) |

## Як додати новий документ

Кожен файл, окрім цього, починається з рядка-ролі:

```
> **Role:** <на яке питання відповідає> · **Audience:** <хто читає>
```

Кладіть новий текст у теку, чия роль відповідає питанню, а не у файл, який зараз відкритий. Після цього додайте рядок у таблицю вище.
