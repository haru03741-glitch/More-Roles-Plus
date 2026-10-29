using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// サムライの体の歪み・残像・オーラ・刀のきらめき
internal sealed partial class SamuraiBlade
{
    // 体の絵 (BodyForms) と帽子・服 (Cosmetics) は人の根の同じ原点の兄弟。両方を同じだけ足元を軸に動かし、終わりに控えた元へ戻す。
    // 跳ねるエモート・踏ん張り・掴まれて飛ぶ間は向こうが同じ所を動かすので、こちらは元へ戻して手を出さない
    private Transform _body, _cos;
    private Vector3 _bp, _bs, _cp, _cs;
    private Quaternion _br, _cr;
    private float _footX, _footY;
    private bool _posed, _blocked;

    private bool BeginBody(PlayerControl pc)
    {
        var root = pc.transform;
        _body = root.Find("BodyForms");
        _cos = pc.cosmetics ? pc.cosmetics.transform : null;
        if (!_body && !_cos) return false;
        if (_body) { _bp = _body.localPosition; _br = _body.localRotation; _bs = _body.localScale; }
        if (_cos) { _cp = _cos.localPosition; _cr = _cos.localRotation; _cs = _cos.localScale; }
        Vector3 at = root.position;
        Vector2 feet = pc.GetTruePosition();
        _footX = (feet.x - at.x) * _inv;
        _footY = (feet.y - at.y) * _inv;
        _posed = true;
        return true;
    }

    private void RestoreBody()
    {
        if (!_posed) return;
        _posed = false;
        if (_body) { _body.localPosition = _bp; _body.localRotation = _br; _body.localScale = _bs; }
        if (_cos) { _cos.localPosition = _cp; _cos.localRotation = _cr; _cos.localScale = _cs; }
    }

    // 他の演出が体を動かし始める直前に呼ばれる。こちらの歪みを外して元へ戻し、向こうが控える元の形を汚さない
    internal static void ReleaseBody(byte id)
    {
        foreach (var b in Live)
        {
            if (b._role.PlayerId != id) continue;
            b.RestoreBody();
            b._blocked = true;
        }
    }

    private bool OthersPose(byte id) => Fx.EmoteArt.IsPlaying(id) || Terrain.CrewBrace.Posed(id) || Terrain.CrewGrip.Flying(id);

    // 体の姿 (ワールドの値・左右の鏡は済んでいる)
    private void WriteBody(PlayerControl pc, in Pose p)
    {
        _blocked = OthersPose(pc.PlayerId);
        if (_blocked) { RestoreBody(); return; }
        bool rest = p.Lean == 0f && p.Sx == 1f && p.Sy == 1f && p.Turn == 1f && p.Hop == 0f && p.Dx == 0f;
        if (rest) { RestoreBody(); return; }
        if (!_posed && !BeginBody(pc)) return;
        float ar = p.Lean * FxMath.Deg2Rad, co = FxMath.Cos(ar), si = FxMath.Sin(ar);
        float kx = p.Sx * p.Turn, ky = p.Sy;
        var rot = FxMath.RotZ(p.Lean);
        float ox = p.Dx * _inv, oy = p.Hop * _inv;
        if (_body) Place(_body, _bp, _bs, kx, ky, co, si, ox, oy, rot);
        if (_cos) Place(_cos, _cp, _cs, kx, ky, co, si, ox, oy, rot);
    }

    // 子の原点を足元からの差で縮め・回して置き直す (子の元の回転は 0 の前提)
    private void Place(Transform tf, Vector3 basePos, Vector3 baseScale, float kx, float ky, float co, float si, float ox, float oy, Quaternion rot)
    {
        float vx = (basePos.x - _footX) * kx, vy = (basePos.y - _footY) * ky;
        tf.localPosition = FxMath.V3(_footX + co * vx - si * vy + ox, _footY + si * vx + co * vy + oy, basePos.z);
        tf.localRotation = rot;
        tf.localScale = FxMath.V3(baseScale.x * kx, baseScale.y * ky, baseScale.z);
    }

    // ---- オーラ (体の後ろの揺らめく光・内は青・外は桃) ----

    private const float AuraZ = 0.0006f;  // 体のすぐ後ろ (深いと床の絵の後ろへ回る)
    private static Sprite _auraSprite, _glintSprite;
    private SpriteRenderer _auraIn, _auraOut, _glint;
    private Transform _auraInTf, _auraOutTf, _glintTf;
    // きらめき: 出す時刻・刀の上の始めと終わりの位置 (拳から)・秒・大きさ
    private float _glT = -10f, _glFrom, _glTo, _glDur, _glSize;
    private float _gl2T = -10f;
    private bool _glOn;

