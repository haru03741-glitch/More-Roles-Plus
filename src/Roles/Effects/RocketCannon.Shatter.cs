using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 詰められたサムライが大砲を内から斬り刻んで抜ける (全員の端末・抜けた知らせ 1 本から同じ流れ)。
// 筒の中心を通る刃の筋が 0.06 秒おきに 4 本走り、そのたびに筒がぶれる → 4 本の線で絵を切った片が一斉に外へ吹き飛び、
// 中からサムライが現れる。片は回りながら落ちて床で跳ね、止まったら試合の終わりまで残る (機関銃の残骸と同じ)
internal sealed partial class RocketCannon
{
    private const int ShatterCuts = 4;
    private const float CutEvery = 0.06f, BurstAt = 0.24f;     // 筋の間隔・割れる時刻 (音の noise_rl_shatter と同じ並び)
    private const float SlashLife = 0.2f, SlashGrow = 0.035f;
    private const float SlashLen = 1.7f, SlashGlowW = 0.32f, SlashCoreW = 0.07f;
    private const float CutCenterH = 0.3f;                      // 切る中心 (台の足元から上へ)
    private const int MaxShardWrecks = 48;

    private sealed class Slash
    {
        public GameObject Glow, Core;
        public Transform GlowTf, CoreTf;
        public SpriteRenderer GlowSr, CoreSr;
        public float Start, Cx, Cy, Z, Rot, R, G, B;
    }

    private sealed class Shard
    {
        public GameObject Go;
        public Transform Tf;
        public Mesh Mesh;
        public float X, Y, Z, Vx, Vy, Spin, Rot, Floor;
        public bool Landed;
    }

    private static readonly List<Slash> Slashes = new();
    private static readonly List<Shard> Shards = new();       // 飛んでいる途中
    private static readonly List<Shard> ShardWrecks = new();  // 床に残っている片
    private static Material _shardMat;
    private static float _flashW;                              // 筋の絵の幅 (ワールド)

    private float _shatterAt = -1f;
    private int _slashesOut;
    private float _cutX, _cutY, _cutZ, _cutFloor;
    private readonly FxMeshCut.Plane[] _cutPlanes = new FxMeshCut.Plane[ShatterCuts];
    private readonly float[] _cutAng = new float[ShatterCuts];

    // 掴んでいた相手が振り払った (全員の端末)。大砲が無ければ普通に消す
    internal static void Shatter(RoleBase owner, PlayerControl launcher)
    {
        var c = For(owner);
        if (c == null) { GunSound.Escape(launcher); return; }
        if (c._shatterAt >= 0f) return;
        if (c._done || c._fireStart >= 0f || !c._cannonSr || !c._cannonSr.sprite || !_flash)
        {
            GunSound.Escape(c._launcher);
            c.Remove();
            return;
        }
        c.BeginShatter();
    }

    private void BeginShatter()
    {
        _shatterAt = Time.time;
        _slashesOut = 0;
        var p = _cannonTf.position;
        _cutX = p.x;
        _cutY = _rootTf.position.y + CutCenterH;
        _cutZ = Z();
        _cutFloor = _rootTf.position.y;
        if (_flashW <= 0f) _flashW = FxMath.Max(0.01f, Terrain.DebrisArt.SoftGlow.bounds.size.x);
        // 刃の向きは毎回少しずつ変える (交わり方で片の形が変わる)。中心から少しずらして片の大きさをばらす
        float a0 = FxMath.Range(0f, 180f);
        for (int i = 0; i < ShatterCuts; i++)
        {
            float a = a0 + i * (180f / ShatterCuts) + FxMath.Range(-14f, 14f);
            _cutAng[i] = a;
            float r = a * (System.MathF.PI / 180f);
            float nx = -FxMath.Sin(r), ny = FxMath.Cos(r);
            _cutPlanes[i] = new FxMeshCut.Plane(nx, ny, FxMath.Range(-0.1f, 0.1f));
        }
        // 撃つ人の手は離れて消える
        if (_handL) _handL.SetActive(false);
        if (_handR) _handR.SetActive(false);
        GunSound.Shatter(_cutX, _cutY);
    }

