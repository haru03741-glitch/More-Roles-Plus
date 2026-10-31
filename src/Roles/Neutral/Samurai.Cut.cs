using System;
using System.Collections.Generic;
using Hazel;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 刀が相手の体に触れた瞬間の当たり。
// 振り終わりにまとめて確かめると、刀が相手を通ってから振り切るまでの分だけ当たりが遅れ、振りの途中のどこで通ったかもホストには分からない。
// そこで刀の絵を描いている端末 (人 = 本人・練習の相手 = ホスト) が、刀が相手の体に重なった瞬間に「ここまでの振り」で当たりを頼む。
// 確かめ (届き・壁・回避・口で受ける) はホストがいつも通りにする。振り終わりの一振りは、触れた瞬間に当てた相手をもう斬らない。
// 居合 (納めた所からの一振り) と突きは今まで通り振り終わり / 突いた時に確かめる
public sealed partial class Samurai
{
    // 自分の端末: 今の一振りの番号と、この一振りで触れた瞬間に頼んだ相手
    private byte _strokeSeq;
    private readonly List<byte> _cutAsked = new();
    // ホスト: 本人から届いた一振りの番号と、その一振りで触れた瞬間に当てた相手
    private byte _hostCutSeq;
    private readonly List<byte> _hostCuts = new();
    internal static int CutsAsked, CutsHit, CutRetryHits;
    private const float RetryCutDeg = 12f; // 練習の相手の振りで、触れて外れた相手へ頼み直すまでに刀が進む角度

    private static void WriteStroke(MessageWriter w, Stroke s)
    {
        // Hazel に short の書き手は無い (int の 4 バイトになる)。読み手の ReadInt16 と合わせて 2 バイトで書く
        w.Write(unchecked((ushort)s.A0));
        w.Write(unchecked((ushort)s.Sweep));
        w.Write(s.Ms);
        w.Write(s.Iai);
        w.Write(s.Tier);
        w.Write(s.Seq);
    }

    private static Stroke ReadStroke(MessageReader r) =>
        new(r.ReadInt16(), r.ReadInt16(), r.ReadUInt16(), r.ReadByte(), r.ReadByte(), r.ReadByte());

    // 自分の端末: 一振りが始まった (番号を進める)
    private void NextStrokeSeq()
    {
        _strokeSeq++;
        _cutAsked.Clear();
    }

    // 刀の絵がこの一振りで相手に触れた時に頼むか (SamuraiBlade が毎フレームの重なりを見る前に聞く)
    internal bool WantsCut => IsLocal ? !_iaiStroke && !Spent && SwipeInput.InStroke : _botPending.Count > 0 && AmongUsClient.Instance && AmongUsClient.Instance.AmHost;

    internal bool CutDone(byte target)
    {
        if (IsLocal) return _cutAsked.Contains(target);
        var b = BotSwinging();
        return b == null || b.Cuts.Contains(target);
    }

