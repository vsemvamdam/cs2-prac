using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2Prac;

/// <summary>
/// Оружие, скины, ножи и агенты. Скины видны только на этом сервере: в инвентарь ничего не добавляется.
/// Применение скина повторяет подход cs2-WeaponPaints: fallback paint kit + атрибуты «set item texture …».
/// </summary>
public partial class CS2Prac
{
    private SkinsDb _skins = new();
    private List<AgentEntry> _agents = new();
    private readonly Dictionary<ulong, Loadout> _loadouts = new();
    private ulong _nextItemId = 65578;

    private static MemoryFunctionVoid<nint, string, float>? _setAttr;

    private string PlayersDir => Path.Combine(UserDir, "players");

    private void LoadSkinData()
    {
        _skins = Json.Read<SkinsDb>(Path.Combine(ModuleDirectory, "data", "skins.json")) ?? new SkinsDb();
        _skins.Index();
        _agents = Json.Read<List<AgentEntry>>(Path.Combine(ModuleDirectory, "data", "agents.json")) ?? new();
        try
        {
            _setAttr = new MemoryFunctionVoid<nint, string, float>(GameData.GetSignature("CAttributeList_SetOrAddAttributeValueByName"));
        }
        catch (Exception e)
        {
            PLog.Info($"Скины недоступны: нет сигнатуры CAttributeList_SetOrAddAttributeValueByName ({e.Message})");
            _setAttr = null;
        }
    }

    private Loadout LoadoutOf(CCSPlayerController p)
    {
        var id = p.SteamID;
        if (_loadouts.TryGetValue(id, out var l)) return l;
        l = Json.Read<Loadout>(Path.Combine(PlayersDir, $"{id}.json")) ?? new Loadout();
        if (Mode == "prefire")
        {
            // В префаере по умолчанию тот же набор, что выдавал OpenPrefirePrac: AK-47 и Desert Eagle.
            if (l.Primary == 0) l.Primary = 7;
            if (l.Secondary == 0) l.Secondary = 1;
        }
        _loadouts[id] = l;
        return l;
    }

    private void SaveLoadout(CCSPlayerController p) => Json.Write(Path.Combine(PlayersDir, $"{p.SteamID}.json"), LoadoutOf(p));

    private static bool IsKnifeClass(string? cls) => cls != null && (cls.Contains("knife") || cls.Contains("bayonet"));

    // ---------------------------------------------------------------- хук выдачи оружия
    private HookResult OnGiveNamedItemPost(DynamicHook hook)
    {
        try
        {
            var services = hook.GetParam<CCSPlayer_ItemServices>(0);
            var weapon = hook.GetReturn<CBasePlayerWeapon>();
            if (services == null || weapon == null || !weapon.IsValid) return HookResult.Continue;
            var pawn = services.Pawn.Value;
            var ctrl = pawn?.Controller.Value;
            if (ctrl == null) return HookResult.Continue;
            var player = new CCSPlayerController(ctrl.Handle);
            if (!player.IsValid || player.IsBot || player.IsHLTV || player.SteamID == 0) return HookResult.Continue;
            ApplySkin(player, weapon);
        }
        catch (Exception e)
        {
            PLog.Info($"GiveNamedItem: {e.Message}");
        }
        return HookResult.Continue;
    }

