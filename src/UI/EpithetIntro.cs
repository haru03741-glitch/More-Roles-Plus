using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.UI;

// 特別枠 (二つ名) を引いた本人の役職の発表: 本編の発表の後ろを夜空とオーロラに沈め、月の船の印を出し、
// 役職名の上に「航路守」→「【二つ名】」を順に出す。役職名が出る所で白い斬り線が走り鞘走りが鳴る。
// 本人の画面だけ。作るのは発表ごとに 1 回で、発表の画面の子なので発表が消えると一緒に消える
internal static class EpithetIntro
{
    private const float TitleAt = 0.6f, NameAt = 0.8f, SlashAt = 1.0f, TextIn = 0.25f, SlashAngle = 10f;
    private const float MarkScreen = 0.45f, MarkAlpha = 0.55f;

    private static int _introId;
    private static WayfinderSky _sky;
    private static TextMeshPro _title, _name;
    private static Transform _slashParent;
    private static float _t0, _slashX, _slashY, _slashZ, _slashW, _slashH, _screenX, _screenY;
    private static int _layer;
    private static bool _sounded, _live;

    // 発表の文を書いた直後に毎回呼ばれる (本編のコルーチンが進むたび)。最初の 1 回で作り、以後は何もしない
    public static void Begin(IntroCutscene intro, RoleBase role)
    {
        if (!intro || role == null || role.Epithet == Epithet.None) return;
        int id = intro.GetInstanceID();
        if (id == _introId) return; // 同じ発表では作り直さない (字を書き終えて止めた後も)
        _introId = id;
        Build(intro, role);
    }

    private static void Build(IntroCutscene intro, RoleBase role)
    {
        var cam = Camera.main;
        if (!cam || !intro.RoleText) return;
        var tf = intro.transform;
        float scale = tf.lossyScale.y > 0.0001f ? tf.lossyScale.y : 1f;
        float h = cam.orthographicSize * 2f / scale + 0.4f, w = h * cam.aspect + 0.4f;
        // 画面の中央 (発表の画面の中での位置)
        var c = tf.InverseTransformPoint(cam.transform.position);
        // 役職名の字が実際に描かれている所の中心 (箱の位置は字の位置と違う)
        var rt = intro.RoleText.transform;
        intro.RoleText.ForceMeshUpdate();
        var rp = tf.InverseTransformPoint(rt.TransformPoint(intro.RoleText.textBounds.center));
        var rz = tf.InverseTransformPoint(rt.position);
        int layer = intro.RoleText.gameObject.layer;

        // 夜空は発表の黒い幕 (FrontMost・z -15) の手前で、人 (z -18) より奥。幕より奥だと隠れ、人より手前だと人が暗く沈む
        var black = tf.Find("FrontMost");
        float z = black ? black.localPosition.z - 1.5f : rz.z + 2f;
        _sky = WayfinderSky.Make(tf, c.x, c.y, z, w, h, WayfinderSky.Tone.Dawn, layer, h * MarkScreen, MarkAlpha);
        var markTf = _sky.Root.transform.Find("Mark");
        var glowTf = _sky.Root.transform.Find("MarkGlow");
        // 印は役職名の後ろに重ねる
        float my = rp.y - c.y;
        if (markTf) markTf.localPosition = FxMath.V3(0f, my, -0.01f);
        if (glowTf) glowTf.localPosition = FxMath.V3(0f, my, 0f);

        var src = intro.RoleBlurbText ? intro.RoleBlurbText : intro.RoleText;
        _title = Clone(src, tf, "EpithetTitle", layer);
        _title.text = Tr.Get("Epithet." + role.Epithet);
        _title.fontSize = src.fontSize * 0.85f;
        _title.characterSpacing = 40f;
        _title.color = FxMath.Rgba(0.86f, 0.9f, 1f, 1f);
        // 「あなたのロールは」は印と「航路守」に重なるので消す
        if (intro.YouAreText) intro.YouAreText.gameObject.SetActive(false);
        _name = Clone(src, tf, "EpithetName", layer);
        _name.text = "【" + Tr.Get(role.Id + ".Epithet") + "】";
        _name.fontSize = src.fontSize * 1.25f;
        _name.fontStyle = FontStyles.Bold;
        _name.color = Color.white; // 元の字の色 (役職の色) がグラデーションに掛かるので白に
        _name.enableVertexGradient = true;
        Color blue = FxMath.Rgba(0.5f, 0.78f, 1f, 1f), pink = FxMath.Rgba(1f, 0.6f, 0.82f, 1f);
        _name.colorGradient = new VertexGradient(blue, pink, blue, pink);

        // 役職名の上に 2 行 (上 = 航路守・下 = 二つ名)。「あなたは」の文はさらに上へ逃がす
        // 役職名は画面の上寄りにあるので、上に「航路守」・下に二つ名を置く (字の箱の中心から字の中心へのずれを足す)
        float half = intro.RoleText.textBounds.extents.y * rt.lossyScale.y / scale;
        PlaceAt(_title, tf, rp.x, rp.y + half + 0.32f, rz.z);
        PlaceAt(_name, tf, rp.x, rp.y - half - 0.22f, rz.z);

        // 斬撃 (居合の段 4 と同じ物) は役職名を横切る。置き場は出す時まで控える
        _slashParent = tf;
        _slashX = rp.x;
        _slashY = rp.y;
        _slashZ = rz.z - 0.3f;
        _slashW = w;
        _slashH = h;
        _screenX = c.x;
        _screenY = c.y;
        _layer = layer;

        _t0 = Time.time;
        _sounded = false;
        _live = true;
        Tick();
        Plugin.Logger.LogInfo($"[epithet] intro role={role.Id} h={h:F2} w={w:F2} roleY={rp.y:F2} z={z:F2} layer={layer}");
    }

