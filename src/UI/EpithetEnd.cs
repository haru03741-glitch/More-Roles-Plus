using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.UI;

// 特別枠 (二つ名) の人が勝った試合の終わりの画面: 黒い背景を夜空とオーロラにし、勝利の字の後ろに印を薄く重ね、
// 字の下に「二つ名【名】 役職」を添える。単独で勝った修羅は赤黒い空と欠けた印。
// 終わりの画面では全員の役職が結果の一覧に出るので、全員の画面に出してよい
internal static class EpithetEnd
{
    private const float MarkScreen = 0.5f, MarkAlpha = 0.42f;

    // 試合が終わった瞬間に写す (終わりの画面では役職の情報が消えている)
    private static string _roleId, _epithet;
    private static bool _blood;

    internal static void Capture(GameResult r)
    {
        _roleId = _epithet = null;
        if (_testRole != null)
        {
            _roleId = _testRole;
            _epithet = Epithet.Wayfinder.ToString();
            _blood = _testBlood;
            return;
        }
        if (r == null || r.Draw) return;
        foreach (var role in RoleState.All)
        {
            if (role == null || role.Epithet == Epithet.None || !r.Winners.Contains(role.PlayerId)) continue;
            _roleId = role.Id;
            _epithet = role.Epithet.ToString();
            // 役職が単独で勝った = 修羅の道 (守の道は勝った陣営と一緒に勝つ)
            _blood = r.Role != null && r.Role.Id == role.Id;
            return;
        }
    }

    internal static string Describe() => _roleId == null ? "none" : $"{_roleId} {(_blood ? "blood" : "dawn")}";

    // 確かめ用: 次の終わりの画面を、この役職が勝ったものとして出す (null = 戻す)
    private static string _testRole;
    private static bool _testBlood;

    internal static void TestSet(string roleId, bool blood)
    {
        _testRole = roleId;
        _testBlood = blood;
    }

    internal static void Show(EndGameManager egm)
    {
        if (_roleId == null || !egm || !egm.WinText) return;
        var cam = Camera.main;
        if (!cam) return;
        var tf = egm.transform;
        float scale = tf.lossyScale.y > 0.0001f ? tf.lossyScale.y : 1f;
        float h = cam.orthographicSize * 2f / scale + 0.4f, w = h * cam.aspect + 0.4f;
        var c = tf.InverseTransformPoint(cam.transform.position);
        var wt = egm.WinText.transform;
        var wp = tf.InverseTransformPoint(wt.position);
        int layer = egm.WinText.gameObject.layer;
        // 空は勝利の字・人・本編の光の帯より奥
        float z = wp.z + 6f;
        if (egm.BackgroundBar) z = FxMath.Max(z, tf.InverseTransformPoint(egm.BackgroundBar.transform.position).z + 1f);
        // 本編の黒い板は空より手前に描かれるので外す (空の夜の色が代わりの背景になる)
        var black = tf.Find("BackgroundLayer");
        if (black) black.gameObject.SetActive(false);
        if (_blood && egm.BackgroundBar) egm.BackgroundBar.material.color = FxMath.Rgba(0.78f, 0.06f, 0.1f, 1f);
        var sky = WayfinderSky.Make(tf, c.x, c.y, z, w, h, _blood ? WayfinderSky.Tone.Blood : WayfinderSky.Tone.Dawn, layer, h * MarkScreen, MarkAlpha);
        float my = wp.y - c.y - 0.3f;
        var markTf = sky.Root.transform.Find("Mark");
        var glowTf = sky.Root.transform.Find("MarkGlow");
        if (markTf) markTf.localPosition = FxMath.V3(0f, my, -0.01f);
        if (glowTf) glowTf.localPosition = FxMath.V3(0f, my, 0f);

        // 勝利の字の上に見出しとして 1 行 (字の下は第三陣営の勝ちの副題と人で詰まっている)
        var go = Object.Instantiate(egm.WinText.gameObject, wt.parent);
        go.name = "MrpEpithet";
        go.transform.localPosition = wt.localPosition + FxMath.V3(0f, 0.82f, 0f);
        go.transform.localScale = wt.localScale * 0.32f;
        var tmp = go.GetComponent<TextMeshPro>();
        string roleName = Tr.Get(_roleId + ".Name");
        tmp.text = Tr.Get("Epithet." + _epithet) + "【" + Tr.Get(_roleId + ".Epithet") + "】 " + roleName;
        tmp.color = Color.white;
        tmp.enableVertexGradient = true;
        Color a = _blood ? FxMath.Rgba(1f, 0.45f, 0.4f, 1f) : FxMath.Rgba(0.5f, 0.78f, 1f, 1f);
        Color b = _blood ? FxMath.Rgba(1f, 0.22f, 0.2f, 1f) : FxMath.Rgba(1f, 0.6f, 0.82f, 1f);
        tmp.colorGradient = new VertexGradient(a, b, a, b);
        Plugin.Logger.LogInfo($"[epithet] end {Describe()} z={z:F2}");
        _roleId = null; // 次の試合の終わりに前の試合の物を出さない
    }
}