    private void ApplySkin(CCSPlayerController player, CBasePlayerWeapon weapon)
    {
        if (_setAttr == null) return;
        var lo = LoadoutOf(player);
        var item = weapon.AttributeManager.Item;
        var isKnife = IsKnifeClass(weapon.DesignerName);
        var changeKnife = isKnife && lo.KnifeDef > 0;
        var targetDef = changeKnife ? lo.KnifeDef : item.ItemDefinitionIndex;
        var hasPaint = lo.Paints.TryGetValue(targetDef, out var paint) && paint > 0;
        if (!changeKnife && !hasPaint) return; // этому оружию ничего не выбрано — не трогаем

        if (changeKnife)
        {
            if (item.ItemDefinitionIndex != lo.KnifeDef)
                weapon.AcceptInput("ChangeSubclass", value: lo.KnifeDef.ToString());
            item.ItemDefinitionIndex = (ushort)lo.KnifeDef;
        }
        try { item.EntityQuality = isKnife ? 3 : 0; } catch (Exception e) { WarnGuidelines(e); }

        var def = (int)item.ItemDefinitionIndex;
        if (!hasPaint)
        {
            NewItemId(item, player); // ванильный нож другой модели
            return;
        }

        item.AttributeList.Attributes.RemoveAll();
        item.NetworkedDynamicAttributes.Attributes.RemoveAll();
        NewItemId(item, player);
        weapon.FallbackPaintKit = paint;
        weapon.FallbackSeed = 0;
        weapon.FallbackWear = 0.0001f;
        foreach (var list in new[] { item.NetworkedDynamicAttributes.Handle, item.AttributeList.Handle })
        {
            _setAttr.Invoke(list, "set item texture prefab", paint);
            _setAttr.Invoke(list, "set item texture seed", 0);
            _setAttr.Invoke(list, "set item texture wear", 0.0001f);
        }
        var legacy = _skins.ByDef.TryGetValue(def, out var w) && w.Skins.FirstOrDefault(s => s.Paint == paint)?.Legacy == true;
        weapon.AcceptInput("SetBodygroup", value: $"body,{(legacy ? 1 : 0)}");
    }

    private bool _guidelinesWarned;

    private void WarnGuidelines(Exception e)
    {
        if (_guidelinesWarned) return;
        _guidelinesWarned = true;
        PLog.Info($"Скины ограничены настройкой CounterStrikeSharp: {e.Message}. Нужен FollowCS2ServerGuidelines=false в configs/core.json (установщик ставит его сам).");
    }

    private void NewItemId(CEconItemView item, CCSPlayerController player)
    {
        var id = _nextItemId++;
        item.ItemID = id;
        item.ItemIDLow = (uint)(id & 0xFFFFFFFF);
        item.ItemIDHigh = (uint)(id >> 32);
        item.AccountID = (uint)(player.SteamID & 0xFFFFFFFF);
    }

    // ---------------------------------------------------------------- выдача оружия
    private static gear_slot_t? SlotOf(CBasePlayerWeapon w)
    {
        try { return w.As<CCSWeaponBase>().VData?.GearSlot; } catch { return null; }
    }

