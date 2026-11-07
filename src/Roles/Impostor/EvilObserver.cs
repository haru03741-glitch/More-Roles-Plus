// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/EvilObserver.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Collections.Generic;
using Il2CppInterop.Runtime.Injection;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// イビルオブザーバー (インポスター): 足元にカメラを置く (向いている方を写す)。カメラは次の会議の後から使える。
// 「モニター」でカメラの景色を見る (壁越し・停電でも見える)。「次へ」で別のカメラへ、もう一度「モニター」で自分に戻る。
// 設定で、置いたカメラが他の人にも見える
public sealed class EvilObserver : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilObserver.Name");
    public override string Reading => "いびるおぶざーばー";
    public override Text Blurb => new("EvilObserver.Blurb");
    public override string Origin => "Uchu";
    public override HelpSection[] HelpSections => new HelpSection[]
        { new("EvilObserver.Place", "observer_camera"), new("EvilObserver.Monitor", "observer_monitor"), new("EvilObserver.Next", "observer_next") };

    public static readonly FloatOpt PlaceCooldown = new("EvilObserver.PlaceCooldown", 20f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt NumOfCamera = new("EvilObserver.NumOfCamera", 3, 1, 10);
    public static readonly BoolOpt CanSeeCameraOtherPlayer = new("EvilObserver.CanSeeCameraOtherPlayer", true);

    private sealed class Cam { public Vector2 At; public bool Left; public bool Active; public GameObject Art; }
    private readonly List<Cam> _cams = new();
    private int _viewing = -1;          // 自分の端末: 見ているカメラ (-1 = 自分)
    private GameObject _target;
    private Transform _lightParent;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("EvilObserver.Place"), "observer_camera")
        {
            Cooldown = () => PlaceCooldown,
            MaxUses = () => NumOfCamera,
            OnUse = () =>
            {
                if (_viewing >= 0) return false;
                Vector2 at = Player.GetTruePosition();
                bool left = Player.cosmetics && Player.cosmetics.FlipX;
                PlaceCall.Send((at.x, at.y, left));
                Place(at, left);
                return true;
            },
        });
        AddAbility(new Ability(new Text("EvilObserver.Monitor"), "observer_monitor")
        {
            CooldownAtStart = false,
            OnUse = () =>
            {
                if (_viewing >= 0) StopView();
                else View(NextActive(-1));
                return false; // 待ち時間は使わない
            },
        });
        AddAbility(new Ability(new Text("EvilObserver.Next"), "observer_next")
        {
            CooldownAtStart = false,
            OnUse = () =>
            {
                if (_viewing < 0) return false;
                View(NextActive(_viewing));
                return false;
            },
        });
        if (IsLocal) Lifespan.OnRelease(StopView);
    }

    private static readonly RemoteCall<(float x, float y, bool left)> PlaceCall = new("EvilObserver.Place", Route.Anyone,
        (w, m) => { w.Write(m.x); w.Write(m.y); w.Write(m.left); }, r => (r.ReadSingle(), r.ReadSingle(), r.ReadBoolean()),
        (sender, m) => { if (RoleState.Of(sender) is EvilObserver o) o.Place(new Vector2(m.x, m.y), m.left); }, perSecond: 2);

    private void Place(Vector2 at, bool left)
    {
        var cam = new Cam { At = at, Left = left };
        if (IsLocal || CanSeeCameraOtherPlayer)
        {
            var sr = PortKit.Marker(Lifespan, at, "observer_camera", 0.55f, IsLocal ? new UnityEngine.Color(1f, 1f, 1f, 0.6f) : UnityEngine.Color.white);
            sr.flipX = left;
            cam.Art = sr.gameObject;
        }
        _cams.Add(cam);
    }

    // 次の会議の後から使える
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        foreach (var c in _cams) c.Active = true;
    }

    private int NextActive(int from)
    {
        for (int k = 1; k <= _cams.Count; k++)
        {
            int i = (from + k + _cams.Count) % _cams.Count;
            if (from < 0) i = k - 1;
            if (_cams[i].Active) return i;
        }
        return -1;
    }

    private static bool _registered;

    private void View(int index)
    {
        if (index < 0) { PortKit.Notice(new Text("EvilObserver.NoCamera").ToString()); return; }
        var hud = Vanilla.Hud;
        var lp = Player;
        if (!hud || !hud.PlayerCam || !lp) return;
        if (!_registered) { _registered = true; ClassInjector.RegisterTypeInIl2Cpp<ObserverTarget>(); }
        if (!_target)
        {
            _target = new GameObject("MrpObserverView");
            _target.AddComponent<ObserverTarget>();
        }
        var c = _cams[index];
        // カメラの向いている方を少し先まで写す
        _target.transform.position = new Vector3(c.At.x + (c.Left ? -1.2f : 1.2f), c.At.y, 0f);
        _viewing = index;
        hud.PlayerCam.SetTarget(_target.GetComponent<ObserverTarget>());
        if (lp.lightSource)
        {
            if (_lightParent == null) _lightParent = lp.lightSource.transform.parent;
            lp.lightSource.transform.SetParent(_target.transform, false);
        }
        if (hud.ShadowQuad) hud.ShadowQuad.gameObject.SetActive(false); // 壁越し・停電でも見える
        MoveSpeed.Add("ObserverView", 0f, 9999f);
    }

    private void StopView()
    {
        if (_viewing < 0) return;
        _viewing = -1;
        var hud = Vanilla.Hud;
        var lp = Player;
        if (hud && hud.PlayerCam && lp) hud.PlayerCam.SetTarget(lp);
        if (lp && lp.lightSource && _lightParent) lp.lightSource.transform.SetParent(_lightParent, false);
        _lightParent = null;
        if (hud && hud.ShadowQuad) hud.ShadowQuad.gameObject.SetActive(true);
        MoveSpeed.Remove("ObserverView");
    }

    private void OnMeetingStart(MeetingStartEvent e)
    {
        if (IsLocal) StopView();
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (_viewing >= 0 && (!PortKit.Alive(Player) || MeetingHud.Instance)) StopView();
    }
}

// カメラが追いかける的 (本編のカメラは MonoBehaviour しか追えない)
public class ObserverTarget : MonoBehaviour
{
    public ObserverTarget(System.IntPtr ptr) : base(ptr) { }
}
