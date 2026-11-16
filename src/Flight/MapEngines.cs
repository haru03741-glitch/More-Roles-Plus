using System;
using HarmonyLib;
using MoreRolesPlus.Roles;
using UnityEngine;

namespace MoreRolesPlus.Flight;

// スケルドのマップの後ろの噴射機を、3D の戦闘形態のスケルドと同じ形の絵に替える
// (上下のエンジン = 筒 + 紺の輪 + 鐘形の噴射口・真ん中 = 金色の襟の原子炉の噴射口・細い噴流)。
// 本編の 5 枚 (HullItems/thruster*) は絵とコマ送りを止めて残し、置き場の目印に使う。
// 足す絵は同じ親の子なので、船を左右反転したマップでも一緒に裏返る。
internal static class MapEngines
{
    private const float Ppu = 100f;
    // 絵の中の基準の点 (単位): エンジン = 筒の後ろの端・原子炉 = 口の前の端・噴流 = 口
    private const float EngineX0 = -4.55f, EngineX1 = 5.0f;
    private const float ReactorX0 = -1.0f, ReactorX1 = 2.6f;
    // 本編の噴射機の中心からのずらし (船の前へ)。3D と同じく、筒は船体の後ろの縁から約 3 単位・原子炉は金色の襟が見える位置
    private const float EngineFwd = -1.2f, ReactorFwd = -0.4f;
    private const float EngineLip = -3.9f, ReactorLip = -0.75f;   // 噴射口の後ろの端 (噴流の付け根)
    private const int Frames = 4;
    private const long FrameMs = 70;

    private static Sprite _engine, _reactor;
    private static Sprite[] _jet, _hot;
    private static SpriteRenderer[] _jets;
    private static Sprite[][] _jetSet;
    private static SpriteRenderer _src;
    private static int _gen = -1, _frame = -1;

    private static bool Load()
    {
        if (_engine) return true;
        _engine = ButtonIcons.Decode("MapArt", "skeld_engine", Ppu, new Vector2(-EngineX0 / (EngineX1 - EngineX0), 0.5f));
        _reactor = ButtonIcons.Decode("MapArt", "skeld_reactor", Ppu, new Vector2(-ReactorX0 / (ReactorX1 - ReactorX0), 0.5f));
        _jet = new Sprite[Frames];
        _hot = new Sprite[Frames];
        for (int i = 0; i < Frames; i++)
        {
            _jet[i] = ButtonIcons.Decode("MapArt", "skeld_jet_" + i, Ppu, new Vector2(1f, 0.5f));
            _hot[i] = ButtonIcons.Decode("MapArt", "skeld_jet_hot_" + i, Ppu, new Vector2(1f, 0.5f));
        }
        if (!_engine || !_reactor) return false;
        for (int i = 0; i < Frames; i++)
            if (!_jet[i] || !_hot[i]) return false;
        return true;
    }

    internal static void OnShipStart(ShipStatus ship)
    {
        _jets = null;
        if (!ship) return;
        var items = ship.transform.Find("HullItems");
        if (!items) return;
        // 船の世代を今の船へ進めてから覚える (進めずに読むと次のフレームで別の船と見なしてコマ送りが止まる)
        Terrain.GameClock.Tick();
        // 同じ船の Start がもう一度来た時は足さず、足した噴流をそのまま回す
        if (items.Find("mrp_reactor"))
        {
            var j0 = items.Find("mrp_jet_0"); var j1 = items.Find("mrp_jet_1"); var jh = items.Find("mrp_jet_hot");
            if (j0 && j1 && jh)
                Begin(new[] { j0.GetComponent<SpriteRenderer>(), j1.GetComponent<SpriteRenderer>(), jh.GetComponent<SpriteRenderer>() });
            return;
        }
        Transform big = null;
        var small = new System.Collections.Generic.List<Transform>();
        for (int i = 0; i < items.childCount; i++)
        {
            var c = items.GetChild(i);
            string n = c.name;
            if (n.StartsWith("thrusterbig", StringComparison.Ordinal)) big = c;
            else if (n.StartsWith("thruster", StringComparison.Ordinal)) small.Add(c);
        }
        if (!big || small.Count != 4 || !Load()) return;

        // 上の対と下の対 (y の大きい 2 つと小さい 2 つ)
        small.Sort((a, b) => b.localPosition.y.CompareTo(a.localPosition.y));
        var jets = new SpriteRenderer[3];
        int layer = big.gameObject.layer;
        // 描く順と材質は本編の噴射機に合わせる (船体・影の板との前後を同じにする)
        _src = big.GetComponent<SpriteRenderer>();
        for (int k = 0; k < 2; k++)
        {
            Vector3 p0 = small[k * 2].localPosition, p1 = small[k * 2 + 1].localPosition;
            var c = new Vector3((p0.x + p1.x) * 0.5f + EngineFwd, (p0.y + p1.y) * 0.5f, (p0.z + p1.z) * 0.5f);
            Make(items, "mrp_engine_" + k, _engine, c, layer);
            jets[k] = Make(items, "mrp_jet_" + k, _jet[0], c + new Vector3(EngineLip, 0f, 0.05f), layer);
        }
        var bp = big.localPosition;
        var rc = new Vector3(bp.x + ReactorFwd, bp.y, bp.z);
        Make(items, "mrp_reactor", _reactor, rc, layer);
        jets[2] = Make(items, "mrp_jet_hot", _hot[0], rc + new Vector3(ReactorLip, 0f, 0.05f), layer);

        foreach (var t in small) Hide(t);
        Hide(big);
        _src = null;
        Begin(jets);
    }

    private static void Begin(SpriteRenderer[] jets)
    {
        if (jets[0] == null || jets[1] == null || jets[2] == null) return;
        _jets = jets;
        _jetSet = new[] { _jet, _jet, _hot };
        _gen = Terrain.GameClock.ShipGen;
        _frame = -1;
    }

    private static SpriteRenderer Make(Transform parent, string name, Sprite sp, Vector3 local, int layer)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = local;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        if (_src)
        {
            sr.sharedMaterial = _src.sharedMaterial;
            sr.sortingLayerID = _src.sortingLayerID;
            sr.sortingOrder = _src.sortingOrder;
        }
        return sr;
    }

    // 本編の噴射機の絵とコマ送りを止める (物は残す)
    private static void Hide(Transform t)
    {
        var sr = t.GetComponent<SpriteRenderer>();
        if (sr) sr.enabled = false;
        var an = t.GetComponent<Animator>();
        if (an) an.enabled = false;
    }

    // 噴流のコマ送り (コマが替わる時だけ絵を差す・3 本で位相をずらす)
    internal static void Tick()
    {
        if (_jets == null) return;
        if (_gen != Terrain.GameClock.ShipGen) { _jets = null; return; }
        int f = (int)(Environment.TickCount64 / FrameMs % Frames);
        if (f == _frame) return;
        _frame = f;
        // 船が消えた直後 (世代が進む前) に消えた絵へ触らない
        if (!_jets[0]) { _jets = null; return; }
        for (int i = 0; i < _jets.Length; i++)
            _jets[i].sprite = _jetSet[i][(f + i) % Frames];
    }
}

[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
internal static class MapEnginesShipStartPatch
{
    // 地形の前計算 (同じ Start の後ろ) より後に足す
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(ShipStatus __instance) => MapEngines.OnShipStart(__instance);
}
