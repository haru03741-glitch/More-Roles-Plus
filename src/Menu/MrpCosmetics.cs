// The 300x375 hat picture format (pivot 0.53/0.575, width * 0.375 px per unit) follows
// https://github.com/TheOtherRolesAU/TheOtherRoles TheOtherRoles/Modules/CustomHats (GPL-3.0) and SuperNewRoles' custom cosmetics.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using InnerNet;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// MRP だけの帽子とバイザー (作ってくださった方の絵を DLL に埋め込んだ物)。
// 本編の帽子の一覧 (HatManager) には足さない。本編が着せ替えの作りを変えても壊れないように、
// サーバーに見える着せ替えは本編の物のまま、「MRP の帽子を何にしたか」だけを MRP の電文で全員に配り、
// 受け取った端末が本編の帽子の絵の所 (HatParent の FrontLayer) を毎フレーム MRP の絵に差し替える。
// 帽子は 2 つ重ねられる (1 つ目は本編の帽子の絵の所、2 つ目はその手前に 1 枚足して描く)。
// バイザーは帽子と同じ枠の絵なので、帽子の後ろ (体の手前) に 1 枚足して描き、本編のバイザーは隠す。
// 外した時は本編の帽子・バイザーを付け直して元に戻す
internal static class MrpCosmetics
{
    internal enum Kind : byte { Hat, Visor }

    internal sealed class Item
    {
        public string Id, Author;
        public Kind Kind;
        public bool Adaptive;              // 赤・青の所を体の色・影の色で塗る
        public string Front, Climb, ClimbLeft; // Resources/Cosmetics の絵の名前 (梯子の絵は無ければ梯子の間は出さない)
        public bool FullBody;              // 体を丸ごと包む物 (着ている間は中の体・肌着を描かない。はみ出して見えるので)
        public bool ClimbSteps;            // 梯子の絵に足を動かす絵 (<Climb>0..3 / <ClimbLeft>0..3) がある
        // 全身の物の口の中 (中の人のバイザーが見える所) の真ん中 (絵の左上からの px) と、そこへ入れるバイザーの大きさ。
        // バイザーをこれより奥にすると、口を透かした絵 (<名前>_m) の奥にバイザーを縮めて口へ合わせて描く
        public Vector2? Mouth;
        public float MouthScale = 1f;
        public string Walk;                // 全身の物の歩きの絵 (<Walk>0..<WalkFrames-1>)。無ければ歩く間も正面の絵
        public string NameKey => "Cos." + Id;
    }

    internal static readonly Item[] All =
    {
        new() { Id = "daten", Author = "あいぬあ", Kind = Kind.Hat, Front = "daten_front" },
        new() { Id = "nankasasatteru", Author = "あいぬあ", Kind = Kind.Hat, Front = "nankasasatteru_front", Climb = "nankasasatteru_climb" },
        new() { Id = "risu", Author = "あいぬあ", Kind = Kind.Hat, Adaptive = true, Front = "risu_front", Climb = "risu_climb" },
        new() { Id = "shirokumabou", Author = "あいぬあ", Kind = Kind.Hat, Front = "shirokumabou_front", Climb = "shirokumabou_climb" },
        new() { Id = "sodateterundesu", Author = "あいぬあ", Kind = Kind.Hat, Front = "sodateterundesu_front", Climb = "sodateterundesu_climb" },
        new() { Id = "tyranno", Author = "あいぬあ", Kind = Kind.Hat, FullBody = true, Front = "tyranno_front", Climb = "tyranno_climb", ClimbLeft = "tyranno_climbleft", Walk = "tyranno_walk", ClimbSteps = true, Mouth = new Vector2(194f, 192f), MouthScale = 0.85f },
        new() { Id = "tyrannocolor", Author = "あいぬあ", Kind = Kind.Hat, Adaptive = true, FullBody = true, Front = "tyrannocolor_front", Climb = "tyrannocolor_climb", ClimbLeft = "tyrannocolor_climbleft", Walk = "tyrannocolor_walk", ClimbSteps = true, Mouth = new Vector2(194f, 192f), MouthScale = 0.85f },
        new() { Id = "wakusei", Author = "あいぬあ", Kind = Kind.Hat, Front = "wakusei_front", Climb = "wakusei_climb" },
        new() { Id = "owo", Author = "ポテサラ", Kind = Kind.Visor, Front = "owo_front" },
    };

