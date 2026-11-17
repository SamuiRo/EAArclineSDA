> **Role:** Why one specific decision went the way it did · **Audience:** Anyone tempted to revisit this decision without the original context

# Автоматично підтверджувати popup Steam Trade Protection

**Status:** Accepted
**Date:** 2026-11-17

## Context

Після запуску Steam Trade Protection у липні 2025 року (SDA issue #1111) Steam відмовляє в підтвердженні трейдів, доки акаунт не прийняв одноразовий popup у вебі. Підтверджений обхідний шлях від користувачів — відкрити трейди в браузері й прийняти popup. Popup надсилає `POST https://steamcommunity.com/trade/new/acknowledge` з `sessionid` і `message=1`. Те саме робить ArchiSteamFarm (`AcknowledgeTradeRestrictions`).

## Decision

- Перед першим accept трейду надсилається acknowledge.
- Якщо будь-який accept отримав відмову, надсилається acknowledge і робиться один повтор.
- Прапорець зберігається для кожного акаунта у `vault.json`.
- Є також ручна дія в GUI та CLI.

## Consequences

- Користувачу не треба заходити в браузер.
- Помилка самого acknowledge не фатальна: popup могли прийняти раніше.
- Застарілий прапорець виправляється шляхом «відмова → acknowledge → повтор». Покрито тестами `ConfirmationTests`.
- Відхилений варіант: робити acknowledge перед кожним підтвердженням. Це зайвий запит щоразу, ASF теж так не робить.
- Не вирішує можливу окрему проблему з підтвердженнями типу `Purchase` на сторонніх майданчиках. Див. ROADMAP.
