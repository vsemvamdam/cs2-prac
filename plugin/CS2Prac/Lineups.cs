using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2Prac;

public partial class CS2Prac
{
    // ---------------------------------------------------------------- состояние карты
    private string _map = "";
    private string _build = "";
    private List<Lineup> _community = new();
    private List<Lineup> _mine = new();
    private MapChecks _checks = new();
    private readonly List<CPointWorldText> _labels = new();
    private readonly Dictionary<int, List<CPointWorldText>> _markers = new();
    private readonly Dictionary<int, string> _activeLineup = new();
    private readonly Dictionary<uint, TrackedNade> _nades = new();
    private readonly Dictionary<int, ThrowRef> _lastThrow = new();
    private CheckRun? _run;

    private sealed class TrackedNade
    {
        public int Slot;
        public string Type = "smoke";
        public string? LineupId;
        public ThrowRef? Ref;
        public string? CheckId;
        public DateTime At = DateTime.UtcNow;
    }

    private sealed class CheckRun
    {
        public CCSPlayerController Player = null!;
        public int Total, Ok, Miss, Pending, NoRef;
        public readonly List<string> Broken = new();
        public readonly HashSet<string> Done = new();
        public bool Finished;
    }

    private static readonly Dictionary<string, string> ProjectileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["smokegrenade_projectile"] = "smoke",
        ["flashbang_projectile"] = "flash",
        ["hegrenade_projectile"] = "he",
        ["decoy_projectile"] = "decoy",
        ["molotov_projectile"] = "molotov",
    };

    private const string ReplayTag = "cs2prac_replay";

    private string LineupsDir => Path.Combine(ModuleDirectory, "data", "lineups");
    private string UserDir => Path.Combine(ModuleDirectory, "userdata");
    private string MinePath => Path.Combine(UserDir, "mine", $"{_map}.json");
    private string ChecksPath => Path.Combine(UserDir, "checks", $"{_map}.json");

    private IEnumerable<Lineup> AllLineups => _community.Concat(_mine);
    private Lineup? FindLineup(string? id) => id == null ? null : AllLineups.FirstOrDefault(l => l.Id == id);

    private void LoadMapData(string map)
    {
        _map = map;
        _community = Json.Read<List<Lineup>>(Path.Combine(LineupsDir, $"{map}.json")) ?? new();
        _mine = Json.Read<List<Lineup>>(MinePath) ?? new();
        foreach (var l in _mine) l.Mine = true;
        _checks = Json.Read<MapChecks>(ChecksPath) ?? new MapChecks();
        _build = ReadBuild();
        _nades.Clear();
        _activeLineup.Clear();
        _markers.Clear();
        _labels.Clear();
        _run = null;
        _hideLabelsNear = null;
        PLog.Info($"{map}: раскидок {_community.Count} + своих {_mine.Count}, сборка CS2 {_build}");
    }

    private static string ReadBuild()
    {
        try
        {
            var inf = Path.Combine(Server.GameDirectory, "csgo", "steam.inf");
            var line = File.ReadLines(inf).FirstOrDefault(l => l.StartsWith("PatchVersion"));
            return line?.Split('=').Last().Trim() ?? "";
        }
        catch { return ""; }
    }

    private void SaveChecks() => Json.Write(ChecksPath, _checks);
    private void SaveMine() => Json.Write(MinePath, _mine);

    private LineupCheck CheckOf(string id)
    {
        if (!_checks.Items.TryGetValue(id, out var c)) _checks.Items[id] = c = new LineupCheck();
        return c;
    }

    private static string TypeRu(string t) => t switch
    {
        "smoke" => "Смок",
        "molotov" => "Молотов",
        "flash" => "Флешка",
        "he" => "Граната",
        "decoy" => "Декой",
        _ => t,
    };

    private static Color TypeColor(string t) => t switch
    {
        "smoke" => Color.FromArgb(255, 220, 226, 235),
        "molotov" => Color.FromArgb(255, 255, 140, 60),
        "flash" => Color.FromArgb(255, 255, 240, 120),
        "he" => Color.FromArgb(255, 255, 90, 90),
        _ => Color.White,
    };

    /// <summary>Статус раскидки для подписей: проверена / не попадает / не проверялась.</summary>
    private string StatusMark(Lineup l)
    {
        if (!_checks.Items.TryGetValue(l.Id, out var c) || c.Status == "untested") return "";
        var stale = c.CheckedBuild.Length > 0 && _build.Length > 0 && c.CheckedBuild != _build;
        if (c.Status == "ok") return stale ? " [OK?]" : " [OK]";
        return " [МИМО]";
    }

    // ---------------------------------------------------------------- подписи на карте
    /// <summary>Точка, рядом с которой подписи не показываем: игрок стоит на раскидке, подпись мешала бы целиться.</summary>
    private float[]? _hideLabelsNear;

    private void RefreshLabels()
    {
        WorldText.Kill(_labels);
        if (Mode != "smokes") return;
        foreach (var l in AllLineups)
        {
            if (_hideLabelsNear != null && Geo.Dist2D(l.Stand, _hideLabelsNear) < 48f && MathF.Abs(l.Stand[2] - _hideLabelsNear[2]) < 72f) continue;
            var pos = new Vector(l.Stand[0], l.Stand[1], l.Stand[2] + 88f);
            var text = $"{TypeRu(l.Type)}: {l.Title}{StatusMark(l)}";
            var color = TypeColor(l.Type);
            if (_checks.Items.TryGetValue(l.Id, out var c) && c.Status == "miss") color = Color.FromArgb(255, 255, 80, 80);
            var e = WorldText.Spawn(text, pos, new QAngle(0, 0, 90), color, 30f, 0.12f, true, billboard: true,
                vjustify: PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_BOTTOM);
            if (e != null) _labels.Add(e);
        }
    }

    private void ClearMarkers(int slot)
    {
        if (_markers.TryGetValue(slot, out var list)) WorldText.Kill(list);
    }

    /// <summary>Маркеры активной раскидки: куда целиться и куда должна прилететь граната.</summary>
    private void ShowMarkers(CCSPlayerController player, Lineup l)
    {
        ClearMarkers(player.Slot);
        var list = _markers.TryGetValue(player.Slot, out var existing) ? existing : (_markers[player.Slot] = new());
        var yaw = l.Aim[1];
        if (l.AimPoint != null)
        {
            var e = WorldText.Spawn("( + )", Geo.Vec(l.AimPoint), WorldText.FacingViewer(yaw), Color.FromArgb(255, 80, 255, 120), 40f, 0.1f,
                vjustify: PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER);
            if (e != null) list.Add(e);
        }
        if (l.Dest != null)
        {
            var e = WorldText.Spawn($"v  {l.Title}  v", new Vector(l.Dest[0], l.Dest[1], l.Dest[2] + 40f), new QAngle(0, 0, 90),
                TypeColor(l.Type), 90f, 0.25f, true, billboard: true,
                vjustify: PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_BOTTOM);
            if (e != null) list.Add(e);
        }
    }

    // ---------------------------------------------------------------- переход к раскидке
    private void GoToLineup(CCSPlayerController player, Lineup l)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || !player.PawnIsAlive)
        {
            player.PrintToChat($" {ChatColors.Red}Сначала нужно ожить (зайди за T или CT).");
            return;
        }
        CloseMenu(player);
        var angle = Geo.Ang(l.Aim);
        pawn.Teleport(Geo.Vec(l.Stand), angle, new Vector(0, 0, 0));
        KeepUpright(player, angle.Y, 6);
        _activeLineup[player.Slot] = l.Id;
        _hideLabelsNear = l.Stand;
        RefreshLabels();
        GiveNade(player, l.Type);
        ShowMarkers(player, l);

        var how = l.JumpThrow ? "джамп-тро" : "обычный бросок";
        player.PrintToChat($" {ChatColors.Gold}{TypeRu(l.Type)}: {ChatColors.White}{l.Title} {ChatColors.Grey}— {how}");
        if (l.Desc.Length > 0 && !l.Desc.StartsWith("setpos", StringComparison.OrdinalIgnoreCase))
            player.PrintToChat($" {ChatColors.Grey}Подсказка: {ChatColors.White}{l.Desc}");
        var status = _checks.Items.TryGetValue(l.Id, out var c) ? c.Status : "untested";
        if (status == "miss")
            player.PrintToChat($" {ChatColors.Red}При последней проверке эта граната не долетела до цели ({c!.LastDistance:0} юн.).");
        player.PrintToCenterHtml(
            $"<font color='#ffc43c'>{Html(l.Title)}</font><br><font color='#ffffff'>{(l.JumpThrow ? "Джамп-тро" : "Обычный бросок")}</font>" +
            (l.Desc.Length > 0 && !l.Desc.StartsWith("setpos", StringComparison.OrdinalIgnoreCase) ? $"<br><font color='#aab2be'>{Html(l.Desc)}</font>" : ""), 6);
    }

    private static string Html(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private void KeepUpright(CCSPlayerController player, float yaw, int frames)
    {
        if (frames <= 0 || !player.IsValid) return;
        var node = player.PlayerPawn.Value?.CBodyComponent?.SceneNode;
        if (node == null) return;
        node.AbsRotation.X = 0;
        node.AbsRotation.Y = yaw;
        node.AbsRotation.Z = 0;
        Server.NextFrame(() => KeepUpright(player, yaw, frames - 1));
    }

    private static string NadeClass(string type, CCSPlayerController p) => type switch
    {
        "smoke" => "weapon_smokegrenade",
        "flash" => "weapon_flashbang",
        "he" => "weapon_hegrenade",
        "decoy" => "weapon_decoy",
        "molotov" => p.Team == CsTeam.CounterTerrorist ? "weapon_incgrenade" : "weapon_molotov",
        _ => "weapon_smokegrenade",
    };

    private static string NadeSlot(string type) => type switch
    {
        "he" => "slot6", "flash" => "slot7", "smoke" => "slot8", "decoy" => "slot9", "molotov" => "slot10", _ => "slot8",
    };

    private void GiveNade(CCSPlayerController player, string type)
    {
        var cls = NadeClass(type, player);
        var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
        var has = weapons != null && weapons.Any(w => w.Value is { IsValid: true } wv && (wv.DesignerName == cls ||
                     (type == "molotov" && (wv.DesignerName == "weapon_molotov" || wv.DesignerName == "weapon_incgrenade"))));
        if (!has) player.GiveNamedItem(cls);
        AddTimer(0.1f, () => { if (player.IsValid) player.ExecuteClientCommand(NadeSlot(type)); });
    }

    // ---------------------------------------------------------------- отслеживание бросков
    private void OnEntitySpawnedHandler(CEntityInstance entity)
    {
        if (entity?.Entity == null) return;
        var designer = entity.Entity.DesignerName;
        if (designer == null || !ProjectileTypes.TryGetValue(designer, out var type)) return;
        var index = entity.Index;
        Server.NextFrame(() => RecordProjectile(index, designer, type, 0));
    }

    private void RecordProjectile(uint index, string designer, string type, int attempt)
    {
        var p = Utilities.GetEntityFromIndex<CBaseCSGrenadeProjectile>((int)index);
        if (p == null || !p.IsValid || p.DesignerName != designer) return;
        if (_nades.TryGetValue(index, out var existing) && existing.CheckId != null) return; // наш повтор при проверке
        if (p.Globalname == ReplayTag || p.Globalname == "custom") return;                      // повторы (наши и MatchZy)

        var pawn = p.Thrower.Value;
        var ctrl = pawn?.Controller.Value;
        if (pawn == null || ctrl == null) return;
        var player = new CCSPlayerController(ctrl.Handle);
        if (!player.IsValid || player.IsBot) return;

        var vel = new[] { p.InitialVelocity.X, p.InitialVelocity.Y, p.InitialVelocity.Z };
        if (MathF.Abs(vel[0]) + MathF.Abs(vel[1]) + MathF.Abs(vel[2]) < 1f && p.AbsVelocity != null)
            vel = new[] { p.AbsVelocity.X, p.AbsVelocity.Y, p.AbsVelocity.Z };
        if (MathF.Abs(vel[0]) + MathF.Abs(vel[1]) + MathF.Abs(vel[2]) < 1f && attempt < 2)
        {
            Server.NextFrame(() => RecordProjectile(index, designer, type, attempt + 1));
            return;
        }
        if (p.AbsOrigin == null || p.AbsRotation == null) return;
        var cPawn = player.PlayerPawn.Value;
        if (cPawn?.AbsOrigin == null) return;

        // Точка вылета: InitialPosition (где граната появилась), а не позиция через кадр — иначе повтор расходится.
        var ip = p.InitialPosition;
        var spawnPos = MathF.Abs(ip.X) + MathF.Abs(ip.Y) + MathF.Abs(ip.Z) > 1f ? new[] { ip.X, ip.Y, ip.Z } : Geo.Arr(p.AbsOrigin);
        var r = new ThrowRef
        {
            Type = type,
            Pos = spawnPos,
            Ang = Geo.Arr(p.AbsRotation),
            Vel = vel,
            AngVel = new[] { p.AngVelocity.X, p.AngVelocity.Y, p.AngVelocity.Z },
            ItemIndex = p.ItemIndex,
            PlayerPos = Geo.Arr(cPawn.AbsOrigin),
            PlayerAng = Geo.Arr(cPawn.EyeAngles),
            Jumped = (cPawn.Flags & (uint)PlayerFlags.FL_ONGROUND) == 0,
            Place = cPawn.LastPlaceName ?? "",
            Gravity = p.GravityScale,
            ActualGravity = p.ActualGravityScale,
            Friction = p.Friction,
            Elasticity = p.Elasticity,
        };
        PLog.Info($"Бросок {type}: вылет ({r.Pos[0]:0.0}, {r.Pos[1]:0.0}, {r.Pos[2]:0.0}), скорость ({r.Vel[0]:0.0}, {r.Vel[1]:0.0}, {r.Vel[2]:0.0}), " +
                  $"гравитация {r.Gravity:0.###}/{r.ActualGravity:0.###}, трение {r.Friction:0.###}, упругость {r.Elasticity:0.###}, тип движения {p.MoveType}");
        _lastThrow[player.Slot] = r;
        _nades[index] = new TrackedNade { Slot = player.Slot, Type = type, Ref = r, LineupId = MatchLineup(player, r)?.Id };
        if (_nades.Count > 256)
            foreach (var old in _nades.Where(n => (DateTime.UtcNow - n.Value.At).TotalSeconds > 60).Select(n => n.Key).ToList())
                _nades.Remove(old);
    }

    /// <summary>К какой раскидке относится бросок: игрок стоит на её точке и смотрит примерно туда же.</summary>
    private Lineup? MatchLineup(CCSPlayerController player, ThrowRef r)
    {
        Lineup? best = null;
        var bestScore = float.MaxValue;
        foreach (var l in AllLineups)
        {
            if (l.Type != r.Type || l.Dest == null) continue;
            var d = Geo.Dist2D(l.Stand, r.PlayerPos);
            if (d > 40f || MathF.Abs(l.Stand[2] - r.PlayerPos[2]) > 72f) continue;
            var dyaw = MathF.Abs(((l.Aim[1] - r.PlayerAng[1]) % 360 + 540) % 360 - 180);
            if (dyaw > 25f) continue;
            var score = d + dyaw * 2f - (_activeLineup.TryGetValue(player.Slot, out var a) && a == l.Id ? 1000 : 0);
            if (score < bestScore) { bestScore = score; best = l; }
        }
        return best;
    }

    private void OnNadeDetonated(int entityIndex, CCSPlayerController? user, string type, float x, float y, float z)
    {
        TrackedNade? t = null;
        uint key = 0;
        if (entityIndex > 0 && _nades.TryGetValue((uint)entityIndex, out t)) key = (uint)entityIndex;
        else if (type == "molotov" && user != null)
        {
            // molotov_detonate не сообщает индекс снаряда: берём последний молотов этого игрока.
            var found = _nades.Where(n => n.Value.Type == "molotov" && (n.Value.Slot == user.Slot || n.Value.CheckId != null))
                .OrderBy(n => n.Value.At).FirstOrDefault();
            if (found.Value != null) { t = found.Value; key = found.Key; }
        }
        if (t == null) return;
        _nades.Remove(key);
        var det = new[] { x, y, z };

        if (t.CheckId != null)
        {
            OnCheckDetonated(t.CheckId, det);
            return;
        }
        if (t.Ref != null) t.Ref.Landed = det;

        var l = FindLineup(t.LineupId);
        if (l?.Dest == null) return;
        var player = Utilities.GetPlayerFromSlot(t.Slot);
        if (player == null || !player.IsValid) return;
        var dist = Geo.Dist(det, l.Dest);
        var ok = dist <= Math.Max(l.Threshold, 60f);
        var c = CheckOf(l.Id);
        if (ok)
        {
            // Удачный бросок становится эталоном для проверки после патчей.
            var better = c.Ref == null || c.Status != "ok" || c.LastDistance < 0 || dist <= c.LastDistance;
            if (better && t.Ref != null) c.Ref = t.Ref;
            c.Status = "ok";
            c.LastDistance = dist;
            c.CheckedBuild = _build;
            c.CheckedAt = DateTime.UtcNow;
            SaveChecks();
            player.PrintToChat($" {ChatColors.Green}Точно! {ChatColors.White}{l.Title} {ChatColors.Grey}({dist:0} юн. от цели). Бросок сохранён как эталон.");
            player.PrintToCenterHtml($"<font color='#50ff78'>ТОЧНО</font><br>{Html(l.Title)}", 3);
        }
        else
        {
            player.PrintToChat($" {ChatColors.Red}Мимо: {ChatColors.White}{l.Title} {ChatColors.Grey}— {dist:0} юн. от цели. Встань на точку ещё раз (E → раскидка) и повтори.");
            player.PrintToCenterHtml($"<font color='#ff5050'>МИМО</font><br>{dist:0} юн. от цели", 3);
        }
        RefreshLabels();
    }

    // ---------------------------------------------------------------- повтор броска
    private static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, int, CSmokeGrenadeProjectile>? _smokeCreate;
    private static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CMolotovProjectile>? _molotovCreate;
    private static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CHEGrenadeProjectile>? _heCreate;
    private static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CDecoyProjectile>? _decoyCreate;
    private static bool _factoriesResolved;

    private static void ResolveFactories()
    {
        if (_factoriesResolved) return;
        _factoriesResolved = true;
        // Сигнатуры берём из gamedata MatchZy (addons/counterstrikesharp/gamedata/matchzy.json) — их обновляют вместе с MatchZy.
        T? Get<T>(string key, Func<string, T> make) where T : BaseMemoryFunction
        {
            try
            {
                var f = make(GameData.GetSignature(key));
                if (f.Handle != IntPtr.Zero) return f;
                PLog.Info($"{key}: сигнатура не найдена в этой сборке CS2 — повтор через entity API");
            }
            catch (Exception e) { PLog.Info($"{key}: {e.Message} — повтор через entity API"); }
            return null;
        }
        _smokeCreate = Get("CSmokeGrenadeProjectile_Create", s => new MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, int, CSmokeGrenadeProjectile>(s));
        _molotovCreate = Get("CMolotovProjectile_Create", s => new MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CMolotovProjectile>(s));
        _heCreate = Get("CHEGrenadeProjectile_Create", s => new MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CHEGrenadeProjectile>(s));
        _decoyCreate = Get("CDecoyProjectile_Create", s => new MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CDecoyProjectile>(s));
    }

    private CBaseCSGrenadeProjectile? Replay(ThrowRef r, CCSPlayerController player)
    {
        ResolveFactories();
        var pos = Geo.Vec(r.Pos);
        var ang = Geo.Ang(r.Ang);
        var vel = Geo.Vec(r.Vel);
        CBaseCSGrenadeProjectile? g = null;
        try
        {
            // Нативные фабрики точнее (как бросок игрока), но их сигнатуры ломаются после патчей.
            g = r.Type switch
            {
                "smoke" => _smokeCreate?.Invoke(pos.Handle, ang.Handle, vel.Handle, vel.Handle, IntPtr.Zero, r.ItemIndex, (int)player.Team),
                "molotov" => _molotovCreate?.Invoke(pos.Handle, ang.Handle, vel.Handle, vel.Handle, IntPtr.Zero, r.ItemIndex),
                "he" => _heCreate?.Invoke(pos.Handle, ang.Handle, vel.Handle, vel.Handle, IntPtr.Zero, r.ItemIndex),
                "decoy" => _decoyCreate?.Invoke(pos.Handle, ang.Handle, vel.Handle, vel.Handle, IntPtr.Zero, r.ItemIndex),
                _ => null,
            };
        }
        catch (Exception e)
        {
            PLog.Info($"Повтор броска (нативно): {e.Message}");
            g = null;
        }
        if (g == null || !g.IsValid)
        {
            try
            {
                g = r.Type switch
                {
                    "smoke" => Spawn<CSmokeGrenadeProjectile>("smokegrenade_projectile"),
                    "molotov" => Spawn<CMolotovProjectile>("molotov_projectile"),
                    "he" => Spawn<CHEGrenadeProjectile>("hegrenade_projectile"),
                    "decoy" => Spawn<CDecoyProjectile>("decoy_projectile"),
                    "flash" => Spawn<CFlashbangProjectile>("flashbang_projectile"),
                    _ => null,
                };
            }
            catch (Exception e)
            {
                PLog.Info($"Повтор броска: {e.Message}");
                return null;
            }
        }
        if (g == null || !g.IsValid) return null;

        g.ItemIndex = r.ItemIndex;
        g.InitialPosition.X = r.Pos[0]; g.InitialPosition.Y = r.Pos[1]; g.InitialPosition.Z = r.Pos[2];
        g.InitialVelocity.X = r.Vel[0]; g.InitialVelocity.Y = r.Vel[1]; g.InitialVelocity.Z = r.Vel[2];
        g.AngVelocity.X = r.AngVel[0]; g.AngVelocity.Y = r.AngVel[1]; g.AngVelocity.Z = r.AngVel[2];
        // Физика как у настоящей гранаты (для старых эталонов — стандартные значения CS для гранат).
        g.GravityScale = r.Gravity > 0 ? r.Gravity : 0.4f;
        if (r.ActualGravity > 0) g.ActualGravityScale = r.ActualGravity;
        g.Friction = r.Friction > 0 ? r.Friction : 0.2f;
        g.Elasticity = r.Elasticity > 0 ? r.Elasticity : 0.45f;
        g.Teleport(pos, ang, vel);
        g.Globalname = ReplayTag;
        PLog.Info($"Повтор {r.Type}: гравитация {g.GravityScale:0.###}/{g.ActualGravityScale:0.###}, трение {g.Friction:0.###}, упругость {g.Elasticity:0.###}, тип движения {g.MoveType}");
        g.TeamNum = player.TeamNum;
        if (player.PlayerPawn.IsValid)
        {
            g.Thrower.Raw = player.PlayerPawn.Raw;
            g.OriginalThrower.Raw = player.PlayerPawn.Raw;
            g.OwnerEntity.Raw = player.PlayerPawn.Raw;
        }
        return g;

        static T? Spawn<T>(string name) where T : CBaseCSGrenadeProjectile
        {
            var e = Utilities.CreateEntityByName<T>(name);
            e?.DispatchSpawn();
            return e;
        }
    }

    private void Rethrow(CCSPlayerController player)
    {
        if (!_lastThrow.TryGetValue(player.Slot, out var r))
        {
            player.PrintToChat($" {ChatColors.Grey}Сначала брось гранату — потом её можно повторять.");
            return;
        }
        Replay(r, player);
    }

    // ---------------------------------------------------------------- проверка всех раскидок
    private void StartCheck(CCSPlayerController player)
    {
        if (_run is { Finished: false })
        {
            player.PrintToChat($" {ChatColors.Grey}Проверка уже идёт.");
            return;
        }
        CloseMenu(player);
        var withDest = AllLineups.Where(l => l.Dest != null).ToList();
        var queue = withDest.Where(l => _checks.Items.TryGetValue(l.Id, out var c) && c.Ref != null).ToList();
        _run = new CheckRun { Player = player, Total = queue.Count, NoRef = withDest.Count - queue.Count };
        if (queue.Count == 0)
        {
            _run.Finished = true;
            player.PrintToChat($" {ChatColors.Gold}Пока нечего проверять. {ChatColors.White}Брось раскидки по подсказкам — каждый точный бросок станет эталоном, и потом сервер сможет сам перепроверить его после патча.");
            return;
        }
        player.PrintToChat($" {ChatColors.Gold}Проверка: {ChatColors.White}сервер бросает {queue.Count} гранат по сохранённым эталонам…");
        var run = _run;
        for (var i = 0; i < queue.Count; i++)
        {
            var l = queue[i];
            AddTimer(0.2f + 0.5f * i, () =>
            {
                if (_run != run || run.Finished || !player.IsValid) return;
                var g = Replay(_checks.Items[l.Id].Ref!, player);
                if (g == null) { PLog.Info($"Проверка {l.Title}: не удалось создать гранату"); run.Done.Add(l.Id); run.Broken.Add(l.Id); return; }
                PLog.Info($"Проверка {l.Title}: граната #{g.Index}");
                _nades[g.Index] = new TrackedNade { Slot = player.Slot, Type = l.Type, CheckId = l.Id };
                run.Pending++;
                WatchLanding(g.Index, l.Id, l.Type, run);
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }
        AddTimer(0.5f * queue.Count + 15f, () => FinishCheck(run), TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>
    /// Следим за повторённой гранатой: если игра не пришлёт событие срабатывания (гранаты, созданные без нативной
    /// фабрики, после некоторых патчей не раскрываются), точкой падения считается место, где граната остановилась
    /// (смок, молотов, декой) или где была через 1,65 с (флешка, граната).
    /// </summary>
    private void WatchLanding(uint index, string id, string type, CheckRun run)
    {
        var started = Server.CurrentTime;
        float[]? last = null;
        var still = 0;
        CounterStrikeSharp.API.Modules.Timers.Timer? timer = null;
        timer = AddTimer(0.1f, () =>
        {
            if (run.Finished || run.Done.Contains(id)) { timer?.Kill(); return; }
            var e = Utilities.GetEntityFromIndex<CBaseCSGrenadeProjectile>((int)index);
            var alive = e != null && e.IsValid && e.AbsOrigin != null;
            var age = Server.CurrentTime - started;
            if (!alive)
            {
                timer?.Kill();
                if (last != null && run.Done.Contains(id) == false) { _nades.Remove(index); OnCheckDetonated(id, last); }
                return;
            }
            var pos = Geo.Arr(e!.AbsOrigin!);
            bool landed;
            if (type is "flash" or "he") landed = age >= 1.65f;
            else
            {
                still = last != null && Geo.Dist(pos, last) < 0.5f ? still + 1 : 0;
                landed = still >= 3 && age > 0.5f;
            }
            last = pos;
            if (!landed && age < 12f) return;
            timer?.Kill();
            _nades.Remove(index);
            OnCheckDetonated(id, pos);
            try { e.Remove(); } catch { }
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void OnCheckDetonated(string id, float[] det)
    {
        var run = _run;
        var l = FindLineup(id);
        if (run == null || run.Finished || l?.Dest == null) return;
        var dist = Geo.Dist(det, l.Dest);
        PLog.Info($"Проверка {l.Title}: упала в {dist:0} юн. от цели");
        var c = CheckOf(id);
        c.LastDistance = dist;
        c.CheckedBuild = _build;
        c.CheckedAt = DateTime.UtcNow;
        if (dist <= Math.Max(l.Threshold, 60f)) { c.Status = "ok"; run.Ok++; }
        else { c.Status = "miss"; run.Miss++; run.Broken.Add(id); }
        run.Done.Add(id);
        run.Pending--;
        if (run.Done.Count >= run.Total) FinishCheck(run);
    }

    private void FinishCheck(CheckRun run)
    {
        if (run.Finished) return;
        run.Finished = true;
        var notLanded = run.Total - run.Done.Count;
        foreach (var l in AllLineups.Where(l => l.Dest != null && _checks.Items.TryGetValue(l.Id, out var c) && c.Ref != null && !run.Done.Contains(l.Id)))
        {
            var c = CheckOf(l.Id);
            c.Status = "miss";
            c.LastDistance = -1;
            c.CheckedBuild = _build;
            c.CheckedAt = DateTime.UtcNow;
            run.Broken.Add(l.Id);
        }
        SaveChecks();
        RefreshLabels();
        ClearSmokes();
        var p = run.Player;
        if (!p.IsValid) return;
        PLog.Info($"Проверка завершена: точно {run.Ok}, мимо {run.Miss}, не сработали {notLanded}, без эталона {run.NoRef}");
        p.PrintToChat($" {ChatColors.Gold}Проверка завершена: {ChatColors.Green}точно {run.Ok}{ChatColors.White}, {ChatColors.Red}мимо {run.Miss + notLanded}{ChatColors.White}, без эталона {run.NoRef}.");
        var page = new MenuPage
        {
            Title = "Проверка раскидок",
            Sub = $"точно {run.Ok} · мимо {run.Miss + notLanded} · без эталона {run.NoRef}",
            Back = () => MainPage(p),
        };
        foreach (var id in run.Broken.Distinct())
        {
            var l = FindLineup(id);
            if (l == null) continue;
            var d = _checks.Items.TryGetValue(id, out var c) ? c.LastDistance : -1;
            page.Add(d >= 0 ? $"{l.Title} ({d:0} юн.)" : $"{l.Title} (не сработала)", pl => GoToLineup(pl, l), Color.FromArgb(255, 255, 110, 110));
        }
        if (page.Items.Count == 0) page.Add("Все проверенные раскидки на месте", null, Color.FromArgb(255, 80, 255, 120));
        OpenMenu(p, page, false);
    }

    private static void ClearSmokes()
    {
        foreach (var name in new[] { "smokegrenade_projectile", "molotov_projectile", "inferno" })
            foreach (var e in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(name))
                try { if (e.IsValid) e.Remove(); } catch { }
    }

    // ---------------------------------------------------------------- свои раскидки
    private void SaveLastThrowAsMine(CCSPlayerController player)
    {
        if (!_lastThrow.TryGetValue(player.Slot, out var r) || r.Landed == null)
        {
            player.PrintToChat($" {ChatColors.Grey}Брось гранату и дождись, пока она сработает, — потом сохраняй.");
            return;
        }
        var n = _mine.Count + 1;
        var place = r.Place.Length > 0 ? r.Place : "";
        var l = new Lineup
        {
            Id = "my-" + Guid.NewGuid().ToString("N")[..10],
            Title = $"Моя #{n}" + (place.Length > 0 ? $" ({place})" : ""),
            Desc = r.Jumped ? "джамп-тро" : "",
            Type = r.Type,
            Side = player.Team == CsTeam.CounterTerrorist ? "CT" : "T",
            JumpThrow = r.Jumped,
            Stand = r.PlayerPos,
            Aim = r.PlayerAng,
            Dest = r.Landed,
            Threshold = 60f,
            Mine = true,
        };
        _mine.Add(l);
        SaveMine();
        var c = CheckOf(l.Id);
        c.Ref = r;
        c.Status = "ok";
        c.LastDistance = 0;
        c.CheckedBuild = _build;
        c.CheckedAt = DateTime.UtcNow;
        SaveChecks();
        RefreshLabels();
        player.PrintToChat($" {ChatColors.Green}Сохранено: {ChatColors.White}{l.Title}. {ChatColors.Grey}Переименовать: напиши в чат !name новое название");
        _lastSaved[player.Slot] = l.Id;
    }

    private readonly Dictionary<int, string> _lastSaved = new();

    private void RenameMine(CCSPlayerController player, string name)
    {
        if (!_lastSaved.TryGetValue(player.Slot, out var id) && !_activeLineup.TryGetValue(player.Slot, out id)) id = "";
        var l = _mine.FirstOrDefault(x => x.Id == id);
        if (l == null)
        {
            player.PrintToChat($" {ChatColors.Grey}Переименовать можно свою раскидку: сохрани её или выбери в «Мои раскидки».");
            return;
        }
        l.Title = name.Trim();
        SaveMine();
        RefreshLabels();
        player.PrintToChat($" {ChatColors.Green}Готово: {ChatColors.White}{l.Title}");
    }

    private void DeleteMine(CCSPlayerController player, Lineup l)
    {
        _mine.Remove(l);
        _checks.Items.Remove(l.Id);
        SaveMine();
        SaveChecks();
        RefreshLabels();
        player.PrintToChat($" {ChatColors.Grey}Удалено: {l.Title}");
    }
}