    internal static Item Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var i in All) if (i.Id == id) return i;
        return null;
    }

    // ── 絵 ─────────────────────────────────────────────

    private static readonly Vector2 Pivot = new(0.53f, 0.575f);
    private const float Ppu = 300f * 0.375f;
    private static readonly Dictionary<string, Sprite> Sprites = new();
    private static readonly Dictionary<string, Sprite> Thumbs = new();

    internal static Sprite Art(string name)
    {
        if (name == null) return null;
        if (Sprites.TryGetValue(name, out var sp) && sp) return sp;
        sp = Roles.ButtonIcons.Decode("Cosmetics", name, Ppu, Pivot);
        if (sp) Sprites[name] = sp;
        return sp;
    }

    // 一覧の札に出す小さい絵 (枠の透明な所を切り落とし、真ん中を原点にした物)
    internal static Sprite Thumb(Item item)
    {
        if (item == null) return null;
        if (Thumbs.TryGetValue(item.Id, out var sp) && sp) return sp;
        var full = Art(item.Front);
        if (!full) return null;
        var tex = full.texture;
        var bb = Bounds(item.Front);
        int x0 = 0, y0 = 0, x1 = -1, y1 = -1;
        if (bb != null) { x0 = (int)bb.Value.x; y0 = (int)bb.Value.y; x1 = (int)bb.Value.z; y1 = (int)bb.Value.w; }
        if (x1 < x0) { x0 = 0; y0 = 0; x1 = tex.width - 1; y1 = tex.height - 1; }
        sp = Sprite.Create(tex, new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1), new Vector2(0.5f, 0.5f), Ppu);
        sp.hideFlags = HideFlags.HideAndDontSave;
        Thumbs[item.Id] = sp;
        return sp;
    }

    private static readonly Dictionary<string, Vector4?> BoundsOf = new();

    // 絵の透明でない所の枠 (左下が原点の px: x0, y0, x1, y1)。描く用の絵は CPU 側のピクセルを捨てているので、
    // 読める絵を一時的に作って調べる
    internal static Vector4? Bounds(string name)
    {
        if (BoundsOf.TryGetValue(name, out var got)) return got;
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        Texture2D read = null;
        try
        {
            using var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream($"MoreRolesPlus.Resources.Cosmetics.{name}.png");
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            read = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (ImageConversion.LoadImage(read, ms.ToArray(), false))
            {
                var px = read.GetPixels32();
                int w = read.width, h = read.height;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (px[y * w + x].a > 20)
                        {
                            if (x < x0) x0 = x;
                            if (x > x1) x1 = x;
                            if (y < y0) y0 = y;
                            if (y > y1) y1 = y;
                        }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"cosmetic bounds {name}: {e.Message}"); }
        finally { if (read) UnityEngine.Object.Destroy(read); }
        got = x1 < 0 ? null : new Vector4(x0, y0, x1, y1);
        BoundsOf[name] = got;
        return got;
    }

    // ── 自分の装備 ─────────────────────────────────────────

    private static ConfigEntry<string> _hat, _hat2, _visor;
    private static ConfigEntry<string> _order;
    public static int Version { get; private set; }

    public static void Bind(ConfigFile cfg)
    {
        _hat = cfg.Bind("Client", "MrpHat", "", "MRP-only hat id (empty = none)");
        _hat2 = cfg.Bind("Client", "MrpHat2", "", "Second MRP-only hat id, drawn in front of the first (empty = none)");
        _visor = cfg.Bind("Client", "MrpVisor", "", "MRP-only visor id (empty = none)");
        _order = cfg.Bind("Client", "MrpLayerOrder", "201", "Stacking order of the MRP items from back to front (0 = hat 1, 1 = hat 2, 2 = visor)");
    }

    public static Item MyHat => Find(_hat?.Value);
    public static Item MyHat2 => Find(_hat2?.Value);
    public static Item MyVisor => Find(_visor?.Value);

    // 重ねる順 (奥 → 手前に並べた枠の番号。0 = 帽子 1・1 = 帽子 2・2 = バイザー)。既定はバイザー・帽子 1・帽子 2
    private static readonly int[] DefaultOrder = { 2, 0, 1 };

    // 毎フレーム聞かれるので、保存値が変わった時だけ読み直す
    private static string _orderFrom;
    private static int[] _orderParsed;

    public static int[] MyOrder
    {
        get
        {
            string v = _order?.Value;
            if (_orderParsed == null || !ReferenceEquals(v, _orderFrom)) { _orderFrom = v; _orderParsed = Parse(v); }
            return (int[])_orderParsed.Clone();
        }
    }

    private static byte MyCode
    {
        get { _ = MyOrder; return Code(_orderParsed); }
    }

    private static int[] Parse(string v)
    {
        if (v is { Length: 3 })
        {
            var o = new int[3];
            bool ok = true;
            for (int i = 0; i < 3 && ok; i++)
            {
                o[i] = v[i] - '0';
                ok = o[i] is >= 0 and <= 2 && Array.IndexOf(o, o[i]) == i;
            }
            if (ok) return o;
        }
        return (int[])DefaultOrder.Clone();
    }

    // 電文に載せる形 (奥から 9・3・1 の重み)
    private static byte Code(int[] o) => (byte)(o[0] * 9 + o[1] * 3 + o[2]);

    // 電文の形 → 枠ごとの前後 (0 = 一番奥〜2 = 一番手前)。27 通りしか無いので最初に表にしておく
    private static readonly int[][] RankTable = BuildRanks();

    private static int[][] BuildRanks()
    {
        var t = new int[27][];
        for (int code = 0; code < 27; code++)
        {
            var o = new[] { code / 9 % 3, code / 3 % 3, code % 3 };
            if (o[0] == o[1] || o[1] == o[2] || o[0] == o[2]) o = DefaultOrder;
            var r = new int[3];
            for (int i = 0; i < 3; i++) r[o[i]] = i;
            t[code] = r;
        }
        return t;
    }

    // 並びの pos 番目と pos+1 番目 (0 = 一番奥) を入れ替える
    public static void Swap(int pos)
    {
        if (_order == null || pos is < 0 or > 1) return;
        var o = MyOrder;
        (o[pos], o[pos + 1]) = (o[pos + 1], o[pos]);
        _order.Value = $"{o[0]}{o[1]}{o[2]}";
        Version++;
        if (PlayerControl.LocalPlayer && AmongUsClient.Instance && AmongUsClient.Instance.AmConnected) Share.Send(Pack());
    }

    // 枠の前後 (0 = 一番奥〜2 = 一番手前)
    internal static int Rank(int slot, int code) => RankTable[code is >= 0 and < 27 ? code : 0][slot];

    // 本編の帽子・バイザーのタブで付け替えた時: その種類の MRP の物を全部外す
    public static void Equip(Kind kind, Item item)
    {
        if (kind == Kind.Hat) { EquipSlot(0, item); if (item == null) EquipSlot(1, null); }
        else EquipSlot(2, item);
    }

    // 枠 (0 = 帽子 1・1 = 帽子 2・2 = バイザー) に付ける (item = null で外す)。
    // もう片方の帽子の枠に同じ物があれば入れ替える。部屋にいれば全員へ配る
    public static void EquipSlot(int slot, Item item)
    {
        var want = slot == 2 ? Kind.Visor : Kind.Hat;
        if (item != null && item.Kind != want) return;
        var entry = slot switch { 0 => _hat, 1 => _hat2, _ => _visor };
        if (entry == null) return;
        string v = item?.Id ?? "";
        if (entry.Value == v) return;
        if (slot < 2 && item != null)
        {
            var otherEntry = slot == 0 ? _hat2 : _hat;
            if (otherEntry != null && otherEntry.Value == v) otherEntry.Value = entry.Value;
        }
        entry.Value = v;
        Version++;
        var me = PlayerControl.LocalPlayer;
        if (me && AmongUsClient.Instance && AmongUsClient.Instance.AmConnected) Share.Send(Pack());
    }

    private static (string, string, string, byte) Pack() => (MyHat?.Id ?? "", MyHat2?.Id ?? "", MyVisor?.Id ?? "", MyCode);

    // ── 配る ─────────────────────────────────────────────

    private static readonly Dictionary<int, (Item Hat, Item Hat2, Item Visor, int Front)> ByClient = new();
    private static bool _sendPending;
    private static long _sendAt;
    private static int _room;

    internal static readonly RemoteCall<(string Hat, string Hat2, string Visor, byte Front)> Share = new("Cosmetic.Share", Route.Anyone,
        (w, v) => { w.Write(v.Hat ?? ""); w.Write(v.Hat2 ?? ""); w.Write(v.Visor ?? ""); w.Write(v.Front); },
        r => (r.ReadString(), r.ReadString(), r.ReadString(), r.ReadByte()), (sender, v) => Receive(sender, v));

    private static void Receive(PlayerControl sender, (string Hat, string Hat2, string Visor, byte Front) v)
    {
        if (!sender) return;
        // 知らない物 (新しい版の物) は無い事にする
        var hat = Of(v.Hat, Kind.Hat);
        var hat2 = Of(v.Hat2, Kind.Hat);
        var visor = Of(v.Visor, Kind.Visor);
        if (hat == null && hat2 == null && visor == null) ByClient.Remove(sender.OwnerId);
        else ByClient[sender.OwnerId] = (hat, hat2, visor, v.Front);
    }

    private static Item Of(string id, Kind kind)
    {
        var i = Find(id);
        return i != null && i.Kind == kind ? i : null;
    }

    // 誰か (自分も) が出てきた: 少し待ってから自分の装備を全員へ配る (後から入った人に届けるため)
    public static void ScheduleSend()
    {
        _sendPending = true;
        _sendAt = Environment.TickCount64 + 1500;
    }

    public static void Tick()
    {
        if (!_sendPending || Environment.TickCount64 < _sendAt) return;
        _sendPending = false;
        if (!PlayerControl.LocalPlayer) return;
        Share.Send(Pack());
    }

    public static void OnJoined()
    {
        int room = AmongUsClient.Instance ? AmongUsClient.Instance.GameId : 0;
        if (room == _room) return;
        _room = room;
        ByClient.Clear();
    }

    public static void OnExit() { _room = 0; HideAll = false; }

    public static void OnLeft(ClientData c)
    {
        if (c != null) ByClient.Remove(c.Id);
    }

    // ── 誰の装備を描くか ─────────────────────────────────────

    // 着せ替え画面で本編の帽子・バイザーを選んでいる間の見本は、本編の物を見せる
    internal static bool PreviewVanillaHat, PreviewVanillaVisor;

    private static readonly Dictionary<IntPtr, CosmeticsLayer> LayerOf = new();
    private static readonly Dictionary<IntPtr, PlayerControl> PlayerOf = new();

    private static CosmeticsLayer Layer(HatParent hp)
    {
        var key = hp.Pointer;
        if (LayerOf.TryGetValue(key, out var l) && l) return l;
        l = hp.GetComponentInParent<CosmeticsLayer>();
        if (l) LayerOf[key] = l;
        return l;
    }

    private static (Item Hat, Item Hat2, Item Visor, int Front) Mine => (MyHat, MyHat2, MyVisor, MyCode);

    private static (Item Hat, Item Hat2, Item Visor, int Front) ItemsOf(PlayerControl pc)
    {
        if (!pc) return default;
        if (pc.AmOwner) return Mine;
        return ByClient.TryGetValue(pc.OwnerId, out var v) ? v : default;
    }

    // 部屋の中の人の MRP の着せ替えを全部隠す (カモフラージャーの灰色の間)
    internal static bool HideAll;

    private static (Item Hat, Item Hat2, Item Visor, int Front) Resolve(CosmeticsLayer layer, HatParent hp)
    {
        // 着せ替え画面の右の見本
        var menu = PlayerCustomizationMenu.Instance;
        if (menu && menu.PreviewArea && menu.PreviewArea.cosmetics == layer)
        {
            var m = Mine;
            return (PreviewVanillaHat ? null : m.Hat, PreviewVanillaHat ? null : m.Hat2, PreviewVanillaVisor ? null : m.Visor, m.Front);
        }
        string hatId = hp.Hat != null ? hp.Hat.ProdId : CosmeticsLayer.EMPTY_HAT_ID;
        int color = layer.ColorId;
        // 全員を灰色にする迷彩の間は誰の物も出さない (灰色が元の色の人の物が全員に出てしまうので)
        if (HideAll && PlayerControl.AllPlayerControls != null && PlayerControl.AllPlayerControls.Count > 0) return default;

        // 部屋の中: 見た目の色の持ち主 (色は部屋の中で 1 人 1 つ。化けた人には化けた先の人の物が出る)。
        // 帽子が持ち主の本編の帽子と違う時 (迷彩などで帽子が外された時) は出さない
        if (PlayerControl.AllPlayerControls != null && PlayerControl.AllPlayerControls.Count > 0)
        {
            foreach (var pc in PlayerControl.AllPlayerControls)
            {
                if (!pc || pc.Data == null) continue;
                var o = pc.Data.DefaultOutfit;
                if (o == null || o.ColorId != color) continue;
                if (!SameHat(o.HatId, hatId)) return default;
                return ItemsOf(pc);
            }
            return default;
        }
        // メインメニュー (自分しかいない)
        var cust = AmongUs.Data.DataManager.Player?.Customization;
        if (cust != null && cust.Color == color && SameHat(cust.Hat, hatId)) return Mine;
        return default;
    }

    private static bool SameHat(string a, string b)
    {
        bool ea = string.IsNullOrEmpty(a) || a == CosmeticsLayer.EMPTY_HAT_ID;
        bool eb = string.IsNullOrEmpty(b) || b == CosmeticsLayer.EMPTY_HAT_ID;
        return ea && eb || a == b;
    }

    // ── 描く ─────────────────────────────────────────────

    private sealed class State
    {
        public bool HatOn, VisorOn, Climb, Visible = true, BodyHidden;
        public SpriteRenderer Visor, Hat2;
    }

    private static readonly Dictionary<IntPtr, State> States = new();

    private static State St(HatParent hp)
    {
        if (!States.TryGetValue(hp.Pointer, out var s)) States[hp.Pointer] = s = new State();
        return s;
    }

    internal static void OnClimb(HatParent hp, bool climb)
    {
        if (hp) St(hp).Climb = climb;
    }

    internal static void OnVisible(HatParent hp, bool v)
    {
        if (hp) St(hp).Visible = v;
    }

    internal static void OnDestroy(HatParent hp)
    {
        if (!hp) return;
        States.Remove(hp.Pointer);
        LayerOf.Remove(hp.Pointer);
    }

    // 本編の帽子が毎フレームの片付けを終えた後
    internal static void Apply(HatParent hp)
    {
        var layer = Layer(hp);
        if (!layer) return;
        var st = St(hp);
        var (h1, h2, visor, front) = Resolve(layer, hp);
        // 1 つ目に入っている物を本編の帽子の所へ、2 つ目をその手前へ (1 つ目が空なら 2 つ目を本編の所へ)
        var hat = h1 ?? h2;
        var top = h1 != null ? h2 : null;
        bool show = st.Visible && layer.Visible;

        var f = hp.FrontLayer;
        var img = layer.visor ? layer.visor.Image : null;
        // 前後は体と肌着の絵 (z が小さいほど手前) を基準に、その手前へ選んだ順で 1 段ずつ並べる。
        // 帽子の絵の z を基準にすると、本編の帽子は肌着と同じ z・体とは 0.000001 しか離れていないので、
        // 奥へずらした物が体の後ろへ回って見えなくなる
        float bodyZ = BodyFront(layer, f);
        // 本編の帽子の所に描く枠 (1 つ目が空なら 2 つ目) は本編の帽子の絵そのもの
        int baseSlot = h1 != null ? 0 : 1;
        float Z(int slot) => bodyZ - (Rank(slot, front) + 1) * StepZ;
        // バイザーが口のある全身の物より奥なら、口を透かしてその奥 (口の中) にバイザーを入れる
        var suit = hat is { Mouth: not null } ? hat : top is { Mouth: not null } ? top : null;
        int suitSlot = suit == hat ? baseSlot : 1;
        bool inMouth = visor != null && suit != null && !st.Climb && Rank(2, front) < Rank(suitSlot, front);
        bool own = hat != null || visor != null;
        if (own && f) PlaceZ(f, Z(baseSlot));

        if (hat != null && f)
        {
            var sp = HatSprite(hat, st.Climb, layer.FlipX, BodyPose(layer), inMouth && suit == hat);
            if (f.sprite != sp) f.sprite = sp;
            f.enabled = show && sp;
            f.flipX = !st.Climb && layer.FlipX;
            SetMaterial(f, hat.Adaptive, layer.ColorId);
            if (hp.BackLayer) hp.BackLayer.enabled = false;
            st.HatOn = true;
        }
        else if (st.HatOn)
        {
            st.HatOn = false;
            try { layer.SetHat(hp.Hat != null ? hp.Hat.ProdId : CosmeticsLayer.EMPTY_HAT_ID, layer.ColorId); }
            catch (Exception e) { Plugin.Logger.LogWarning($"cosmetic hat restore: {e.Message}"); }
        }

        // 全身の物を着ている間は中の体と肌着を描かない (本編が切り替える enabled には触らず、描くかだけ止める)
        bool hide = hat != null && f && f.enabled && (hat.FullBody || top != null && top.FullBody);
        if (hide || st.BodyHidden)
        {
            var body = layer.currentBodySprite != null ? layer.currentBodySprite.BodySprite : null;
            var skin = layer.skin ? layer.skin.layer : null;
#if ANDROID
            // Android 版の Unity には forceRenderingOff が無い。本編が enabled を戻しても毎回ここで書き直す
            if (body) body.enabled = !hide;
            if (skin) skin.enabled = !hide;
#else
            if (body) body.forceRenderingOff = hide;
            if (skin) skin.forceRenderingOff = hide;
#endif
            st.BodyHidden = hide;
        }

        // 2 つ目の帽子 (1 つ目の手前)
        if (top != null && f)
        {
            var r = Extra(ref st.Hat2, "MrpHat2", hp, f);
            var sp = HatSprite(top, st.Climb, layer.FlipX, BodyPose(layer), inMouth && suit == top);
            if (r.sprite != sp) r.sprite = sp;
            r.flipX = !st.Climb && layer.FlipX;
            Follow(r, f, Z(1));
            r.color = f.color;
            SetMaterial(r, top.Adaptive, layer.ColorId);
            r.enabled = show && sp && f.enabled;
        }
        else if (st.Hat2) st.Hat2.enabled = false;

        // バイザー (帽子の後ろ・体の手前。被り物の帽子なら隠れる)
        if (visor != null && f)
        {
            var r = Extra(ref st.Visor, "MrpVisor", hp, f);
            var sp = Art(visor.Front);
            if (r.sprite != sp) r.sprite = sp;
            r.flipX = layer.FlipX;
            Follow(r, f, Z(2));
            if (inMouth) IntoMouth(r, f, visor, suit, layer.FlipX);
            if (img) img.enabled = false;
            var body = layer.currentBodySprite != null ? layer.currentBodySprite.BodySprite : null;
            float a = body ? body.color.a : 1f;
            r.color = new Color(1f, 1f, 1f, a);
            SetMaterial(r, visor.Adaptive, layer.ColorId);
            r.enabled = show && sp && !st.Climb;
            st.VisorOn = true;
        }
        else if (st.VisorOn)
        {
            st.VisorOn = false;
            if (st.Visor) st.Visor.enabled = false;
            var vl = layer.visor;
            try
            {
                string id = vl && vl.visorData != null ? vl.visorData.ProdId : CosmeticsLayer.EMPTY_VISOR_ID;
                layer.SetVisor(id, layer.ColorId);
                if (vl && vl.Image) vl.Image.enabled = true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"cosmetic visor restore: {e.Message}"); }
        }
    }

    private static Sprite HatSprite(Item hat, bool climb, bool left, int pose, bool hole)
    {
        if (climb) return ClimbSprite(hat, left, pose);
        string name = pose >= 0 && pose < ClimbPose0 && hat.Walk != null ? WalkName(hat, pose) : hat.Front;
        return (hole ? Art(HoleName(name)) : null) ?? Art(name) ?? Art(hat.Front);
    }

    private static readonly Dictionary<string, string> HoleNames = new();

    // 口を透かした絵の名前 (毎フレーム文字列を作らない)
    private static string HoleName(string name)
    {
        if (!HoleNames.TryGetValue(name, out var h)) HoleNames[name] = h = name + "_m";
        return h;
    }

    // バイザーを全身の物の口の中へ: 縮めて、バイザーの絵の真ん中を口の真ん中へ合わせる
    private static void IntoMouth(SpriteRenderer r, SpriteRenderer f, Item visor, Item suit, bool flip)
    {
        var vc = Center(visor.Front);
        if (vc == null) return;
        float s = suit.MouthScale;
        // 絵の中の位置 (左下が原点の px) を、絵の基準点 (Pivot) からの差にする
        var piv = new Vector2(Pivot.x * ArtW, Pivot.y * ArtH);
        var m = new Vector2(suit.Mouth.Value.x, ArtH - suit.Mouth.Value.y) - piv;
        var v = vc.Value - piv;
        var off = (m - v * s) / Ppu;
        if (flip) off.x = -off.x;
        var ft = f.transform;
        var p = ft.position + ft.TransformVector(new Vector3(off.x, off.y, 0f));
        r.transform.position = new Vector3(p.x, p.y, r.transform.position.z);
        r.transform.localScale = ft.localScale * s;
    }

    private const float ArtW = 300f, ArtH = 375f;
    private static readonly Dictionary<string, Vector2?> Centers = new();

    // 絵の透明でない所の真ん中 (左下が原点の px)
    private static Vector2? Center(string name)
    {
        if (Centers.TryGetValue(name, out var c)) return c;
        var b = Bounds(name);
        c = b == null ? null : new Vector2((b.Value.x + b.Value.z + 1) * 0.5f, (b.Value.y + b.Value.w + 1) * 0.5f);
        Centers[name] = c;
        return c;
    }

    private static Sprite ClimbSprite(Item hat, bool left, int pose)
    {
        string name = left && hat.ClimbLeft != null ? hat.ClimbLeft : hat.Climb;
        if (hat.ClimbSteps && pose >= ClimbPose0)
            return Art(StepName(name, pose - ClimbPose0)) ?? Art(name);
        return Art(name);
    }

    private static readonly Dictionary<string, string[]> StepNames = new();

    private static string StepName(string name, int i)
    {
        if (!StepNames.TryGetValue(name, out var names))
        {
            names = new string[ClimbFrames];
            for (int k = 0; k < ClimbFrames; k++) names[k] = name + k;
            StepNames[name] = names;
        }
        return names[i];
    }

    private const int ClimbFrames = 4;      // 着せ替えの梯子の足の絵の数 (1 周分)
    private const int BodyClimbFrames = 12; // 本編の体の梯子の絵 (Climbup0001〜 / Climbdown0001〜) の 1 周
    private const int ClimbPose0 = 100;     // 梯子の足の絵の何枚目かを姿勢の値ではこれに足して表す
    private const int PoseStand = -1;

    private static readonly Dictionary<string, string[]> WalkNames = new();

    // 歩きの絵の名前 (毎フレーム文字列を作らない)
    private static string WalkName(Item hat, int i)
    {
        if (!WalkNames.TryGetValue(hat.Id, out var names))
        {
            names = new string[WalkFrames];
            for (int k = 0; k < WalkFrames; k++) names[k] = hat.Walk + k;
            WalkNames[hat.Id] = names;
        }
        return names[i];
    }

    private const int WalkFrames = 6;   // 着せ替えの歩きの絵の数 (1 歩き分)
    private const int BodyWalkFrames = 12; // 本編の体の歩きの絵 (Walk0001〜Walk0012) の数

    // 体の今の姿勢: 歩きの絵の何枚目か (0〜)・梯子の足の絵 (ClimbPose0 + 何枚目)・立っている (PoseStand)。
    // 本編の体の歩きの 1 周を着せ替えの歩きの 1 周に合わせる
    private static int BodyPose(CosmeticsLayer layer)
    {
        var body = layer.currentBodySprite != null ? layer.currentBodySprite.BodySprite : null;
        var sp = body ? body.sprite : null;
        if (!sp) return PoseStand;
        string n = sp.name;
        if (n.StartsWith("Walk", StringComparison.Ordinal))
        {
            int k = Number(n, 4);
            return k > 0 ? (k - 1) % BodyWalkFrames * WalkFrames / BodyWalkFrames : PoseStand;
        }
        if (n.StartsWith("Climbup", StringComparison.Ordinal) || n.StartsWith("Climbdown", StringComparison.Ordinal))
        {
            int k = Number(n, n[5] == 'u' ? 7 : 9);
            return k > 0 ? ClimbPose0 + (k - 1) % BodyClimbFrames * ClimbFrames / BodyClimbFrames : PoseStand;
        }
        return PoseStand;
    }

    private static int Number(string n, int from)
    {
        int k = 0;
        for (int i = from; i < n.Length; i++)
        {
            char c = n[i];
            if (c < '0' || c > '9') break;
            k = k * 10 + (c - '0');
        }
        return k;
    }

    private static SpriteRenderer Extra(ref SpriteRenderer r, string name, HatParent hp, SpriteRenderer f)
    {
        if (r) return r;
        var go = new GameObject(name) { layer = f.gameObject.layer };
        go.transform.SetParent(hp.transform, false);
        r = go.AddComponent<SpriteRenderer>();
        return r;
    }

    // 着せ替えの 1 段の前後の幅。体と肌着の間 (約 0.000001) と同じくらいにして、
    // 近くに立つ他の人の体より手前へ出ないようにする
    private const float StepZ = 0.000001f;

    // 体と肌着のうち一番手前の z (着せ替えはこれより手前に並べる)
    private static float BodyFront(CosmeticsLayer layer, SpriteRenderer f)
    {
        float z = float.MaxValue;
        var body = layer.currentBodySprite != null ? layer.currentBodySprite.BodySprite : null;
        if (body) z = Mathf.Min(z, body.transform.position.z);
        var skin = layer.skin ? layer.skin.layer : null;
        if (skin) z = Mathf.Min(z, skin.transform.position.z);
        if (z == float.MaxValue) z = f ? f.transform.position.z + StepZ : 0f;
        return z;
    }

    private static void PlaceZ(SpriteRenderer r, float z)
    {
        var p = r.transform.position;
        if (p.z != z) r.transform.position = new Vector3(p.x, p.y, z);
    }

    // 本編の帽子の絵と同じ所・同じ描く順に置き、前後だけ z に変える
    private static void Follow(SpriteRenderer r, SpriteRenderer f, float z)
    {
        var ft = f.transform;
        var p = ft.position;
        r.transform.position = new Vector3(p.x, p.y, z);
        r.transform.localScale = ft.localScale;
        r.sortingLayerID = f.sortingLayerID;
        r.sortingOrder = f.sortingOrder;
        r.maskInteraction = f.maskInteraction;
    }

    private static void SetMaterial(SpriteRenderer r, bool adaptive, int color)
    {
        var hm = HatManager.Instance;
        if (!hm) return;
        var cur = r.sharedMaterial;
        bool masked = cur && (cur == hm.MaskedMaterial || cur == hm.MaskedPlayerMaterial);
        var want = adaptive ? (masked ? hm.MaskedPlayerMaterial : hm.PlayerMaterial) : (masked ? hm.MaskedMaterial : hm.DefaultShader);
        if (want && cur != want) r.sharedMaterial = want;
        if (adaptive) PlayerMaterial.SetColors(color, r);
    }

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder($"cosmetics hat={MyHat?.Id ?? "-"} hat2={MyHat2?.Id ?? "-"} visor={MyVisor?.Id ?? "-"} order={string.Join("", MyOrder)} remote={ByClient.Count} drawn=");
        int on = 0;
        foreach (var s in States.Values) if (s.HatOn || s.VisorOn) on++;
        return sb.Append(on).ToString();
    }

    // 確認用: cosmetic [hat|hat2|visor] <id|none>
    internal static string Simulate(string args)
    {
        var a = (args ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 2 && (a[0] == "hat" || a[0] == "hat2" || a[0] == "visor"))
        {
            int slot = a[0] == "hat" ? 0 : a[0] == "hat2" ? 1 : 2;
            var item = a[1] == "none" ? null : Find(a[1]);
            if (a[1] != "none" && (item == null || item.Kind != (slot == 2 ? Kind.Visor : Kind.Hat))) return "ERR cosmetic unknown " + a[1];
            EquipSlot(slot, item);
        }
        else if (a.Length == 2 && a[0] == "order" && _order != null)
        {
            _order.Value = a[1];
            Version++;
        }
        else if (a.Length == 2 && a[0] == "swap" && int.TryParse(a[1], out int sw)) Swap(sw);
        else if (a.Length == 1 && a[0] == "z")
        {
            // 自分の体の絵の前後 (z が小さいほど手前) を並べて出す (切り分け用)
            var me = PlayerControl.LocalPlayer;
            if (!me) return "ERR no player";
            var list = new List<(float, string)>();
            foreach (var r in me.GetComponentsInChildren<SpriteRenderer>(true))
                if (r.enabled && r.gameObject.activeInHierarchy && r.sprite)
                    list.Add((r.transform.position.z, $"{r.transform.position.z:0.000000} so={r.sortingOrder} {r.name}:{r.sprite.name}"));
            list.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            return "OK z\n" + string.Join("\n", list.ConvertAll(x => x.Item2));
        }
        else if (a.Length > 0) return "ERR cosmetic [hat|hat2|visor] <id|none> | order <奥→手前の 3 桁>";
        return "OK " + Describe();
    }
}