    /// <summary>Убирает оружие из слота (основное / пистолет / нож) и через мгновение выдаёт новое — хук наденет скин.</summary>
    private void ReplaceInSlot(CCSPlayerController player, gear_slot_t slot, string cls, string? switchTo = null)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || !player.PawnIsAlive) return;
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons != null)
        {
            foreach (var h in weapons.ToList())
            {
                var w = h.Value;
                if (w == null || !w.IsValid) continue;
                if (SlotOf(w) == slot) w.AddEntityIOEvent("Kill", w, null, "", 0.05f);
            }
        }
        AddTimer(0.2f, () =>
        {
            if (!player.IsValid || !player.PawnIsAlive) return;
            player.GiveNamedItem(cls);
            if (switchTo != null) AddTimer(0.1f, () => { if (player.IsValid) player.ExecuteClientCommand(switchTo); });
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void GiveWeapon(CCSPlayerController player, WeaponEntry w)
    {
        var lo = LoadoutOf(player);
        var isPistol = _skins.Categories.TryGetValue("pistol", out var pistols) && pistols.Any(x => x.Def == w.Def);
        if (isPistol) lo.Secondary = w.Def; else lo.Primary = w.Def;
        SaveLoadout(player);
        ReplaceInSlot(player, isPistol ? gear_slot_t.GEAR_SLOT_PISTOL : gear_slot_t.GEAR_SLOT_RIFLE, w.Class, isPistol ? "slot2" : "slot1");
    }

    private void SetPaint(CCSPlayerController player, WeaponEntry w, int paint)
    {
        var lo = LoadoutOf(player);
        if (paint > 0) lo.Paints[w.Def] = paint; else lo.Paints.Remove(w.Def);
        if (IsKnifeClass(w.Class))
        {
            lo.KnifeDef = w.Def;
            SaveLoadout(player);
            RefreshKnife(player);
            return;
        }
        SaveLoadout(player);
        GiveWeapon(player, w);
    }

    private void RefreshKnife(CCSPlayerController player)
    {
        var cls = player.Team == CsTeam.CounterTerrorist ? "weapon_knife" : "weapon_knife_t";
        ReplaceInSlot(player, gear_slot_t.GEAR_SLOT_KNIFE, cls, "slot3");
    }

    private void SetAgent(CCSPlayerController player, AgentEntry? a)
    {
        var lo = LoadoutOf(player);
        if (player.Team == CsTeam.CounterTerrorist) lo.AgentCT = a?.Model ?? ""; else lo.AgentT = a?.Model ?? "";
        SaveLoadout(player);
        ApplyAgent(player);
        if (a == null) player.PrintToChat($" {ChatColors.Grey}Стандартный агент вернётся после возрождения.");
    }

    private void ApplyAgent(CCSPlayerController player)
    {
        var lo = LoadoutOf(player);
        var model = player.Team == CsTeam.CounterTerrorist ? lo.AgentCT : lo.AgentT;
        if (string.IsNullOrEmpty(model)) return;
        Server.NextFrame(() =>
        {
            var pawn = player.IsValid ? player.PlayerPawn.Value : null;
            if (pawn == null || !pawn.IsValid) return;
            try { pawn.SetModel($"agents/models/{model}.vmdl"); }
            catch (Exception e) { PLog.Info($"Агент {model}: {e.Message}"); }
        });
    }

    /// <summary>После возрождения: выбранное оружие, нож и агент.</summary>
    private void ApplyLoadoutOnSpawn(CCSPlayerController player)
    {
        var lo = LoadoutOf(player);
        AddTimer(0.25f, () =>
        {
            if (!player.IsValid || !player.PawnIsAlive) return;
            if (lo.Primary > 0 && _skins.ByDef.TryGetValue(lo.Primary, out var p))
                ReplaceInSlot(player, gear_slot_t.GEAR_SLOT_RIFLE, p.Class, "slot1");
            if (lo.Secondary > 0 && _skins.ByDef.TryGetValue(lo.Secondary, out var s))
                ReplaceInSlot(player, gear_slot_t.GEAR_SLOT_PISTOL, s.Class);
            if (lo.KnifeDef > 0) RefreshKnife(player);
            ApplyAgent(player);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    // ---------------------------------------------------------------- меню
    private static readonly (string key, string title)[] WeaponCats =
    {
        ("rifle", "Винтовки"), ("sniper", "Снайперские"), ("pistol", "Пистолеты"), ("smg", "Пистолеты-пулемёты"), ("heavy", "Тяжёлое"),
    };

    private MenuPage LoadoutPage(CCSPlayerController p)
    {
        var page = new MenuPage { Title = "Оружие и скины", Sub = "скины видны только на этом сервере", Back = () => MainPage(p) };
        page.Add("Популярные скины", pl => OpenMenu(pl, PopularPage(pl)), Color.FromArgb(255, 255, 196, 60));
        foreach (var (key, title) in WeaponCats)
        {
            var k = key; var t = title;
            page.Add(t, pl => OpenMenu(pl, CategoryPage(pl, k, t)));
        }
        page.Add("Нож", pl => OpenMenu(pl, KnivesPage(pl)));
        page.Add("Агент", pl => OpenMenu(pl, AgentsPage(pl)));
        page.Add("Сбросить всё", pl =>
        {
            _loadouts[pl.SteamID] = new Loadout();
            SaveLoadout(pl);
            pl.PrintToChat($" {ChatColors.Grey}Выбор оружия и скинов сброшен. Стандартные вещи — после возрождения.");
            OpenMenu(pl, LoadoutPage(pl));
        }, Color.FromArgb(255, 170, 178, 190));
        if (_setAttr == null) page.Add("Скины сейчас недоступны (нет сигнатуры после патча)", null, disabled: true);
        return page;
    }

    private MenuPage PopularPage(CCSPlayerController p)
    {
        var page = new MenuPage { Title = "Популярные скины", Back = () => LoadoutPage(p) };
        (int def, string en)[] picks =
        {
            (9, "Dragon Lore"), (9, "Gungnir"), (9, "Medusa"), (9, "Asiimov"), (7, "Fire Serpent"), (7, "Wild Lotus"),
            (7, "Gold Arabesque"), (7, "Vulcan"), (16, "Howl"), (16, "Poseidon"), (60, "Welcome to the Jungle"), (60, "Printstream"),
            (1, "Blaze"), (61, "Kill Confirmed"), (4, "Fade"), (507, "Fade"), (515, "Fade"), (508, "Doppler (Ruby)"),
            (507, "Doppler (Sapphire)"), (515, "Marble Fade"), (505, "Gamma Doppler (Emerald)"),
        };
        foreach (var (def, en) in picks)
        {
            if (!_skins.ByDef.TryGetValue(def, out var w)) continue;
            var s = w.Skins.FirstOrDefault(x => x.En == en);
            if (s == null) continue;
            page.Add($"{w.Name} | {s.En}", pl => { SetPaint(pl, w, s.Paint); CloseMenu(pl); Announce(pl, w, s); });
        }
        return page;
    }

    private void Announce(CCSPlayerController p, WeaponEntry w, SkinEntry? s) =>
        p.PrintToChat($" {ChatColors.Gold}{w.NameRu}{ChatColors.White}{(s != null ? " | " + SkinLabel(s) : " без скина")}");

    /// <summary>«Dragon Lore · История о драконе»: в игре и в разговорах скины чаще называют по-английски.</summary>
    private static string SkinLabel(SkinEntry s)
    {
        var ru = s.Ru.Split(" (")[0].Trim();
        return ru.Length == 0 || ru == s.En.Split(" (")[0] ? s.En : $"{s.En} · {ru}";
    }

    private MenuPage CategoryPage(CCSPlayerController p, string cat, string title)
    {
        var page = new MenuPage { Title = title, Back = () => LoadoutPage(p) };
        if (!_skins.Categories.TryGetValue(cat, out var list)) return page;
        var lo = LoadoutOf(p);
        foreach (var w in list)
        {
            var ww = w;
            var cur = lo.Paints.TryGetValue(w.Def, out var pt) ? w.Skins.FirstOrDefault(s => s.Paint == pt)?.En : null;
            page.Add(cur != null ? $"{w.NameRu}  ({cur})" : w.NameRu, pl => OpenMenu(pl, SkinsPage(pl, ww, () => CategoryPage(pl, cat, title))));
        }
        return page;
    }

    private MenuPage SkinsPage(CCSPlayerController p, WeaponEntry w, Func<MenuPage> back)
    {
        var page = new MenuPage { Title = w.NameRu, Sub = "сначала популярные", Back = back };
        var knife = IsKnifeClass(w.Class);
        page.Add(knife ? "Без скина" : "Взять без скина", pl => { SetPaint(pl, w, 0); CloseMenu(pl); Announce(pl, w, null); },
            Color.FromArgb(255, 170, 178, 190));
        foreach (var s in w.Skins)
        {
            var ss = s;
            page.Add(SkinLabel(s), pl => { SetPaint(pl, w, ss.Paint); CloseMenu(pl); Announce(pl, w, ss); },
                s.Popular ? Color.FromArgb(255, 255, 214, 120) : Color.White);
        }
        return page;
    }

    private MenuPage KnivesPage(CCSPlayerController p)
    {
        var page = new MenuPage { Title = "Нож", Back = () => LoadoutPage(p) };
        page.Add("Стандартный нож", pl =>
        {
            var lo = LoadoutOf(pl);
            lo.KnifeDef = 0;
            SaveLoadout(pl);
            RefreshKnife(pl);
            CloseMenu(pl);
        }, Color.FromArgb(255, 170, 178, 190));
        foreach (var k in _skins.Knives)
        {
            var kk = k;
            page.Add(k.NameRu, pl => OpenMenu(pl, SkinsPage(pl, kk, () => KnivesPage(pl))));
        }
        return page;
    }

    private MenuPage AgentsPage(CCSPlayerController p)
    {
        var team = p.Team == CsTeam.CounterTerrorist ? 3 : 2;
        var page = new MenuPage { Title = team == 3 ? "Агент спецназа" : "Агент террористов", Sub = "для текущей стороны", Back = () => LoadoutPage(p) };
        page.Add("Стандартный", pl => { SetAgent(pl, null); CloseMenu(pl); }, Color.FromArgb(255, 170, 178, 190));
        foreach (var a in _agents.Where(x => x.Team == team))
        {
            var aa = a;
            page.Add(a.Ru, pl => { SetAgent(pl, aa); CloseMenu(pl); });
        }
        return page;
    }
}
