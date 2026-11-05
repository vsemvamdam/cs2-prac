using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2Prac;

[MinimumApiVersion(300)]
public partial class CS2Prac : BasePlugin
{
    public override string ModuleName => "CS2 Prac";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "vsemvamdam";
    public override string ModuleDescription => "Табло с раскидками на официальных картах, проверка смоков после патчей, оружие и скины";

    /// <summary>smokes | prefire — задаёт лаунчер в файле mode.txt рядом с плагином.</summary>
    public string Mode { get; private set; } = "smokes";

    private readonly Dictionary<int, BoardMenu> _menus = new();
    private readonly Dictionary<int, DateTime> _lastUse = new();
    private readonly Dictionary<int, int> _chatTick = new();

    public override void Load(bool hotReload)
    {
        PLog.L = Logger;
        Mode = ReadMode();
        LoadSkinData();
        PLog.Info($"CS2 Prac {ModuleVersion}: режим {Mode}, оружия {_skins.ByDef.Count}, агентов {_agents.Count}, скины {(_setAttr != null ? "доступны" : "НЕДОСТУПНЫ")}");

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawnedHandler);
        RegisterListener<Listeners.OnPlayerButtonsChanged>(OnButtons);
        RegisterListener<Listeners.OnClientDisconnect>(slot =>
        {
            _menus.Remove(slot);
            ClearMarkers(slot);
            _activeLineup.Remove(slot);
            _lastThrow.Remove(slot);
        });
        RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
        {
            foreach (var a in _agents) manifest.AddResource($"agents/models/{a.Model}.vmdl");
        });

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerConnectFull>(OnConnectFull);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventSmokegrenadeDetonate>((e, _) => { OnNadeDetonated(e.Entityid, null, "smoke", e.X, e.Y, e.Z); return HookResult.Continue; });
        RegisterEventHandler<EventFlashbangDetonate>((e, _) => { OnNadeDetonated(e.Entityid, e.Userid, "flash", e.X, e.Y, e.Z); return HookResult.Continue; });
        RegisterEventHandler<EventHegrenadeDetonate>((e, _) => { OnNadeDetonated(e.Entityid, e.Userid, "he", e.X, e.Y, e.Z); return HookResult.Continue; });
        RegisterEventHandler<EventMolotovDetonate>((e, _) => { OnNadeDetonated(0, e.Userid, "molotov", e.X, e.Y, e.Z); return HookResult.Continue; });

        VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveNamedItemPost, HookMode.Post);
        StartSwitchPolling();
        AddCommandListener("say", OnSay, HookMode.Pre);
        AddCommandListener("say_team", OnSay, HookMode.Pre);

        // css_prac не регистрируем: это команда MatchZy (включает режим практики)
        AddCommand("css_menu", "Меню CS2 Prac", (p, _) => { if (p != null) ToggleMenu(p); });
        AddCommand("css_m", "Меню CS2 Prac", (p, _) => { if (p != null) ToggleMenu(p); });
        AddCommand("css_skins", "Оружие и скины", (p, _) => { if (p != null) OpenMenu(p, LoadoutPage(p), false); });
        AddCommand("css_ws", "Оружие и скины", (p, _) => { if (p != null) OpenMenu(p, LoadoutPage(p), false); });
        AddCommand("css_name", "Переименовать свою раскидку", (p, info) =>
        {
            if (p == null) return;
            var name = info.ArgString.Trim().Trim('"');
            if (name.Length == 0) { p.PrintToChat($" {ChatColors.Grey}Напиши: !name Новое название"); return; }
            RenameMine(p, name);
        });
        AddCommand("css_check", "Проверить раскидки", (p, _) => { if (p != null) StartCheck(p); });

        if (hotReload) OnMapStart(Server.MapName);
    }

    public override void Unload(bool hotReload)
    {
        VirtualFunctions.GiveNamedItemFunc.Unhook(OnGiveNamedItemPost, HookMode.Post);
        foreach (var m in _menus.Values) try { m.Reset(); } catch { }
        WorldText.Kill(_labels);
        foreach (var slot in _markers.Keys.ToList()) ClearMarkers(slot);
    }

    private string ReadMode()
    {
        try
        {
            var f = Path.Combine(ModuleDirectory, "mode.txt");
            var m = File.Exists(f) ? File.ReadAllText(f).Trim().ToLowerInvariant() : "smokes";
            return m == "prefire" ? "prefire" : "smokes";
        }
        catch { return "smokes"; }
    }

    private void OnMapStart(string map)
    {
        Mode = ReadMode();
        LoadMapData(map);
        _menus.Clear();
        // Сущности можно создавать, когда карта полностью загрузилась.
        AddTimer(3f, RefreshLabels, TimerFlags.STOP_ON_MAPCHANGE);
    }

    // ---------------------------------------------------------------- игроки
    private HookResult OnConnectFull(EventPlayerConnectFull e, GameEventInfo info)
    {
        var p = e.Userid;
        if (p == null || !p.IsValid || p.IsBot) return HookResult.Continue;
        // Пробуем загрузить бинды 1–9 сами: файл cs2prac_binds.cfg кладёт установщик в папку cfg игры.
        AddTimer(2f, () => { if (p.IsValid) p.ExecuteClientCommand("exec cs2prac_binds"); }, TimerFlags.STOP_ON_MAPCHANGE);
        AddTimer(4f, () =>
        {
            if (!p.IsValid) return;
            p.PrintToChat($" {ChatColors.Gold}CS2 Prac{ChatColors.White} — нажми {ChatColors.Green}E{ChatColors.White}, чтобы открыть меню. Пункты — цифрами 1–9 или выстрелом по строке табло.");
            if (!LoadoutOf(p).DigitKeysOk)
                p.PrintToChat($" {ChatColors.Grey}Если цифры не выбирают пункты, один раз введи в консоли: {ChatColors.White}exec cs2prac_binds");
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn e, GameEventInfo info)
    {
        var p = e.Userid;
        if (p == null || !p.IsValid || p.IsBot) return HookResult.Continue;
        ApplyLoadoutOnSpawn(p);
        return HookResult.Continue;
    }

    /// <summary>E открывает и закрывает меню. Двойное нажатие не нужно — одиночное, с защитой от дребезга.</summary>
    private void OnButtons(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
    {
        if (player.IsBot || (pressed & PlayerButtons.Use) == 0) return;
        var now = DateTime.UtcNow;
        if (_lastUse.TryGetValue(player.Slot, out var t) && (now - t).TotalMilliseconds < 300) return;
        _lastUse[player.Slot] = now;
        ToggleMenu(player);
    }

    private HookResult OnWeaponFire(EventWeaponFire e, GameEventInfo info)
    {
        var p = e.Userid;
        if (p == null || !p.IsValid || p.IsBot) return HookResult.Continue;
        if (e.Weapon.Contains("grenade") || e.Weapon.Contains("flashbang") || e.Weapon.Contains("molotov") || e.Weapon.Contains("decoy"))
            return HookResult.Continue;
        if (!_menus.TryGetValue(p.Slot, out var m) || !m.IsOpen) return HookResult.Continue;
        var pawn = p.PlayerPawn.Value;
        if (pawn?.AbsOrigin == null) return HookResult.Continue;
        var eye = new[] { pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + pawn.ViewOffset.Z };
        m.TryHitByShot(eye, pawn.EyeAngles.X, pawn.EyeAngles.Y);
        return HookResult.Continue;
    }

    /// <summary>
    /// Пункт выбран цифрой (css_N). Если это была клавиша, а не «!N» в чате, — бинды в игре загружены,
    /// и лаунчер больше не будет запускать CS2 с +exec cs2prac_binds.
    /// </summary>
    public void OnMenuKeyUsed(CCSPlayerController p)
    {
        if (LoadoutOf(p).DigitKeysOk) return;
        var tick = Server.TickCount;
        Server.NextFrame(() =>
        {
            if (!p.IsValid) return;
            if (_chatTick.TryGetValue(p.Slot, out var t) && Math.Abs(t - tick) <= 2) return; // «!1» в чате тоже приходит как css_1
            var lo = LoadoutOf(p);
            if (lo.DigitKeysOk) return;
            lo.DigitKeysOk = true;
            SaveLoadout(p);
            try { File.WriteAllText(Path.Combine(UserDir, "digit_keys_ok.txt"), DateTime.UtcNow.ToString("O")); } catch { }
            PLog.Info($"Цифры 1–9 работают у {p.PlayerName}");
        });
    }

    private HookResult OnSay(CCSPlayerController? p, CommandInfo info)
    {
        if (p != null && p.IsValid) _chatTick[p.Slot] = Server.TickCount;
        return HookResult.Continue;
    }

    // ---------------------------------------------------------------- меню
    private BoardMenu MenuOf(CCSPlayerController p)
    {
        if (!_menus.TryGetValue(p.Slot, out var m) || m.Player.Handle != p.Handle)
            _menus[p.Slot] = m = new BoardMenu(this, p);
        return m;
    }

    public void OpenMenu(CCSPlayerController p, MenuPage page, bool keepPlacement = true) => MenuOf(p).Show(page, keepPlacement);

    public void CloseMenu(CCSPlayerController p)
    {
        if (_menus.TryGetValue(p.Slot, out var m) && m.IsOpen) m.Close();
    }

    private void ToggleMenu(CCSPlayerController p)
    {
        if (_menus.TryGetValue(p.Slot, out var m) && m.IsOpen) { m.Close(); return; }
        if (MenuManager.GetActiveMenu(p) != null) MenuManager.CloseActiveMenu(p);
        if (_hideLabelsNear != null && NearestLineup(p) == null) { _hideLabelsNear = null; RefreshLabels(); } // ушёл с точки — подписи обратно
        OpenMenu(p, MainPage(p), false);
    }

    private static string MapTitle(string map) => map.StartsWith("de_") ? map[3..] : map;

    public MenuPage MainPage(CCSPlayerController p)
    {
        if (Mode == "prefire")
        {
            var pf = new MenuPage { Title = $"Префаер · {MapTitle(_map)}", Sub = "E — закрыть" };
            pf.Add("Маршруты префаера", pl => { CloseMenu(pl); pl.ExecuteClientCommandFromServer("css_prefire"); });
            pf.Add("Оружие и скины", pl => OpenMenu(pl, LoadoutPage(pl)));
            pf.Add("Выйти из маршрута", pl => { CloseMenu(pl); pl.ExecuteClientCommandFromServer("css_prefire exit"); });
            pf.Add("Перейти в режим смоков", pl => SwitchFromMenu(pl, "smokes"), Color.FromArgb(255, 150, 200, 255));
            return pf;
        }

        var all = AllLineups.ToList();
        var page = new MenuPage { Title = $"CS2 Prac · {MapTitle(_map)}", Sub = BuildSub() };
        var near = NearestLineup(p);
        if (near != null)
            page.Add($"Встать точно: {near.Title}", pl => GoToLineup(pl, near), Color.FromArgb(255, 80, 255, 120));
        var groups = new[]
        {
            (types: new[] { "smoke" }, title: "Смоки", color: TypeColor("smoke")),
            (types: new[] { "molotov" }, title: "Молотовы", color: TypeColor("molotov")),
            (types: new[] { "flash", "he" }, title: "Флешки и гранаты", color: TypeColor("flash")),
        };
        foreach (var g in groups)
        {
            var n = all.Count(l => g.types.Contains(l.Type) && !l.Mine);
            if (n == 0) continue;
            var gg = g;
            page.Add($"{g.title} ({n})", pl => OpenMenu(pl, SidePage(pl, gg.types, gg.title)), g.color);
        }
        page.Add($"Мои раскидки ({_mine.Count})", pl => OpenMenu(pl, MinePage(pl)));
        page.Add("Тренировка и проверка после патча", pl => OpenMenu(pl, ToolsPage(pl)), Color.FromArgb(255, 150, 200, 255));
        page.Add("Оружие и скины", pl => OpenMenu(pl, LoadoutPage(pl)));
        page.Add("Перейти в режим префаера", pl => SwitchFromMenu(pl, "prefire"), Color.FromArgb(255, 150, 200, 255));
        return page;
    }

    private string BuildSub()
    {
        var withRef = _checks.Items.Values.Count(c => c.Ref != null);
        var stale = _checks.Items.Values.Any(c => c.Ref != null && c.CheckedBuild.Length > 0 && _build.Length > 0 && c.CheckedBuild != _build);
        var s = $"раскидок {AllLineups.Count()} · эталонов {withRef}";
        if (stale) s += " · CS2 обновилась — проверь!";
        return s;
    }

    private Lineup? NearestLineup(CCSPlayerController p)
    {
        var pawn = p.PlayerPawn.Value;
        if (pawn?.AbsOrigin == null) return null;
        var pos = Geo.Arr(pawn.AbsOrigin);
        return AllLineups
            .Select(l => (l, d: Geo.Dist2D(l.Stand, pos)))
            .Where(x => x.d < 48f && MathF.Abs(x.l.Stand[2] - pos[2]) < 72f)
            .OrderBy(x => x.d).Select(x => x.l).FirstOrDefault();
    }

    private MenuPage SidePage(CCSPlayerController p, string[] types, string title)
    {
        var list = _community.Where(l => types.Contains(l.Type)).ToList();
        var sides = list.Select(l => l.Side).Distinct().ToList();
        if (sides.Count <= 1) return ListPage(p, list, title, () => MainPage(p));
        var page = new MenuPage { Title = title, Sub = "за какую сторону", Back = () => MainPage(p) };
        foreach (var side in new[] { "T", "CT", "" })
        {
            var part = list.Where(l => l.Side == side).ToList();
            if (part.Count == 0) continue;
            var name = side switch { "T" => "За террористов", "CT" => "За спецназ", _ => "Прочие" };
            var tt = $"{title} · {side switch { "T" => "T", "CT" => "CT", _ => "прочие" }}";
            page.Add($"{name} ({part.Count})", pl => OpenMenu(pl, ListPage(pl, part, tt, () => SidePage(pl, types, title))));
        }
        return page;
    }

    private MenuPage ListPage(CCSPlayerController p, List<Lineup> list, string title, Func<MenuPage> back)
    {
        var page = new MenuPage { Title = title, Sub = "выбери — телепорт на точку броска", Back = back };
        foreach (var l in list)
        {
            var ll = l;
            var mark = StatusMark(l);
            var color = mark.Contains("МИМО") ? Color.FromArgb(255, 255, 110, 110) : mark.Contains("OK") ? Color.FromArgb(255, 120, 255, 150) : Color.White;
            var kind = list.Select(x => x.Type).Distinct().Count() > 1 ? TypeRu(l.Type) + ": " : "";
            page.Add($"{kind}{l.Title}{(l.JumpThrow ? " · джамп" : "")}{mark}", pl => GoToLineup(pl, ll), color);
        }
        return page;
    }

    private MenuPage MinePage(CCSPlayerController p)
    {
        var page = new MenuPage { Title = "Мои раскидки", Sub = "брось гранату — потом «сохранить»", Back = () => MainPage(p) };
        page.Add("Сохранить последний бросок", pl => { SaveLastThrowAsMine(pl); OpenMenu(pl, MinePage(pl)); }, Color.FromArgb(255, 80, 255, 120));
        foreach (var l in _mine)
        {
            var ll = l;
            page.Add($"{TypeRu(l.Type)}: {l.Title}{StatusMark(l)}", pl => OpenMenu(pl, MineItemPage(pl, ll)));
        }
        return page;
    }

    private MenuPage MineItemPage(CCSPlayerController p, Lineup l)
    {
        var page = new MenuPage { Title = l.Title, Sub = TypeRu(l.Type), Back = () => MinePage(p) };
        page.Add("Встать на точку", pl => { _lastSaved[pl.Slot] = l.Id; GoToLineup(pl, l); });
        page.Add("Переименовать: напиши в чат !name …", pl => { _lastSaved[pl.Slot] = l.Id; pl.PrintToChat($" {ChatColors.Grey}Напиши в чат: !name Новое название"); });
        page.Add("Удалить", pl => { DeleteMine(pl, l); OpenMenu(pl, MinePage(pl)); }, Color.FromArgb(255, 255, 110, 110));
        return page;
    }

    private MenuPage ToolsPage(CCSPlayerController p)
    {
        var page = new MenuPage { Title = "Тренировка", Back = () => MainPage(p) };
        page.Add("Проверить все раскидки (после патча)", StartCheck, Color.FromArgb(255, 150, 200, 255));
        page.Add("Повторить последний бросок", pl => { Rethrow(pl); });
        page.Add("Вернуться на точку последнего броска", pl =>
        {
            if (!_lastThrow.TryGetValue(pl.Slot, out var r)) { pl.PrintToChat($" {ChatColors.Grey}Бросков ещё не было."); return; }
            CloseMenu(pl);
            var pawn = pl.PlayerPawn.Value;
            if (pawn == null) return;
            var a = Geo.Ang(r.PlayerAng);
            pawn.Teleport(Geo.Vec(r.PlayerPos), a, new Vector(0, 0, 0));
            KeepUpright(pl, a.Y, 6);
            GiveNade(pl, r.Type);
        });
        page.Add("Убрать дым и огонь", pl => { ClearSmokes(); pl.PrintToChat($" {ChatColors.Grey}Дым и огонь убраны."); });
        page.Add("Поставить бота здесь", pl => { CloseMenu(pl); pl.ExecuteClientCommandFromServer("css_bot"); });
        page.Add("Убрать ботов", pl => { pl.ExecuteClientCommandFromServer("css_nobots"); });
        page.Add("Подписи и маркеры на карте: вкл/выкл", pl =>
        {
            if (_labels.Count > 0) { WorldText.Kill(_labels); ClearMarkers(pl.Slot); _activeLineup.Remove(pl.Slot); }
            else { _hideLabelsNear = null; RefreshLabels(); }
        });
        return page;
    }
}
