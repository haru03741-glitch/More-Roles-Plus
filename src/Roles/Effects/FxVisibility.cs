using System;
using System.Globalization;
using System.Text;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 演出 (光・音・矢印・印など) を誰に見せるか / 聞かせるか。組み合わせて使う
[Flags]
public enum Audience : byte
{
    None = 0,
    Self = 1,    // 演出を起こした本人
    Team = 2,    // 本人と同じ陣営 (第三陣営は同じ役職どうしだけ)
    Target = 4,  // 演出の相手 (狙われた人・守られた人など)
    Nearby = 8,  // 演出の場所の近くにいる人
    Dead = 16,   // 幽霊 (観戦の人も含む)
    All = 32,    // 全員
}

// 演出を止める時 (本人には止めない)
[Flags]
public enum FxHide : byte
{
    None = 0,
    Meeting = 1, // 会議の間
    Comms = 2,   // 通信妨害の間
}

// 演出 1 つの見える範囲。役職クラスの static readonly に置くと、設定画面にその役職の行として 1 行出て、
// ホストが「選んでよい範囲」の中から選ぶ (値は全員に同期される)。
//   public static readonly FxVisibility Flash = new("光が見える人", "Who sees the flash", Audience.Self, FxVisibility.Personal);
// 推理を壊す演出 (占いの結果・自分だけの印) は Personal / Involved を渡して「全員」を選べないようにする。
// 見せる時は FxSignal.Show で全員へ 1 本送り、受け取った端末がこの範囲で自分に出すかを決める
// Show は出来事 1 つにつき 1 回 (毎フレーム送ると通信の本数の予算を食う)。出し続ける物 (矢印など) は受け取った端末が自分で出し続け、
// 毎フレーム LocalCanSee を聞く時は結果を数百 ms ごとに持っておく (近くの人の判定は位置を読むのでゴミが出る)
public sealed class FxVisibility : Opt
{
    // 本人と幽霊だけ (他の人に見えると推理が成り立たない物)
    public static readonly Audience[] Personal = { Audience.Self, Audience.Self | Audience.Dead };

    // 当事者まで (本人・相手・仲間)
    public static readonly Audience[] Involved =
    {
        Audience.Self, Audience.Self | Audience.Target, Audience.Self | Audience.Team, Audience.Self | Audience.Team | Audience.Target,
        Audience.Self | Audience.Target | Audience.Dead, Audience.Self | Audience.Team | Audience.Target | Audience.Dead,
    };

    // 周りにも分かってよい物 (見せること自体が駆け引きになる物)
    public static readonly Audience[] Open =
    {
        Audience.All, Audience.Self | Audience.Target | Audience.Nearby, Audience.Self | Audience.Target | Audience.Nearby | Audience.Dead,
        Audience.Self | Audience.Target, Audience.Self | Audience.Team | Audience.Target, Audience.Self,
    };

    private readonly Audience[] _choices;
    public readonly FxHide Hide;
    private readonly float _radius;
    private readonly FloatOpt _radiusOpt;

    // radius = 近くの人の半径 (世界の単位)。ホストに変えさせる時は FloatOpt を別の行に置いて radiusOpt に渡す
    public FxVisibility(string ja, string en, Audience defaultAudience, Audience[] allowed, FxHide hide = FxHide.None, float radius = 5f, FloatOpt radiusOpt = null)
        : base(new Text(ja, en), IndexIn(allowed, defaultAudience))
    {
        _choices = allowed;
        Hide = hide;
        _radius = radius;
        _radiusOpt = radiusOpt;
    }

    private static int IndexIn(Audience[] allowed, Audience a)
    {
        if (allowed == null || allowed.Length == 0) throw new ArgumentException("FxVisibility: allowed is empty");
        int i = Array.IndexOf(allowed, a);
        if (i < 0) throw new ArgumentException($"FxVisibility: default {a} is not in allowed");
        return i;
    }

