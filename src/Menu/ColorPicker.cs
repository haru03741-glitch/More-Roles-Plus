using System;
using MoreRolesPlus.Options;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// 自分の色を作る板 (Esc →「MRP の設定」→「自分の色を作る」)。
//   左: 彩度 (横) × 明るさ (縦) の四角と、その下の色相の帯。押したまま動かすと色が変わる
//   右: できた色の見本・#RRGGBB の入力欄 (部屋の中だけ。チャットの入力欄を写して作る)・決める・本編の色に戻す
// 四角の絵は色相が変わった時だけ作り直す (動かしている間は 50ms に 1 回まで)。毎フレームの処理は板を開いている間だけ
internal static class ColorPicker
{
    private const int SvPx = 64, HuePx = 128;
    private const float SvSize = 2.6f, HueH = 0.3f;
    private static readonly Vector2 SvAt = new(-1.5f, 0.45f), HueAt = new(-1.5f, -1.35f);

    private static SpriteRenderer _panel, _sv, _hue, _preview, _svMark, _hueMark;
    private static Texture2D _svTex;     // 四角の絵と帯の絵は起動中ずっと使い回す (板は画面ごとに作り直される)
    private static Sprite _svSprite, _hueSprite;
    private static readonly byte[] SvBytes = new byte[SvPx * SvPx * 4];
    private static TextMeshPro _status;
    private static SearchBox _hex;
    private static float _h, _s = 1f, _v = 1f, _bakedH = -1f;
    private static long _bakedAt;
    private static int _drag; // 0 = なし・1 = 四角・2 = 色相の帯
    private static bool _writingHex;
    private static UnityAction _apply, _reset, _back;
    private static bool _open;      // 毎フレームの問い合わせはこの値だけ
    private static string _shownHex = "";

    public static bool IsOpen => _open && _panel;

    public static void Show()
    {
        var proto = ClientOptions.Proto;
        if (!proto) return;
        if (!_panel)
        {
            var menu = proto.GetComponentInParent<OptionsMenuBehaviour>();
            if (!menu) return;
            Build(menu, proto);
        }
        ClientOptions.ShowPanel(false);
        ClientOptions.HideTab(true);
        MakeHex();
        _panel.gameObject.SetActive(true);
        _open = true;
        // 今の自分の色から始める (作った色が無ければ本編の色)
        Color32 c = CustomColors.HasMine(out var mine) ? mine : LocalVanillaColor();
        FromColor(c);
        _status.text = "";
        _shownHex = "";
        Refresh(true);
    }

    public static void Hide()
    {
        if (_panel) _panel.gameObject.SetActive(false);
        _open = false;
        _drag = 0;
    }

    private static Color32 LocalVanillaColor()
    {
        var me = PlayerControl.LocalPlayer;
        int id = me && me.Data != null && me.Data.DefaultOutfit != null ? me.Data.DefaultOutfit.ColorId : 0;
        var pc = Palette.PlayerColors;
        return id >= 0 && id < pc.Length ? pc[id] : new Color32(0, 183, 255, 255);
    }

    private static void Build(OptionsMenuBehaviour menu, ToggleButtonBehaviour proto)
    {
        _panel = ClientOptions.NewPanel(menu, "MrpColorPicker");
        int layer = _panel.gameObject.layer;

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
        _sv = Sprite(layer, "Sv", _svSprite, new Vector3(SvAt.x, SvAt.y, -2f));
        _hue = Sprite(layer, "Hue", _hueSprite, new Vector3(HueAt.x, HueAt.y, -2f));
        _hue.transform.localScale = new Vector3(1f, HueH * HuePx / SvSize, 1f);

        // 印: 白い丸に黒いふち
        MenuArt.Panel(_panel.transform, "SvMarkEdge", 0.1f, new Vector2(0.2f, 0.2f), Vector3.zero, Color.black, null);
        _svMark = MenuArt.Panel(_panel.transform, "SvMark", 0.07f, new Vector2(0.14f, 0.14f), Vector3.zero, Color.white, null);
        _panel.transform.Find("SvMarkEdge").SetParent(_svMark.transform, true);
        MenuArt.Panel(_panel.transform, "HueMarkEdge", 0.06f, new Vector2(0.16f, HueH + 0.16f), Vector3.zero, Color.black, null);
        _hueMark = MenuArt.Panel(_panel.transform, "HueMark", 0.04f, new Vector2(0.1f, HueH + 0.1f), Vector3.zero, Color.white, null);
        _panel.transform.Find("HueMarkEdge").SetParent(_hueMark.transform, true);

        _preview = MenuArt.Panel(_panel.transform, "Preview", 0.2f, new Vector2(2.4f, 1.1f), new Vector3(1.75f, 1.2f, -2f), Color.white, null);

        var title = Label(proto, layer, new Vector3(1.75f, 2.2f, -2f), new Text("自分の色を作る", "Make your color"));
        title.fontSize *= 1.2f;
        _status = Label(proto, layer, new Vector3(1.75f, -2.3f + 0.55f, -2f), "");
        _status.fontSize *= 0.8f;


        var apply = ClientOptions.MakeButton(proto, _panel.transform, "Apply", new Text("この色にする", "Use this color"), out var applyBack, out var applyRoll);
        apply.transform.localPosition = new Vector3(1.75f, -0.5f, -6f);
        Tint(applyBack, applyRoll, new Color32(0, 165, 255, 255));
        ClientOptions.SetClick(apply, _apply ??= (UnityAction)Apply);

        var reset = ClientOptions.MakeButton(proto, _panel.transform, "Reset", new Text("本編の色に戻す", "Use the game's color"), out var resetBack, out var resetRoll);
        reset.transform.localPosition = new Vector3(1.75f, -1.1f, -6f);
        Tint(resetBack, resetRoll, new Color32(77, 77, 77, 255));
        ClientOptions.SetClick(reset, _reset ??= (UnityAction)Reset);

        var back = ClientOptions.MakeButton(proto, _panel.transform, "Back", new Text("戻る", "Back"), out var backBack, out _);
        back.transform.localPosition = new Vector3(2.6f, -2.3f, -6f);
        backBack.color = Palette.DisabledGrey;
        ClientOptions.SetClick(back, _back ??= (UnityAction)(() =>
        {
            Hide();
            ClientOptions.ShowPanel(true);
        }));
    }

