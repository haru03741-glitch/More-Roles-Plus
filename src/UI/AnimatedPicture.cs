using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.UI;

// 動く絵 (コマを決まった速さで差し替える)。差し替えるのは画面に出ている間だけで、コマが変わったフレームだけ書く (Tick)
internal sealed class AnimatedPicture : Widget
{
    private static readonly List<AnimatedPicture> Live = new();

    public Sprite[] Frames;
    public float Fps;
    public Vector2 Size;
    public Color Color = Color.white;
    public bool Fit;

    private GameObject _go;
    private SpriteRenderer _sr;
    private int _shown = -1;

    public AnimatedPicture(Sprite[] frames, float fps, float w, float h)
    {
        Frames = frames;
        Fps = fps;
        Size = new Vector2(w, h);
    }

    public override GameObject Build(Transform parent, BuildCtx ctx, float maxWidth, out Vector2 size)
    {
        var go = Node("AnimatedPicture", parent, ctx.Layer);
        size = Size;
        if (Frames == null || Frames.Length == 0 || !Frames[0]) return go;
        _sr = go.AddComponent<SpriteRenderer>();
        _sr.color = Color;
        // コマは全部同じ大きさ (最初のコマで合わせる)
        var b = Frames[0].bounds.size;
        if (b.x > 0f && b.y > 0f)
        {
            float sx = Size.x / b.x, sy = Size.y / b.y;
            if (Fit) sx = sy = Math.Min(sx, sy);
            go.transform.localScale = new Vector3(sx, sy, 1f);
        }
        _go = go;
        Apply(Time.unscaledTime);
        Live.Add(this);
        return go;
    }

    public static void Tick()
    {
        if (Live.Count == 0) return;
        float t = Time.unscaledTime;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var a = Live[i];
            // 生きているかを Unity に聞くのはコマが変わる時だけ (毎フレーム聞かない)
            if (a.Frame(t) == a._shown) continue;
            if (!a._go)
            {
                Live.RemoveAt(i);
                continue;
            }
            // 閉じたタブの中 (非表示) では動かさない
            if (a._go.activeInHierarchy) a.Apply(t);
        }
    }

    private int Frame(float t) => (int)(t * Fps) % Frames.Length;

    private void Apply(float t)
    {
        int f = Frame(t);
        if (f == _shown) return;
        _shown = f;
        var sp = Frames[f];
        if (sp) _sr.sprite = sp;
    }
}
