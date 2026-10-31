// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Crewmate/Phosphorus.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ホスフォラス (クルー): ランタンを置ける。ランタンは次のターンから全員に見え、「点灯」で周りを照らす
public sealed class Phosphorus : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#F9BC51";
    public override Text Name => new("Phosphorus.Name");
    public override string Reading => "ほすふぉらす";
    public override Text Blurb => new("Phosphorus.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Phosphorus.Put", "snr_lantern"), new("Phosphorus.Light", "snr_light") };
    public static readonly FloatOpt PutCooldown = new("Phosphorus.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt PutLimit = new("Phosphorus.Limit", 1, 1, 10);
    public static readonly FloatOpt LightCooldown = new("Phosphorus.LightCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt LightDuration = new("Phosphorus.Duration", 10f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt LightRange = new("Phosphorus.Range", 0.5f, 0.1f, 5f, 0.1f, "x");

    private sealed class Lantern
    {
        public GameObject Go;
        public SpriteRenderer Rend, Light;
        public bool Active;
    }

    private readonly List<Lantern> _lanterns = new();
    private static Sprite _mask;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Phosphorus.Put"), "snr_lantern")
        {
            Cooldown = () => PutCooldown,
            MaxUses = () => PutLimit,
            OnUse = () =>
            {
                var at = Player.GetTruePosition();
                PutCall.Send(at);
                Put(at);
                return true;
            },
        });
        AddAbility(new Ability(new Text("Phosphorus.Light"), "snr_light")
        {
            Cooldown = () => LightCooldown,
            Duration = () => LightDuration,
            Cancellable = true,
            OnUse = () =>
            {
                if (!_lanterns.Exists(l => l.Active)) return false;
                LightCall.Send(true);
                Lighting(true);
                return true;
            },
            OnEnd = () => { LightCall.Send(false); Lighting(false); },
        });
        Lifespan.OnRelease(() => { foreach (var l in _lanterns) if (l.Go) Object.Destroy(l.Go); _lanterns.Clear(); });
    }

    // 会議の後: 置いたランタンを全員に見えるようにする
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        foreach (var l in _lanterns)
        {
            if (!l.Go) continue;
            l.Rend.color = UnityEngine.Color.white;
            l.Active = true;
        }
    }

    private static readonly RemoteCall<Vector2> PutCall = new("Phosphorus.Put", Route.Anyone,
        (w, p) => { w.Write(p.x); w.Write(p.y); }, r => new Vector2(r.ReadSingle(), r.ReadSingle()),
        (sender, p) => { if (RoleState.Of(sender) is Phosphorus ph) ph.Put(p); });
    private static readonly RemoteCall<bool> LightCall = new("Phosphorus.Light", Route.Anyone,
        (w, on) => w.Write(on), r => r.ReadBoolean(),
        (sender, on) => { if (RoleState.Of(sender) is Phosphorus ph) ph.Lighting(on); });

    // 全員の端末で: ランタンを置く (置いた本人には薄く見え、ほかの人には見えない)
    private void Put(Vector2 at)
    {
        var go = new GameObject("MrpLantern");
        go.transform.position = new Vector3(at.x, at.y, at.y / 1000f + 0.001f);
        go.layer = 9;
        var rend = go.AddComponent<SpriteRenderer>();
        rend.sprite = ButtonIcons.Load("snr_lantern", 440f, new Vector2(0.5f, 0.5f));
        rend.color = new Color(1f, 1f, 1f, IsLocal ? 0.5f : 0f);
        var light = MakeLight(at, LightRange);
        light.transform.SetParent(go.transform, true);
        _lanterns.Add(new Lantern { Go = go, Rend = rend, Light = light });
    }

    private void Lighting(bool on)
    {
        foreach (var l in _lanterns)
        {
            if (!l.Go || !l.Active) continue;
            l.Light.enabled = on;
            l.Rend.color = on ? new Color(1f, 0.95f, 0.6f) : UnityEngine.Color.white;
        }
    }

    // 影の層に置いて、本編の視界の影を切り抜く光
    private static SpriteRenderer MakeLight(Vector2 pos, float range)
    {
        float size = ShipStatus.Instance.MaxLightRadius * range * 6f;
        var go = new GameObject("MrpLanternLight");
        go.transform.position = new Vector3(pos.x, pos.y, -50f);
        go.transform.localScale = new Vector3(size, size, 1f);
        go.layer = LayerMask.NameToLayer("Shadow");
        var r = go.AddComponent<SpriteRenderer>();
        r.sprite = Mask();
        r.material.shader = PlayerControl.LocalPlayer.LightPrefab.LightCutawayMaterial.shader;
        r.enabled = false;
        return r;
    }

    // 真ん中ほど濃い丸 (1 単位の大きさ)
    private static Sprite Mask()
    {
        if (_mask) return _mask;
        const int n = 128;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(n / 2f, n / 2f)) / (n / 2f);
                float a = Mathf.Clamp01((1f - d) * 3f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        tex.Apply();
        _mask = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        _mask.hideFlags |= HideFlags.HideAndDontSave;
        return _mask;
    }
}
