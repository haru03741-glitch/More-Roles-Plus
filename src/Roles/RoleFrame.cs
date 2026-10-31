using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 役職の OnFixedUpdate と NameMark を呼ぶ所。上書きした型だけを一覧に入れるので、書いていない役職の分は何も回らない
internal static class RoleFrame
{
    internal const string MarkName = "MrpNameMark";
    private const float MarkSeconds = 0.25f;
    private static int _markEvery; // MarkSeconds に当たる FixedUpdate の回数 (間隔は本編の設定で決まるので初めて要る時に測る)

    private struct Caps
    {
        public bool Frame, FrameLocal, FrameHost, Mark, Public;
    }

    private static readonly Dictionary<Type, Caps> ByType = new();
    private static readonly List<Assignable> Frame = new();
    private static readonly List<bool> FrameHost = new();
    private static readonly List<Assignable> Marks = new();
    private static readonly List<RoleBase> Publics = new(); // 全員の画面で名前に印が付く役職 (RoleBase.PublicMark)

    public static void OnAssigned(Assignable role)
    {
        var c = CapsOf(role.GetType());
        if (c.Frame && (!c.FrameLocal || role.IsLocal))
        {
            Frame.Add(role);
            FrameHost.Add(c.FrameHost);
        }
        if (c.Mark && role.IsLocal)
        {
            Marks.Add(role);
            if (_markEvery == 0) _markEvery = Math.Max(1, (int)(MarkSeconds / Time.fixedDeltaTime + 0.5f));
        }
        if (c.Public && role is RoleBase rb)
        {
            Publics.Add(rb);
            if (_markEvery == 0) _markEvery = Math.Max(1, (int)(MarkSeconds / Time.fixedDeltaTime + 0.5f));
        }
    }

    private static Caps CapsOf(Type t)
    {
        if (ByType.TryGetValue(t, out var c)) return c;
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Public;
        var frame = t.GetMethod(nameof(Assignable.OnFixedUpdate), F, Type.EmptyTypes);
        var mark = t.GetMethod(nameof(Assignable.NameMark), F, new[] { typeof(PlayerControl) });
        c.Frame = frame != null && frame.DeclaringType != typeof(Assignable);
        if (c.Frame)
        {
            c.FrameLocal = frame.GetCustomAttribute<LocalOnlyAttribute>() != null;
            c.FrameHost = frame.GetCustomAttribute<HostOnlyAttribute>() != null;
        }
        c.Mark = mark != null && mark.DeclaringType != typeof(Assignable);
        var pub = t.GetProperty(nameof(RoleBase.PublicMark), F);
        var seenBy = t.GetMethod(nameof(RoleBase.MarkSeenBy), F, new[] { typeof(PlayerControl) });
        c.Public = (pub != null && pub.DeclaringType != typeof(RoleBase)) || (seenBy != null && seenBy.DeclaringType != typeof(RoleBase));
        return ByType[t] = c;
    }

    private static int _tick;

    public static void Tick()
    {
        if (Frame.Count > 0) RunFrame();
        PortKit.TickLater();
        if ((Marks.Count > 0 || Publics.Count > 0) && ++_tick >= _markEvery)
        {
            _tick = 0;
            try { RefreshMarks(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"name mark: {e.Message}"); }
        }
    }

    private static void RunFrame()
    {
        bool? host = null;
        for (int i = 0; i < Frame.Count; i++)
        {
            var r = Frame[i];
            if (FrameHost[i])
            {
                host ??= AmongUsClient.Instance && AmongUsClient.Instance.AmHost;
                if (!host.Value) continue;
            }
            try { r.OnFixedUpdate(); }
            catch (Exception e)
            {
                // 毎フレーム同じ例外を書くとログだけで重くなるので、その役職の分はこの試合の間止める
                Plugin.Logger.LogError($"{r.Id}.OnFixedUpdate (stopped for this match): {e}");
                Frame.RemoveAt(i);
                FrameHost.RemoveAt(i);
                i--;
            }
        }
    }

    // ---- 名前の後ろの印 ----

    private sealed class Slot
    {
        public GameObject Go;
        public TextMeshPro Tmp;
        public string Mark, Name;
    }

    private static readonly Slot[] InGame = new Slot[256];
    private static readonly Slot[] InMeeting = new Slot[256];
    private static readonly PlayerControl[] ById = new PlayerControl[256];
    private static readonly StringBuilder Sb = new();

