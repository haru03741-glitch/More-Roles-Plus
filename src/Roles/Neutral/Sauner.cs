// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Sauner.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// サウナー (第三陣営・エアシップだけ): サウナ (ラウンジ) → シャワー → 外気浴 (展望デッキ) の順に、それぞれの部屋で
// 決まった時間を過ごす (部屋の外や会議の間は時間が進まない)。外気浴まで終えると 1 人で勝つ
public sealed class Sauner : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#DB9865";
    public override Text Name => new("Sauner.Name");
    public override string Reading => "さうなー";
    public override Text Blurb => new("Sauner.Blurb");
    public override string Origin => "SNR";
    public override byte[] OnlyMaps => new byte[] { 4 };
    public override HelpSection[] HelpSections => new HelpSection[] { new("Sauner.Stages") };

    public static readonly FloatOpt DarkroomTime = new("Sauner.DarkroomTime", 180f, 20f, 600f, 20f, "s");
    public static readonly FloatOpt ShowerTime = new("Sauner.ShowerTime", 20f, 20f, 600f, 20f, "s");
    public static readonly FloatOpt DeckTime = new("Sauner.DeckTime", 60f, 20f, 600f, 20f, "s");

    private enum Stage { Darkroom, Shower, Deck }

    private Stage _stage;
    private float _timer = -1f;
    private bool _won;
    private float _flashTimer;
    private WorldArrow _arrow;

    private static float Limit(Stage s) => s switch { Stage.Darkroom => DarkroomTime, Stage.Shower => ShowerTime, _ => DeckTime };

    private static UnityEngine.Color StageColor(Stage s) => s switch
    {
        Stage.Darkroom => new Color32(255, 123, 99, 103),
        Stage.Shower => new Color32(65, 161, 255, 103),
        _ => new Color32(38, 200, 94, 103),
    };

    private static Vector2[] Targets(Stage s) => s switch
    {
        Stage.Darkroom => new[] { new Vector2(12.5f, 2.2f) },
        Stage.Shower => new[] { new Vector2(22.5f, 2.55f) },
        _ => new[] { new Vector2(-13.7f, -15f), new Vector2(8f, -14.6f) },
    };

    private static bool Inside(Stage s, Vector2 p) => s switch
    {
        Stage.Darkroom => In(new Vector2(11.125f, 3.2f), new Vector2(13.8f, 0.9f), p),
        Stage.Shower => In(new Vector2(20.3f, 3.5f), new Vector2(24.9f, 1.4f), p),
        _ => In(new Vector2(-14.7f, -13.95f), new Vector2(11.1f, -17f), p),
    };

    private static bool In(Vector2 up, Vector2 down, Vector2 p) => up.x <= p.x && p.x <= down.x && up.y >= p.y && p.y >= down.y;

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (_timer < 0f) _timer = DarkroomTime;
        bool meeting = MeetingHud.Instance || ExileController.Instance;
        if (!PortKit.Alive(Player))
        {
            _arrow?.SetActive(false);
            StatusEffects.Hide("Sauner");
            return;
        }
        bool inside = !meeting && Inside(_stage, Player.transform.position);
        // 部屋の中では部屋の色で画面がゆっくり光る
        if (inside)
        {
            _flashTimer -= Time.fixedDeltaTime;
            if (_flashTimer <= 0f)
            {
                _flashTimer = 6f;
                PortKit.Flash(StageColor(_stage), 6f);
            }
        }
        else _flashTimer = 0f;
        UpdateArrow(inside, meeting);
        UpdateText(meeting);
        if (_won || !inside) return;
        _timer -= Time.fixedDeltaTime;
        if (_timer > 0f) return;
        switch (_stage)
        {
            case Stage.Darkroom: _stage = Stage.Shower; _timer = ShowerTime; break;
            case Stage.Shower: _stage = Stage.Deck; _timer = DeckTime; break;
            default:
                _won = true;
                WinCall.Send(0);
                if (AmongUsClient.Instance.AmHost) GameEnd.Win(this);
                break;
        }
    }

    private static readonly Net.RemoteCall<byte> WinCall = new("Sauner.Win", Net.Route.ToHost,
        (w, _) => w.Write((byte)0), r => r.ReadByte(), (sender, _) => { if (RoleState.Of(sender) is Sauner s && PortKit.Alive(sender)) GameEnd.Win(s); });

    private void UpdateArrow(bool inside, bool meeting)
    {
        _arrow ??= new WorldArrow(Lifespan, StageColor(_stage));
        if (meeting || inside || _won)
        {
            _arrow.SetActive(false);
            return;
        }
        Vector2 me = Player.transform.position;
        Vector2 best = default;
        float bestD = float.MaxValue;
        foreach (var t in Targets(_stage))
        {
            float d = (t - me).sqrMagnitude;
            if (d < bestD) { bestD = d; best = t; }
        }
        _arrow.SetActive(true);
        _arrow.Update(best);
    }

    private string _lastText;

    private void UpdateText(bool meeting)
    {
        int left = _timer < Limit(_stage) ? Mathf.Max(0, (int)(_timer + 1)) : -1;
        string stage = new Text($"Sauner.{_stage}").ToString();
        string action = new Text($"Sauner.{_stage}.Action").ToString();
        string text = $"{stage}: {action}" + (left >= 0 ? new Text("Sauner.Remaining").With("n", left).ToString() : "");
        if (text == _lastText) return;
        _lastText = text;
        var c = StageColor(_stage);
        StatusEffects.ShowUntil("Sauner", Text.Raw(text), "#" + ColorUtility.ToHtmlStringRGB(c), Text.Raw(""));
    }
}
