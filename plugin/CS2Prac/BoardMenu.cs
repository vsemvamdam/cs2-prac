using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2Prac;

public sealed class MenuItem
{
    public string Text { get; init; } = "";
    public Action<CCSPlayerController>? Act { get; init; }
    public Color Color { get; init; } = Color.White;
    public bool Disabled { get; init; }
}

public sealed class MenuPage
{
    public string Title { get; init; } = "";
    public string Sub { get; init; } = "";
    public List<MenuItem> Items { get; } = new();
    /// <summary>Что открыть по «8 — Назад» на первой странице. null — закрыть меню.</summary>
    public Func<MenuPage>? Back { get; init; }
    public Color TitleColor { get; init; } = Color.FromArgb(255, 255, 196, 60);

    public MenuPage Add(string text, Action<CCSPlayerController>? act, Color? color = null, bool disabled = false)
    {
        Items.Add(new MenuItem { Text = text, Act = act, Color = color ?? Color.White, Disabled = disabled });
        return this;
    }
}

/// <summary>
/// Меню в виде табло в мире перед игроком. Пункты выбираются цифрами 1–9 (команды css_1…css_9 из
/// CounterStrikeSharp, на них забиндены клавиши) или выстрелом / ударом ножа по строке табло.
/// 1–7 — пункты, 8 — назад (на главной странице — восьмой пункт), 9 — ещё / закрыть.
/// </summary>
public sealed class BoardMenu : IMenuInstance
{
    public const int PerPage = 7;
    // Табло близко к игроку (чтобы не уходило за стены) и пропорционально мелкое: на экране тот же размер.
    const float Distance = 48f;
    const float K = Distance / 95f;
    const float Upp = 0.15f * K;          // мировых юнитов на пиксель шрифта
    const float TitleFont = 64f, SubFont = 34f, ItemFont = 44f;
    const float TitleH = 14f * K, SubH = 8.5f * K, ItemH = 8.5f * K, Gap = 2f * K, NavX = 22f * K;
    const float RaiseZ = 14f * K, ShiftLeft = 12f * K; // выше и левее, чтобы оружие в руках не закрывало табло

    private static readonly IMenu Dummy = new ChatMenu("CS2 Prac");
    private readonly CS2Prac _plugin;
    private readonly CCSPlayerController _player;
    private readonly List<CPointWorldText> _ents = new();
    private readonly Stack<int> _offsets = new();
    private MenuPage _page = new();
    private int _pageIdx;

    // Геометрия последнего показанного табло — для выбора выстрелом.
    private float[]? _center;
    private float _yaw;
    private readonly List<(float top, float bottom, float half, float across, int key)> _rows = new();

    public BoardMenu(CS2Prac plugin, CCSPlayerController player)
    {
        _plugin = plugin;
        _player = player;
    }

    IMenu IMenuInstance.Menu => Dummy;
    CCSPlayerController? IMenuInstance.Player => _player;
    bool IMenuInstance.CloseOnSelect => false;
    int IMenuInstance.Page => _pageIdx;
    int IMenuInstance.CurrentOffset => _pageIdx * PerPage;
    int IMenuInstance.NumPerPage => PerPage;
    Stack<int> IMenuInstance.PrevPageOffsets => _offsets;

    public CCSPlayerController Player => _player;
    public bool IsOpen => MenuManager.GetActiveMenu(_player) == this;
    // На корневой странице (без «Назад») клавиша 8 свободна, поэтому там помещается 8 пунктов.
    private int PerPageNow => _page.Back == null && _page.Items.Count == PerPage + 1 ? PerPage + 1 : PerPage;
    private int PageCount => Math.Max(1, (_page.Items.Count + PerPageNow - 1) / PerPageNow);

    public void Show(MenuPage page, bool keepPlacement = true)
    {
        _page = page;
        _pageIdx = 0;
        if (!keepPlacement) _center = null;
        if (!IsOpen)
        {
            MenuManager.CloseActiveMenu(_player);
            MenuManager.GetActiveMenus()[_player.Handle] = this;
            _center = null;
        }
        Display();
    }

    public void NextPage()
    {
        if (_pageIdx + 1 < PageCount) { _pageIdx++; Display(); }
    }

    public void PrevPage()
    {
        if (_pageIdx > 0) { _pageIdx--; Display(); }
    }

    /// <summary>Вызывается MenuManager при закрытии меню.</summary>
    public void Reset()
    {
        WorldText.Kill(_ents);
        _rows.Clear();
        _center = null;
    }

    public void Close() => MenuManager.CloseActiveMenu(_player);

    public void OnKeyPress(CCSPlayerController player, int key) => Press(player, key, false);