    private static void RefreshMarks()
    {
        Array.Clear(ById, 0, ById.Length);
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || !p.cosmetics) continue;
            ById[p.PlayerId] = p;
            Apply(InGame, p.PlayerId, p.cosmetics.nameText, MarkFor(p));
        }
        // 名札は会議ごとに作り直される (前の会議の分は名札ごと消えているので Apply が作り直す)
        var mh = MeetingHud.Instance;
        if (!mh) return;
        foreach (var pva in mh.playerStates)
        {
            if (!pva) continue;
            byte pid = pva.PlayerId.Value;
            var seen = ById[pid];
            if (!seen) continue;
            Apply(InMeeting, pid, pva.NameText, MarkFor(seen));
        }
    }

    private static string MarkFor(PlayerControl seen)
    {
        Sb.Clear();
        for (int i = 0; i < Marks.Count; i++)
        {
            string m;
            try { m = Marks[i].NameMark(seen); }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"{Marks[i].Id}.NameMark (stopped for this match): {e}");
                Marks.RemoveAt(i);
                i--;
                continue;
            }
            if (!string.IsNullOrEmpty(m)) Sb.Append(m);
        }
        for (int i = 0; i < Publics.Count; i++)
        {
            if (Publics[i].PlayerId != seen.PlayerId) continue;
            string m = Publics[i].MarkSeenBy(PlayerControl.LocalPlayer);
            if (!string.IsNullOrEmpty(m)) Sb.Append(m);
        }
        return Sb.Length == 0 ? null : Sb.ToString();
    }

    // 名前の文字を複製して、見えない「印 + 名前」の後ろに見える印を置く。真ん中揃えのまま、
    // 名前の位置が本物の名前と重なり、印がそのすぐ後ろに来る
    private static void Apply(Slot[] slots, byte pid, TextMeshPro name, string mark)
    {
        var s = slots[pid];
        if (mark == null)
        {
            if (s != null && s.Mark != null)
            {
                s.Mark = null;
                if (s.Go) s.Go.SetActive(false);
            }
            return;
        }
        if (!name) return;
        string nameText = name.text;
        if (s != null && s.Go && s.Mark == mark && s.Name == nameText) return;
        if (s == null || !s.Go)
        {
            s ??= slots[pid] = new Slot();
            var go = UnityEngine.Object.Instantiate(name.gameObject, name.transform);
            go.name = MarkName;
            for (int i = go.transform.childCount - 1; i >= 0; i--)
            {
                var child = go.transform.GetChild(i).gameObject;
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = Vector3.one;
            s.Go = go;
            s.Tmp = go.GetComponent<TextMeshPro>();
            s.Tmp.enableWordWrapping = false;
        }
        s.Mark = mark;
        s.Name = nameText;
        s.Tmp.text = "<alpha=#00>" + Plain(mark) + Plain(nameText) + "<alpha=#FF>" + mark;
        if (!s.Go.activeSelf) s.Go.SetActive(true);
    }

    // 書式の札 (<color=...> など) を除いた文字 (見えない分の幅を見える分と揃えるため。札があると色が戻って見えてしまう)
    private static string Plain(string s)
    {
        if (s.IndexOf('<') < 0) return s;
        var sb = new StringBuilder(s.Length);
        bool tag = false;
        foreach (char ch in s)
        {
            if (ch == '<') tag = true;
            else if (ch == '>') tag = false;
            else if (!tag) sb.Append(ch);
        }
        return sb.ToString();
    }

    // 試合の途中で役職が外れた時 (RoleState.Replace)。外れた役職の毎フレームの処理と名前の印を止める
    public static void Remove(Assignable role)
    {
        int i = Frame.IndexOf(role);
        if (i >= 0)
        {
            Frame.RemoveAt(i);
            FrameHost.RemoveAt(i);
        }
        if (Marks.Remove(role) | (role is RoleBase rb && Publics.Remove(rb)))
        {
            ClearSlots(InGame);
            ClearSlots(InMeeting);
        }
    }

    public static void Clear()
    {
        Frame.Clear();
        FrameHost.Clear();
        Marks.Clear();
        Publics.Clear();
        _tick = 0;
        ClearSlots(InGame);
        ClearSlots(InMeeting);
        Array.Clear(ById, 0, ById.Length);
    }

    private static void ClearSlots(Slot[] slots)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            var s = slots[i];
            if (s == null) continue;
            if (s.Go) UnityEngine.Object.Destroy(s.Go);
            slots[i] = null;
        }
    }
}
