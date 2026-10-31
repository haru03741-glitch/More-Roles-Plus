using HarmonyLib;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus;

[Settings(Tab.General, "MapSettings.Title", order: -60)]
public static class MapSettings
{
    // ゲーム設定のタブのマップ選びの下 (マップのバリエーション) で選ぶ
    public static readonly BoolOpt Mirror = new("MapSettings.Mirror", false) { OffTab = true };
}

// 船を左右反転する (Skeld の dlekS を全マップへ)。船の根の横の大きさを負にすると子の絵・当たり判定・部屋の範囲は
// 一緒に裏返る。根の子でない物 (湧く位置の Vector2・Airship の湧き場所選び・地図) は鏡に映して書き直す。
// 反転は船の Start の前 (地形の前計算が船の形を読む前) に 1 回だけ掛ける
internal static class MapFlip
{
    internal static bool Next;      // 次に作られる船を反転するか (確かめ用。設定とどちらかが ON なら反転する)
    internal static bool Flipped;   // 今の船が反転しているか

    // 船の根の x も符号を変えて、鏡の軸を世界の x = 0 にそろえる (反転前の点 p は反転後に -p.x へ来る。浮動小数でも誤差が出ない)
    internal static float Mirror(float x) => -x;

    private static System.IntPtr _ship; // 反転した船 (同じ船の Start がもう一度来ても反転したままと扱う)

    internal static void Apply(ShipStatus ship)
    {
        if (ship && ship.Pointer == _ship && ship.transform.localScale.x < 0f) return;
        Flipped = false;
        _ship = System.IntPtr.Zero;
        if (!(Next || MapSettings.Mirror) || !ship) return;
        var t = ship.transform;
        var s = t.localScale;
        if (s.x < 0f) return;
        _ship = ship.Pointer;
        var p = t.position;
        t.position = new Vector3(-p.x, p.y, p.z);
        t.localScale = new Vector3(-s.x, s.y, s.z);
        Flipped = true;

        // 胞子の霧の型は視界を遮る影として描かれ、裏面を描かない材質なので、裏向きのままだと霧の中の人が隠れない
        if (ship.TryCast<FungleShipStatus>() != null)
            foreach (var m in ship.GetComponentsInChildren<Mushroom>(true))
                if (m.sporeMask) FaceUp(m.sporeMask.transform);

        ship.InitialSpawnCenter = MirrorV2(ship.InitialSpawnCenter);
        ship.MeetingSpawnCenter = MirrorV2(ship.MeetingSpawnCenter);
        ship.MeetingSpawnCenter2 = MirrorV2(ship.MeetingSpawnCenter2);

        Plugin.Logger.LogInfo($"[MapFlip] flipped type={ship.Type} x={t.position.x:0.##} scale={t.localScale.x:0.###}");
    }

    private static Vector2 MirrorV2(Vector2 v) => new(Mirror(v.x), v.y);

    // 倍率の積が負 (裏向き) の物を、自分の横の倍率の符号を変えて表へ戻す
    internal static void FaceUp(Transform t)
    {
        var l = t.lossyScale;
        if (l.x * l.y >= 0f) return;
        var s = t.localScale;
        t.localScale = new Vector3(-s.x, s.y, s.z);
    }

    // 反転前の船で焼いた格子 (原点 ox・幅 w 升・1 単位 ppu 升) の、反転後の原点
    internal static float GridOrigin(float ox, int w, float ppu) => Mirror(ox + w / ppu);

    // 1 升 1 ビット (k = y × w + x) の格子を左右逆にする
    internal static void FlipBitRows(byte[] bits, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int a = row, b = row + w - 1; a < b; a++, b--)
            {
                bool va = (bits[a >> 3] & (1 << (a & 7))) != 0, vb = (bits[b >> 3] & (1 << (b & 7))) != 0;
                if (va == vb) continue;
                bits[a >> 3] ^= (byte)(1 << (a & 7));
                bits[b >> 3] ^= (byte)(1 << (b & 7));
            }
        }
    }

    // FlipBitRows の写し版: 各行の先頭 span 升を左右逆にし、残りの升は空にする
    internal static byte[] MirrorBitRows(byte[] src, int w, int h, int span)
    {
        var o = new byte[src.Length];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int j = 0; j < span; j++)
            {
                int a = row + span - 1 - j;
                if ((src[a >> 3] & (1 << (a & 7))) == 0) continue;
                int b = row + j;
                o[b >> 3] |= (byte)(1 << (b & 7));
            }
        }
        return o;
    }

    // 4 つ組 (xMin, yMin, xMax, yMax) の並びを鏡に映す
    internal static float[] MirrorBoxes(float[] v)
    {
        var o = (float[])v.Clone();
        for (int i = 0; i + 3 < o.Length; i += 4) { o[i] = Mirror(v[i + 2]); o[i + 2] = Mirror(v[i]); }
        return o;
    }
}

