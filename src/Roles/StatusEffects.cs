using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 自分に付いている効果と残り時間。タスク欄の先頭の段 (役職の説明の下) に 1 行ずつ「名前: 残り」を効果の色で並べる
// (色を付けないと本編の重要タスクの赤になる)。
// 本編はタスク欄の文字を毎フレーム段の文字から組み立てるので、段の文字を書き換えるのは秒の数が変わった時と増減の時だけ。
//   StatusEffects.ShowUntil("Shield", new Text("StatusEffects.Shield"), "#66CCFF", new Text("StatusEffects.UntilMeeting"));
//   StatusEffects.ShowFor("Haste", new Text("Haste.Effect"), "#FFCC00", 10f);
// 船が替わると全部消える。自分の端末の表示だけで、他の人には届かない
public static class StatusEffects
{
    private sealed class Entry
    {
        public string Key;
        public Text Label;
        public string Color;
        public float End;   // Time.time で切れる時刻 (Until の時は使わない)
        public Text Until;  // 条件で切れる効果の残りの書き方 (HasUntil の時だけ)
        public bool HasUntil;
        public int Shown;   // 最後に書いた残りの秒
    }

    private static readonly List<Entry> _list = new();
    private static int _shipGen = -1;
    private static float _next;

    public static bool Any => _list.Count != 0;

    // 秒で切れる効果を出す (同じ key は置き換え)
    public static void ShowFor(string key, Text label, string color, float seconds) =>
        Put(new Entry { Key = key, Label = label, Color = color, End = Time.time + seconds });

    // 条件で切れる効果を出す (Hide で消す)。until = 残りの代わりに出す文 (例「最初の会議まで」)
    public static void ShowUntil(string key, Text label, string color, Text until) =>
        Put(new Entry { Key = key, Label = label, Color = color, Until = until, HasUntil = true });

    public static void Hide(string key)
    {
        if (_list.RemoveAll(e => e.Key == key) > 0) Redraw();
    }

    private static void Put(Entry e)
    {
        SyncShip();
        _list.RemoveAll(x => x.Key == e.Key);
        e.Shown = Left(e);
        _list.Add(e);
        Redraw();
    }

    private static int Left(Entry e) => e.HasUntil ? -1 : (int)System.Math.Ceiling(System.Math.Max(e.End - Time.time, 0f));

    // 毎フレーム。効果が無ければ数の比較だけ、あれば 0.25 秒ごとに残りの秒を見る
    internal static void Tick()
    {
        if (_list.Count == 0) return;
        float now = Time.time;
        if (now < _next) return;
        _next = now + 0.25f;
        if (SyncShip()) return;
        bool dirty = false;
        for (int i = _list.Count - 1; i >= 0; i--)
        {
            var e = _list[i];
            if (e.HasUntil) continue;
            int left = Left(e);
            if (left <= 0) { _list.RemoveAt(i); dirty = true; }
            else if (left != e.Shown) { e.Shown = left; dirty = true; }
        }
        if (dirty) Redraw();
    }

    // 船が替わったら (試合の終わり・フリープレイを抜けた) 全部消す。消したら true
    private static bool SyncShip()
    {
        int gen = Terrain.GameClock.ShipGen;
        if (gen == _shipGen) return false;
        _shipGen = gen;
        if (_list.Count == 0) return false;
        _list.Clear();
        Redraw();
        return true;
    }

    private static void Redraw()
    {
        var lp = PlayerControl.LocalPlayer;
        if (lp) RoleDisplay.EnsureTaskHeader(lp);
    }

    // タスク欄の段に足す行 (先頭に改行付き)
    internal static string Lines()
    {
        if (_list.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var e in _list)
        {
            string left = e.HasUntil ? e.Until.ToString() : new Text("StatusEffects.Seconds").With("sec", e.Shown).ToString();
            sb.Append('\n').Append("<color=").Append(e.Color).Append('>').Append(e.Label.ToString()).Append(": ").Append(left).Append("</color>");
        }
        return sb.ToString();
    }

    // 確かめ用
    internal static string Describe()
    {
        var sb = new StringBuilder($"n={_list.Count}");
        foreach (var e in _list) sb.Append($" [{e.Key} {(e.HasUntil ? "until" : e.Shown + "s")}]");
        return sb.ToString();
    }
}
