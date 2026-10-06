// Based on https://github.com/waffle-ful/Aeterna-End-K-not Modules/FxHands.cs (GPL-3.0)
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Fx;

// 手の部品: プレイヤー色のひな形 (赤=本体色・青=影色) の手を本人の色で塗って出し、キーの通りに動かす。
// 手に持たせる物 (ハンマー等) は同じ原点に重ねて一緒に回す (物の絵の原点 = 握る所)。
// 描くのは手元の画面だけ。メインスレッド専用。毎フレームの算術は FxMath (Unity の算術は interop でゴミが出る)
internal static class FxHands
{
    // t 秒の時点の姿。間は直線で補間する
    internal struct Key
    {
        public float T;
        public float Dx, Dy, Rot, Scale, Alpha, Shake;

        public Key(float t, float rot, float scale = 1f, float alpha = 1f, float dx = 0f, float dy = 0f, float shake = 0f)
        {
            T = t;
            Rot = rot;
            Scale = scale;
            Alpha = alpha;
            Dx = dx;
            Dy = dy;
            Shake = shake;
        }
    }

    private sealed class Hand
    {
        public GameObject Go;
        public Transform Tf;
        public SpriteRenderer Sr;
        public SpriteRenderer Prop;
        public Key[] Keys;
        public float Start;
        public Vector2 Pos;
        public float Z;
        public float Size;
        public bool Flip;
    }

    private const int MaxLive = 16;
    private const float SelfPpu = 100f;     // 握った手の絵 (140 px) は大きさ 1 で 1.4 単位
    private const float PropBehindZ = 0.0005f;

    private static readonly List<Hand> Live = new();
    private static readonly Stack<Hand> Pool = new();
    private static Material _playerMaterial;

    // keys の通りに握った手を動かす。pos はワールド座標 (握る所)、size は絵の倍率、flip で左右反転 (回転も鏡に)。
    // prop は同じ原点に重ねて持たせる物 (プレイヤー色に塗らない・手の奥)。propRot = 手に対する物の傾き (度・左右反転で鏡に)。
    // z は奥行き (本編のクルーは y/1000)
    internal static void Play(Key[] keys, Vector2 pos, int colorId, float size, bool flip, float delay, float z, Sprite prop, float propRot = 0f)
    {
        if (keys == null || keys.Length == 0 || Live.Count >= MaxLive) return;
        if (colorId < 0 || colorId >= Palette.PlayerColors.Length) colorId = 0;

        Hand h = null;
        try
        {
            Material mat = PlayerMat();
            Sprite grip = GripSprite();
            if (!mat || !grip) return;

            h = Pool.Count > 0 ? Pool.Pop() : null;
            if (h == null || !h.Go)
            {
                var go = new GameObject("MrpHand") { layer = 0 };
                Object.DontDestroyOnLoad(go);
                h = new Hand { Go = go, Tf = go.transform, Sr = go.AddComponent<SpriteRenderer>() };
                h.Sr.sharedMaterial = mat;
                var pgo = new GameObject("MrpHandProp") { layer = 0 };
                pgo.transform.SetParent(go.transform, false);
                pgo.transform.localPosition = FxMath.V3(0f, 0f, PropBehindZ);
                h.Prop = pgo.AddComponent<SpriteRenderer>();
            }

            PlayerMaterial.SetColors(colorId, h.Sr);
            h.Keys = keys;
            h.Start = Time.time + delay;
            h.Pos = pos;
            h.Z = z;
            h.Size = size;
            h.Flip = flip;
            h.Sr.sprite = grip;
            h.Sr.flipX = flip;
            h.Prop.sprite = prop;
            h.Prop.flipX = flip;
            h.Prop.transform.localRotation = FxMath.RotZ(flip ? -propRot : propRot);
            h.Sr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
            h.Prop.color = FxMath.Rgba(1f, 1f, 1f, 0f);

            h.Tf.position = FxMath.V3(pos.x, pos.y, z);
            h.Tf.localScale = FxMath.V3(0f, 0f, 1f);
            h.Go.SetActive(true);
            Live.Add(h);
            h = null;
        }
        catch (System.Exception e)
        {
            Plugin.Logger.LogError($"[FxHands] play: {e}");
            // 準備の途中で落ちた手は Live にも Pool にも居ない → 隠して Pool へ戻す (残すと画面に居座る)
            if (h != null && h.Go)
            {
                h.Go.SetActive(false);
                Pool.Push(h);
            }
        }
    }

