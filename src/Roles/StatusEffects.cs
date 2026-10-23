using System.Collections.Generic;
using System.Text;

namespace MoreRolesPlus.Roles;

// 自分に付いている効果と残り時間。タスク欄の先頭の段 (役職の説明の下) に 1 行ずつ「名前: 残り」を効果の色で並べる
// (色を付けないと本編の重要タスクの赤になる)。
// 本編はタスク欄の文字を毎フレーム段の文字から組み立てるので、段の文字を書き換えるのは秒の数が変わった時と増減の時だけ。
//   StatusEffects.ShowUntil("Shield", new Text("StatusEffects.Shield"), "#66CCFF", new Text("StatusEffects.UntilMeeting"));
//   StatusEffects.ShowFor("Haste", new Text("Haste.Effect"), "#FFCC00", 10f);
// 船が替わると全部消える。自分の端末の表示だけで、他の人には届かない。
// 出す・消す・切れるは時戻しの取り消しに積むので、戻すと戻し先の時刻に出ていた効果が同じ残りで出直す
// (逆再生の間は切らさず、戻し終わりに全部の切れる時刻を戻した時間だけ後ろへずらす)
public static class StatusEffects
{
    private sealed class Entry
    {
        public string Key;
        public Text Label;
        public string Color;
        public long End;    // 切れる時刻 (Environment.TickCount64・Until の時は使わない)
        public Text Until;  // 条件で切れる効果の残りの書き方 (HasUntil の時だけ)
        public bool HasUntil;
        public int Shown;   // 最後に書いた残りの秒
    }

    private static readonly List<Entry> _list = new();
    private static int _shipGen = -1;
    private static long _next;

    public static bool Any => _list.Count != 0;

    // 秒で切れる効果を出す (同じ key は置き換え)
    public static void ShowFor(string key, Text label, string color, float seconds) =>
        Put(new Entry { Key = key, Label = label, Color = color, End = System.Environment.TickCount64 + (long)(seconds * 1000f) });

    // 条件で切れる効果を出す (Hide で消す)。until = 残りの代わりに出す文 (例「最初の会議まで」)
    public static void ShowUntil(string key, Text label, string color, Text until) =>
        Put(new Entry { Key = key, Label = label, Color = color, Until = until, HasUntil = true });

    public static void Hide(string key)
    {
        int i = _list.FindIndex(e => e.Key == key);
        if (i < 0) return;
        var old = _list[i];
        _list.RemoveAt(i);
        Rewind.Undo(_ => Restore(old));
        Redraw();
    }

    private static void Put(Entry e)
    {
        SyncShip();
        int i = _list.FindIndex(x => x.Key == e.Key);
        var old = i >= 0 ? _list[i] : null;
        if (i >= 0) _list.RemoveAt(i);
        e.Shown = Left(e);
        _list.Add(e);
        Rewind.Undo(_ =>
        {
            // 後の取り消しが出し直した写しも外すので、物でなく名前で外す
            _list.RemoveAll(x => x.Key == e.Key);
            if (old != null) Restore(old);
            else Redraw();
        });
        Redraw();
    }

    // 時戻し: 消えた効果を出し直す (切れる時刻は戻し終わりの Rewound でまとめてずらす)
    private static void Restore(Entry e)
    {
        _list.RemoveAll(x => x.Key == e.Key);
        var r = new Entry { Key = e.Key, Label = e.Label, Color = e.Color, End = e.End, Until = e.Until, HasUntil = e.HasUntil };
        r.Shown = Left(r);
        _list.Add(r);
        Redraw();
    }

    private static int Left(Entry e) =>
        e.HasUntil ? -1 : (int)System.Math.Ceiling(System.Math.Max(e.End - System.Environment.TickCount64, 0L) / 1000.0);

    // 毎フレーム。効果が無ければ数の比較だけ、あれば 0.25 秒ごとに残りの秒を見る (ゲームの時計は読まない)。
    // ここで投げると同じ Update の後ろの処理まで止まるので囲む
    internal static void Tick()
    {
        if (_list.Count == 0) return;
        long now = System.Environment.TickCount64;
        if (now < _next) return;
        _next = now + 250;
        try { Step(); }
        catch (System.Exception ex) { Plugin.Logger.LogError($"status effects: {ex}"); }
    }

    // 時戻しの戻し終わり: 戻した時間 (戻し終わり − 戻し先の時刻) だけ切れる時刻を後ろへ。残りが戻し先の時と同じになる
    internal static void Rewound(long shiftMs)
    {
        if (_list.Count == 0) return;
        foreach (var e in _list)
            if (!e.HasUntil) { e.End += shiftMs; e.Shown = Left(e); }
        Redraw();
    }

    private static void Step()
    {
        if (SyncShip()) return;
        if (TimeRewind.Playing) return; // 逆再生の間は切らさない (切れる時刻は戻し終わりにずらす)
        bool dirty = false;
        for (int i = _list.Count - 1; i >= 0; i--)
        {
            var e = _list[i];
            if (e.HasUntil) continue;
            int left = Left(e);
            if (left <= 0)
            {
                _list.RemoveAt(i);
                Rewind.Undo(_ => Restore(e));
                dirty = true;
            }
            else if (left != e.Shown) { e.Shown = left; dirty = true; }
        }
        if (dirty) Redraw();
    }

    // 船が替わったら (試合の終わり・フリープレイを抜けた) 全部消す。消したら true
    private static bool SyncShip()
    {
        _ = Terrain.GameClock.Ship; // 船ができた直後 (盾は船の Start で付く) にも世代番号を今の船に揃えてから読む
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