    private void Press(CCSPlayerController player, int key, bool byShot)
    {
        if (player.Handle != _player.Handle) return;
        if (!byShot) _plugin.OnMenuKeyUsed(player);   // css_N: клавиша-цифра или «!N» в чате — разбирается там

        if (key == 8 && PerPageNow == PerPage)
        {
            if (_pageIdx > 0) { _pageIdx--; Display(); }
            else if (_page.Back != null) Show(_page.Back());
            else Close();
            return;
        }
        if (key == 9)
        {
            if (_pageIdx + 1 < PageCount) { _pageIdx++; Display(); }
            else Close();
            return;
        }
        var idx = _pageIdx * PerPageNow + key - 1;
        if (key < 1 || key > PerPageNow || idx >= _page.Items.Count) return;
        var item = _page.Items[idx];
        if (item.Disabled || item.Act == null) return;
        item.Act(player);
    }

    public void Display()
    {
        WorldText.Kill(_ents);
        _rows.Clear();
        var pawn = _player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null) return;

        if (_center == null)
        {
            _yaw = pawn.EyeAngles.Y;
            var (fx, fy) = Geo.Forward2D(_yaw);
            var eyeZ = pawn.AbsOrigin.Z + pawn.ViewOffset.Z;
            var (lx, ly) = (MathF.Sin(_yaw * MathF.PI / 180f), -MathF.Cos(_yaw * MathF.PI / 180f));
            _center = new[] { pawn.AbsOrigin.X + fx * Distance - lx * ShiftLeft, pawn.AbsOrigin.Y + fy * Distance - ly * ShiftLeft, eyeZ + RaiseZ };
        }

        var first = _pageIdx * PerPageNow;
        var items = _page.Items.Skip(first).Take(PerPageNow).ToList();
        var hasMore = _pageIdx + 1 < PageCount;
        var total = TitleH + (string.IsNullOrEmpty(_page.Sub) && PageCount == 1 ? 0 : SubH) + ItemH * (items.Count + 1) + 2 * Gap;
        var z = _center[2] + total / 2f;
        var ang = WorldText.FacingViewer(_yaw);
        var (rx, ry) = (MathF.Sin(_yaw * MathF.PI / 180f), -MathF.Cos(_yaw * MathF.PI / 180f));

        Vector At(float across, float zz) => new(_center[0] + rx * across, _center[1] + ry * across, zz);

        Put(_page.Title.ToUpperInvariant(), 0, z, _page.TitleColor, TitleFont, true);
        z -= TitleH;
        var sub = _page.Sub;
        if (PageCount > 1) sub = (sub.Length > 0 ? sub + "   " : "") + $"стр. {_pageIdx + 1}/{PageCount}";
        if (sub.Length > 0)
        {
            Put(sub, 0, z, Color.FromArgb(255, 205, 212, 222), SubFont, true);
            z -= SubH;
        }
        z -= Gap;

        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var color = it.Disabled ? Color.FromArgb(255, 110, 110, 110) : it.Color;
            var text = $"{i + 1}   {it.Text}";
            Put(text, 0, z, color, ItemFont, true, i + 1);
            z -= ItemH;
        }

        var nav = Color.FromArgb(255, 150, 200, 255);
        var next = hasMore ? "9  Ещё >>" : "9  Закрыть";
        if (_pageIdx == 0 && _page.Back == null)
        {
            Put(next, 0f, z, nav, SubFont + 4, true, 9);
        }
        else
        {
            Put(_pageIdx > 0 ? "8  << Пред." : "8  << Назад", -NavX, z, nav, SubFont + 4, true, 8);
            Put(next, NavX, z, nav, SubFont + 4, true, 9);
        }

        void Put(string text, float across, float zz, Color c, float font, bool bg, int key = 0)
        {
            var e = WorldText.Spawn(text, At(across, zz), ang, c, font, Upp, bg);
            if (e != null) _ents.Add(e);
            if (key > 0)
            {
                var h = font * Upp;
                var half = Math.Max(6f * K, text.Length * font * Upp * 0.27f);
                _rows.Add((zz + K, zz - h - K, half, across, key));
            }
        }
    }

    /// <summary>Выбор пункта выстрелом: луч из глаз игрока пересекается с плоскостью табло.</summary>
    public bool TryHitByShot(float[] eye, float pitch, float yaw)
    {
        if (_center == null || _rows.Count == 0) return false;
        var (dx, dy, dz) = Geo.Dir(pitch, yaw);
        var (nx, ny) = Geo.Forward2D(_yaw);               // нормаль плоскости табло (горизонтальная)
        var denom = dx * nx + dy * ny;
        if (MathF.Abs(denom) < 1e-3f) return false;
        var t = ((_center[0] - eye[0]) * nx + (_center[1] - eye[1]) * ny) / denom;
        if (t <= 0 || t > 400) return false;
        var hx = eye[0] + dx * t; var hy = eye[1] + dy * t; var hz = eye[2] + dz * t;
        var (rx, ry) = (MathF.Sin(_yaw * MathF.PI / 180f), -MathF.Cos(_yaw * MathF.PI / 180f));
        var across = (hx - _center[0]) * rx + (hy - _center[1]) * ry;
        foreach (var r in _rows)
        {
            if (hz <= r.top && hz >= r.bottom && MathF.Abs(across - r.across) <= r.half)
            {
                Press(_player, r.key, true);
                return true;
            }
        }
        return false;
    }
}