    // 縦長の炎の形 (下は丸く・上へ細く伸びる)。体に隠れる中心より、体の輪郭のすぐ外に当たる縁を明るくする
    private static float Flame(float x, float y)
    {
        float yy = y + 0.12f;
        float w = 0.40f - 0.14f * FxMath.Clamp01(yy * 1.6f);
        float r = FxMath.Sqrt(x * x / (w * w) + yy * yy / (0.40f * 0.40f));
        if (r >= 1f) return 0f;
        float e = (r - 0.66f) / 0.17f;
        float rim = FxMath.Exp(-e * e);
        float fill = 0.35f * (1f - r);
        return FxMath.Max(rim, fill) * FxMath.Clamp01((1f - r) / 0.12f);
    }

    // 十字の光 (細い 2 本の筋+丸い芯)
    private static float Star(float x, float y)
    {
        float ax = FxMath.Abs(x), ay = FxMath.Abs(y);
        float r = FxMath.Sqrt(x * x + y * y) * 2f;
        if (r >= 1f) return 0f;
        float fall = (1f - r) * (1f - r);
        float h = FxMath.Clamp01(1f - ay / 0.035f) * fall;
        float v = FxMath.Clamp01(1f - ax / 0.035f) * fall;
        float core = FxMath.Clamp01((0.22f - r) / 0.2f);
        return FxMath.Max(FxMath.Max(h, v), core);
    }

    private void EnsureFx(int layer)
    {
        _auraSprite ??= FxSprites.Rect(64, 96, Flame, "MrpSamuraiAura");
        _glintSprite ??= FxSprites.Rect(64, 64, Star, "MrpSamuraiGlint");
        _auraOut = MakeSr("AuraOut", _rootTf, layer, _auraSprite, out _auraOutTf);
        _auraIn = MakeSr("AuraIn", _rootTf, layer, _auraSprite, out _auraInTf);
        _glint = MakeSr("Glint", _gripTf, layer, _glintSprite, out _glintTf);
        _auraOut.color = _auraIn.color = _glint.color = FxMath.Rgba(1f, 1f, 1f, 0f);
    }

    private static SpriteRenderer MakeSr(string name, Transform parent, int layer, Sprite sprite, out Transform tf)
    {
        var go = new GameObject(name) { layer = layer };
        tf = go.transform;
        tf.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        return sr;
    }

    private void WriteAura(float level, float hop)
    {
        if (!_auraIn) return;
        float now = Time.time;
        // 2 つの揺れを重ねて炎がゆらぐ (縦だけ伸び縮み・少し横へ揺れる)
        float f1 = FxMath.Sin(now * 13f), f2 = FxMath.Sin(now * 29f + 1.7f);
        float fy = 1f + 0.07f * f1 + 0.04f * f2;
        float sway = 0.012f * f2;
        float y = 0.24f + hop;
        _auraOutTf.localPosition = FxMath.V3(sway, y + 0.04f, AuraZ);
        _auraOutTf.localScale = FxMath.V3(1.55f, 1.75f * fy, 1f);
        _auraInTf.localPosition = FxMath.V3(-sway * 0.5f, y, AuraZ - 0.0002f);
        _auraInTf.localScale = FxMath.V3(1.2f, 1.35f * (2f - fy), 1f);
        float a = FxMath.Clamp01(level);
        if (_fallenTint)
        {
            _auraOut.color = FxMath.Rgba(1f, 0.15f, 0.25f, 0.7f * a);
            _auraIn.color = FxMath.Rgba(1f, 0.45f, 0.35f, 0.8f * a);
        }
        else
        {
            _auraOut.color = FxMath.Rgba(1f, 0.30f, 0.62f, 0.7f * a);
            _auraIn.color = FxMath.Rgba(0.25f, 0.5f, 1f, 0.8f * a);
        }
        WriteGlint(now);
    }

    // 抜く: 鯉口で光り (0.1 秒後)、構えに着く所で刀の根元から切っ先へ光が走る。納める: 鍔が鞘に当たる所で光る
    private void SpawnGlint(float delay, bool drawn)
    {
        _glT = Time.time + delay * SlowMo;
        _glFrom = _glTo = 0.06f;
        _glDur = 0.12f;
        _glSize = 0.30f;
        _gl2T = drawn ? Time.time + 0.43f * SlowMo : -10f;
    }

