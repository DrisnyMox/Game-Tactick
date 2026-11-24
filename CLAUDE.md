# CLAUDE.md

Устройство игры, сцены, управление и направление — в [README.md](README.md). Здесь только то, чего не видно сразу из кода.

## Карта

Стартовая сцена — `Base` (первая в Build Settings), дальше `Raid`. `SampleScene` — бой 5 на 5 на арене.

- **Цикл «база → вылазка → база»** — `Assets/Loop/Scripts`.
  - `BaseHub` + `BaseStation`: база, окна склада и торговца (IMGUI). Верстак — своё окно на Canvas `WorkbenchScreen` (`WorkbenchCardView`, `WorkbenchCostView`), крафт и шанс цвета качества от уровня комплекта — `Crafting`. Собирает `Tools/Unity/SetupWorkbench.cs` (после `SetupEquipment.cs`), проверка — `ProbeWorkbench.cs`.
  - `RaidDirector`: правила вылазки (старт, рейдеры, эвакуация, глубокая вода). HUD рисует `RaidHud`, места добычи рейдеров — `RaidGatherSpots`, карман — `ProtectedPocket`.
  - `Profile`: сохранение в PlayerPrefs (JSON), итог вылазки `Profile.LastRaid`. Предметы — `ItemId`, `Items`, `ItemCounts`, `Recipe`, `Trader`. Имена сцен — `SceneNames`.
  - `Backpack`, `Pickup`, `ExtractionZone`, `Noise`, `LoopGui`.
  - Снаряжение: части — `GearPiece` в `Profile.gear`, свойства — `Gear`, модели — `GearKind` (номера в сохранениях), слоты — `GearSlot`, цвет качества — `GearQuality` (номера в сохранениях), комплекты — `GearSet` (номера в сохранениях, опыт — `Profile.setXp`), бесплатный серый базовый набор, который не теряется, — `Gear.IsFree`, рецепты верстака — `GearRecipe`. Цвет качества на игроке красит шейдер `Game Tactick/Gear` (`Assets/Models/Gear/Shaders`) по маске из `Tools/Blender/quality_mask.py`, иконки — по одной на часть и цвет. Окно на базе (I) — `EquipmentScreen` с `GearSlotView`, `GearCardView` и игроком `OutfitPreview`: CinemachineCamera «Outfit Camera» (облёт от камеры игры к лицу и обратно делает сам `OutfitPreview`, у Cinemachine переход мгновенный), игрок на время окна на слое `Preview` (7), его рисует Overlay-камера из стека Main Camera поверх фона «Equipment Backdrop Canvas» (Screen Space - Camera). Собирает `Tools/Unity/SetupEquipment.cs`, проверка — `ProbeEquipment.cs` и `ProbeEquipmentBlend.cs` (облёт).
- **Персонаж** — `Assets/Characters/Survivor/Scripts`, поверх `ThirdPersonController` из Starter Assets.
  - `SurvivorShooter`: стрельба, прицел, отдача. HUD игрока — `ShooterHud`, кисти на рукояти и цевье — `TwoBoneIK` (точки `rightHandIK` и `leftHandIK` на винтовке считает `SetupScrapRifle.cs`).
  - `CharacterHealth`: здоровье, команда, рэгдолл, события `Died` и `Killed`, сбит с ног взрывом (`KnockedDown`) и подъём (`RagdollGetUp`); удары костей о блоки — `RagdollImpact` (вешается в `Awake`). `ShooterBot`: бот и рейдер. `ShooterWeapon`: точки на винтовке и эффекты выстрела.
  - `GrenadeThrower`: бросок (игрок и `ShooterBot.TryGrenade`), запас — `Backpack` или без счёта. `Grenade`: полёт и взрыв (`VoxelArena.Explode`, `CharacterHealth.OnBlast`). Префаб — `SetupGrenade.cs`.
- **Арена** — `Assets/VoxelArena/Scripts`.
  - `VoxelArena` (partial): `VoxelArena.cs` — состояние и API вокселей, `.Generation.cs` — генерация, `.Meshing.cs` — меши и коллайдеры чанков.
  - Раскладка: `ArenaLayoutGenerator` (partial, шаги Burst-джобы `BuildLayoutJob` по файлам) для арены и `IslandLayout` для острова. Обе выдают `ArenaLayout`.
  - `ArenaDecorator`: постройки и деревья в вокселях. `ArenaNavMesh`: NavMesh по чанкам. Джобы мешей — в `Jobs/`.
- **UI** — `Assets/Scripts/UI`: `ArenaLoadingScreen`, `SettingsMenu` (Esc), `FpsCounter`.

## Связи из инспектора и настроек

