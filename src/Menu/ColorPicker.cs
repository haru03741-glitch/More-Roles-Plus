using System;
using HarmonyLib;
using MoreRolesPlus.Options;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// 自分の色を作る所 (本編の着せ替え画面の色のタブ。本編の 18 色の右の空いた所に置く)。
//   彩度 (横) × 明るさ (縦) の四角と、その下の色相の帯。押したまま動かすと右の人形がその色になり、本編の「装備」で決まる
//   #RRGGBB の入力欄は部屋の中だけ (チャットの入力欄を写して作る)。本編の色を押す / 装備すると作った色をやめる
// 四角の絵は色相が変わった時だけ作り直す (動かしている間は 50ms に 1 回まで)。毎フレームの処理は色のタブを開いている間だけ
internal static class ColorPicker
{
    private const int SvPx = 64, HuePx = 128;
    private const float SvSize = 2.2f, HueH = 0.26f;
    private static readonly Vector2 SvAt = new(0f, 0.3f), HueAt = new(0f, -1.15f);

    private static GameObject _root;
    private static PlayerTab _tab;
    private static SpriteRenderer _sv, _hue, _svMark, _hueMark;
    private static Texture2D _svTex;     // 四角の絵と帯の絵は起動中ずっと使い回す (着せ替え画面は開くたびに作り直される)
    private static Sprite _svSprite, _hueSprite;
    private static readonly byte[] SvBytes = new byte[SvPx * SvPx * 4];
    private static SearchBox _hex;
    private static float _h, _s = 1f, _v = 1f, _bakedH = -1f;
    private static long _bakedAt, _previewAt;
    private static int _drag; // 0 = なし・1 = 四角・2 = 色相の帯
    private static int _lastChip = -1;
    private static bool _writingHex;
    private static bool _customSelected; // 本編の色でなく作った色を選んでいる
    private static bool _open;      // 毎フレームの問い合わせはこの値だけ
    private static string _shownHex = "";

    public static bool IsOpen => _open && _root;
    // 動かした色がまだ決まっていない (本編の「装備」を出す)
    public static bool Pending { get; private set; }

    // 色のタブが開いた (本編の OnEnable の後)
    public static void OnTabEnable(PlayerTab tab)
    {
        if (!_root || _tab == null || _tab.Pointer != tab.Pointer) Build(tab);
        if (!_root) return;
        _tab = tab;
        _root.SetActive(true);
        _open = true;
        if (Pending) CustomColors.Rebuild(); // 別のタブへ移る前に決めなかった見本の色を戻す
        Pending = false;
        _drag = 0;
        _lastChip = tab.currentColor;
        MakeHex();
        _shownHex = "";
        PaintChips();
        // 作った色があればそれを選んだ状態から始める (無ければ本編が選んだ色)
        _customSelected = CustomColors.HasMine(out var mine);
        FromColor(_customSelected ? mine : CustomColors.VanillaBody(tab.currentColor));
        Refresh(true, _customSelected);
    }

    private static void Close()
    {
        _open = false;
        _drag = 0;
        if (!Pending) return;
        Pending = false;
        CustomColors.Rebuild(); // 決めなかった見本の色を戻す
    }

    private static int MySlot()
    {
        var me = PlayerControl.LocalPlayer;
        if (me && me.Data != null && me.Data.DefaultOutfit != null) return me.Data.DefaultOutfit.ColorId;
        return _tab ? _tab.currentColor : 0;
    }

