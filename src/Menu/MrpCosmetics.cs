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
// バイザーは帽子と同じ枠の絵なので、帽子の所に MRP の絵を 1 枚足して描き、本編のバイザーは隠す。
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
        public string NameKey => "Cos." + Id;
    }

    internal static readonly Item[] All =
    {
        new() { Id = "daten", Author = "あいぬあ", Kind = Kind.Hat, Front = "daten_front" },
        new() { Id = "nankasasatteru", Author = "あいぬあ", Kind = Kind.Hat, Front = "nankasasatteru_front", Climb = "nankasasatteru_climb" },
        new() { Id = "risu", Author = "あいぬあ", Kind = Kind.Hat, Adaptive = true, Front = "risu_front", Climb = "risu_climb" },
        new() { Id = "shirokumabou", Author = "あいぬあ", Kind = Kind.Hat, Front = "shirokumabou_front", Climb = "shirokumabou_climb" },
        new() { Id = "sodateterundesu", Author = "あいぬあ", Kind = Kind.Hat, Front = "sodateterundesu_front", Climb = "sodateterundesu_climb" },
        new() { Id = "tyranno", Author = "あいぬあ", Kind = Kind.Hat, Front = "tyranno_front", Climb = "tyranno_climb", ClimbLeft = "tyranno_climbleft" },
        new() { Id = "tyrannocolor", Author = "あいぬあ", Kind = Kind.Hat, Adaptive = true, Front = "tyrannocolor_front", Climb = "tyrannocolor_climb", ClimbLeft = "tyrannocolor_climbleft" },
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
        int x0 = tex.width, y0 = tex.height, x1 = -1, y1 = -1;
        try
        {
            var px = tex.GetPixels32();
            for (int y = 0; y < tex.height; y++)
                for (int x = 0; x < tex.width; x++)
                    if (px[y * tex.width + x].a > 20)
                    {
                        if (x < x0) x0 = x;
                        if (x > x1) x1 = x;
                        if (y < y0) y0 = y;
                        if (y > y1) y1 = y;
                    }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"cosmetic thumb {item.Id}: {e.Message}"); }
        if (x1 < x0) { x0 = 0; y0 = 0; x1 = tex.width - 1; y1 = tex.height - 1; }
        sp = Sprite.Create(tex, new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1), new Vector2(0.5f, 0.5f), Ppu);
        sp.hideFlags = HideFlags.HideAndDontSave;
        Thumbs[item.Id] = sp;
        return sp;
    }

    // ── 自分の装備 ─────────────────────────────────────────

    private static ConfigEntry<string> _hat, _visor;
    public static int Version { get; private set; }

    public static void Bind(ConfigFile cfg)
    {
        _hat = cfg.Bind("Client", "MrpHat", "", "MRP-only hat id (empty = none)");
        _visor = cfg.Bind("Client", "MrpVisor", "", "MRP-only visor id (empty = none)");
    }

    public static Item MyHat => Find(_hat?.Value);
    public static Item MyVisor => Find(_visor?.Value);

    // 付ける (item = null で外す)。部屋にいれば全員へ配る
    public static void Equip(Kind kind, Item item)
    {
        var entry = kind == Kind.Hat ? _hat : _visor;
        if (entry == null) return;
        string v = item != null && item.Kind == kind ? item.Id : "";
        if (entry.Value == v) return;
        entry.Value = v;
        Version++;
        var me = PlayerControl.LocalPlayer;
        if (me && AmongUsClient.Instance && AmongUsClient.Instance.AmConnected) Share.Send((MyHat?.Id ?? "", MyVisor?.Id ?? ""));
    }

    // ── 配る ─────────────────────────────────────────────

    private static readonly Dictionary<int, (Item Hat, Item Visor)> ByClient = new();
    private static bool _sendPending;
    private static long _sendAt;
    private static int _room;

    internal static readonly RemoteCall<(string Hat, string Visor)> Share = new("Cosmetic.Share", Route.Anyone,
        (w, v) => { w.Write(v.Hat ?? ""); w.Write(v.Visor ?? ""); }, r => (r.ReadString(), r.ReadString()), (sender, v) => Receive(sender, v));

    private static void Receive(PlayerControl sender, (string Hat, string Visor) v)
    {
        if (!sender) return;
        // 知らない物 (新しい版の物) は無い事にする
        var hat = Find(v.Hat);
        var visor = Find(v.Visor);
        if (hat != null && hat.Kind != Kind.Hat) hat = null;
        if (visor != null && visor.Kind != Kind.Visor) visor = null;
        if (hat == null && visor == null) ByClient.Remove(sender.OwnerId);
        else ByClient[sender.OwnerId] = (hat, visor);
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
        Share.Send((MyHat?.Id ?? "", MyVisor?.Id ?? ""));
    }

    public static void OnJoined()
    {
        int room = AmongUsClient.Instance ? AmongUsClient.Instance.GameId : 0;
        if (room == _room) return;
        _room = room;
        ByClient.Clear();
    }

    public static void OnExit() => _room = 0;

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

    private static (Item Hat, Item Visor) Mine => (MyHat, MyVisor);

    private static (Item Hat, Item Visor) ItemsOf(PlayerControl pc)
    {
        if (!pc) return default;
        if (pc.AmOwner) return Mine;
        return ByClient.TryGetValue(pc.OwnerId, out var v) ? v : default;
    }

    private static (Item Hat, Item Visor) Resolve(CosmeticsLayer layer, HatParent hp)
    {
        // 着せ替え画面の右の見本
        var menu = PlayerCustomizationMenu.Instance;
        if (menu && menu.PreviewArea && menu.PreviewArea.cosmetics == layer)
        {
            var m = Mine;
            return (PreviewVanillaHat ? null : m.Hat, PreviewVanillaVisor ? null : m.Visor);
        }
        string hatId = hp.Hat != null ? hp.Hat.ProdId : CosmeticsLayer.EMPTY_HAT_ID;
        int color = layer.ColorId;

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
        public bool HatOn, VisorOn, Climb, Visible = true;
        public SpriteRenderer Visor;
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
        var (hat, visor) = Resolve(layer, hp);
        bool show = st.Visible && layer.Visible;

        var f = hp.FrontLayer;
        if (hat != null && f)
        {
            Sprite sp;
            if (st.Climb) sp = Art(layer.FlipX && hat.ClimbLeft != null ? hat.ClimbLeft : hat.Climb);
            else sp = Art(hat.Front);
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

        var vl = layer.visor;
        if (visor != null && f)
        {
            if (!st.Visor)
            {
                var go = new GameObject("MrpVisor") { layer = f.gameObject.layer };
                go.transform.SetParent(hp.transform, false);
                st.Visor = go.AddComponent<SpriteRenderer>();
            }
            var r = st.Visor;
            var sp = Art(visor.Front);
            if (r.sprite != sp) r.sprite = sp;
            r.flipX = layer.FlipX;
            var img = vl ? vl.Image : null;
            var ft = f.transform;
            // 位置は帽子の絵と同じ枠、前後は本編のバイザーの所 (体の手前・帽子の後ろ)
            float z = img ? img.transform.position.z : ft.position.z + 0.0001f;
            r.transform.position = new Vector3(ft.position.x, ft.position.y, z);
            r.transform.localScale = ft.localScale;
            if (img)
            {
                r.sortingLayerID = img.sortingLayerID;
                r.sortingOrder = img.sortingOrder;
                r.maskInteraction = img.maskInteraction;
                img.enabled = false;
            }
            else
            {
                r.sortingLayerID = f.sortingLayerID;
                r.sortingOrder = f.sortingOrder;
                r.maskInteraction = f.maskInteraction;
            }
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
            try
            {
                string id = vl && vl.visorData != null ? vl.visorData.ProdId : CosmeticsLayer.EMPTY_VISOR_ID;
                layer.SetVisor(id, layer.ColorId);
                if (vl && vl.Image) vl.Image.enabled = true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"cosmetic visor restore: {e.Message}"); }
        }
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
        var sb = new System.Text.StringBuilder($"cosmetics hat={MyHat?.Id ?? "-"} visor={MyVisor?.Id ?? "-"} remote={ByClient.Count} drawn=");
        int on = 0;
        foreach (var s in States.Values) if (s.HatOn || s.VisorOn) on++;
        return sb.Append(on).ToString();
    }

    // 確認用: cosmetic [hat|visor] <id|none>
    internal static string Simulate(string args)
    {
        var a = (args ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 2 && (a[0] == "hat" || a[0] == "visor"))
        {
            var kind = a[0] == "hat" ? Kind.Hat : Kind.Visor;
            var item = a[1] == "none" ? null : Find(a[1]);
            if (a[1] != "none" && (item == null || item.Kind != kind)) return "ERR cosmetic unknown " + a[1];
            Equip(kind, item);
        }
        else if (a.Length > 0) return "ERR cosmetic [hat|visor] <id|none>";
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