// 地図 (ミニマップ・管理室・サボタージュ) は本編が反転前の船の向きで点を置く (x に船の倍率の符号を掛ける)。
// 中身ごと鏡に映し、文字だけ裏返し直す。閉じるボタンは左上のまま。鏡に映したかは地図の子の印で持つ
// (同じ地図が反転しない試合で使われたら戻す・作り直された地図は印が無い)
[HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.Show))]
internal static class MapFlipMapShowPatch
{
    private const string Mark = "MrpMirrored";

    public static void Postfix(MapBehaviour __instance)
    {
        if (!__instance) return;
        var root = __instance.transform;
        var mark = root.Find(Mark);
        if ((mark != null) == MapFlip.Flipped) return;
        if (mark != null) Object.Destroy(mark.gameObject);
        else new GameObject(Mark).transform.SetParent(root, false);
        for (int i = 0; i < root.childCount; i++)
        {
            var c = root.GetChild(i);
            if (c.name is "CloseButton" or Mark or Menu.MeetingMemo.LayerName or Roles.ArchitectPlan.LayerName) continue;
            // 背景の絵は flipX で裏返す (地図の大きさ合わせ (AspectSize) が絵の幅を読むので、負の倍率にすると全体が逆さになる)。その子は鏡に映す
            var sr = c.GetComponent<SpriteRenderer>();
            if (!sr) { Mirror(c); continue; }
            sr.flipX = !sr.flipX;
            for (int j = 0; j < c.childCount; j++) Mirror(c.GetChild(j));
        }
    }

    // t を親の中で鏡に映し、その下の文字だけ裏返し直す (2 回で元に戻る)
    private static void Mirror(Transform t)
    {
        var p = t.localPosition;
        var s = t.localScale;
        t.localPosition = new Vector3(-p.x, p.y, p.z);
        t.localScale = new Vector3(-s.x, s.y, s.z);
        foreach (var tx in t.GetComponentsInChildren<TMPro.TMP_Text>(true))
        {
            var ts = tx.transform.localScale;
            tx.transform.localScale = new Vector3(-ts.x, ts.y, ts.z);
        }
        // 裏向き (倍率の積が負) の絵は裏面を描かない材質 (サボタージュのボタンなど) で消えるので表へ戻す。
        // 親から順に来るので、子は親を直した後の向きで判定する
        foreach (var sr in t.GetComponentsInChildren<SpriteRenderer>(true))
        {
            var st = sr.transform;
            var l = st.lossyScale;
            if (l.x * l.y >= 0f) continue;
            var ss = st.localScale;
            st.localScale = new Vector3(-ss.x, ss.y, ss.z);
        }
    }
}

// Airship の湧き場所選びは開くたびに元の絵 (prefab) から作られるので、作られた物の場所だけを鏡に映す
[HarmonyPatch(typeof(SpawnInMinigame), nameof(SpawnInMinigame.Begin))]
internal static class MapFlipSpawnInPatch
{
    public static void Prefix(SpawnInMinigame __instance)
    {
        if (!MapFlip.Flipped || __instance.Locations == null) return;
        var locs = __instance.Locations;
        for (int i = 0; i < locs.Length; i++)
        {
            var l = locs[i];
            l.Location = new Vector3(MapFlip.Mirror(l.Location.x), l.Location.y, l.Location.z);
            locs[i] = l;
        }
    }
}

// Fungle のジップラインは降りた後、着地点 (世界の位置) をジップライン自身の TransformPoint にもう一度通して歩かせる。
// 反転していなければずれは小さいが、反転すると x が裏返って反転前の側へ壁を抜けて歩く。
// 通した後が「反転前の行き先の鏡写し」(ジップラインの位置 + 着地点) になる値へ、歩き始める前に 1 回だけ置き換える。
// (CoAlightPlayerFromZipline 自体は Vector3 の引数を取るのでパッチせず、中の MoveNext に当てる)
[HarmonyPatch(typeof(ZiplineBehaviour._CoAlightPlayerFromZipline_d__46), nameof(ZiplineBehaviour._CoAlightPlayerFromZipline_d__46.MoveNext))]
internal static class MapFlipZiplineLandingPatch
{
    public static void Prefix(ZiplineBehaviour._CoAlightPlayerFromZipline_d__46 __instance)
    {
        if (!MapFlip.Flipped || __instance.__1__state != 0) return;
        var zip = __instance.__4__this;
        if (!zip) return;
        var t = zip.transform;
        __instance.landingPosition = t.InverseTransformPoint(t.position + __instance.landingPosition);
    }
}

[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
internal static class MapFlipShipStartPatch
{
    [HarmonyPriority(Priority.First)]
    public static void Prefix(ShipStatus __instance) => MapFlip.Apply(__instance);
}
