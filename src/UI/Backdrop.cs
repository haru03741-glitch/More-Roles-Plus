// Based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Configuration/NebulaSettingMenu.cs (GPL-3.0)
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.UI;

// 欄の後ろに暗く敷く大きな絵。絵の中央を欄の形に切り出して敷き、絵を替えた時は右から滑り込みながら浮かび上がる。
// 動くのは替えてから約 0.2 秒だけ (Tick は動いている物が無ければ何もしない)
internal sealed class Backdrop : Widget
{
    public Vector2 Size;
    public float Bright = 0.2f;
    private SpriteRenderer _sr;
    private Sprite _src;   // 切り出す前の絵 (同じ絵を渡されたら何もしない)
    private Sprite _cut;
    private float _t = 1f;
    private static readonly List<Backdrop> Moving = new();

    private const float Slide = 0.24f, Speed = 4.8f;
    internal const string ImageName = "BackdropImage";
    internal const int SortOrder = -10;

    public Backdrop(float w, float h, Sprite initial = null)
    {
        Size = new Vector2(w, h);
        _src = initial;
    }

    public override GameObject Build(Transform parent, BuildCtx ctx, float maxWidth, out Vector2 size)
    {
        var go = Node("Backdrop", parent, ctx.Layer);
        // 滑り込みは子の位置で動かす (自分の位置は並べる箱が決める)
        _sr = Node(ImageName, go.transform, ctx.Layer).AddComponent<SpriteRenderer>();
        _sr.sortingOrder = SortOrder;
        var first = _src;
        _src = null;
        Show(first, false);
        size = Size;
        return go;
    }

    public void Show(Sprite sp, bool animate = true)
    {
        if (!_sr || ReferenceEquals(sp, _src) && (sp == null || _cut)) return;
        _src = sp;
        if (_cut) Object.Destroy(_cut);
        _cut = null;
        _sr.sprite = null;
        if (!sp) return;
        // 欄の縦横比で絵の中央を切り出す (はみ出しを隠す仕掛けを使わずに済む)
        var r = sp.rect;
        float aspect = Size.x / Size.y;
        float w = r.width, h = r.height, x = r.x, y = r.y;
        if (w / h > aspect)
        {
            float nw = h * aspect;
            x += (w - nw) / 2f;
            w = nw;
        }
        else
        {
            float nh = w / aspect;
            y += (h - nh) / 2f;
            h = nh;
        }
        _cut = Sprite.Create(sp.texture, new Rect(x, y, w, h), new Vector2(0.5f, 0.5f), w / Size.x, 0, SpriteMeshType.FullRect);
        _cut.hideFlags = HideFlags.HideAndDontSave;
        _sr.sprite = _cut;
        _t = animate ? 0f : 1f;
        Apply();
        if (animate && !Moving.Contains(this)) Moving.Add(this);
    }

    private void Apply()
    {
        float pp = (1f - _t) * (1f - _t);
        _sr.color = new Color(Bright, Bright, Bright, 1f - pp);
        _sr.transform.localPosition = new Vector3(pp * Slide, 0f, 0f);
    }

    public static void Tick()
    {
        if (Moving.Count == 0) return;
        float dt = Time.deltaTime;
        for (int i = Moving.Count - 1; i >= 0; i--)
        {
            var b = Moving[i];
            if (!b._sr)
            {
                if (b._cut) Object.Destroy(b._cut);
                Moving.RemoveAt(i);
                continue;
            }
            b._t += dt * Speed;
            if (b._t >= 1f) b._t = 1f;
            b.Apply();
            if (b._t >= 1f) Moving.RemoveAt(i);
        }
    }
}