    private static void Build(PlayerTab tab)
    {
        if (_root) Object.Destroy(_root);
        _tab = tab;
        int layer = tab.gameObject.layer;
        _root = new GameObject("MrpColorPicker") { layer = layer };
        _root.transform.SetParent(tab.transform, false);

        // 本編の色の右端と、右の人形の左の間の真ん中に置く (どちらも画面の縦横比で動くので、開くたびに測る)
        float left = 0f, top = 1.65f;
        var chips = tab.ColorChips;
        for (int i = 0; chips != null && i < chips.Count; i++)
        {
            var c = chips[i];
            if (!c) continue;
            left = Math.Max(left, c.transform.localPosition.x + 0.45f);
            top = Math.Max(top, c.transform.localPosition.y);
        }
        float right = left + 3.4f;
        if (tab.PlayerPreview) right = tab.transform.InverseTransformPoint(tab.PlayerPreview.transform.position).x - 1.4f;
        _root.transform.localPosition = new Vector3((left + right) / 2f, top - 1.55f, -1f);

        if (!_svTex)
        {
            _svTex = new Texture2D(SvPx, SvPx, TextureFormat.RGBA32, false) { name = "MrpColorSv", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _svTex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _bakedH = -1f;
        }
        if (!_svSprite)
        {
            _svSprite = UnityEngine.Sprite.Create(_svTex, new Rect(0, 0, SvPx, SvPx), new Vector2(0.5f, 0.5f), SvPx / SvSize);
            _svSprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        }
        if (!_hueSprite)
        {
            _hueSprite = UnityEngine.Sprite.Create(HueTex(), new Rect(0, 0, HuePx, 1), new Vector2(0.5f, 0.5f), HuePx / SvSize);
            _hueSprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        }
        _bakedH = -1f;
        _sv = Sprite(layer, "Sv", _svSprite, new Vector3(SvAt.x, SvAt.y, 0f));
        _hue = Sprite(layer, "Hue", _hueSprite, new Vector3(HueAt.x, HueAt.y, 0f));
        _hue.transform.localScale = new Vector3(1f, HueH * HuePx / SvSize, 1f);

        // 印: 白い丸に黒いふち
        var t = _root.transform;
        MenuArt.Panel(t, "SvMarkEdge", 0.1f, new Vector2(0.2f, 0.2f), Vector3.zero, Color.black, null);
        _svMark = MenuArt.Panel(t, "SvMark", 0.07f, new Vector2(0.14f, 0.14f), Vector3.zero, Color.white, null);
        Behind(t.Find("SvMarkEdge"), _svMark.transform);
        MenuArt.Panel(t, "HueMarkEdge", 0.06f, new Vector2(0.16f, HueH + 0.16f), Vector3.zero, Color.black, null);
        _hueMark = MenuArt.Panel(t, "HueMark", 0.04f, new Vector2(0.1f, HueH + 0.1f), Vector3.zero, Color.white, null);
        Behind(t.Find("HueMarkEdge"), _hueMark.transform);

        // 文字は本編のタブの見出し (「色」) を写して作る
        var proto = tab.transform.Find("Text");
        if (proto)
        {
            var title = Label(proto.gameObject, layer, new Vector3(0f, SvAt.y + SvSize / 2f + 0.3f, 0f), new Text("色を作る", "Make a color"));
            title.fontSize *= 0.7f;
        }
    }

    // ふちを印の子にして少し奥へ置く (同じ奥行きだと描く順が決まらず、ふちが印を隠すことがある)
    private static void Behind(Transform edge, Transform mark)
    {
        edge.SetParent(mark, false);
        edge.localPosition = new Vector3(0f, 0f, 0.01f);
    }

    // #hex の欄は開くたびに作り直す (入力欄の部品は 1 つしか文字の変化を受け取れず、設定画面の検索欄を開くとそちらへ移るため)。
    // チャットが無い所 (メインメニューの着せ替え) では作らない
    private static void MakeHex()
    {
        if (_hex != null && _hex.Root) Object.Destroy(_hex.Root);
        _hex = SearchBox.Create(_root.transform, new Vector3(0f, HueAt.y - 0.55f, 0f), SvSize, 0.4f, new Text("#RRGGBB を入れる", "Type #RRGGBB"), OnHex);
        var glass = _hex != null && _hex.Root ? _hex.Root.transform.Find("MrpSearchIcon") : null;
        if (glass) glass.gameObject.SetActive(false);
    }

    // 本編の色の見本は本編の色のまま塗る (自分の色番号の所は作った色に書き換えてあるため)
    private static void PaintChips()
    {
        var chips = _tab ? _tab.ColorChips : null;
        for (int i = 0; chips != null && i < chips.Count; i++)
        {
            var c = chips[i];
            var sr = c ? c.GetComponent<SpriteRenderer>() : null;
            if (sr) sr.color = CustomColors.VanillaBody(i);
        }
    }

    private static SpriteRenderer Sprite(int layer, string name, Sprite sp, Vector3 pos)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = pos;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        return sr;
    }