- Игрок в сценах — вариант `PlayerSurvivor8b8745d5` (другая модель), боты и рейдеры — `PlayerSurvivor`. Компоненты правятся в `PlayerSurvivor`, вариант их наследует; кости, меш, аватар, рэгдолл и `weaponLocalPosition`/`weaponLocalEuler` у варианта свои (`SetupPlayerModel.cs`). Пробы ищут игрока по имени `PlayerSurvivor`, это имя объекта в сцене.
- Снаряжение на игроке: каждая часть (`Assets/Models/Gear`) — дочерний `SkinnedMeshRenderer` варианта, последним среди детей: `SetupPlayerModel.cs` берёт тело как первый `SkinnedMeshRenderer`. `WornGear` прячет треугольники тела под надетыми частями. Ставит части `Tools/Unity/SetupGear.cs`.
- Кожу в игре считают 2 кости на вершину: качество Mobile (Editor и WebGL) — `skinWeights` 2. Подгонка и замер снаряжения в Blender повторяют это (`gear_poses.SKIN_BONES`).
- `PlayerInput` на `PlayerSurvivor` работает в режиме Send Messages. Он вызывает по имени `OnAim`, `OnFire`, `OnReload` у `SurvivorShooter`, `OnGrenade` у `GrenadeThrower` и `OnMove`, `OnLook` и другие у `StarterAssetsInputs`. Эти методы не переименовывать. Записи `InputMove`/`InputLook` в Action Events префаба — старые, в режиме Send Messages не работают.
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
- В Play: `ProbeLoop.cs`, `ProbeLoopStep.cs`, `ProbeRaidRules.cs` (вылазка), `ProbeGrenade.cs` (граната). После `editor_play` нужен `editor_focus`, а на время проб — `unity command set_autotick --enable true` и `Application.runInBackground = true` (пробы ставят его сами), иначе Editor в фоне почти не идёт: без `runInBackground` Play стоит на втором кадре.
- Снаряжение: перед работой с надеваемыми частями читать `Docs/Gear.md` (что вышло с `ClothChest`, `ClothGloves`, `ClothBoots`, `NomadBoots`, `ClothPantsRope`, `NomadChest`, `NomadPants`, `NomadGloves`, `SunPants` и на чём уже ошиблись). Вырезание из тела: часть вырезается из тела модели генератора (персонажа в этой части), затем надевается на тело игрока (штаны — `Tools/Blender/place_pants.py`, верх с накидкой — `Tools/Blender/place_chest.py`, перчатки, одну из которых подогнал пользователь, — `Tools/Blender/place_gloves.py`, вторая отражением). Общие шаги посадки — `Tools/Blender/gear_fit.py`, порядок работы с новой частью — скилл `.claude/skills/gear-fit`. Ещё есть подгонка `Tools/Blender/fit_gear.py`, оболочка из тела игрока с запечённым рисунком `Tools/Blender/cut_gear.py` (перчатки) и жёсткая часть без смены формы `Tools/Blender/place_gear.py`, затем `SetupGear.cs` (ставит все части, включены в префабе — его `Parts`; на базе и в вылазке надетое решает профиль); в Play сцены `SurvivorPlayground` — `ProbeGear.cs` (пишет позы игры в `Tools/Blender/gear_poses.json`), затем замер `Tools/Blender/measure_gear.py` (кожа сквозь часть, дыры, совпадение мешей с игрой; `--faces` — кожа сквозь грани между вершинами) и снимки вблизи `ProbeGearShots.cs`, для обуви и штанов (с поясом) `ProbeGearFeetShots.cs` (`Temp/GearShots`). Blender — `E:\Software\Blender\blender.exe --background --factory-startup --python`. Системного Python нет (`python` — заглушка Windows), скрипты — через Blender.
- Вылазка в Play забирает рюкзак профиля (`Profile.TakeCarriedForRaid`) и сохраняет. Перед пробами в `Raid` копировать ключ `Profile.v1` в PlayerPrefs и возвращать после.

## Правила кода

- Один тип — один файл. Большой компонент делится на обычные C# классы (`RaidHud`, `ShooterHud`), Burst-джобы и `VoxelArena` — на partial-файлы по шагам.
- `[SerializeField]` поля без модификатора (private), доступ снаружи — через свойства. При переименовании сериализуемого поля нужен `[FormerlySerializedAs]`.
- Файлы в UTF-8 с LF в рабочей копии (git переводит в CRLF), комментарии и строки на русском.
- Комментарии объясняют «почему». У каждого класса — `<summary>` о его роли в игре.
- Fail Fast: обязательные ссылки на null не проверяются.
- WebGL: не вешать клавиши на F1–F12, их забирает браузер.
