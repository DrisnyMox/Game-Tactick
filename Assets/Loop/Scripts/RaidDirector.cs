using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Вылазка: сцена Raid. Каждая вылазка — новая карта (случайный сид арены).
///
/// Начало: снаряжение из Profile.Carried ложится в рюкзак игрока (бронежилет — больше здоровья, большой магазин —
/// больше патронов в магазине), перезарядка берёт патроны из рюкзака.
/// После генерации карты: игрок встаёт на базу один, рейдеры — по карте не ближе minRaiderDistance от него.
/// Остров (IslandLayout): база один — причал высадки, лучи эвакуации — на трёх дальних причалах, ящики — в руинах
/// и остовах, за краем карты океан, глубже мелководья игрока не пускает. Арена RTS: лучи — база два и две
/// дальние точки захвата, ящики — у остальных точек. В двух ящиках чертежи.
///
/// Во время вылазки: выпавший рюкзак становится мешком (Pickup) на месте; уничтоженный или эвакуированный
/// рейдер через raiderRespawnDelay выходит на карту новым, с аптечкой и двумя гранатами. H — аптечка, G — граната
/// (GrenadeThrower берёт гранаты из рюкзака). Бег игрока шумит (Noise).
/// Игрок эвакуировался — рюкзак уходит на склад (Profile.Extract), уничтожен — рюкзак потерян, кроме защищённого
/// кармана (ProtectedPocket, Profile.Lose).
/// Итог на экране, затем сцена Base.
///
/// HUD рисует RaidHud, места добычи рейдеров — RaidGatherSpots.
/// Компонент и ссылки ставит Tools/Unity/SetupRaid.cs.
/// </summary>
[RequireComponent(typeof(VoxelArena))]
public class RaidDirector : MonoBehaviour
{
    /// <summary>Вылазка в этой сцене; null — сцена без вылазки (бой на арене)</summary>
    public static RaidDirector Current { get; private set; }

    [SerializeField] CharacterHealth player;
    [Header("Материалы")]
    [SerializeField] Material beamMaterial;
    [SerializeField] Material ringMaterial;
    [SerializeField] Material bagMaterial;
    [SerializeField] Material crateMaterial;
    [SerializeField] Material blueprintMaterial;
    [Tooltip("Океан за краем карты острова")]
    [SerializeField] Material oceanMaterial;
    [Header("Правила")]
    [SerializeField] float armorHealth = 150f;
    [SerializeField] int bigMagazineSize = 45;
    [SerializeField] float raiderRespawnDelay = 60f;
    [Tooltip("Ближе этого к игроку рейдеры на карту не выходят, м")]
    [SerializeField] float minRaiderDistance = 50f;
    [SerializeField] int blueprintCrates = 2;
    [Tooltip("Остров: сколько ящиков лежит просто на песке, кроме ящиков в руинах и остовах")]
    [SerializeField] int sandCrates = 6;
    [Tooltip("Ящики на песке не ближе этого друг к другу, к другим ящикам и к высадке, м")]
    [SerializeField] float sandCrateSpacing = 25f;
    [Tooltip("Защищённый карман: сколько штук самого дорогого материала остаётся, если игрока уничтожат (чертёж — всегда один)")]
    [SerializeField] int pocketMaterials = 5;
    [Tooltip("Насколько выше поверхности ставить персонажа, м")]
    [SerializeField] float lift = 0.1f;

    enum State { Playing, Extracted, Lost }

    VoxelArena arena;
    Backpack playerBackpack;
    SurvivorShooter playerShooter;
    StarterAssets.StarterAssetsInputs playerInput;
    CharacterController playerController;
    RaidHud hud;
    float nextStepNoise;
    readonly List<CharacterHealth> raiders = new();
    readonly Dictionary<CharacterHealth, float> respawnTimes = new();
    readonly List<CharacterHealth> due = new();
    // Сколько патронов и гранат кладёт отладочная клавиша P
    const int DebugAmmo = 100;
    const int DebugGrenades = 10;
    // Сколько гранат у нового рейдера
    const int RaiderGrenades = 2;
    // Игрок уничтожен: итог через столько секунд, когда тело уже упало
    const float LostSummaryDelay = 2.5f;

    // Клетки раскладки, где можно выйти на карту: суша без воды, деревьев и камней, вне баз
    readonly List<int2> spawnCells = new();
    ItemCounts playerDrop;
    ItemCounts playerKept;
    State state;
    float endTime;
    Vector3 lastDry;
    float lastDeepNotice = float.NegativeInfinity;
    // По мелководью архипелага (1–3 вокселя воды) ходить можно, глубже — нет
    const int maxWadeVoxels = 4;