    // 斬られている間 (Tick から)。割れたら大砲を消す
    private void TickShatter(float now)
    {
        float t = now - _shatterAt;
        while (_slashesOut < ShatterCuts && t >= _slashesOut * CutEvery)
        {
            int i = _slashesOut++;
            var pl = _cutPlanes[i];
            SpawnSlash(_cutX + pl.Nx * pl.D, _cutY + pl.Ny * pl.D, _cutZ - 0.003f, _cutAng[i], i / (float)(ShatterCuts - 1));
            float sa = FxMath.Range(0f, 2f * System.MathF.PI);
            SpawnPuff(_cutX + pl.Nx * pl.D, _cutY + pl.Ny * pl.D, FxMath.Cos(sa) * 3f, FxMath.Sin(sa) * 3f, 0.14f, 0.05f, 0.2f, _cutZ - 0.002f, true, PuffKind.Spark, sa * (180f / System.MathF.PI));
        }
        if (t < BurstAt)
        {
            // 斬られるたびに筒がぶれる
            float k = 1f - (t % CutEvery) / CutEvery;
            _cannonTf.localPosition = FxMath.V3(FxMath.Range(-0.045f, 0.045f) * k, FxMath.Range(-0.02f, 0.02f) * k, 0f);
            return;
        }
        Burst();
        Remove();
    }

    private void Burst()
    {
        _cannonTf.localPosition = FxMath.V3(0f, 0f, 0f);
        if (!_shardMat)
        {
            var shader = Shader.Find("Sprites/Default");
            _shardMat = shader ? new Material(shader) : new Material(_cannonSr.sharedMaterial);
            _shardMat.mainTexture = _cannonSr.sprite.texture;
        }
        int made = 0;
        var planes = new FxMeshCut.Plane[ShatterCuts];
        for (int mask = 0; mask < 1 << ShatterCuts; mask++)
        {
            for (int i = 0; i < ShatterCuts; i++)
            {
                var pl = _cutPlanes[i];
                planes[i] = (mask & 1 << i) != 0 ? pl : new FxMeshCut.Plane(-pl.Nx, -pl.Ny, -pl.D);
            }
            var mesh = FxMeshCut.Cut(_cannonSr, _cutX, _cutY, planes, ShatterCuts, null, "MrpCannonShard");
            if (!mesh) continue;
            var ctr = mesh.bounds.center;
            float cx = ctr.x, cy = ctr.y, d = FxMath.Sqrt(cx * cx + cy * cy);
            if (d < 0.02f) { cx = 0f; cy = 1f; d = 1f; }
            float v = FxMath.Range(2.2f, 3.6f);
            var go = new GameObject("MrpCannonShard");
            var ship = ShipStatus.Instance;
            if (ship) go.transform.SetParent(ship.transform, true);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _shardMat;
            var s = new Shard
            {
                Go = go, Tf = go.transform, Mesh = mesh, X = _cutX, Y = _cutY, Z = _cutZ,
                Vx = cx / d * v, Vy = cy / d * v * 0.8f + FxMath.Range(1.6f, 2.6f),
                Spin = (cx >= 0f ? -1f : 1f) * FxMath.Range(300f, 820f),
                Floor = _cutFloor + FxMath.Range(-0.25f, 0.2f),
            };
            s.Tf.position = FxMath.V3(_cutX, _cutY, _cutZ);
            Shards.Add(s);
            made++;
        }
        // 割れた瞬間: 白い核・にじみ・吹き出す煙・火花。近い人の画面が揺れる
        SpawnPuff(_cutX, _cutY, 0f, 0f, 0.5f, 1.0f, 0.12f, _cutZ - 0.004f, true, PuffKind.Core);
        SpawnPuff(_cutX, _cutY, 0f, 0f, 0.7f, 1.6f, 0.28f, _cutZ - 0.0035f, true, PuffKind.Glow);
        for (int i = 0; i < 6; i++)
        {
            float a = (i + FxMath.Range(-0.3f, 0.3f)) * (2f * System.MathF.PI / 6f), v = FxMath.Range(1.2f, 1.9f);
            SpawnPuff(_cutX, _cutY, FxMath.Cos(a) * v, FxMath.Sin(a) * v * 0.75f, 0.25f, 0.55f, FxMath.Range(0.5f, 0.7f), _cutZ - 0.001f, false);
        }
        for (int i = 0; i < 10; i++)
        {
            float a = FxMath.Range(0f, 2f * System.MathF.PI), v = FxMath.Range(3f, 5.5f);
            SpawnPuff(_cutX, _cutY, FxMath.Cos(a) * v, FxMath.Sin(a) * v, 0.16f, 0.06f, FxMath.Range(0.22f, 0.38f), _cutZ - 0.003f, true, PuffKind.Spark, a * (180f / System.MathF.PI));
        }
        var lp = PlayerControl.LocalPlayer;
        var hud = Vanilla.Hud;
        if (lp && hud && hud.PlayerCam)
        {
            var me = lp.transform.position;
            float dx = me.x - _cutX, dy = me.y - _cutY;
            if (dx * dx + dy * dy < 36f) hud.PlayerCam.ShakeScreen(0.15f, 0.8f);
        }
        Plugin.Logger.LogInfo($"rocket cannon: shattered into {made}");
    }

