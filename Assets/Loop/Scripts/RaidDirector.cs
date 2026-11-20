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
/// рейдер через raiderRespawnDelay выходит на карту новым, с пустым рюкзаком. H — аптечка. Бег игрока шумит (Noise).
/// Игрок эвакуировался — рюкзак уходит на склад (Profile.Extract), уничтожен — рюкзак потерян, кроме защищённого
/// кармана (PocketOf, Profile.Lose).
/// Итог на экране, затем сцена Base.
///
/// HUD: метки эвакуации с расстоянием, отсчёт в круге, рюкзак, подписи добычи, всплывающие «+1 Дерево».
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

    struct GatherSpot
    {
        public int3 min, max;
    }

    VoxelArena arena;
    Backpack playerBackpack;
    SurvivorShooter playerShooter;
    StarterAssets.StarterAssetsInputs playerInput;
    CharacterController playerController;
    float nextStepNoise;
    // Игрок шумел (выстрел или бег): HUD показывает «шум» до этого времени
    float noisyUntil;
    readonly List<CharacterHealth> raiders = new();
    readonly Dictionary<CharacterHealth, float> respawnTimes = new();
    readonly List<CharacterHealth> due = new();
    // Сколько патронов кладёт отладочная клавиша P
    const int DebugAmmo = 100;

    readonly List<GatherSpot> gatherSpots = new();
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

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Current = null;

    void Awake()
    {
        Current = this;
        useGUILayout = false;
        arena = GetComponent<VoxelArena>();
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

        foreach (var character in CharacterHealth.All)
        {
            if (character == player)
                continue;
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
        int k = arena.CellVoxels;
        CollectGatherSpots(layout, k);
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
                crates.Add(SiteFloor(layout, resource));
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
    Vector3 SiteFloor(ArenaLayout layout, ResourcePoint site)
    {
        return arena.VoxelToWorld(SiteRoot(layout, site.cell) + new float3(0.5f, 0f, 0.5f));
    }

    // Воксель на поверхности посередине клетки: от него ArenaDecorator строит постройки острова
    int3 SiteRoot(ArenaLayout layout, int2 cell)
    {
        int k = arena.CellVoxels;
        int x = cell.x * k + k / 2, z = cell.y * k + k / 2;
        return new int3(x, arena.GroundY(x, z), z);
    }

    static GatherSpot Box(int3 root, int3 min, int3 max) => new GatherSpot { min = root + min, max = root + max };

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

    // Деревья, камни, рудные бугры, руины и остовы: боксы вокселей, где рейдеры добывают. Причалы не трогают.
    // На острове постройки в вокселях (ArenaDecorator): у деревьев низ ствола до 2,3 м, где пальма ещё не клонится,
    // руины 27×19 со стенами 14, остов до 25 вокселей от центра в любую сторону
    void CollectGatherSpots(ArenaLayout layout, int k)
    {
        gatherSpots.Clear();
        bool island = layout.wadeableWater;
        foreach (var cell in layout.trees)
        {
            int h = layout.height[cell.y * layout.sizeX + cell.x];
            gatherSpots.Add(island
                ? Box(SiteRoot(layout, cell), new int3(-2, 0, -2), new int3(4, 10, 4))
                : new GatherSpot { min = new int3((cell.x - 2) * k, h * k, (cell.y - 2) * k), max = new int3((cell.x + 3) * k, (h + 8) * k, (cell.y + 3) * k) });
        }
        foreach (var cell in layout.rocks)
        {
            int h = layout.height[cell.y * layout.sizeX + cell.x];
            gatherSpots.Add(new GatherSpot { min = new int3((cell.x - 3) * k, h * k, (cell.y - 3) * k), max = new int3((cell.x + 4) * k, (h + 4) * k, (cell.y + 4) * k) });
        }
        foreach (var resource in layout.resources)
        {
            var cell = resource.cell;
            int h = layout.height[cell.y * layout.sizeX + cell.x];
            switch (resource.kind)
            {
                case ResourceKind.CapturePoint:
                    gatherSpots.Add(new GatherSpot { min = new int3((cell.x - 2) * k, (h - 1) * k, (cell.y - 2) * k), max = new int3((cell.x + 3) * k, h * k, (cell.y + 3) * k) });
                    break;
                case ResourceKind.Ruin:
                    gatherSpots.Add(Box(SiteRoot(layout, cell), new int3(-13, 0, -9), new int3(14, 15, 10)));
                    break;
                case ResourceKind.Wreck:
                    gatherSpots.Add(Box(SiteRoot(layout, cell), new int3(-25, -2, -25), new int3(26, 19, 26)));
                    break;
            }
        }
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

    const int NearSpots = 5;
    readonly int[] nearSpot = new int[NearSpots];
    readonly long[] nearDistance = new long[NearSpots];

    /// <summary>Случайное место добычи из 5 ближайших к from; -1 — мест не осталось</summary>
    public int RandomNearGatherSpot(Vector3 from)
    {
        if (gatherSpots.Count == 0)
            return -1;
        var voxel = (int3)math.floor(arena.WorldToVoxel(from));
        int found = 0;
        for (int i = 0; i < gatherSpots.Count; i++)
        {
            var d3 = (gatherSpots[i].min + gatherSpots[i].max) / 2 - voxel;
            long d = (long)d3.x * d3.x + (long)d3.z * d3.z;
            // Короткий список ближайших по возрастанию: новое место встаёт на своё место, последнее выпадает
            if (found == NearSpots && d >= nearDistance[NearSpots - 1])
                continue;
            int slot = found < NearSpots ? found++ : NearSpots - 1;
            while (slot > 0 && nearDistance[slot - 1] > d)
            {
                nearSpot[slot] = nearSpot[slot - 1];
                nearDistance[slot] = nearDistance[slot - 1];
                slot--;
            }
            nearSpot[slot] = i;
            nearDistance[slot] = d;
        }
        return nearSpot[UnityEngine.Random.Range(0, found)];
    }

    /// <summary>Верхний воксель-материал в месте добычи (в него стреляет рейдер); false — всё выбито</summary>
    public bool GatherTarget(int spot, out Vector3 point)
    {
        var box = gatherSpots[spot];
        for (int y = box.max.y - 1; y >= box.min.y; y--)
            for (int z = box.min.z; z < box.max.z; z++)
                for (int x = box.min.x; x < box.max.x; x++)
                {
                    if (!Items.FromBlock(arena.GetBlock(new int3(x, y, z)), out _))
                        continue;
                    point = arena.VoxelToWorld(new float3(x + 0.5f, y + 0.5f, z + 0.5f));
                    return true;
                }
        point = default;
        return false;
    }

    /// <summary>Место выбито: убрать из списка (последнее встаёт на его номер)</summary>
    public void RemoveGatherSpot(int spot)
    {
        gatherSpots[spot] = gatherSpots[^1];
        gatherSpots.RemoveAt(gatherSpots.Count - 1);
    }

    // Верх земли под точкой: луч сверху по слою Default (арена)
    Vector3 Ground(Vector3 point)
    {
        if (Physics.Raycast(point + Vector3.up * 15f, Vector3.down, out var hit, 40f, 1, QueryTriggerInteraction.Ignore))
            return hit.point;
        return point;
    }

    static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

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
            playerKept = PocketOf(items);
            for (int i = 0; i < Items.Count; i++)
                items[(ItemId)i] -= playerKept[(ItemId)i];
            playerDrop = items;
        }
        if (!items.IsEmpty)
            Pickup.Spawn(Ground(chest), items, "рюкзак " + backpack.Owner.name, bagMaterial, 0.55f);
    }

    /// <summary>
    /// Защищённый карман игрока: что из рюкзака не выпадает, если персонажа уничтожат. Один чертёж, а без чертежа —
    /// до pocketMaterials штук самого дорогого материала
    /// </summary>
    ItemCounts PocketOf(ItemCounts items)
    {
        var pocket = new ItemCounts();
        for (int i = 0; i < Items.Count; i++)
        {
            var id = (ItemId)i;
            if (Items.IsBlueprint(id) && items[id] > 0)
            {
                pocket[id] = 1;
                return pocket;
            }
        }
        ItemId best = default;
        int bestPrice = 0;
        for (int i = 0; i < Items.Count; i++)
        {
            var id = (ItemId)i;
            if (Items.IsMaterial(id) && items[id] > 0 && Items.SellPrice(id) > bestPrice)
            {
                best = id;
                bestPrice = Items.SellPrice(id);
            }
        }
        if (bestPrice > 0)
            pocket[best] = Mathf.Min(pocketMaterials, items[best]);
        return pocket;
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
            noisyUntil = Time.time + 1f;
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
        Notify($"{character.name} эвакуировался с добычей");
        character.Withdraw();
        respawnTimes[character] = Time.time + raiderRespawnDelay;
    }

    void PlayerBackpack_Gained(ItemId item, int count)
    {
        // Подряд идущие «+1 Дерево» складываются в одну строку
        if (toasts.Count > 0 && toasts[^1].item == item && Time.time - toasts[^1].time < 1.5f)
        {
            var last = toasts[^1];
            last.count += count;
            last.time = Time.time;
            last.text = $"+{last.count} {Items.Name(item)}";
            toasts[^1] = last;
            return;
        }
        toasts.Add(new Toast { item = item, count = count, time = Time.time, text = $"+{count} {Items.Name(item)}" });
        if (toasts.Count > 5)
            toasts.RemoveAt(0);
    }

    void Notify(string text)
    {
        notices.Add((text, Time.time));
        if (notices.Count > 4)
            notices.RemoveAt(0);
    }

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
            Notify("на карту вышел новый рейдер");
        }

        if (state == State.Playing && Keyboard.current.hKey.wasPressedThisFrame)
            playerBackpack.UseMedkit();
        // Отладка: P кладёт патроны в рюкзак. Только в редакторе и в development-сборке
        if (Debug.isDebugBuild && state == State.Playing && Keyboard.current.pKey.wasPressedThisFrame)
        {
            playerBackpack.Add(ItemId.Ammo, DebugAmmo);
            Notify($"отладка: +{DebugAmmo} патронов");
        }
        if (state == State.Playing && !arena.IsGenerating && arena.Layout.seaLevel > 0)
            KeepOutOfDeepWater();
        if (state == State.Playing && !arena.IsGenerating)
            MakeStepNoise();

        // Уничтожен: итог через пару секунд, когда тело уже упало
        if (state == State.Lost && !LoopGui.ModalOpen && Time.time - endTime > 2.5f)
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
            Notify("дальше глубоко: без лодки не уплыть");
        }
    }

    void ReturnToBase()
    {
        LoopGui.SetModal(false, player.gameObject);
        SceneManager.LoadScene(SceneNames.Base);
    }

    // ---------- HUD ----------

    struct Toast
    {
        public ItemId item;
        public int count;
        public float time;
        public string text;
    }

    readonly List<Toast> toasts = new();
    readonly List<(string text, float time)> notices = new();
    static readonly Color ExtractColor = new(0.45f, 1f, 0.55f);
    static readonly Color PickupColor = new(1f, 0.85f, 0.4f);
    static readonly Color PocketColor = new(0.55f, 0.85f, 1f);

    void OnGUI()
    {
        if (ArenaLoadingScreen.IsShown || SettingsMenu.IsOpen)
            return;
        int font = LoopGui.FontSize;

        if (state != State.Playing)
        {
            DrawSummary(font);
            return;
        }

        var cam = Camera.main;
        DrawExtractionMarks(cam, font);
        DrawPickupLabels(cam, font);
        DrawBackpack(font);
        DrawNoise(font);
        DrawToasts(font);
        DrawNotices(font);
    }

    void DrawExtractionMarks(Camera cam, int font)
    {
        var style = LoopGui.Label(font, TextAnchor.LowerCenter);
        foreach (var zone in ExtractionZone.All)
        {
            var position = zone.transform.position;
            float distance = Flat(position - player.transform.position).magnitude;
            var screen = cam.WorldToScreenPoint(position + Vector3.up * 3f);
            if (screen.z > 0f)
                LoopGui.Shadowed(new Rect(screen.x - 150, Screen.height - screen.y - font * 2.5f, 300, font * 2.5f),
                    $"ЭВАКУАЦИЯ\n{Mathf.RoundToInt(distance)} м", style, ExtractColor);

            float held = zone.HeldBy(player);
            if (held >= 0f)
            {
                var center = LoopGui.Label(font * 2, TextAnchor.MiddleCenter);
                int left = Mathf.CeilToInt(zone.HoldTime - held);
                LoopGui.Shadowed(new Rect(0, Screen.height * 0.25f, Screen.width, font * 3), $"Эвакуация через {left}", center, ExtractColor);
            }
        }
    }

    void DrawPickupLabels(Camera cam, int font)
    {
        var style = LoopGui.Label(Mathf.RoundToInt(font * 0.8f), TextAnchor.LowerCenter);
        foreach (var pickup in Pickup.All)
        {
            var position = pickup.transform.position;
            if (Vector3.Distance(position, player.transform.position) > 30f)
                continue;
            var screen = cam.WorldToScreenPoint(position + Vector3.up * 1.4f);
            if (screen.z <= 0f)
                continue;
            LoopGui.Shadowed(new Rect(screen.x - 150, Screen.height - screen.y - font * 1.5f, 300, font * 1.5f), pickup.Label, style, PickupColor);
        }
    }

    void DrawBackpack(int font)
    {
        var style = LoopGui.Label(Mathf.RoundToInt(font * 0.8f), TextAnchor.LowerLeft);
        var items = playerBackpack.Contents;
        float line = font * 1.1f;
        // Над строкой здоровья (её рисует SurvivorShooter внизу слева)
        float y = Screen.height - 20 - font * 2.2f;
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            var id = (ItemId)i;
            if (items[id] <= 0 || id == ItemId.Ammo)
                continue;
            string text = id == ItemId.Medkit ? $"{Items.Name(id)} {items[id]}  [H]" : $"{Items.Name(id)} {items[id]}";
            LoopGui.Shadowed(new Rect(20, y - line, 400, line), text, style, Items.Tint(id));
            y -= line;
        }
        var color = playerBackpack.IsFull ? new Color(1f, 0.5f, 0.4f) : Color.white;
        string weight = playerBackpack.IsFull ? $"рюкзак полон {playerBackpack.Weight}/{playerBackpack.Capacity}" : $"рюкзак {playerBackpack.Weight}/{playerBackpack.Capacity}";
        LoopGui.Shadowed(new Rect(20, y - line, 400, line), weight, style, color);
        y -= line;
        // Что останется, если уничтожат
        var pocket = PocketOf(items);
        if (!pocket.IsEmpty)
            LoopGui.Shadowed(new Rect(20, y - line, 500, line), "защищённый карман: " + pocket.Describe(), style, PocketColor);
    }

    // Игрок только что шумел (выстрел, бег): рейдеры рядом могли услышать
    void DrawNoise(int font)
    {
        if (Time.time >= noisyUntil)
            return;
        var style = LoopGui.Label(Mathf.RoundToInt(font * 0.9f), TextAnchor.MiddleCenter);
        var color = new Color(1f, 0.8f, 0.4f, Mathf.Clamp01((noisyUntil - Time.time) / 0.5f));
        LoopGui.Shadowed(new Rect(0, Screen.height * 0.7f, Screen.width, font * 1.5f), "шум", style, color);
    }

    void DrawToasts(int font)
    {
        var style = LoopGui.Label(Mathf.RoundToInt(font * 0.9f), TextAnchor.LowerRight);
        float line = font * 1.2f;
        float y = Screen.height - 20 - font * 2.2f;
        for (int i = toasts.Count - 1; i >= 0; i--)
        {
            float age = Time.time - toasts[i].time;
            if (age > 2.5f)
            {
                toasts.RemoveAt(i);
                continue;
            }
            var color = Items.Tint(toasts[i].item);
            color.a = Mathf.Clamp01((2.5f - age) / 0.5f);
            LoopGui.Shadowed(new Rect(0, y - line, Screen.width - 20, line), toasts[i].text, style, color);
            y -= line;
        }
    }

    void DrawNotices(int font)
    {
        var style = LoopGui.Label(Mathf.RoundToInt(font * 0.8f), TextAnchor.UpperCenter);
        float y = font * 1.5f;
        for (int i = notices.Count - 1; i >= 0; i--)
        {
            if (Time.time - notices[i].time > 5f)
            {
                notices.RemoveAt(i);
                continue;
            }
            LoopGui.Shadowed(new Rect(0, y, Screen.width, font * 1.3f), notices[i].text, style, new Color(1f, 0.75f, 0.6f));
            y += font * 1.2f;
        }
    }

    void DrawSummary(int font)
    {
        if (state == State.Lost && Time.time - endTime <= 2.5f)
            return;
        var result = Profile.LastRaid;
        float w = Mathf.Min(Screen.width - 40, font * 26), h = font * 13;
        var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        LoopGui.Panel(rect, 0.85f);

        var title = LoopGui.Label(font * 2, TextAnchor.UpperCenter);
        LoopGui.Shadowed(new Rect(rect.x, rect.y + font, w, font * 3), result.extracted ? "Эвакуация удалась" : "Вы уничтожены",
            title, result.extracted ? ExtractColor : new Color(1f, 0.45f, 0.4f));

        var body = LoopGui.Label(font, TextAnchor.UpperCenter);
        body.wordWrap = true;
        string text = (result.extracted ? "Вынесено: " : "Потеряно: ") + result.items.Describe();
        if (!result.kept.IsEmpty)
            text += "\nЗащищённый карман сохранил: " + result.kept.Describe();
        if (result.unlocked.Length > 0)
            text += "\n\nОткрыты рецепты: " + string.Join(", ", result.unlocked);
        LoopGui.Shadowed(new Rect(rect.x + font, rect.y + font * 4, w - font * 2, h - font * 7), text, body, Color.white);
        body.wordWrap = false;

        if (GUI.Button(new Rect(rect.x + (w - font * 10) * 0.5f, rect.yMax - font * 2.8f, font * 10, font * 2), "На базу  [Enter]"))
            ReturnToBase();
    }
}
