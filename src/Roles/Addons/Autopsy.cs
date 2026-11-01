using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Options;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Roles.Addons;

// オートプシー (アドオン): 死んだ人の死因が名札に出る。設定で倒した人の役職と、試合中に死体の上の死因も
public sealed class Autopsy : AddonBase
{
    public const string Mint = "#80FFDD";

    public override string Color => Mint;
    public override Text Name => new("Autopsy.Name");
    public override string Reading => "おーとぷしー";
    public override Text Blurb => new("Autopsy.Blurb");
    public override string Origin => "TOHY";

    protected internal override bool DefaultOnMadmate => false;
    protected internal override bool DefaultOnImpostor => false;
    protected internal override bool DefaultOnNeutral => false;

    public static readonly BoolOpt ShowKillerRole = new("Autopsy.ShowKillerRole", false);
    public static readonly BoolOpt OnBodies = new("Autopsy.OnBodies", false);

    // 死体の上の死因が見える最大の距離
    private const float BodyRange = 2.5f;
    private const int CheckEvery = 8; // 30Hz の 8 回に 1 回 ≈ 0.27 秒

    private sealed class Label
    {
        public byte Pid;
        public DeadBody Body;
        public GameObject Go;
        public float X, Y;
        public bool Shown;
    }

    private readonly List<Label> _labels = new();
    private int _tick;

    public override string NameMark(PlayerControl seen)
    {
        if (seen.Data == null || !seen.Data.IsDead) return null;
        string why = Reason(seen.PlayerId);
        return why == null ? null : $" <size=65%><color={Mint}>({why})</color></size>";
    }

    // 死因の短い文 (記録が無ければ null)
    private static string Reason(byte pid)
    {
        var d = MatchLog.Of(pid);
        if (d == null) return null;
        return d.Kind switch
        {
            MatchLog.Kind.Killed => ShowKillerRole && !string.IsNullOrEmpty(d.KillerRoleBare)
                ? new Text("Autopsy.KilledByRole").With("role", d.KillerRoleBare).ToString()
                : new Text("Autopsy.Killed").ToString(),
            MatchLog.Kind.Suicide => new Text("Autopsy.Suicide").ToString(),
            MatchLog.Kind.Exiled => new Text("Autopsy.Exiled").ToString(),
            MatchLog.Kind.Spaced => new Text("Autopsy.Spaced").ToString(),
            MatchLog.Kind.Left => new Text("Autopsy.Left").ToString(),
            _ => null,
        };
    }

    // 試合中の死体の上: 倒されて死体が出たら字を置く (見せるのは近くて壁の無い時だけ)
    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (!OnBodies || !e.Target) return;
        byte pid = e.Target.PlayerId;
        // 死体はキルの演出の後に出るので少し待つ
        PortKit.After(0.6f, () => AddLabel(pid));
    }

    private void AddLabel(byte pid)
    {
        if (MeetingHud.Instance) return;
        string why = Reason(pid);
        var lp = PlayerControl.LocalPlayer;
        var src = lp && lp.cosmetics ? lp.cosmetics.nameText : null;
        if (why == null || !src) return;
        DeadBody body = null;
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
            if (b && b.ParentId == pid) { body = b; break; }
        if (!body) return;

        var go = Object.Instantiate(src.gameObject, body.transform);
        go.name = "MrpAutopsyLabel";
        for (int i = go.transform.childCount - 1; i >= 0; i--) Object.Destroy(go.transform.GetChild(i).gameObject);
        foreach (var c in go.GetComponents<Component>())
            if (!c.TryCast<Transform>() && !c.TryCast<TextMeshPro>() && !c.TryCast<Renderer>() && !c.TryCast<MeshFilter>()) Object.Destroy(c);
        go.layer = body.gameObject.layer;
        go.transform.localPosition = FxMath.V3(0f, 0.75f, -0.5f);
        var t = go.GetComponent<TextMeshPro>();
        t.text = $"<color={Mint}>{why}</color>";
        t.fontSize = 2f;
        go.SetActive(false);

        var pos = body.TruePosition;
        _labels.Add(new Label { Pid = pid, Body = body, Go = go, X = pos.x, Y = pos.y });
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (_labels.Count == 0 || ++_tick < CheckEvery) return;
        _tick = 0;
        bool alive = PortKit.Alive(Player) && ShipStatus.Instance;
        Vector2 me = alive ? Player.GetTruePosition() : default;
        // 視界 (停電で狭くなる) より遠い死体と、影の境 (壁・閉じた扉) の向こうの死体には出さない
        float reach = alive ? System.Math.Min(BodyRange, ShipStatus.Instance.CalculateLightRadius(Player.Data)) : 0f;
        for (int i = _labels.Count - 1; i >= 0; i--)
        {
            var l = _labels[i];
            if (!l.Body || !l.Go) { _labels.RemoveAt(i); continue; }
            bool show = false;
            if (alive)
            {
                float dx = l.X - me.x, dy = l.Y - me.y;
                show = dx * dx + dy * dy <= reach * reach
                    && !PhysicsHelpers.AnythingBetween(me, FxMath.V2(l.X, l.Y), Constants.ShadowMask, false);
            }
            if (show != l.Shown)
            {
                l.Shown = show;
                l.Go.SetActive(show);
            }
        }
    }

    // 会議で死体は消える (字も一緒に消える)
    private void OnMeeting(MeetingStartEvent e) => _labels.Clear();
}