    // 毎フレーム。出ている手が無ければ件数の比較だけで帰る
    internal static void Tick()
    {
        if (Live.Count == 0) return;

        float now = Time.time;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            Hand h = Live[i];
            if (!h.Go)
            {
                Live.RemoveAt(i);
                continue;
            }

            float t = now - h.Start;
            if (t < 0f) continue;

            Key[] k = h.Keys;
            if (t >= k[^1].T)
            {
                Release(h);
                Live.RemoveAt(i);
                continue;
            }

            int j = 0;
            while (j + 1 < k.Length && k[j + 1].T <= t) j++;
            Key a = k[j];
            Key b = j + 1 < k.Length ? k[j + 1] : a;
            float u = b.T > a.T ? (t - a.T) / (b.T - a.T) : 1f;

            float sx = h.Flip ? -1f : 1f;
            float shake = FxMath.Lerp(a.Shake, b.Shake, u);
            float jx = shake > 0f ? FxMath.Sin(t * 173f) * shake : 0f;
            float jy = shake > 0f ? FxMath.Sin(t * 131f + 1.3f) * shake : 0f;
            float sc = FxMath.Lerp(a.Scale, b.Scale, u) * h.Size;
            float rot = sx * FxMath.Lerp(a.Rot, b.Rot, u);
            h.Tf.position = FxMath.V3(h.Pos.x + sx * FxMath.Lerp(a.Dx, b.Dx, u) + jx, h.Pos.y + FxMath.Lerp(a.Dy, b.Dy, u) + jy, h.Z);
            h.Tf.rotation = FxMath.RotZ(rot);
            h.Tf.localScale = FxMath.V3(sc, sc, 1f);
            float al = FxMath.Clamp01(FxMath.Lerp(a.Alpha, b.Alpha, u));
            h.Sr.color = FxMath.Rgba(1f, 1f, 1f, al);
            h.Prop.color = FxMath.Rgba(1f, 1f, 1f, al);
        }
    }

    internal static void ClearAll()
    {
        foreach (Hand h in Live) Release(h);
        Live.Clear();
    }

    private static void Release(Hand h)
    {
        if (!h.Go) return;

        h.Go.SetActive(false);
        if (Pool.Count < MaxLive) Pool.Push(h);
        else Object.Destroy(h.Go);
    }

    // 握った手 (拳)。本編の握った手はポーラス・エアシップ・ファングルのタスク画面にしか無いので自作の絵
    internal static Sprite GripSprite() => Roles.ButtonIcons.Load("hand_grip", SelfPpu, FxMath.V2(0.5f, 0.5f));

    internal static Material PlayerMat()
    {
        if (_playerMaterial) return _playerMaterial;
        if (!GameManager.Instance || GameManager.Instance.deadBodyPrefab == null || GameManager.Instance.deadBodyPrefab.Length == 0) return null;

        DeadBody prefab = GameManager.Instance.deadBodyPrefab[0];
        SpriteRenderer src = prefab.bodyRenderers != null && prefab.bodyRenderers.Length > 0 ? prefab.bodyRenderers[0] : null;
        return _playerMaterial = src ? src.sharedMaterial : null;
    }

    // 今の状態 (確認用)
    internal static string Describe() => $"live={Live.Count} pool={Pool.Count} mat={(_playerMaterial ? _playerMaterial.shader.name : "null")}";
}