    // 刃の筋: 青→桃 (刀の振りと同じ色) のにじみの上に白い芯。走って伸び、細って消える
    private static void SpawnSlash(float x, float y, float z, float deg, float u)
    {
        var s = new Slash { Start = Time.time, Cx = x, Cy = y, Z = z, Rot = deg };
        s.R = 0.35f + 0.65f * u; s.G = 0.62f - 0.12f * u; s.B = 1f - 0.2f * u;
        s.Glow = NewStreak("MrpCannonSlashGlow", out s.GlowTf, out s.GlowSr);
        s.Core = NewStreak("MrpCannonSlashCore", out s.CoreTf, out s.CoreSr);
        s.GlowTf.position = FxMath.V3(x, y, z);
        s.CoreTf.position = FxMath.V3(x, y, z - 0.0002f);
        var rot = FxMath.RotZ(deg);
        s.GlowTf.rotation = rot;
        s.CoreTf.rotation = rot;
        Slashes.Add(s);
    }

    private static GameObject NewStreak(string name, out Transform tf, out SpriteRenderer sr)
    {
        var go = new GameObject(name);
        var ship = ShipStatus.Instance;
        if (ship) go.transform.SetParent(ship.transform, true);
        tf = go.transform;
        sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Terrain.DebrisArt.SoftGlow;
        tf.localScale = FxMath.V3(0f, 0f, 1f);
        return go;
    }

    private static void TickSlashes(float now)
    {
        for (int i = Slashes.Count - 1; i >= 0; i--)
        {
            var s = Slashes[i];
            float f = (now - s.Start) / SlashLife;
            if (f >= 1f || !s.Glow || !s.Core)
            {
                if (s.Glow) Object.Destroy(s.Glow);
                if (s.Core) Object.Destroy(s.Core);
                Slashes.RemoveAt(i);
                continue;
            }
            float grow = EaseOut((now - s.Start) / SlashGrow);
            float thin = f < 0.3f ? 1f : 1f - EaseIn((f - 0.3f) / 0.7f);
            float len = SlashLen * grow / _flashW;
            s.GlowTf.localScale = FxMath.V3(len * 1.05f, SlashGlowW * thin / _flashW, 1f);
            s.CoreTf.localScale = FxMath.V3(len, SlashCoreW * thin / _flashW, 1f);
            s.GlowSr.color = FxMath.Rgba(s.R, s.G, s.B, thin);
            s.CoreSr.color = FxMath.Rgba(1f, 1f, 1f, thin);
        }
    }

    // 片: 落ちて床で跳ね、止まったら残す (止まった物は毎フレーム触らない)
    private static void TickShards(float dt)
    {
        for (int i = Shards.Count - 1; i >= 0; i--)
        {
            var h = Shards[i];
            if (!h.Go)
            {
                if (h.Mesh) Object.Destroy(h.Mesh);
                Shards.RemoveAt(i);
                continue;
            }
            h.Vy -= 9f * dt;
            h.X += h.Vx * dt;
            h.Y += h.Vy * dt;
            h.Rot += h.Spin * dt;
            if (h.Y < h.Floor && h.Vy < 0f)
            {
                h.Y = h.Floor;
                if (h.Vy > -1.5f) h.Landed = true;
                else { h.Vy = -h.Vy * 0.3f; h.Vx *= 0.5f; h.Spin *= 0.3f; }
            }
            h.Tf.position = FxMath.V3(h.X, h.Y, h.Landed ? h.Floor / 1000f + 0.0005f : h.Z);
            h.Tf.rotation = FxMath.RotZ(h.Rot);
            if (!h.Landed) continue;
            Shards.RemoveAt(i);
            if (ShardWrecks.Count >= MaxShardWrecks)
            {
                var old = ShardWrecks[0];
                if (old.Go) Object.Destroy(old.Go);
                if (old.Mesh) Object.Destroy(old.Mesh);
                ShardWrecks.RemoveAt(0);
            }
            ShardWrecks.Add(h);
        }
    }

    private static void ClearShatter()
    {
        foreach (var s in Slashes)
        {
            if (s.Glow) Object.Destroy(s.Glow);
            if (s.Core) Object.Destroy(s.Core);
        }
        Slashes.Clear();
        foreach (var list in new[] { Shards, ShardWrecks })
        {
            foreach (var h in list)
            {
                if (h.Go) Object.Destroy(h.Go);
                if (h.Mesh) Object.Destroy(h.Mesh);
            }
            list.Clear();
        }
        if (_shardMat) Object.Destroy(_shardMat);
        _shardMat = null;
    }
}
