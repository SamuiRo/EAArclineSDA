# EAArclineSDA

Спочатку прочитайте повністю [docs/project/HANDOFF.md](docs/project/HANDOFF.md). Де шукати решту, показує [docs/README.md](docs/README.md).

- Збірка й тести: `.\.dotnet\dotnet.exe test EAArclineSDA.slnx` (SDK встановлено локально).
- Версія: одне джерело — `<Version>` у `Directory.Build.props`. Кожну minor чи major зміну записуйте в `docs/project/CHANGELOG.md` у тому ж коміті. Якщо зміна стосується формату maFile, оновлюйте також `docs/features/MAFILE_FORMAT.md`.
- Інваріанти, які не можна ламати: [docs/project/ARCHITECTURE.md](docs/project/ARCHITECTURE.md).
