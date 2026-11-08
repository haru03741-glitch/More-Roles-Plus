using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Dev;

// ボット (ダミー) の会議の発言。ホストの端末だけで、ボットの体から本編のチャットを送る。
// - 死体を通報したボットは、話し合いの初めに誰の死体をどこで見つけたかを言う
// - ほかの生きているボットは、話し合いの間に短い文を 1 つ (ときどき 2 つ) 言う
// - 投票した時は、入れた先 (スキップ・人) と合う文を言う
internal static class BotChat
{
    private sealed class Plan
    {
        public float At;
        public string Text;
    }

    private static readonly System.Random Rng = new();
    private static readonly Dictionary<byte, List<Plan>> Plans = new();
    private static IntPtr _meeting;
    private static float _startAt;
    private static byte _reporter = 255, _body = 255;
    private static string _room;

    private const int GenericLines = 8;
    private static readonly List<int> Unused = new(); // この会議でまだ誰も言っていない文

    // 通報する直前に呼ぶ (body = null で緊急会議)
    internal static void OnReport(PlayerControl bot, NetworkedPlayerInfo body, Vector2 at)
    {
        if (!bot) return;
        _reporter = bot.PlayerId;
        _body = body != null ? body.PlayerId : (byte)255;
        _room = body != null ? Rules.SelectSpawn.RoomName(at) : null;
    }

    // ボットが投票した時 (253 = スキップ)
    internal static void OnVote(PlayerControl bot, byte target)
    {
        if (!Alive(bot) || Rng.Next(10) < 3) return; // 毎回は言わない
        string text = target == 253 ? new Text("BotChat.VoteSkip").ToString()
            : Name(target) is { } n ? new Text("BotChat.VoteFor").With("name", n).ToString() : null;
        if (text != null) Add(bot.PlayerId, Time.time + 0.4f + (float)Rng.NextDouble(), text);
    }

    // 会議の間、毎フレーム (ホストだけ)
    internal static void Tick(MeetingHud hud)
    {
        if (!hud) return;
        float now = Time.time;
        if (hud.Pointer != _meeting)
        {
            _meeting = hud.Pointer;
            _startAt = now;
            Plans.Clear();
            Unused.Clear();
            for (int i = 0; i < GenericLines; i++) Unused.Add(i);
            PlanMeeting(now);
        }
        var st = hud.state;
        if (st is MeetingHud.MeetingStates.Results or MeetingHud.MeetingStates.Proceeding) return;
        foreach (var kv in Plans)
        {
            var list = kv.Value;
            for (int i = 0; i < list.Count; i++)
            {
                if (now < list[i].At) continue;
                var pc = Roles.PortKit.ById(kv.Key);
                if (Alive(pc)) Send(pc, list[i].Text);
                list.RemoveAt(i);
                i--;
            }
        }
    }

    private static void PlanMeeting(float now)
    {
        foreach (var pc in Dummy.All)
        {
            if (!Alive(pc) || pc == DummyPilot.Possessed || pc == DummyPilot.Voter) continue;
            if (pc.PlayerId == _reporter)
            {
                string first = _body != 255 && Name(_body) is { } dead
                    ? new Text("BotChat.FoundBody").With("name", dead).With("room", _room ?? "?").ToString()
                    : new Text("BotChat.PressedButton").ToString();
                Add(pc.PlayerId, now + 1.5f + (float)Rng.NextDouble(), first);
                continue;
            }
            Add(pc.PlayerId, now + 3f + (float)Rng.NextDouble() * 9f, Generic(pc));
            if (Rng.Next(10) < 3) Add(pc.PlayerId, now + 12f + (float)Rng.NextDouble() * 8f, Generic(pc));
        }
        _reporter = _body = 255;
        _room = null;
    }

    private static string Generic(PlayerControl self)
    {
        // 3 割は誰かを名指しする (自分と死んだ人は除く)
        if (Rng.Next(10) < 3)
        {
            var cands = new List<PlayerControl>();
            foreach (var p in PlayerControl.AllPlayerControls)
                if (Alive(p) && p != self) cands.Add(p);
            if (cands.Count > 0)
                return new Text("BotChat.Suspect").With("name", cands[Rng.Next(cands.Count)].Data.PlayerName).ToString();
        }
        if (Unused.Count == 0) for (int i = 0; i < GenericLines; i++) Unused.Add(i);
        int k = Rng.Next(Unused.Count);
        int line = Unused[k];
        Unused.RemoveAt(k);
        return new Text("BotChat.Line" + line).ToString();
    }

    private static void Add(byte id, float at, string text)
    {
        if (!Plans.TryGetValue(id, out var list)) Plans[id] = list = new List<Plan>();
        list.Add(new Plan { At = at, Text = text });
    }

    private static void Send(PlayerControl pc, string text)
    {
        try { pc.RpcSendChat(text); }
        catch (Exception e) { Plugin.Logger.LogWarning($"bot chat: {e.Message}"); }
    }

    private static string Name(byte id)
    {
        var info = GameData.Instance ? GameData.Instance.GetPlayerById(id) : null;
        return info != null && !string.IsNullOrEmpty(info.PlayerName) ? info.PlayerName : null;
    }

    private static bool Alive(PlayerControl pc) => pc && pc.Data != null && !pc.Data.IsDead && !pc.Data.Disconnected;

    internal static void Reset()
    {
        Plans.Clear();
        _meeting = IntPtr.Zero;
        _reporter = _body = 255;
        _room = null;
    }
}