    /// <summary>Места добычи рейдеров на этой карте</summary>
    public RaidGatherSpots GatherSpots { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Current = null;

    void Awake()
    {
        Current = this;
        useGUILayout = false;
        arena = GetComponent<VoxelArena>();
        GatherSpots = new RaidGatherSpots(arena);
        // Каждая вылазка — новая карта. Сид ставится до VoxelArena.Start, где начинается генерация
        arena.Seed = UnityEngine.Random.Range(1, int.MaxValue);
        arena.Generated += Arena_Generated;
        Backpack.Dropped += Backpack_Dropped;
        ExtractionZone.Extracted += Zone_Extracted;
    }

    void Start()
    {
        playerBackpack = player.GetComponent<Backpack>();
        playerShooter = player.GetComponent<SurvivorShooter>();
        playerInput = player.GetComponent<StarterAssets.StarterAssetsInputs>();
        playerController = player.GetComponent<CharacterController>();
        hud = new RaidHud(player, playerBackpack, pocketMaterials);
        playerBackpack.Gained += PlayerBackpack_Gained;
        Noise.Made += Noise_Made;
        player.Died += Player_Died;

        var carried = Profile.TakeCarriedForRaid();
        playerBackpack.Load(carried);
        if (carried[ItemId.Armor] > 0)
            player.SetMaxHealth(armorHealth);
        if (carried[ItemId.BigMagazine] > 0)
            playerShooter.MagazineSize = bigMagazineSize;
        playerShooter.Reserve = playerBackpack;
        player.GetComponent<GrenadeThrower>().Supply = playerBackpack;

        foreach (var character in CharacterHealth.All)
        {
            if (character == player)
                continue;
            character.GetComponent<GrenadeThrower>().Supply = character.GetComponent<Backpack>();
            if (!character.GetComponent<ShooterBot>().Raider)
                Debug.LogError($"[Raid] {character.name}: ShooterBot не в режиме рейдера. Запусти Tools/Unity/SetupRaid.cs.", character);
            // Каждый рейдер сам за себя: своя команда, стреляет и в игрока, и в других рейдеров, слышит их шум
            character.SetTeam(player.Team + 1 + raiders.Count);
            raiders.Add(character);
            character.Died += Raider_Died;
        }
    }

    void OnDestroy()
    {
        if (Current == this)
            Current = null;
        arena.Generated -= Arena_Generated;
        Backpack.Dropped -= Backpack_Dropped;
        Noise.Made -= Noise_Made;
        ExtractionZone.Extracted -= Zone_Extracted;
    }

    // ---------- Карта ----------

    void Arena_Generated()
    {
        var layout = arena.Layout;
        GatherSpots.Collect(layout);
        CollectSpawnCells(layout);

        // Игрок — на базе один лицом к воротам
        var look = arena.GateOne - arena.BaseOne;
        look.y = 0f;
        player.Revive(Ground(arena.BaseOne) + Vector3.up * lift, Quaternion.LookRotation(look));
        lastDry = player.transform.position;
        // Revive зарядил полный магазин даром, а первый магазин тоже берётся из рюкзака
        playerShooter.SetLoaded(playerBackpack.TakeAmmo(playerShooter.MagazineSize));

        // Остров: эвакуация — 3 причала, дальние от высадки и не рядом друг с другом; ящики — в руинах, остовах и на песке.
        // Арена RTS: эвакуация — база два и две дальние точки захвата, ящики — у остальных точек
        var exitCandidates = new List<Vector3>();
        var crates = new List<Vector3>();
        var exits = new List<Vector3>();
        bool island = layout.seaLevel > 0;
        foreach (var resource in layout.resources)
        {
            if (resource.kind == ResourceKind.Landing)
            {
                if (!resource.cell.Equals(layout.baseOne))
                    exitCandidates.Add(arena.CellTop(resource.cell));
            }
            else if (resource.kind == ResourceKind.CapturePoint)
                exitCandidates.Add(arena.CellTop(resource.cell));
            else
                crates.Add(SiteFloor(resource));
        }
        if (island)
            AddSandCrates(layout, crates);
        if (!island)
            exits.Add(Ground(arena.BaseTwo));
        exitCandidates.Sort((a, b) => Flat(b - arena.BaseOne).sqrMagnitude.CompareTo(Flat(a - arena.BaseOne).sqrMagnitude));
        foreach (var point in exitCandidates)
        {
            if (exits.Count >= 3)
                break;
            bool apart = true;
            foreach (var exit in exits)
                apart &= Flat(point - exit).magnitude > 40f;
            if (apart)
                exits.Add(point);
        }
        for (int i = 0; i < exits.Count; i++)
            ExtractionZone.Spawn(exits[i], $"Extraction {i + 1}", beamMaterial, ringMaterial);
        // Ящик сбоку от рудного бугра, а не на нём
        if (!island)
            foreach (var point in exitCandidates)
                if (!exits.Contains(point))
                    crates.Add(Ground(point + new Vector3(3f, 0f, 0f)));

        // Ящики: в первых blueprintCrates — чертёж (по очереди бронежилет и магазин), в остальных патроны и аптечка
        Shuffle(crates);
        for (int i = 0; i < crates.Count; i++)
        {
            var items = new ItemCounts();
            bool blueprint = i < blueprintCrates;
            if (blueprint)
                items[i % 2 == 0 ? ItemId.ArmorBlueprint : ItemId.BigMagazineBlueprint] = 1;
            else
            {
                items[ItemId.Ammo] = 30;
                items[ItemId.Medkit] = UnityEngine.Random.value < 0.5f ? 1 : 0;
            }
            Pickup.Spawn(crates[i], items, blueprint ? "чертёж" : "ящик", blueprint ? blueprintMaterial : crateMaterial, blueprint ? 0.6f : 0.8f);
        }

        if (island)
            SpawnOcean(layout);

        foreach (var raider in raiders)
            SendRaider(raider);
    }

    // Ящики просто на песке: голые клетки суши (без дерева, камня и травы) не ближе sandCrateSpacing
    // друг к другу, к ящикам построек и к высадке игрока
    void AddSandCrates(ArenaLayout layout, List<Vector3> crates)
    {
        for (int attempt = 0, added = 0; attempt < 300 && added < sandCrates; attempt++)
        {
            var cell = spawnCells[UnityEngine.Random.Range(0, spawnCells.Count)];
            if ((layout.flags[cell.y * layout.sizeX + cell.x] & ArenaLayout.FlagSand) == 0)
                continue;
            var point = arena.CellTop(cell);
            bool apart = Flat(point - arena.BaseOne).magnitude > sandCrateSpacing;
            foreach (var crate in crates)
                apart &= Flat(point - crate).magnitude > sandCrateSpacing;
            if (!apart)
                continue;
            crates.Add(point);
            added++;
        }
    }

    // Пол посередине руин или палуба остова: обе постройки стоят центром на точке
    Vector3 SiteFloor(ResourcePoint site)
    {
        return arena.VoxelToWorld(RaidGatherSpots.SiteRoot(arena, site.cell) + new float3(0.5f, 0f, 0.5f));
    }

    // Океан за краем карты: четыре полосы на уровне воды вокруг арены. Внутри карты их нет:
    // сквозь воду мелководья видно песчаное дно
    void SpawnOcean(ArenaLayout layout)
    {
        float y = arena.VoxelToWorld(new float3(0f, layout.seaLevel * arena.CellVoxels - layout.waterDropVoxels, 0f)).y - 0.03f;
        var b = arena.WorldBounds;
        const float far = 2000f;
        OceanStrip(new Vector3(b.min.x - far, y, b.max.z), new Vector3(b.max.x + far, y, b.max.z + far));
        OceanStrip(new Vector3(b.min.x - far, y, b.min.z - far), new Vector3(b.max.x + far, y, b.min.z));
        OceanStrip(new Vector3(b.min.x - far, y, b.min.z), new Vector3(b.min.x, y, b.max.z));
        OceanStrip(new Vector3(b.max.x, y, b.min.z), new Vector3(b.max.x + far, y, b.max.z));
    }

    void OceanStrip(Vector3 min, Vector3 max)
    {
        // Plane Unity — 10×10 м
        var strip = GameObject.CreatePrimitive(PrimitiveType.Plane);
        strip.name = "Ocean";
        Destroy(strip.GetComponent<Collider>());
        strip.transform.position = (min + max) * 0.5f;
        strip.transform.localScale = new Vector3((max.x - min.x) / 10f, 1f, (max.z - min.z) / 10f);
        var renderer = strip.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = oceanMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    // Клетки, где голая земля: поверхность столбца на высоте раскладки (нет дерева или камня сверху), не вода,
    // не база. Каждая четвёртая по обеим осям — хватает и быстро
    void CollectSpawnCells(ArenaLayout layout)
    {
        spawnCells.Clear();
        int k = arena.CellVoxels;
        for (int z = 4; z < layout.sizeZ - 4; z += 4)
        {
            for (int x = 4; x < layout.sizeX - 4; x += 4)
            {
                int i = z * layout.sizeX + x;
                if (layout.baseArea[i] != 0 || layout.waterTop[i] != 0 || (layout.flags[i] & ArenaLayout.FlagRock) != 0)
                    continue;
                int vx = x * k + k / 2, vz = z * k + k / 2;
                if (arena.IsWaterAt(vx, vz) || arena.SurfaceY(vx, vz) != arena.GroundY(vx, vz))
                    continue;
                spawnCells.Add(new int2(x, z));
            }
        }
    }

    // Рейдер выходит на карту: случайная голая клетка не ближе minRaiderDistance к игроку, пустой рюкзак с аптечкой
    void SendRaider(CharacterHealth raider)
    {
        var position = arena.BaseTwo;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            var candidate = arena.CellTop(spawnCells[UnityEngine.Random.Range(0, spawnCells.Count)]);
            bool apart = Flat(candidate - player.transform.position).magnitude >= minRaiderDistance;
            // Рейдеры воюют друг с другом: новый не выходит на карту в упор к другому
            foreach (var other in raiders)
                apart &= other == raider || other.IsDead || !other.gameObject.activeInHierarchy
                         || Flat(candidate - other.transform.position).magnitude >= minRaiderDistance * 0.6f;
            if (apart)
            {
                position = candidate;
                break;
            }
        }
        var kit = new ItemCounts();
        kit[ItemId.Medkit] = 1;
        kit[ItemId.Grenade] = RaiderGrenades;
        raider.GetComponent<Backpack>().Load(kit);
        raider.Revive(position + Vector3.up * lift, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
        raider.GetComponent<ShooterBot>().BeginRaid();
    }

    /// <summary>Ближайшая к from точка эвакуации</summary>
    public Vector3 NearestExtraction(Vector3 from)
    {
        var best = arena.BaseTwo;
        float bestDistance = float.MaxValue;
        foreach (var zone in ExtractionZone.All)
        {
            float d = Flat(zone.transform.position - from).sqrMagnitude;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = zone.transform.position;
            }
        }
        return best;
    }

    // Верх земли под точкой: луч сверху по слою Default (арена)
    Vector3 Ground(Vector3 point)
    {
        if (Physics.Raycast(point + Vector3.up * 15f, Vector3.down, out var hit, 40f, 1, QueryTriggerInteraction.Ignore))
            return hit.point;
        return point;
    }

    internal static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

    static void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // ---------- События ----------

    void Backpack_Dropped(Backpack backpack, Vector3 chest, ItemCounts items)
    {
        // Защищённый карман игрока остаётся с ним, на землю падает остальное
        if (backpack.Owner == player)
        {
            playerKept = ProtectedPocket.Of(items, pocketMaterials);
            for (int i = 0; i < Items.Count; i++)
                items[(ItemId)i] -= playerKept[(ItemId)i];
            playerDrop = items;
        }
        if (!items.IsEmpty)
            Pickup.Spawn(Ground(chest), items, "рюкзак " + backpack.Owner.name, bagMaterial, 0.55f);
    }

    void Player_Died(CharacterHealth character)
    {
        if (state != State.Playing)
            return;
        state = State.Lost;
        endTime = Time.time;
        // Обработчик рюкзака подписан раньше (Awake) и уже выложил его содержимое в playerDrop и карман в playerKept
        Profile.Lose(playerDrop ?? new ItemCounts(), playerKept ?? new ItemCounts());
    }

    void Raider_Died(CharacterHealth raider) => respawnTimes[raider] = Time.time + raiderRespawnDelay;

    void Noise_Made(Vector3 position, float radius, CharacterHealth source)
    {
        if (source == player)
            hud.ShowNoise();
    }

    // Бег игрока шумит: пока он бежит по земле, шаг слышно на Noise.SprintRadius. Шагом — тихо
    void MakeStepNoise()
    {
        var velocity = playerController.velocity;
        velocity.y = 0f;
        if (!playerInput.sprint || !playerController.isGrounded || velocity.magnitude < 3f || Time.time < nextStepNoise)
            return;
        nextStepNoise = Time.time + 0.4f;
        Noise.Make(player.transform.position, Noise.SprintRadius, player);
    }

    void Zone_Extracted(ExtractionZone zone, CharacterHealth character)
    {
        if (character == player)
        {
            if (state != State.Playing)
                return;
            state = State.Extracted;
            endTime = Time.time;
            // Патроны из магазина тоже уходят с вылазки
            playerBackpack.Add(ItemId.Ammo, playerShooter.Ammo);
            playerShooter.SetLoaded(0);
            Profile.Extract(playerBackpack.Contents);
            playerBackpack.Load(new ItemCounts());
            LoopGui.SetModal(true, player.gameObject);
            return;
        }
        hud.Notify($"{character.name} эвакуировался с добычей");
        character.Withdraw();
        respawnTimes[character] = Time.time + raiderRespawnDelay;
    }

    void PlayerBackpack_Gained(ItemId item, int count) => hud.AddToast(item, count);

    // ---------- Кадр ----------

    void Update()
    {
        due.Clear();
        foreach (var pair in respawnTimes)
            if (Time.time >= pair.Value)
                due.Add(pair.Key);
        foreach (var raider in due)
        {
            respawnTimes.Remove(raider);
            SendRaider(raider);
            hud.Notify("на карту вышел новый рейдер");
        }

        if (state == State.Playing && Keyboard.current.hKey.wasPressedThisFrame)
            playerBackpack.UseMedkit();
        // Отладка: P кладёт патроны и гранаты в рюкзак. Только в редакторе и в development-сборке
        if (Debug.isDebugBuild && state == State.Playing && Keyboard.current.pKey.wasPressedThisFrame)
        {
            playerBackpack.Add(ItemId.Ammo, DebugAmmo);
            playerBackpack.Add(ItemId.Grenade, DebugGrenades);
            hud.Notify($"отладка: +{DebugAmmo} патронов, +{DebugGrenades} гранат");
        }
        // Сбитый взрывом лежит рэгдоллом, его корень стоит на месте: возврат из глубины подождёт подъёма
        if (state == State.Playing && !arena.IsGenerating && arena.Layout.seaLevel > 0 && !player.KnockedDown)
            KeepOutOfDeepWater();
        if (state == State.Playing && !arena.IsGenerating)
            MakeStepNoise();

        // Уничтожен: итог через пару секунд, когда тело уже упало
        if (state == State.Lost && !LoopGui.ModalOpen && Time.time - endTime > LostSummaryDelay)
            LoopGui.SetModal(true, player.gameObject);

        if (LoopGui.ModalOpen && Keyboard.current.enterKey.wasPressedThisFrame)
            ReturnToBase();
    }

    // Плавать нельзя: по мелководью игрок ходит, а глубже maxWadeVoxels его возвращает на последнее место,
    // где воды не было
    void KeepOutOfDeepWater()
    {
        var position = player.transform.position;
        var voxel = (int3)math.floor(arena.WorldToVoxel(position));
        var dims = arena.Dims;
        int depth = 0;
        if (voxel.x >= 0 && voxel.z >= 0 && voxel.x < dims.x && voxel.z < dims.z)
            for (int y = arena.SurfaceY(voxel.x, voxel.z); arena.GetBlock(new int3(voxel.x, y, voxel.z)) == VoxelBlocks.Water; y++)
                depth++;
        else
            depth = int.MaxValue;

        if (depth == 0)
        {
            lastDry = position;
            return;
        }
        if (depth < maxWadeVoxels)
            return;
        var controller = player.GetComponent<CharacterController>();
        controller.enabled = false;
        player.transform.position = lastDry;
        controller.enabled = true;
        if (Time.time - lastDeepNotice > 3f)
        {
            lastDeepNotice = Time.time;
            hud.Notify("дальше глубоко: без лодки не уплыть");
        }
    }

    void ReturnToBase()
    {
        LoopGui.SetModal(false, player.gameObject);
        SceneManager.LoadScene(SceneNames.Base);
    }

    // ---------- HUD ----------

    void OnGUI()
    {
        if (ArenaLoadingScreen.IsShown || SettingsMenu.IsOpen)
            return;
        if (state == State.Playing)
            hud.Draw();
        else if (!(state == State.Lost && Time.time - endTime <= LostSummaryDelay) && hud.DrawSummary())
            ReturnToBase();
    }
}
