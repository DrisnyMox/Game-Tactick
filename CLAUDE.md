# CLAUDE.md

Устройство игры, сцены, управление и направление — в [README.md](README.md). Здесь только то, чего не видно сразу из кода.

## Карта

Стартовая сцена — `Base` (первая в Build Settings), дальше `Raid`. `SampleScene` — бой 5 на 5 на арене.

- **Цикл «база → вылазка → база»** — `Assets/Loop/Scripts`.
  - `BaseHub` + `BaseStation`: база, окна склада, торговца, верстака.
  - `RaidDirector`: правила вылазки (старт, рейдеры, эвакуация, глубокая вода). HUD рисует `RaidHud`, места добычи рейдеров — `RaidGatherSpots`, карман — `ProtectedPocket`.
  - `Profile`: сохранение в PlayerPrefs (JSON), итог вылазки `Profile.LastRaid`. Предметы — `ItemId`, `Items`, `ItemCounts`, `Recipe`, `Trader`. Имена сцен — `SceneNames`.
  - `Backpack`, `Pickup`, `ExtractionZone`, `Noise`, `LoopGui`.
- **Персонаж** — `Assets/Characters/Survivor/Scripts`, поверх `ThirdPersonController` из Starter Assets.
  - `SurvivorShooter`: стрельба, прицел, отдача. HUD игрока — `ShooterHud`, левая рука на цевье — `TwoBoneIK`.
  - `CharacterHealth`: здоровье, команда, рэгдолл, события `Died` и `Killed`. `ShooterBot`: бот и рейдер. `ShooterWeapon`: точки на винтовке и эффекты выстрела.
- **Арена** — `Assets/VoxelArena/Scripts`.
  - `VoxelArena` (partial): `VoxelArena.cs` — состояние и API вокселей, `.Generation.cs` — генерация, `.Meshing.cs` — меши и коллайдеры чанков.
  - Раскладка: `ArenaLayoutGenerator` (partial, шаги Burst-джобы `BuildLayoutJob` по файлам) для арены и `IslandLayout` для острова. Обе выдают `ArenaLayout`.
  - `ArenaDecorator`: постройки и деревья в вокселях. `ArenaNavMesh`: NavMesh по чанкам. Джобы мешей — в `Jobs/`.
- **UI** — `Assets/Scripts/UI`: `ArenaLoadingScreen`, `SettingsMenu` (Esc), `FpsCounter`.

## Связи из инспектора и настроек

- `PlayerInput` на `PlayerSurvivor` работает в режиме Send Messages. Он вызывает по имени `OnAim`, `OnFire`, `OnReload` у `SurvivorShooter` и `OnMove`, `OnLook` и другие у `StarterAssetsInputs`. Эти методы не переименовывать. Записи `InputMove`/`InputLook` в Action Events префаба — старые, в режиме Send Messages не работают.
- Animation Event нет.
- `followCamera` у `SurvivorShooter` игрока обязателен и задаётся в каждой сцене (в префабе пусто), иначе `NullReferenceException` в `Awake`.
- Порядок выполнения задан атрибутами, в Project Settings ничего нет: `ShooterBot` −50 (ввод бота раньше), `SurvivorShooter` 100 (после `ThirdPersonController`).
- Слои: `Hitbox` (6) — кости рэгдолла, чтобы проверка земли не находила свои ступни. Матрица столкновений — всё со всем. `RaidDirector.Ground` бьёт лучом по маске `1` (Default — арена).
- Теги: `CinemachineTarget` и `Weapon` — из Starter Assets.
- Enter Play Mode без перезагрузки домена: статические поля и события сбрасывает `ResetStatics` с `[RuntimeInitializeOnLoadMethod]`. Новой статике нужен такой же сброс.

## Скрипты Tools/Unity

Лежат вне `Assets`. Собирают сцены (`Setup*`) и проверяют игру (`Probe*`). Компилируются отдельно при запуске через `unity command run_script`, поэтому ошибки в них сборка проекта не показывает.

- Многие пробы читают закрытые поля и методы через рефлексию по имени: `ShooterHud.markerStart`, `KillFeed.lines`, `ArenaNavMesh.seamLinks`, `RaidDirector.ReturnToBase`, `Profile.data` и другие. Перед переименованием ищи имя и в `Tools/Unity`.
- `Setup*` пишут сериализуемые поля через `SerializedObject.FindProperty("имя")`.
- Генератор карты сверяется `ProbeLayoutHash.cs`: хэши для сидов 1, 2, 3, 42 не должны меняться от правок без изменения карты.

## Проверка после правки кода

Нужен открытый Unity Editor с этим проектом. Запуск из корня проекта:

- `powershell -NoProfile -File Tools\Unity\CheckCompile.ps1` — обновляет ассеты (у новых `.cs` появляются `.meta`), перекомпилирует проект и печатает ошибки компиляции. Код выхода 1 — есть ошибки.
- `powershell -NoProfile -File Tools\Unity\CheckTools.ps1 [Tools/Unity/X.cs ...]` — сухая компиляция скриптов `Tools/Unity` (`run_script --dry_run`, ничего не запускается). Без аргументов проверяет все, это около 5 минут. Нужен после переименований в коде игры. Ссылки через рефлексию так не проверить.
- В Play: `ProbeLoop.cs`, `ProbeLoopStep.cs`, `ProbeRaidRules.cs` (вылазка). После `editor_play` нужен `editor_focus`, а на время проб — `unity command set_autotick --enabled true`, иначе Editor в фоне почти не идёт.

## Правила кода

- Один тип — один файл. Большой компонент делится на обычные C# классы (`RaidHud`, `ShooterHud`), Burst-джобы и `VoxelArena` — на partial-файлы по шагам.
- `[SerializeField]` поля без модификатора (private), доступ снаружи — через свойства. При переименовании сериализуемого поля нужен `[FormerlySerializedAs]`.
- Файлы в UTF-8 с LF в рабочей копии (git переводит в CRLF), комментарии и строки на русском.
- Комментарии объясняют «почему». У каждого класса — `<summary>` о его роли в игре.
- Fail Fast: обязательные ссылки на null не проверяются.
- WebGL: не вешать клавиши на F1–F12, их забирает браузер.
