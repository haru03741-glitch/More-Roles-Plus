using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ノヴァの光線とサムライ。満タンでない光線はサムライで止まる (Victims)。どうなったかはホストが決めて全員へ配る:
//   溜めが少ない (DeflectBelow 未満) = 抜いて光線の方を向いていれば刀で弾いて散らす (気を使う)
//   その上〜満タンの手前 = 刀で光線を押し止める押し合い。LockTime 秒の間に溜め量で決まる数だけ連打できれば押し切って光線を断つ。
//                          できなければ刀ごと押し負けて吹き飛ばされる
//   満タン = 刀で受けても致命傷 (体力 1 で吹き飛ぶ)・受けられなければ倒れる。光線はそのまま先へ抜ける
// 抜いていない・背を向けている・気切れ・よろけている時は受けられず、吹き飛ばされて体力が減る。転がっている最中は当たらない
public sealed partial class Nova
{
    internal const float DeflectBelow = 0.25f;  // これ未満の溜めは刀で弾ける
    internal const float DeflectKi = 20f;       // 弾くのに要る気
    private const int DeflectDamage = 25;       // 弾けなかった時
    private const int LockDamageMin = 40, LockDamageGain = 30; // 押し合いに負けた/受けられなかった時 (溜め量で増える)
    private const float MashMin = 3f, MashGain = 4f; // 押し切るのに要る連打 (回/秒)。溜め DeflectBelow で MashMin・満タン手前で MashMin + MashGain
    private const float WonStagger = 1f;        // 押し切られたノヴァが動けない秒
    internal const float SamuraiStop = 0.4f;    // 光線が止まる所 = サムライの手前 (刀の先)
    private static bool _samuraiStop;           // 今の Victims で光線がサムライに止められた

    // ホスト: 押し合いの相手 (255 = 無し)・終わる時刻・連打の数・要る数・溜め量
    private byte _blSam = 255;
    private float _blEnd, _blNextMeter, _blLevel;
    private int _blCount, _blNeed;

    private static float LockTime => Neutral.Samurai.LockTime;
    private static int LockDamage(float level) => LockDamageMin + (int)(LockDamageGain * level);

    // ホスト: 光線がサムライ s に届いた (from = 撃った所)
    internal void HostSamurai(Neutral.Samurai s, Vector2 from, float level)
    {
        var p = s.Player;
        var me = Player;
        if (!p || !me || !PortKit.Alive(p)) return;
        Plugin.Logger.LogInfo($"nova-samurai: hit sam={s.PlayerId} level={level:0.00} drawn={s.Drawn} hp={Duel.HpOf(s.PlayerId)} guard={s.HostGuardsBeam(from, 0f)}");
        if (DuelDodge.HostSafe(s.PlayerId)) return;
        if (level >= 0.999f)
        {
            bool held = s.HostGuardsBeam(from, 0f);
            int left = Duel.HpOf(s.PlayerId);
            HostSmash(me, p, held ? left - 1 : left);
            if (held) Broadcast(Kind.Fatal, s.PlayerId);
            return;
        }
        if (level < DeflectBelow)
        {
            if (!s.HostGuardsBeam(from, DeflectKi)) { HostSmash(me, p, DeflectDamage); return; }
            s.HostSpendBeam(DeflectKi);
            Broadcast(Kind.Deflect, s.PlayerId);
            return;
        }
        if (_blSam != 255 || s.Locked || BeamLocked(s.PlayerId) || !s.HostGuardsBeam(from, 1f)) { HostSmash(me, p, LockDamage(level)); return; }
        float k = (level - DeflectBelow) / (1f - DeflectBelow);
        _blSam = s.PlayerId;
        _blLevel = level;
        _blCount = 0;
        _blNeed = System.Math.Max(1, (int)System.MathF.Round(LockTime * (MashMin + MashGain * k)));
        _blEnd = Time.time + LockTime;
        _blNextMeter = Time.time + 0.2f;
        Duel.HostJoin(PlayerId, s.PlayerId);
        Broadcast(Kind.LockStart, s.PlayerId, (byte)System.Math.Min(_blNeed, 255));
        Plugin.Logger.LogInfo($"nova-samurai: lock sam={s.PlayerId} level={level:0.00} need={_blNeed}");
    }