[HarmonyPatch]
internal static class MrpCosmeticsPatches
{
    [HarmonyPatch(typeof(HatParent), nameof(HatParent.LateUpdate)), HarmonyPostfix]
    public static void Late(HatParent __instance)
    {
        try { MrpCosmetics.Apply(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"cosmetics: {e.Message}"); }
    }

    [HarmonyPatch(typeof(HatParent), nameof(HatParent.SetClimbAnim)), HarmonyPostfix]
    public static void Climb(HatParent __instance) => MrpCosmetics.OnClimb(__instance, true);

    [HarmonyPatch(typeof(HatParent), nameof(HatParent.SetIdleAnim)), HarmonyPostfix]
    public static void Idle(HatParent __instance) => MrpCosmetics.OnClimb(__instance, false);

    [HarmonyPatch(typeof(HatParent), nameof(HatParent.SetFloorAnim)), HarmonyPostfix]
    public static void Floor(HatParent __instance) => MrpCosmetics.OnClimb(__instance, false);

    [HarmonyPatch(typeof(HatParent), nameof(HatParent.Visible), MethodType.Setter), HarmonyPostfix]
    public static void Visible(HatParent __instance, bool value) => MrpCosmetics.OnVisible(__instance, value);

    [HarmonyPatch(typeof(HatParent), nameof(HatParent.OnDestroy)), HarmonyPrefix]
    public static void Destroyed(HatParent __instance) => MrpCosmetics.OnDestroy(__instance);