    // #hex の欄は開くたびに作り直す (入力欄の部品は 1 つしか文字の変化を受け取れず、設定画面の検索欄を開くとそちらへ移るため)
    private static void MakeHex()
    {
        if (_hex != null && _hex.Root) Object.Destroy(_hex.Root);
        _hex = SearchBox.Create(_panel.transform, new Vector3(1.75f, 0.2f, -2f), 2.4f, 0.45f, new Text("#RRGGBB を入れる", "Type #RRGGBB"), OnHex);
        var glass = _hex != null && _hex.Root ? _hex.Root.transform.Find("MrpSearchIcon") : null;
        if (glass) glass.gameObject.SetActive(false);
    }

    private static void Tint(SpriteRenderer back, ButtonRolloverHandler roll, Color c)
    {
        if (back) back.color = c;
        if (roll) roll.ChangeOutColor(c);
    }

    private static SpriteRenderer Sprite(int layer, string name, Sprite sp, Vector3 pos)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(_panel.transform, false);
        go.transform.localPosition = pos;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        return sr;
    }

    private static TextMeshPro Label(ToggleButtonBehaviour proto, int layer, Vector3 pos, string text)
    {
        var go = Object.Instantiate(proto.Text.gameObject, _panel.transform);
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

    // 印・見本・入力欄を今の色に合わせる。force でなければ四角の絵は 50ms に 1 回まで
    private static void Refresh(bool force)
    {
        if (_bakedH != _h && (force || Environment.TickCount64 - _bakedAt >= 50)) BakeSv();
        float half = SvSize / 2f;
        _svMark.transform.localPosition = new Vector3(SvAt.x - half + _s * SvSize, SvAt.y - half + _v * SvSize, -3f);
        _hueMark.transform.localPosition = new Vector3(HueAt.x - half + _h * SvSize, HueAt.y, -3f);
        var c = Current();
        _preview.color = c;
        string hex = "#" + CustomColors.Hex(c);
        if (_hex != null && hex != _shownHex)
        {
            _shownHex = hex;
            _writingHex = true;
            _hex.Type(hex);
            _writingHex = false;
        }
    }

    private static void OnHex(string q)
    {
        if (_writingHex || !CustomColors.TryParse(q, out var c)) return;
        FromColor(c);
        _writingHex = true;
        try { Refresh(true); }
        finally { _writingHex = false; }
    }

    // 試合中と、ホストが名前と色の変更を止めている部屋では変えない (/color と同じ)
    private static bool Blocked()
    {
        if (ShipStatus.Instance) _status.text = new Text("色は試合の外 (ロビー) で変えてください", "Change your color outside a game (in the lobby)");
        else if (AmongUsClient.Instance && AmongUsClient.Instance.AmConnected && !AmongUsClient.Instance.AmHost && !Commands.HostSettings.PlayersCanRename)
            _status.text = new Text("この部屋ではホストが名前と色の変更を止めています", "The host has turned off name and color changes");
        else return false;
        return true;
    }

    private static void Apply()
    {
        if (Blocked()) return;
        var c = Current();
        CustomColors.SetMine(c);
        _status.text = new Text($"#{CustomColors.Hex(c)} にしました", $"Now using #{CustomColors.Hex(c)}");
    }

    private static void Reset()
    {
        if (Blocked()) return;
        CustomColors.SetMine(null);
        FromColor(LocalVanillaColor());
        Refresh(true);
        _status.text = new Text("本編の色に戻しました", "Back to the game's color");
    }

    // ---- 押したまま動かす (板を開いている間だけ) ----

    public static void Tick()
    {
        if (!_open) return;
        if (!_panel) { _open = false; return; }
        if (Input.GetMouseButtonUp(0))
        {
            if (_drag != 0) Refresh(true);
            _drag = 0;
            return;
        }
        bool down = Input.GetMouseButtonDown(0);
        if (!down && _drag == 0) return;
        if (!Input.GetMouseButton(0)) { _drag = 0; return; }
        var cam = Camera.main;
        if (!cam) return;
        var local = _panel.transform.InverseTransformPoint(cam.ScreenToWorldPoint(Input.mousePosition));
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
        Refresh(false);
    }

    // 確認用: 色を置く / 決める
    internal static string Simulate(string args)
    {
        if (args == "open") Show();
        else if (args == "apply") Apply();
        else if (args == "reset") Reset();
        else if (CustomColors.TryParse(args, out var c)) { FromColor(c); if (IsOpen) Refresh(true); }
        else if (args.StartsWith("type ")) { if (_hex != null) _hex.Type(args[5..]); }
        else if (args.Length > 0) return "ERR picker [open | apply | reset | type <文> | #RRGGBB]";
        return $"OK picker open={IsOpen} h={_h:F3} s={_s:F3} v={_v:F3} color=#{CustomColors.Hex(Current())} status=[{(_status ? _status.text : "")}]";
    }
}