    // 刀の絵が相手 target の体に重なった (人 = 本人の端末・練習の相手 = ホスト)
    internal void OnBladeContact(byte target)
    {
        if (IsLocal)
        {
            _cutAsked.Add(target);
            float a0 = SwipeInput.StrokeStartA, sweep = SwipeInput.Angle - a0;
            float secs = Time.time - SwipeInput.StrokeStartT;
            // 当たりの強さはその時の刀の速さ (8 度/秒の刻みで居合の欄に載せる・突きと居合は使わない欄)
            byte speed = (byte)Math.Clamp((int)MathF.Round(FxMath.Abs(SwipeInput.Speed) / 8f), 1, 255);
            // 始めの角度は連続 (±180 を越える) なので、送る時は -180〜180 へ (掃引角は連続の差のまま)
            var s = new Stroke((short)MathF.Round(SwipeInput.Wrap(a0)), (short)MathF.Round(sweep), (ushort)Math.Clamp((int)(secs * 1000f), 1, 60000), speed, 1, _strokeSeq);
            CutsAsked++;
            if (AmongUsClient.Instance && AmongUsClient.Instance.AmHost) HostCut(s, target);
            else CutCall.Send((s, target));
            return;
        }
        var b = BotSwinging();
        if (b == null)
        {
            if (HitWatch.On) HitWatch.CutMiss(PlayerId, target, "no-swing");
            return;
        }
        // 練習の相手の振りの再生と同じ式 (始めが速く終わりで止まる) で今の刀の向き。威力は振り終わりの一振りと同じ
        float k = FxMath.Clamp01((Time.time - (b.At - b.Secs)) / b.Secs);
        float e = 1f - (1f - k) * (1f - k);
        // 振り始めから刀の絵が体に重なっていると、まだ振っていない分だけ外れる。外れた相手は振り終わりの判定に残し、
        // 刀が少し進んでから頼み直す (当たった相手だけ HostCutApply が Cuts に入れる)
        float swept = FxMath.Abs(b.Sweep * e);
        if (b.Tried.TryGetValue(target, out float tried) && swept - tried < RetryCutDeg) return;
        bool retry = b.Tried.ContainsKey(target);
        b.Tried[target] = swept;
        CutsAsked++;
        HostCutApply(b.A0, b.Sweep * e, FxMath.Max(k * b.Secs, 0.01f), b.Dmg, target, b.Cuts);
        if (retry && b.Cuts.Contains(target)) CutRetryHits++;
    }

    // 練習の相手の今の刀の再生にあたる一振り (始めた時刻が今より前の中で一番新しい物)
    private BotPending BotSwinging()
    {
        float now = Time.time;
        for (int i = _botPending.Count - 1; i >= 0; i--)
            if (_botPending[i].At - _botPending[i].Secs <= now) return _botPending[i];
        return null;
    }

    private static readonly RemoteCall<(Stroke s, byte target)> CutCall = new("Samurai.Cut", Route.ToHost,
        (w, v) => { WriteStroke(w, v.s); w.Write(v.target); }, r => (ReadStroke(r), r.ReadByte()),
        (sender, v) => (RoleState.Of(sender) as Samurai)?.HostCut(v.s, v.target), perSecond: 8);

    // ホスト: 本人から届いた「触れた瞬間」の当たり
    private void HostCut(Stroke s, byte target)
    {
        // 居合の欄は速さに使っているので、居合かの見分けは納めているかだけで
        if ((s.Tier & ThrustFlag) != 0 || HostRefuse(new Stroke(s.A0, s.Sweep, s.Ms, 0, s.Tier, s.Seq), out bool iai) != null || iai) return;
        if (s.Seq != _hostCutSeq)
        {
            _hostCutSeq = s.Seq;
            _hostCuts.Clear();
        }
        if (_hostCuts.Contains(target)) return;
        float sweep = Math.Clamp((int)s.Sweep, -(int)SwipeInput.MaxSweep, (int)SwipeInput.MaxSweep);
        float secs = Math.Clamp(s.Ms / 1000f, 0.01f, SwipeInput.MaxSeconds);
        // 速さは客の申告なので、人の手で出せる速さまでに丸める
        float deg = FxMath.Min(s.Iai * 8f, SwipeInput.MaxSweep / MinStrokeSeconds);
        HostCutApply(Math.Clamp((int)s.A0, -180, 180), sweep, secs, Damage(deg), target, _hostCuts);
    }

    // ホスト: a0 から sweep 度を secs 秒で振った所で target に触れた。当たったら cuts に足す (振り終わりの一振りで飛ばす)。
    // その途中までの振りで舌を斬った / 口で受けた別の人も cuts に入る (振り終わりの一振りでその人を斬らない)
    private void HostCutApply(float a0, float sweep, float secs, int damage, byte target, List<byte> cuts)
    {
        _hostStrokeIai = _hostStrokeFull = _hostStrokeSmash = false;
        _hostStrokeSecs = secs;
        if (!HostHit(a0, sweep, damage, only: target, spareOut: cuts))
        {
            if (HitWatch.On) HitWatch.CutMiss(PlayerId, target, $"false a0={a0:0} sweep={sweep:0} secs={secs:0.00}");
            return;
        }
        CutsHit++;
        if (!cuts.Contains(target)) cuts.Add(target);
    }
}
