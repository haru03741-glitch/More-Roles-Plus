using System;
using System.Collections.Generic;
using System.Text;
using MoreRolesPlus.Roles;
using UnityEngine;

namespace MoreRolesPlus.Bridge;

// 逆再生に入らない絵を実機で探す (確認用・呼んだ時だけ走る)。
//   rewindleak mark  = 今ある絵を全部覚える (試合の始め・能力を使う前に)
//   rewindleak       = 覚えた後に出てきて今出ている絵のうち、FxRoot の根・控え (TrackWorld)・画面の登録 (TrackHud)・人の体の外にある物を、いちばん上の親の名前ごとに数えて出す
// 出てきた名前は逆再生で前へ進む・残る絵の候補 (地形の絵は状態から作り直すので出てもよい)
internal static class RewindLeak
{
    private static readonly HashSet<IntPtr> Marked = new();
    private static bool _marked;

    internal static void Register()
    {
        TestBridge.Register("rewindleak", "[mark] 逆再生に入らない絵を探す: mark で今ある絵を覚え、能力を使った後に引数なしで、覚えた後に出てきて出ている絵のうち FxRoot・画面の登録・人の体の外にある物を親の名前ごとに出す", (args, reply) =>
        {
            var all = UnityEngine.Object.FindObjectsOfType<Renderer>();
            if (args.Trim() == "mark")
            {
                Marked.Clear();
                foreach (var r in all) if (r) Marked.Add(r.Pointer);
                _marked = true;
                reply($"OK rewindleak marked={Marked.Count}");
                return;
            }
            if (!_marked) { reply("ERR rewindleak 先に rewindleak mark"); return; }
            var skip = new List<Transform>();
            if (FxRoot.Current) skip.Add(FxRoot.Current);
            if (FxRoot.CurrentWorld) skip.Add(FxRoot.CurrentWorld);
            foreach (var h in FxRoot.HudRoots) if (h) skip.Add(h);
            int world = 0;
            foreach (var w in FxRoot.WorldObjs) if (w) { skip.Add(w); if (w.gameObject.activeInHierarchy) world++; }
            foreach (var pc in PlayerControl.AllPlayerControls) if (pc) skip.Add(pc.transform);
            var count = new Dictionary<string, int>();
            int n = 0;
            foreach (var r in all)
            {
                if (!r || !r.enabled || !r.gameObject.activeInHierarchy || Marked.Contains(r.Pointer)) continue;
                var tf = r.transform;
                bool under = false;
                foreach (var s in skip) if (tf.IsChildOf(s)) { under = true; break; }
                if (under) continue;
                string key = Top(tf);
                count[key] = count.TryGetValue(key, out int c) ? c + 1 : 1;
                n++;
            }
            var sb = new StringBuilder($"OK rewindleak new={n} trackedWorldShown={world} playing={RewindPlayback.Active}");
            var list = new List<KeyValuePair<string, int>>(count);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < list.Count && i < 24; i++) sb.Append($" | {list[i].Key}×{list[i].Value}");
            reply(sb.ToString());
        });
    }

    // 船の子なら船の下のいちばん上の物の名前・それ以外は場面の根の名前 (船の下は「船/名前」)
    private static string Top(Transform tf)
    {
        var ship = ShipStatus.Instance ? ShipStatus.Instance.transform : null;
        var t = tf;
        while (t.parent && t.parent != ship) t = t.parent;
        return t.parent == ship && ship ? "船/" + t.name : t.name;
    }
}