    // 本編の帽子・バイザーを付けたら、同じ所の MRP の物は外す (付けたのに見た目が変わらないと困るので)
    [HarmonyPatch(typeof(HatsTab), nameof(HatsTab.ClickEquip)), HarmonyPostfix]
    public static void HatEquip() => MrpCosmetics.Equip(MrpCosmetics.Kind.Hat, null);

    [HarmonyPatch(typeof(VisorsTab), nameof(VisorsTab.ClickEquip)), HarmonyPostfix]
    public static void VisorEquip() => MrpCosmetics.Equip(MrpCosmetics.Kind.Visor, null);

    // 本編の帽子・バイザーのタブを開いている間は、見本に本編の物を見せる
    [HarmonyPatch(typeof(HatsTab), nameof(HatsTab.OnEnable)), HarmonyPostfix]
    public static void HatTabOn() { MrpCosmetics.PreviewVanillaHat = true; MrpCosmetics.PreviewVanillaVisor = false; }

    [HarmonyPatch(typeof(VisorsTab), nameof(VisorsTab.OnEnable)), HarmonyPostfix]
    public static void VisorTabOn() { MrpCosmetics.PreviewVanillaVisor = true; MrpCosmetics.PreviewVanillaHat = false; }

    [HarmonyPatch(typeof(PlayerCustomizationMenu), nameof(PlayerCustomizationMenu.OpenTab)), HarmonyPostfix]
    public static void TabOpened(PlayerCustomizationMenu __instance)
    {
        // 帽子・バイザー以外のタブへ移ったら見本を MRP の物に戻す (OnEnable は帽子・バイザーのタブの時だけ上で立てる)
        bool hats = false, visors = false;
        foreach (var t in __instance.Tabs)
        {
            if (!t.Tab || !t.Tab.gameObject.activeSelf) continue;
            if (t.Tab.TryCast<HatsTab>()) hats = true;
            if (t.Tab.TryCast<VisorsTab>()) visors = true;
        }
        MrpCosmetics.PreviewVanillaHat = hats;
        MrpCosmetics.PreviewVanillaVisor = visors;
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Start)), HarmonyPostfix]
    public static void Spawned() => MrpCosmetics.ScheduleSend();

    [HarmonyPatch(typeof(AmongUsClient), "OnGameJoined"), HarmonyPrefix]
    public static void Joined()
    {
        try { MrpCosmetics.OnJoined(); }
        catch (Exception e) { Plugin.Logger.LogError($"cosmetics: {e.Message}"); }
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.ExitGame)), HarmonyPrefix]
    public static void Exit() => MrpCosmetics.OnExit();

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft)), HarmonyPrefix]
    public static void Left([HarmonyArgument(0)] ClientData data) => MrpCosmetics.OnLeft(data);
}