    // ホスト: 押し合いの連打の写しと終わり (このノヴァの FixedUpdate ごと)
    private void HostLockTick()
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) { _blSam = 255; return; }
        byte sam = _blSam;
        var gd = GameData.Instance;
        var sp = gd ? gd.GetPlayerById(sam)?.Object : null;
        float now = Time.time;
        if (MeetingHud.Instance || !PortKit.Alive(Player) || !PortKit.Alive(sp))
        {
            _blSam = 255;
            Broadcast(Kind.Cancel, sam);
            return;
        }
        if (now >= _blNextMeter)
        {
            _blNextMeter = now + 0.2f;
            // 光線の押しは時間とともに要る数へ近づく。サムライの連打がそれより多ければバーはサムライの側へ寄る
            float t = FxMath.Clamp01(1f - (_blEnd - now) / LockTime);
            Broadcast(Kind.Meter, sam, (byte)System.Math.Min(_blCount, 255), (byte)System.Math.Min((int)(_blNeed * t), 255));
        }
        if (now < _blEnd) return;
        _blSam = 255;
        Plugin.Logger.LogInfo($"nova-samurai: lock end sam={sam} mash={_blCount}/{_blNeed}");
        if (_blCount >= _blNeed)
        {
            Broadcast(Kind.LockWon, sam);
            return;
        }
        Broadcast(Kind.LockLost, sam);
        HostSmash(Player, sp, LockDamage(_blLevel));
    }

    // ホスト: 本人の端末から届いた連打 (押し合い中のサムライなら数えて true)
    internal static bool HostBeamMash(byte sam, int n)
    {
        foreach (var r in RoleState.All)
        {
            if (r is not Nova nv || nv._blSam != sam) continue;
            nv._blCount += n;
            return true;
        }
        return false;
    }

    // このサムライが光線と押し合っているか (ホスト)
    internal static bool BeamLocked(byte sam)
    {
        foreach (var r in RoleState.All)
            if (r is Nova nv && nv._blSam == sam) return true;
        return false;
    }

    // ホスト: 吹き飛ばして体力を減らす。倒れる分だけキルとして確かめる (盾などに止められたら体力 1 で残す)
    private static void HostSmash(PlayerControl nova, PlayerControl v, int dmg)
    {
        if (!nova || !v) return;
        byte id = v.PlayerId;
        int left = Duel.HpOf(id);
        if (dmg < 0) dmg = 0;
        if (left - dmg <= 0)
        {
            DuelTongue.Finishing = true;
            try { if (BeforeMurderEvent.Ask(nova, v) != null) dmg = left - 1; }
            finally { DuelTongue.Finishing = false; }
        }
        if (!Duel.HostDamage(nova.PlayerId, id, dmg, start: dmg < left, smash: true)) return;
        RangedKillSnapPatch.Mark(nova.PlayerId, 2f);
        nova.RpcMurderPlayer(v, true);
        Duel.HostLeave(id);
    }

    // ---- 全員の端末 ----

    private enum Kind : byte { Deflect = 1, LockStart = 2, Meter = 3, LockWon = 4, LockLost = 5, Fatal = 6, Cancel = 7 }

    private static readonly RemoteCall<(byte kind, byte nova, byte sam, byte a, byte b)> SamuraiCall = new("Nova.Samurai", Route.HostToAll,
        (w, m) => { w.Write(m.kind); w.Write(m.nova); w.Write(m.sam); w.Write(m.a); w.Write(m.b); },
        r => (r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte()),
        (_, m) => Apply((Kind)m.kind, m.nova, m.sam, m.a, m.b));

    private void Broadcast(Kind k, byte sam, byte a = 0, byte b = 0)
    {
        SamuraiCall.Send(((byte)k, PlayerId, sam, a, b));
        Apply(k, PlayerId, sam, a, b); // 配る電文はホストの手元では動かない
    }

    private static PlayerControl ById(byte id) => GameData.Instance ? GameData.Instance.GetPlayerById(id)?.Object : null;

    private static void Apply(Kind k, byte nova, byte sam, byte a, byte b)
    {
        var np = ById(nova);
        var sp = ById(sam);
        var s = RoleState.Of(sam) as Neutral.Samurai;
        if (s == null) return;
        // 打ち切りはノヴァが抜けた後でも届けば解く (寄せたカメラと HUD を残さない)
        if (k == Kind.Cancel)
        {
            s.EndBeamLock(lost: false, knock: false);
            if (np) NovaFx.CutBeam(np);
            SamuraiSound.StopLock();
            if (np && np.AmOwner) MoveSpeed.Remove("NovaLock");
            return;
        }
        if (!np || !sp) return;
        bool mine = np.AmOwner;
        switch (k)
        {
            case Kind.Deflect:
                s.OnBeamDeflect(nova);
                NovaFx.SplitBeam(np, deflect: true);
                Contact(np, sp, 1.3f);
                SamuraiSound.Clash(np, sp);
                break;
            case Kind.LockStart:
                s.BeginBeamLock(nova);
                NovaFx.HoldBeam(np, LockTime + 0.2f);
                NovaFx.SplitBeam(np, deflect: false, LockTime + 0.2f);
                Contact(np, sp, 1.2f);
                SamuraiSound.LockOn(np, sp);
                RangedKillSnapPatch.Mark(nova, LockTime + 2f);
                if (mine) MoveSpeed.Add("NovaLock", 0f, LockTime + 0.3f);
                break;
            case Kind.Meter:
                UI.DuelHud.LockMeter(sam, nova, a, b);
                Contact(np, sp, 0.45f);
                break;
            case Kind.LockWon:
                s.EndBeamLock(lost: false);
                NovaFx.CutBeam(np);
                Contact(np, sp, 1.5f);
                SamuraiSound.LockEnd(np, sp);
                if (mine) { MoveSpeed.Remove("NovaLock"); MoveSpeed.Add("NovaLock", 0f, WonStagger); }
                break;
            case Kind.LockLost:
                s.EndBeamLock(lost: true);
                PortKit.After(0.25f, () => NovaFx.CutBeam(np));
                SamuraiSound.LockEnd(np, sp);
                if (mine) MoveSpeed.Remove("NovaLock");
                break;
            case Kind.Fatal:
                s.OnBeamFatal(nova);
                Contact(np, sp, 1.6f);
                SamuraiSound.Clash(np, sp);
                break;
        }
    }

    // 刀が光線を受けている所 (サムライの体の中心から光線の来る方へ) に火花
    private static void Contact(PlayerControl nova, PlayerControl sam, float size)
    {
        Vector3 c = sam.transform.position, o = nova.transform.position;
        float dx = o.x - c.x, dy = o.y - c.y, d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d < 0.01f) return;
        float k = SamuraiStop / d;
        DuelHitFx.Clash(FxMath.V3(c.x + dx * k, c.y + dy * k + SamuraiBlade.LockLift, c.z - 0.02f), sam.gameObject.layer, size);
    }
}
