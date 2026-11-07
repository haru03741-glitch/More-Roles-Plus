// Based on https://github.com/ykundesu/SuperNewRoles SuperNewRoles/MapCustoms/SelectSpawn.cs and Modules/MapLoader.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Rules;

// Airship 以外のマップでも、試合の始めと会議の後に湧く場所を Airship と同じ画面 (本編の SpawnInMinigame) で選ぶ (MapRules.SelectSpawn)。
// - 画面の部品は Airship の船にしか無いので、試合が始まった時に Airship の船を非同期で読み込み、試合の終わりまで持つ。
//   試合の終わりに手放すと、本編が場面の切り替えで回す GC と掃除で消える (読むと PC で約 150MB・手放すまで戻らない)。
//   メモリの厳しい Android では読み込まず、普段どおりに湧く
// - 部屋はランダムスポーンの部屋の真ん中から画面のボタンの数だけ選び、ボタンの絵はその場所を船のカメラで撮って作る
// - 選んだ後の移動は本編の画面に任せる (Airship と同じ)。反転した船では MapFlip が場所を鏡に映すので、ここは本編の向きで渡す
// - ランダムスポーンも入っている時は、選ばせずに行き先の 1 つだけを見せる (Airship の本編の画面も同じ・OneSpawnPatch)
internal static class SelectSpawn
{
    private const int AirshipMap = 4;
    private const int ShotPx = 256;
    // ボタンの絵の大きさ (本編のボタンの間隔 2.2 に収まる幅)。部屋ごとに同じ大きさにそろえる
    private static readonly Vector2 ShotSize = new(1.8f, 1.5f);
    // 写さない層: 5 = UI / 8 = 人 / 10 = 影 / 14 = 幽霊 (PortalArt の窓と同じ)
    private const int HiddenLayers = (1 << 5) | (1 << 8) | (1 << 10) | (1 << 14);

    private static readonly System.Random Rng = new();
    private static AsyncOperationHandle<GameObject> _op;
    private static bool _loading;
    private static int _shipGen = -1;
    private static bool _afterMeeting;
    private static bool _hudHidden; // 画面を開いている間は HUD を隠す (本編の Airship と同じ)
    private static int _hiddenLayers = HiddenLayers;
    private static readonly List<Object> Made = new(); // 撮った絵 (次に開く時と試合の終わりに捨てる)

    // ランダムスポーンだけの時も、行き先の 1 つを同じ画面で見せる (Airship の本編と同じ)
    internal static bool Active =>
        (MapRules.SelectSpawn || MapRules.RandomSpawn) && MapRules.MapId != AirshipMap && !OperatingSystem.IsAndroid() && Options.GameModeSwitch.IsNormal;

    // 船ができた時 (RulesHooks.OnShipStart)
    internal static void OnShipStart()
    {
        if (!Active || _loading) return;
        var prefabs = AmongUsClient.Instance ? AmongUsClient.Instance.ShipPrefabs : null;
        if (prefabs == null || prefabs.Count <= AirshipMap) return;
        _op = prefabs[AirshipMap].LoadAssetAsync<GameObject>();
        _loading = true;
        Plugin.Logger.LogInfo("select spawn: loading the Airship for the spawn picker");
    }

    internal static void AfterMeeting() => _afterMeeting = Active;

    // FixedUpdate (RulesHooks.Tick)
    internal static void Tick()
    {
        if (_hudHidden && !Minigame.Instance) ShowHud();
        if (!_loading) return;
        if (!ShipStatus.Instance) { Release(); return; }
        var lp = PlayerControl.LocalPlayer;
        bool start = _shipGen != Terrain.GameClock.ShipGen;
        if (!start && !_afterMeeting) return;
        // イントロが終わって動けるようになってから・他の画面を開いていない時
        if (IntroCutscene.Instance || !lp || !lp.CanMove || Minigame.Instance || !_op.IsDone) return;
        _shipGen = Terrain.GameClock.ShipGen;
        _afterMeeting = false;
        if (lp.Data == null || lp.Data.IsDead) return;
        try { Open(); }
        catch (Exception e) { Plugin.Logger.LogError($"select spawn: {e}"); }
    }