    public Audience Value => _choices[Index];
    public float Radius => _radiusOpt != null ? _radiusOpt.Value : _radius;

    internal override int Count => _choices.Length;
    protected override bool Wraps => true;
    internal override string Display() => Describe(Value);

    // 保存は範囲の値で書く (選べる範囲の並びを後で変えても同じ範囲に戻るように)
    internal override string Save() => ((int)Value).ToString(CultureInfo.InvariantCulture);
    internal override void Load(string s)
    {
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return;
        int i = Array.IndexOf(_choices, (Audience)v);
        if (i >= 0) Index = i;
    }

    public static string Describe(Audience a)
    {
        if ((a & Audience.All) != 0) return new Text("全員", "Everyone");
        if (a == Audience.None) return new Text("誰にも出さない", "No one");
        var sb = new StringBuilder();
        void Part(Audience bit, Text t)
        {
            if ((a & bit) == 0) return;
            if (sb.Length > 0) sb.Append('+');
            sb.Append((string)t);
        }
        Part(Audience.Self, new Text("本人", "Self"));
        Part(Audience.Team, new Text("仲間", "Team"));
        Part(Audience.Target, new Text("相手", "Target"));
        Part(Audience.Nearby, new Text("近くの人", "Nearby"));
        Part(Audience.Dead, new Text("幽霊", "Ghosts"));
        return sb.ToString();
    }

    // viewer にこの演出を出すか。actor = 起こした人・target = 相手 (無ければ 255)・at = 場所 (近くの人を数える時)
    public bool CanSee(PlayerControl viewer, byte actor, byte target, Vector2 at) => Allows(Value, Hide, Radius, viewer, actor, target, at);

    // 自分の端末に出すか
    public bool LocalCanSee(byte actor, byte target, Vector2 at) => CanSee(PlayerControl.LocalPlayer, actor, target, at);

    internal static bool Allows(Audience a, FxHide hide, float radius, PlayerControl viewer, byte actor, byte target, Vector2 at)
    {
        if (!viewer || viewer.Data == null) return false;
        byte vid = viewer.PlayerId;
        bool self = vid == actor;
        if ((a & Audience.Self) != 0 && self) return true;
        if (!self && hide != FxHide.None)
        {
            if ((hide & FxHide.Meeting) != 0 && MeetingHud.Instance) return false;
            if ((hide & FxHide.Comms) != 0 && FxSignal.CommsDown()) return false;
        }
        if ((a & Audience.All) != 0) return true;
        if ((a & Audience.Target) != 0 && vid == target && target != 255) return true;
        if ((a & Audience.Dead) != 0 && viewer.Data.IsDead) return true;
        if ((a & Audience.Team) != 0 && SameTeam(actor, vid)) return true;
        if ((a & Audience.Nearby) != 0)
        {
            var p = viewer.GetTruePosition();
            float dx = p.x - at.x, dy = p.y - at.y;
            if (dx * dx + dy * dy <= radius * radius) return true;
        }
        return false;
    }

    private static bool SameTeam(byte a, byte b)
    {
        var ra = RoleState.Of(a);
        var rb = RoleState.Of(b);
        if (ra == null || rb == null || ra.Team != rb.Team) return false;
        return ra.Team != Team.Neutral || ra.GetType() == rb.GetType();
    }

    // 演出を受け取った時の処理 (役職が OnAssigned などで入れる。全員の端末で、自分に見える時だけ呼ばれる)
    internal Action<FxEvent> Handler;
    public void OnShow(Action<FxEvent> handler) => Handler = handler;
}

// 届いた演出 1 つ
public readonly struct FxEvent
{
    public readonly byte Actor, Target, Arg;
    public readonly Vector2 At;

    public FxEvent(byte actor, byte target, Vector2 at, byte arg)
    {
        Actor = actor;
        Target = target;
        At = at;
        Arg = arg;
    }
}