    private static TextMeshPro Label(GameObject proto, int layer, Vector3 pos, string text)
    {
        var go = Object.Instantiate(proto, _root.transform);
        go.name = "Label";
        go.layer = layer;
        var tr = go.GetComponent<TextTranslatorTMP>();
        if (tr) Object.Destroy(tr);
        go.transform.localPosition = pos;
        var tmp = go.GetComponent<TextMeshPro>();
        tmp.text = text;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        return tmp;
    }

    // ---- 色 ----

    private static Color32 Current() => Hsv(_h, _s, _v);

    private static Color32 Hsv(float h, float s, float v)
    {
        h = (h - MathF.Floor(h)) * 6f;
        int i = (int)h;
        float f = h - i, p = v * (1f - s), q = v * (1f - s * f), t = v * (1f - s * (1f - f));
        float r, g, b;
        switch (i % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        return new Color32((byte)(r * 255f + 0.5f), (byte)(g * 255f + 0.5f), (byte)(b * 255f + 0.5f), 255);
    }

    private static void FromColor(Color32 c)
    {
        float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b)), d = max - min;
        _v = max;
        _s = max > 0f ? d / max : 0f;
        if (d > 0f)
        {
            float h = max == r ? (g - b) / d : max == g ? 2f + (b - r) / d : 4f + (r - g) / d;
            _h = (h / 6f + 1f) % 1f;
        }
    }

    private static unsafe Texture2D HueTex()
    {
        var px = new byte[HuePx * 4];
        for (int x = 0; x < HuePx; x++)
        {
            var c = Hsv(x / (float)HuePx, 1f, 1f);
            px[x * 4] = c.r; px[x * 4 + 1] = c.g; px[x * 4 + 2] = c.b; px[x * 4 + 3] = 255;
        }
        var tex = new Texture2D(HuePx, 1, TextureFormat.RGBA32, false) { name = "MrpColorHue", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return tex;
    }

    // 四角の絵 (今の色相で、横 = 彩度・縦 = 明るさ)
    private static unsafe void BakeSv()
    {
        var hue = Hsv(_h, 1f, 1f);
        float hr = hue.r, hg = hue.g, hb = hue.b;
        for (int y = 0; y < SvPx; y++)
        {
            float v = y / (float)(SvPx - 1);
            for (int x = 0; x < SvPx; x++)
            {
                float s = x / (float)(SvPx - 1);
                int i = (y * SvPx + x) * 4;
                SvBytes[i] = (byte)((255f + (hr - 255f) * s) * v);
                SvBytes[i + 1] = (byte)((255f + (hg - 255f) * s) * v);
                SvBytes[i + 2] = (byte)((255f + (hb - 255f) * s) * v);
                SvBytes[i + 3] = 255;
            }
        }
        fixed (byte* p = SvBytes) _svTex.LoadRawTextureData((IntPtr)p, SvBytes.Length);
        _svTex.Apply(false, false);
        _bakedH = _h;
        _bakedAt = Environment.TickCount64;
    }

    // 印・入力欄を今の色に合わせる。changed = 人が色を動かした (右の人形を見本の色にして「装備」を出す)。
    // force でなければ四角の絵と人形の塗り直しは 50ms に 1 回まで
    private static void Refresh(bool force, bool changed = false)
    {
        long now = Environment.TickCount64;
        if (_bakedH != _h && (force || now - _bakedAt >= 50)) BakeSv();
        float half = SvSize / 2f;
        _svMark.transform.localPosition = new Vector3(SvAt.x - half + _s * SvSize, SvAt.y - half + _v * SvSize, -2f);
        _hueMark.transform.localPosition = new Vector3(HueAt.x - half + _h * SvSize, HueAt.y, -2f);
        var c = Current();
        string hex = "#" + CustomColors.Hex(c);
        if (_hex != null && hex != _shownHex)
        {
            _shownHex = hex;
            _writingHex = true;
            _hex.Type(hex);
            _writingHex = false;
        }
        if (!changed) return;
        _customSelected = true;
        Pending = !IsMine(c);
        if (!force && now - _previewAt < 50) return;
        _previewAt = now;
        var preview = _tab ? _tab.PlayerPreview : null;
        CustomColors.PreviewSlot(MySlot(), c, preview ? preview.cosmetics : null);
        var menu = PlayerCustomizationMenu.Instance;
        if (menu) menu.SetItemName("");
    }

    private static bool IsMine(Color32 c) => CustomColors.HasMine(out var m) && m.r == c.r && m.g == c.g && m.b == c.b;

    private static bool _saying;

    private static void Say(string msg)
    {
        var menu = PlayerCustomizationMenu.Instance;
        if (!menu) return;
        _saying = true;
        try { menu.SetItemName(msg); }
        finally { _saying = false; }
    }

    // 本編が着せ替え画面の名前を書く時: 作った色を選んでいる間は作った色の名前にする
    public static void OnSetItemName(ref string text)
    {
        if (_saying || !IsOpen || !_customSelected) return;
        text = CustomColors.NameOf(Current());
    }

    private static void OnHex(string q)
    {
        if (_writingHex || !CustomColors.TryParse(q, out var c)) return;
        FromColor(c);
        _writingHex = true;
        try { Refresh(true, true); }
        finally { _writingHex = false; }
    }

    // 試合中と、ホストが名前と色の変更を止めている部屋では変えない (/color と同じ)
    private static bool Blocked()
    {
        string msg = null;
        if (ShipStatus.Instance) msg = new Text("色は試合の外 (ロビー) で変えてください", "Change your color outside a game (in the lobby)");
        else if (AmongUsClient.Instance && AmongUsClient.Instance.AmConnected && !AmongUsClient.Instance.AmHost && !Commands.HostSettings.PlayersCanRename)
            msg = new Text("この部屋ではホストが名前と色の変更を止めています", "The host has turned off name and color changes");
        if (msg == null) return false;
        Say(msg);
        return true;
    }

    // 本編の色のタブの「装備」。true = 作った色を決めた (本編の処理は止める)
    public static bool OnEquip()
    {
        if (!IsOpen) return false;
        if (!_customSelected)
        {
            // 本編の色を装備した: 作った色はやめる
            if (CustomColors.HasMine(out _) && !Blocked()) CustomColors.SetMine(null);
            return false;
        }
        if (!Pending || Blocked()) return true;
        Pending = false;
        CustomColors.SetMine(Current());
        return true;
    }

    // 「装備」の出し分けを MRP が決めるか。作った色を選んでいる = 決めた色と同じなら装備中。
    // 本編の色を選んでいて作った色を使っている = 同じ色番号でも「装備」を出す (装備すると作った色をやめる)
    public static bool Equipped(out bool equipped)
    {
        if (_customSelected) { equipped = !Pending && IsMine(Current()); return true; }
        equipped = false;
        return CustomColors.HasMine(out _);
    }

    // 本編の色を押した (タブの今の色番号が変わった): 動かしていた色をやめて、押した色から始める
    public static void CheckChip(PlayerTab tab)
    {
        if (!_open || _tab == null || tab.Pointer != _tab.Pointer) return;
        int cur = tab.currentColor;
        if (cur == _lastChip) return;
        _lastChip = cur;
        _customSelected = false;
        if (Pending)
        {
            Pending = false;
            CustomColors.Rebuild();
        }
        FromColor(CustomColors.VanillaBody(cur));
        Refresh(true);
    }

    // ---- 押したまま動かす (色のタブを開いている間だけ) ----

    public static void Tick()
    {
        if (!_open) return;
        if (!_root || !_root.activeInHierarchy) { Close(); return; }
        if (Input.GetMouseButtonUp(0))
        {
            if (_drag != 0) Refresh(true, true);
            _drag = 0;
            return;
        }
        bool down = Input.GetMouseButtonDown(0);
        if (!down && _drag == 0) return;
        if (!Input.GetMouseButton(0)) { _drag = 0; return; }
        var cam = Camera.main;
        if (!cam) return;
        var local = _root.transform.InverseTransformPoint(cam.ScreenToWorldPoint(Input.mousePosition));
        float half = SvSize / 2f;
        float u = (local.x - (SvAt.x - half)) / SvSize;
        if (down)
        {
            float w = (local.y - (SvAt.y - half)) / SvSize;
            if (u >= 0f && u <= 1f && w >= 0f && w <= 1f) _drag = 1;
            else if (u >= 0f && u <= 1f && Math.Abs(local.y - HueAt.y) <= HueH) _drag = 2;
            else return;
        }
        u = Math.Clamp(u, 0f, 1f);
        if (_drag == 1)
        {
            _s = u;
            _v = Math.Clamp((local.y - (SvAt.y - half)) / SvSize, 0f, 1f);
        }
        else _h = Math.Min(u, 0.999f);
        Refresh(false, true);
    }

    // 確認用: 色を置く (動かしたのと同じ) / 装備を押す
    internal static string Simulate(string args)
    {
        if (args == "equip") { if (_tab) _tab.ClickEquip(); }
        else if (CustomColors.TryParse(args, out var c)) { FromColor(c); if (IsOpen) Refresh(true, true); }
        else if (args.StartsWith("type ")) { if (_hex != null) _hex.Type(args[5..]); }
        else if (args.Length > 0) return "ERR picker [equip | type <文> | #RRGGBB] (着せ替え画面の色のタブを開いておく)";
        var menu = PlayerCustomizationMenu.Instance;
        return $"OK picker open={IsOpen} pending={Pending} chip={(_tab ? _tab.currentColor : -1)} h={_h:F3} s={_s:F3} v={_v:F3} color=#{CustomColors.Hex(Current())} custom={_customSelected} item=[{(menu && menu.itemName ? menu.itemName.text : "")}]";
    }
}