    internal static void Open()
    {
        var go = _op.Status == AsyncOperationStatus.Succeeded ? _op.Result : null;
        var ship = go ? go.GetComponent<AirshipStatus>() : null;
        var prefab = ship ? ship.SpawnInGame : null;
        var cam = Camera.main;
        int map = MapRules.MapId;
        var spots = RandomSpawn.SpotsFor(map);
        if (!prefab || !cam || spots == null || ShipStatus.Instance == null)
        {
            // 画面を出せない時はランダムスポーンの設定どおりに湧く
            Plugin.Logger.LogWarning("select spawn: picker not available");
            RandomSpawn.Go("select spawn fallback", force: true);
            return;
        }
        ClearMade();
        _hiddenLayers = HiddenLayers | PlayerLayers();

        var mg = Object.Instantiate(prefab, cam.transform, false);
        mg.transform.localPosition = new Vector3(0f, 0f, -600f);
        bool one = MapRules.RandomSpawn;
        int count = one ? 1 : Math.Min(mg.LocationButtons.Length, spots.Length);
        var picked = Pick(spots.Length, count);
        var vanilla = prefab.Locations;
        var locs = new SpawnInMinigame.SpawnLocation[count];
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            Vector2 world = RandomSpawn.ToShip(map, spots[picked[i]]);
            // MapFlip が開く時に鏡に映すので、MRP の反転の分は戻して渡す
            float x = MapFlip.Flipped ? MapFlip.Mirror(world.x) : world.x;
            var like = vanilla[i % vanilla.Length];
            locs[i] = new SpawnInMinigame.SpawnLocation
            {
                Name = like.Name,
                Location = new Vector3(x, world.y, 0f),
                Image = Shot(world),
                Rollover = null,
                RolloverSfx = like.RolloverSfx,
            };
            names[i] = RoomName(world);
        }
        mg.Locations = one ? Repeat(locs[0], mg.LocationButtons.Length) : locs;
        mg.Begin(null);
        if (one) KeepOne(mg);
        var hud = Vanilla.Hud;
        if (hud) { hud.SetHudActive(false); _hudHidden = true; }
        // 本編は名前 (StringNames) から字を書き、ボタンの並びも入れ替えるので、ボタンの絵がどの部屋の物かで名前を書き直す
        foreach (var button in mg.LocationButtons)
        {
            if (!button) continue;
            // 押せる所を絵の範囲に (本編の当たり判定は Airship の絵の置き方に合わせてある)
            var col = button.GetComponent<BoxCollider2D>();
            if (col)
            {
                col.size = ShotSize;
                col.offset = new Vector2(0f, ShotSize.y * 0.5f);
            }
            var sr = button.GetComponent<SpriteRenderer>() ?? button.GetComponentInChildren<SpriteRenderer>();
            var t = button.GetComponentInChildren<TextMeshPro>();
            if (!sr || !t || !sr.sprite) continue;
            for (int i = 0; i < count; i++)
                if (locs[i].Image && locs[i].Image.Pointer == sr.sprite.Pointer) { t.text = names[i]; break; }
        }
        Plugin.Logger.LogInfo($"select spawn: opened with {count} rooms ({string.Join(", ", names)}) hidden=0x{_hiddenLayers:X}");
    }

    // 本編はボタンの数だけ場所を並べる (足りないと止まる) ので、同じ場所をボタンの数だけ渡す
    internal static SpawnInMinigame.SpawnLocation[] Repeat(SpawnInMinigame.SpawnLocation loc, int n)
    {
        var a = new SpawnInMinigame.SpawnLocation[Math.Max(n, 1)];
        for (int i = 0; i < a.Length; i++) a[i] = loc;
        return a;
    }

    // ボタンを 1 つだけ残して並びの真ん中へ
    internal static void KeepOne(SpawnInMinigame mg)
    {
        float sum = 0f;
        int n = 0;
        foreach (var b in mg.LocationButtons)
            if (b) { sum += b.transform.localPosition.x; n++; }
        bool first = true;
        foreach (var b in mg.LocationButtons)
        {
            if (!b) continue;
            if (!first) { b.gameObject.SetActive(false); continue; }
            first = false;
            var p = b.transform.localPosition;
            b.transform.localPosition = new Vector3(sum / n, p.y, p.z);
        }
    }

    // Airship でランダムスポーンの行き先を全部の部屋から選んだ時の、ボタンの絵と名前
    internal static SpawnInMinigame.SpawnLocation AirshipSpot(SpawnInMinigame.SpawnLocation like, Vector2 world, out string name)
    {
        // メモリの厳しい Android では部屋を撮らず、本編のボタンの絵をそのまま使う
        bool shoot = !OperatingSystem.IsAndroid();
        if (shoot)
        {
            ClearMade();
            _hiddenLayers = HiddenLayers | PlayerLayers();
        }
        float x = MapFlip.Flipped ? MapFlip.Mirror(world.x) : world.x;
        name = RoomName(world);
        return new SpawnInMinigame.SpawnLocation
        {
            Name = like.Name,
            Location = new Vector3(x, world.y, 0f),
            Image = shoot ? Shot(world) : like.Image,
            Rollover = null,
            RolloverSfx = like.RolloverSfx,
        };
    }

    // 人の絵の部品がいる層 (着せ替えなどが人の層の外にあることがある)。開く時に 1 回だけ集める
    private static int PlayerLayers()
    {
        int mask = 0;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p) continue;
            foreach (var r in p.GetComponentsInChildren<Renderer>(true))
                if (r) mask |= 1 << r.gameObject.layer;
        }
        return mask;
    }

    private static int[] Pick(int n, int k)
    {
        var all = new int[n];
        for (int i = 0; i < n; i++) all[i] = i;
        for (int i = 0; i < k; i++)
        {
            int j = Rng.Next(i, n);
            (all[i], all[j]) = (all[j], all[i]);
        }
        var r = new int[k];
        Array.Copy(all, r, k);
        return r;
    }

    internal static string RoomName(Vector2 world)
    {
        var ship = ShipStatus.Instance;
        if (ship != null && ship.AllRooms != null)
            foreach (var room in ship.AllRooms)
                if (room && room.roomArea && room.roomArea.OverlapPoint(world))
                    return TranslationController.Instance.GetString(room.RoomId);
        return "?";
    }

    // その場所を船のカメラで撮った絵
    private static Sprite Shot(Vector2 world)
    {
        var main = Camera.main;
        Vector2 size = ShotSize;
        int w = ShotPx, h = (int)(ShotPx * size.y / size.x);
        var rt = RenderTexture.GetTemporary(w, h, 24);
        var camGo = new GameObject("MrpSpawnShotCam");
        var cam = camGo.AddComponent<Camera>();
        try
        {
            cam.CopyFrom(main);
            int hidden = _hiddenLayers;
            var hud = Vanilla.Hud;
            if (hud && hud.ShadowQuad) hidden |= 1 << hud.ShadowQuad.gameObject.layer;
            cam.cullingMask = main.cullingMask & ~hidden;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = main.backgroundColor;
            cam.orthographic = true;
            cam.orthographicSize = 1.7f; // 部屋の真ん中から縦 3.4 の範囲
            cam.aspect = (float)w / h;
            cam.transform.position = new Vector3(world.x, world.y, main.transform.position.z);
            cam.targetTexture = rt;
            cam.enabled = false;
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "MrpSpawnShot", wrapMode = TextureWrapMode.Clamp };
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false, true); // 読み戻しは要らないので CPU 側の写しを捨てる
            RenderTexture.active = prev;
            Made.Add(tex);
            // 本編のボタンの絵は下端が原点 (名前の字の上に絵が乗る)
            var sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0f), w / size.x, 0, SpriteMeshType.FullRect);
            Made.Add(sprite);
            return sprite;
        }
        finally
        {
            cam.targetTexture = null;
            Object.Destroy(camGo);
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    private static void ShowHud()
    {
        _hudHidden = false;
        var hud = Vanilla.Hud;
        var lp = PlayerControl.LocalPlayer;
        if (hud && lp && lp.Data != null && lp.Data.Role) hud.SetHudActive(lp, lp.Data.Role, true);
    }

    private static void ClearMade()
    {
        foreach (var o in Made) if (o) Object.Destroy(o);
        Made.Clear();
    }

    // 試合が終わった (船が消えた) ら手放す。中身は本編の場面の切り替えの掃除で消える
    private static void Release()
    {
        if (_hudHidden) ShowHud();
        ClearMade();
        _loading = false;
        _afterMeeting = false;
        _shipGen = -1;
        var prefabs = AmongUsClient.Instance ? AmongUsClient.Instance.ShipPrefabs : null;
        // 読み込みの途中で試合が終わった時も手放す (Asset はまだ空でも読み込みは持っている)
        if (prefabs != null && prefabs.Count > AirshipMap && prefabs[AirshipMap].IsValid()) prefabs[AirshipMap].ReleaseAsset();
        Plugin.Logger.LogInfo("select spawn: released the Airship");
    }
}