    private void WriteGlint(float now)
    {
        if (!_glint) return;
        float t = (now - _glT) / SlowMo;
        float from = _glFrom, to = _glTo, dur = _glDur, size = _glSize;
        float t2 = (now - _gl2T) / SlowMo;
        if (t2 >= 0f && t2 < 0.16f)
        {
            t = t2;
            from = 0.12f;
            to = TipLen - 0.04f;
            dur = 0.16f;
            size = 0.22f;
        }
        if (t < 0f || t >= dur)
        {
            if (_glOn) { _glOn = false; _glint.color = FxMath.Rgba(1f, 1f, 1f, 0f); }
            return;
        }
        float u = t / dur;
        float pop = u < 0.3f ? u / 0.3f : 1f - (u - 0.3f) / 0.7f;
        float s = size * pop;
        _glintTf.localPosition = FxMath.V3(from + (to - from) * u, 0.01f, -0.002f);
        _glintTf.localRotation = FxMath.RotZ(u * 90f);
        _glintTf.localScale = FxMath.V3(s, s, 1f);
        if (!_glOn) { _glOn = true; _glint.color = FxMath.Rgba(0.85f, 0.93f, 1f, 1f); }
    }

    // ---- 残像 (振っている間に体の絵の写しを置いていき、その場で薄れる) ----

    private sealed class Ghost
    {
        public GameObject Go;
        public Transform Tf;
        public SpriteRenderer Sr;
        public float T;
        public bool Red;
    }

    private const int MaxGhosts = 12;
    private const float GhostLife = 0.16f;
    private static readonly List<Ghost> Ghosts = new();
    private static readonly Stack<Ghost> GhostPool = new();
    private float _lastGhost = -10f;

    private void SpawnGhost(float now)
    {
        if (now - _lastGhost < Frame || _hidden || Ghosts.Count >= MaxGhosts) return;
        var pc = _role.Player;
        var body = pc && pc.cosmetics ? pc.cosmetics.currentBodySprite : null;
        var src = body != null ? body.BodySprite : null;
        var mat = FxHands.PlayerMat();
        if (!src || !mat) return;
        _lastGhost = now;
        var g = GhostPool.Count > 0 ? GhostPool.Pop() : null;
        if (g == null || !g.Go)
        {
            var go = new GameObject("MrpSamuraiGhost") { layer = pc.gameObject.layer };
            Object.DontDestroyOnLoad(go);
            g = new Ghost { Go = go, Tf = go.transform, Sr = go.AddComponent<SpriteRenderer>() };
            g.Sr.sharedMaterial = mat;
        }
        var stf = src.transform;
        var pos = stf.position;
        g.Tf.position = FxMath.V3(pos.x, pos.y, pos.z + 0.003f);
        g.Tf.rotation = stf.rotation;
        g.Tf.localScale = stf.lossyScale;
        g.Sr.sprite = src.sprite;
        g.Sr.flipX = src.flipX;
        if (_colorId >= 0 && _colorId < Palette.PlayerColors.Length) PlayerMaterial.SetColors(_colorId, g.Sr);
        g.T = now;
        g.Red = _fallenTint;
        g.Go.SetActive(true);
        Ghosts.Add(g);
    }

    // 毎フレーム。残像が無ければ件数の比較だけで帰る
    private static void TickFx()
    {
        if (Ghosts.Count == 0) return;
        float now = Time.time;
        for (int i = Ghosts.Count - 1; i >= 0; i--)
        {
            var g = Ghosts[i];
            if (!g.Go) { Ghosts.RemoveAt(i); continue; }
            float u = (now - g.T) / (GhostLife * SlowMo);
            if (u >= 1f)
            {
                g.Go.SetActive(false);
                GhostPool.Push(g);
                Ghosts.RemoveAt(i);
                continue;
            }
            float a = 0.5f * (1f - u) * (1f - u);
            g.Sr.color = g.Red ? FxMath.Rgba(1f, 0.4f, 0.45f, a) : FxMath.Rgba(0.55f, 0.75f, 1f, a);
        }
    }

    private void HideFx()
    {
        if (_auraIn) _auraIn.color = _auraOut.color = FxMath.Rgba(1f, 1f, 1f, 0f);
        if (_glint) _glint.color = FxMath.Rgba(1f, 1f, 1f, 0f);
        _glOn = false;
        ReleaseSmear();
        if (_smearGo) _smearGo.SetActive(false);
    }

    private void DisposeFx()
    {
        if (_smearGo) Object.Destroy(_smearGo);
        if (_smearMesh) Object.Destroy(_smearMesh);
        _smearGo = null;
        _smearMesh = null;
        _smT = -10f;
        _smOn = false;
    }
}