[HarmonyPatch(typeof(PlayerTab))]
internal static class ColorPickerPatches
{
    [HarmonyPatch(nameof(PlayerTab.OnEnable)), HarmonyPostfix]
    public static void Enable(PlayerTab __instance)
    {
        try { ColorPicker.OnTabEnable(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"color picker: {e}"); }
    }

    // 本編は毎フレームこれで「装備」と「装備中」を出し分ける
    [HarmonyPatch(nameof(PlayerTab.IsSelectedItemEquipped)), HarmonyPrefix]
    public static bool Equipped(PlayerTab __instance, ref bool __result)
    {
        if (!ColorPicker.IsOpen) return true;
        ColorPicker.CheckChip(__instance);
        if (!ColorPicker.Equipped(out bool equipped)) return true;
        __result = equipped;
        return false;
    }

    [HarmonyPatch(nameof(PlayerTab.ClickEquip)), HarmonyPrefix]
    public static bool Equip() => !ColorPicker.OnEquip();
}

[HarmonyPatch(typeof(PlayerCustomizationMenu))]
internal static class ColorPickerNamePatch
{
    [HarmonyPatch(nameof(PlayerCustomizationMenu.SetItemName)), HarmonyPrefix]
    public static void SetItemName([HarmonyArgument(0)] ref string text) => ColorPicker.OnSetItemName(ref text);
}
