using System.Collections.Generic;
using MoreRolesPlus.Fx;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 自分の端末の画面 (下の中央・部屋の名前の上) に出す小さな札: 役職の絵・字・ゲージ。
// ホスト用 MOD (TownOfHost 系) は残り時間や状態を名前の後ろの字でしか見せられないが、MRP は全員が MOD を入れているので画面の部品で見せる。
// 役職ごとに 1 つ作り、毎フレーム Set で出す/隠す。出ている札は作った順に下から積む。
public sealed class HudReadout
{
    private const float Width = 3.3f, Height = 0.5f, Step = 0.6f, Bottom = 1.25f;
    private static readonly List<HudReadout> Live = new();
    private static Sprite _pill, _white;

    private readonly Sprite _iconSprite;
    private readonly Color _color;
    private GameObject _root;
    private AspectPosition _aspect;
    private SpriteRenderer _back, _icon, _barBack, _barFill;
    private TextMeshPro _text;
    private bool _on;
    private string _shown;
    private int _slot = -1;

    public HudReadout(Lifespan life, Sprite icon, Color color)
    {
        _iconSprite = icon;
        _color = color;
        Live.Add(this);
        life.OnRelease(Destroy);
    }

    // on = 出すか・text = 字 (色の札も可)・fill = ゲージ (0〜1、負なら出さない)
    public void Set(bool on, string text = null, float fill = -1f)
    {
        var map = MapBehaviour.Instance;
        on &= !(map && map.IsOpen) && !ExileController.Instance && !MeetingHud.Instance;
        if (on) Ensure();
        if (!_root) return;
        if (_root.activeSelf != on) _root.SetActive(on);
        _on = on;
        Relayout();
        if (!on) return;
        if (_text && text != _shown) { _text.text = text ?? ""; _shown = text; }
        bool bar = fill >= 0f;
        if (_barBack.enabled != bar) { _barBack.enabled = bar; _barFill.enabled = bar; }
        if (bar)
        {
            float f = Mathf.Clamp01(fill);
            float w = Width - 1.0f;
            _barFill.transform.localScale = new Vector3(w * f, 0.07f, 1f);
            _barFill.transform.localPosition = new Vector3(-Width / 2f + 0.65f + w * f / 2f, -0.15f, -0.2f);
        }
    }

    public void Destroy()
    {
        Live.Remove(this);
        if (_root) Object.Destroy(_root);
        _root = null;
        Relayout();
    }

    // 出ている札を作った順に下から積む
    private static void Relayout()
    {
        int n = 0;
        foreach (var r in Live)
        {
            if (!r._root || !r._on) continue;
            if (r._slot != n)
            {
                r._slot = n;
                r._aspect.DistanceFromEdge = new Vector3(0f, Bottom + n * Step, -20f);
                r._aspect.AdjustPosition();
            }
            n++;
        }
    }

    private void Ensure()
    {
        if (_root) return;
        var hud = Vanilla.Hud;
        var src = hud && hud.TaskPanel ? hud.TaskPanel.taskText : null;
        if (!hud || !src) return;
        _root = FxRoot.NewHud("MrpReadout", 5);
        _aspect = _root.AddComponent<AspectPosition>();
        _aspect.Alignment = AspectPosition.EdgeAlignments.Bottom;
        _aspect.DistanceFromEdge = new Vector3(0f, Bottom, -20f);
        _slot = -1;

        _pill ??= FxSprites.Rect(384, 64, Pill, "MrpReadoutPill");
        _white ??= FxSprites.Rect(4, 4, (_, _) => 1f, "MrpReadoutWhite");
        _back = Part("Back", _pill, Vector3.zero, new Vector3(Height, Height, 1f));
        _back.color = new Color(0.08f, 0.08f, 0.12f, 0.82f);
        // 縁は役職の色
        var rim = Part("Rim", _pill, new Vector3(0f, 0f, 0.01f), new Vector3(Height * 1.0f + 0.04f, Height + 0.04f, 1f));
        rim.color = new Color(_color.r, _color.g, _color.b, 0.9f);
        rim.transform.localScale = new Vector3((Width + 0.06f) / 6f, Height + 0.06f, 1f);
        _back.transform.localScale = new Vector3(Width / 6f, Height, 1f);

        if (_iconSprite)
        {
            _icon = Part("Icon", _iconSprite, new Vector3(-Width / 2f + 0.3f, 0f, -0.1f), Vector3.one);
            float k = 0.42f / Mathf.Max(_iconSprite.bounds.size.y, 0.01f);
            _icon.transform.localScale = new Vector3(k, k, 1f);
        }
        _barBack = Part("BarBack", _white, new Vector3(-Width / 2f + 0.65f + (Width - 1.0f) / 2f, -0.15f, -0.15f), new Vector3(Width - 1.0f, 0.07f, 1f));
        _barBack.color = new Color(1f, 1f, 1f, 0.18f);
        _barFill = Part("BarFill", _white, Vector3.zero, Vector3.zero);
        _barFill.color = _color;
        _barBack.enabled = _barFill.enabled = false;

        _text = Label(src, _root.transform);
        _shown = null;
    }

    // 角の丸い横長の札 (幅 6・高さ 1)
    private static float Pill(float x, float y)
    {
        float px = Mathf.Abs(x) * 6f - 2.5f, py = Mathf.Abs(y);
        float d = Mathf.Sqrt(Mathf.Max(px, 0f) * Mathf.Max(px, 0f) + py * py);
        return Mathf.Clamp01((0.5f - d) * 32f);
    }

    private SpriteRenderer Part(string name, Sprite sp, Vector3 pos, Vector3 scale)
    {
        var go = new GameObject(name) { layer = 5 };
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        return sr;
    }

    // 本編のタスクの字を写して字だけにする (DodgePad と同じ作り)
    private static TextMeshPro Label(TextMeshPro src, Transform parent)
    {
        var go = Object.Instantiate(src.gameObject, parent);
        go.name = "Text";
        go.layer = 5;
        for (int i = go.transform.childCount - 1; i >= 0; i--) Object.Destroy(go.transform.GetChild(i).gameObject);
        foreach (var comp in go.GetComponents<Component>())
            if (!comp.TryCast<Transform>() && !comp.TryCast<TextMeshPro>() && !comp.TryCast<Renderer>() && !comp.TryCast<MeshFilter>()) Object.Destroy(comp);
        go.transform.localPosition = new Vector3(0.22f, 0.04f, -0.2f);
        go.transform.localScale = Vector3.one;
        var tmp = go.GetComponent<TextMeshPro>();
        tmp.rectTransform.sizeDelta = new Vector2(Width - 0.9f, 0.4f);
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = 0.9f;
        tmp.fontSizeMax = 1.6f;
        tmp.enableWordWrapping = false;
        tmp.margin = default;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.text = "";
        return tmp;
    }
}