    // 字の描かれる中心が (x, y) に来るように置く
    private static void PlaceAt(TextMeshPro tmp, Transform space, float x, float y, float z)
    {
        var t = tmp.transform;
        t.localPosition = FxMath.V3(x, y, z);
        tmp.ForceMeshUpdate();
        var c = space.InverseTransformPoint(t.TransformPoint(tmp.textBounds.center));
        t.localPosition = FxMath.V3(x - (c.x - x), y - (c.y - y), z);
    }

    private static TextMeshPro Clone(TextMeshPro src, Transform parent, string name, int layer)
    {
        var go = Object.Instantiate(src.gameObject, parent);
        go.name = name;
        go.layer = layer;
        for (int i = go.transform.childCount - 1; i >= 0; i--) Object.Destroy(go.transform.GetChild(i).gameObject);
        var tmp = go.GetComponent<TextMeshPro>();
        tmp.enableWordWrapping = false;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.alpha = 0f;
        return tmp;
    }

    internal static void Tick()
    {
        if (!_live) return;
        if (!_sky.Root)
        {
            _live = false;
            return;
        }
        float t = Time.time - _t0;
        float tk = FxMath.Clamp01((t - TitleAt) / TextIn), nk = FxMath.Clamp01((t - NameAt) / TextIn);
        _title.alpha = tk;
        _title.characterSpacing = 40f - 25f * (1f - (1f - tk) * (1f - tk));
        _name.alpha = nk;
        float ns = 1.15f - 0.15f * (1f - (1f - nk) * (1f - nk));
        _name.transform.localScale = FxMath.V3(ns, ns, 1f);

        if (t >= SlashAt && !_sounded)
        {
            _sounded = true;
            if (_slashParent) IntroIai.Play(_slashParent, _slashX, _slashY, _slashZ, SlashAngle, _layer, _slashW, _slashH, _screenX, _screenY);
            var lp = PlayerControl.LocalPlayer;
            if (lp)
            {
                SamuraiSound.Swing(lp, 1f);
                SamuraiSound.Rend(lp, 4);
            }
        }
        // 字が出きって斬撃も放ったら、これ以上は書かない (空は WayfinderSky・斬撃は IntroIai が動かす)
        if (_sounded && tk >= 1f && nk >= 1f) _live = false;
    }
}
