using System;
using AmongUs.GameOptions;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Builtin;

// 本編の役職 (素のクルーとインポスター・エンジニア・シェイプシフターなど) を MRP の役職と同じ並びで扱う。
// - 出現率と人数は MRP の設定 (役職タブ・保存・同期も MRP の役職と同じ)。配るのも MRP の配り方
//   (上限・同時に出ない組・「役職が付かなかった人」の埋め方が本編の役職にも効く)。
// - 役職ごとの細かい設定 (クールダウンなど) は本編の設定のまま。本編の役職の処理は各端末で本編の設定から読むので、
//   画面の行は本編の設定を直接読み書きし、本編の同期で全員へ届ける (VanillaOpt)。
// - 幽霊になってから付く役職 (守護天使など) は本編が死んだ時に配るので、MRP は配らず、出現率と人数を本編の設定へ写す。
// - 表示 (イントロ・名前の上・タスク欄) は本編のまま。
public abstract class VanillaRole : RoleBase
{
    public abstract RoleTypes Type { get; }
    protected abstract Text FallbackName { get; }

    public override string Id => Type.ToString();
    public override Team Team => IsImpostorType(Type) ? Team.Impostor : Team.Crew;
    public override string Color => Team == Team.Impostor ? "#FF1919" : "#8CFFFF";
    public override RoleTypes BaseRole => Type;
    internal override bool IsVanilla => true;
    internal override bool AssignedOnDeath => Type is RoleTypes.GuardianAngel or RoleTypes.SpiritGuide;

    // 名前と説明は本編の訳 (言語の設定に合わせて本編が出す文)。本編の役職がまだ読めない起動直後は控えの名前
    public override Text Name
    {
        get
        {
            var b = Behaviour();
            if (!b) return FallbackName;
            return Text.Raw(b.NiceName);
        }
    }

    public override Text Blurb
    {
        get
        {
            var b = Behaviour();
            if (!b) return new Text("VanillaRole.DefaultBlurb");
            return Text.Raw(b.Blurb);
        }
    }

    // 文のファイルに部品 (<Id>.Summary) があれば MRP の役職と同じ組み立て、無ければ本編の長い説明
    public override Text Help
    {
        get
        {
            if (RoleDoc.Has(this)) return base.Help;
            var b = Behaviour();
            if (!b || !TranslationController.InstanceExists) return Blurb;
            return Text.Raw(TranslationController.Instance.GetString(b.BlurbNameLong));
        }
    }

    public override string Reading => FallbackName.Builtin(true);

    // 節の絵は本編の物: "@ability" = 役職の能力ボタン・"@viper" = バイパーのキルの絵・"@kill" / "@sabotage" / "@vent" / "@report" / "@use" = HUD のボタン
    public override Sprite SectionIcon(HelpSection s)
    {
        switch (s.Icon)
        {
            case "@ability":
                var ab = Behaviour()?.Ability;
                return ab != null ? ab.Image : null;
            case "@viper":
                var vb = Behaviour();
                var viper = vb ? vb.TryCast<ViperRole>() : null;
                return viper && viper.killSprite ? viper.killSprite : Hud(h => h.KillButton);
            case "@kill": return Hud(h => h.KillButton);
            case "@sabotage": return Hud(h => h.SabotageButton);
            case "@vent": return Hud(h => h.ImpostorVentButton);
            case "@report": return Hud(h => h.ReportButton);
            case "@use": return Hud(h => h.UseButton);
            default: return base.SectionIcon(s);
        }
    }

    private static Sprite Hud(Func<HudManager, ActionButton> pick)
    {
        var hud = Vanilla.Hud;
        if (!hud) return null;
        var b = pick(hud);
        return b && b.graphic ? b.graphic.sprite : null;
    }

    internal RoleBehaviour Behaviour()
    {
        var rm = RoleManager.Instance;
        if (!rm || rm.AllRoles == null) return null;
        foreach (var r in rm.AllRoles)
            if (r && r.Role == Type) return r;
        return null;
    }

    private static bool IsImpostorType(RoleTypes t) => t is RoleTypes.Impostor or RoleTypes.Shapeshifter or RoleTypes.Phantom or RoleTypes.Viper;

    // 素のクルーとインポスター。本編の配り方を通らないので出現率と人数を本編の設定へ写さない
    internal bool IsPlain => Type is RoleTypes.Crewmate or RoleTypes.Impostor;
}

public sealed class VanillaCrewmate : VanillaRole
{
    public override RoleTypes Type => RoleTypes.Crewmate;
    protected override Text FallbackName => new("VanillaCrewmate.Name");
    public override HelpSection[] HelpSections => new HelpSection[] { new("Crewmate.Task", "@use"), new("Crewmate.Report", "@report") };
}

public sealed class VanillaImpostor : VanillaRole
{
    public override RoleTypes Type => RoleTypes.Impostor;
    protected override Text FallbackName => new("VanillaImpostor.Name");
    public override HelpSection[] HelpSections => new HelpSection[] { new("Impostor.Kill", "@kill"), new("Impostor.Sabotage", "@sabotage"), new("Impostor.Vent", "@vent") };
}

public sealed class VanillaScientist : VanillaRole
{
    public override RoleTypes Type => RoleTypes.Scientist;
    protected override Text FallbackName => new("VanillaScientist.Name");
    public override HelpSection[] HelpSections => new HelpSection[] { new("Scientist.Vitals", "@ability") };
}

public sealed class VanillaEngineer : VanillaRole
{
    public override RoleTypes Type => RoleTypes.Engineer;
    protected override Text FallbackName => new("VanillaEngineer.Name");
    public override HelpSection[] HelpSections => new HelpSection[] { new("Engineer.Vent", "@ability") };
}

public sealed class VanillaNoisemaker : VanillaRole
{
    public override RoleTypes Type => RoleTypes.Noisemaker;
    protected override Text FallbackName => new("VanillaNoisemaker.Name");
    public override HelpSection[] HelpSections => new HelpSection[] { new("Noisemaker.Alert") };
}

public sealed class VanillaTracker : VanillaRole
{
    public override RoleTypes Type => RoleTypes.Tracker;
    protected override Text FallbackName => new("VanillaTracker.Name");
    public override HelpSection[] HelpSections => new HelpSection[] { new("Tracker.Track", "@ability") };
}

public sealed class VanillaDetective : VanillaRole
{
    public override RoleTypes Type => RoleTypes.Detective;
    protected override Text FallbackName => new("VanillaDetective.Name");
    public override HelpSection[] HelpSections => new HelpSection[] { new("Detective.Notes", "@ability"), new("Detective.Interrogate") };
}

public sealed class VanillaJudge : VanillaRole
{
    public override RoleTypes Type => RoleTypes.Judge;
    protected override Text FallbackName => new("VanillaJudge.Name");
    public override HelpSection[] HelpSections => new HelpSection[] { new("Judge.Overrule") };
}

public sealed class VanillaGuardianAngel : VanillaRole
{
    public override RoleTypes Type => RoleTypes.GuardianAngel;
    protected override Text FallbackName => new("VanillaGuardianAngel.Name");
    public override HelpSection[] HelpSections => new HelpSection[] { new("GuardianAngel.Protect", "@ability") };
}

public sealed class VanillaSpiritGuide : VanillaRole
{
    public override RoleTypes Type => RoleTypes.SpiritGuide;
    protected override Text FallbackName => new("VanillaSpiritGuide.Name");
    public override HelpSection[] HelpSections => new HelpSection[] { new("SpiritGuide.Message", "@ability") };
}

public sealed class VanillaShapeshifter : VanillaRole
{
    public override RoleTypes Type => RoleTypes.Shapeshifter;
    protected override Text FallbackName => new("VanillaShapeshifter.Name");
    public override HelpSection[] HelpSections => new HelpSection[] { new("Shapeshifter.Shift", "@ability") };
}

public sealed class VanillaPhantom : VanillaRole
{
    public override RoleTypes Type => RoleTypes.Phantom;
    protected override Text FallbackName => new("VanillaPhantom.Name");
    public override HelpSection[] HelpSections => new HelpSection[] { new("Phantom.Vanish", "@ability") };
}

public sealed class VanillaViper : VanillaRole
{
    public override RoleTypes Type => RoleTypes.Viper;
    protected override Text FallbackName => new("VanillaViper.Name");
    public override HelpSection[] HelpSections => new HelpSection[] { new("Viper.Acid", "@viper") };
}

// 本編の設定 1 つを読み書きする行。値は本編の設定にあり、MRP の保存・同期には入れない
internal sealed class VanillaOpt : Opt
{
    private readonly BaseGameSetting _setting;
    private readonly FloatGameSetting _float;
    private readonly IntGameSetting _int;
    private readonly CheckboxGameSetting _check;
    private readonly StringGameSetting _string;
    private readonly StringNames[] _values; // 文字の選択肢 (StringGameSetting)
    private readonly float _min, _step;
    private readonly int _count;
    private readonly bool _impostors; // インポスターの人数: 上限は接続先と部屋の定員で変わる
    internal bool IsImpostorCount => _impostors;

    private VanillaOpt(Text label, BaseGameSetting s, FloatGameSetting f, IntGameSetting i, CheckboxGameSetting c, StringGameSetting str) : base(label, 0)
    {
        _setting = s;
        _float = f;
        _int = i;
        _check = c;
        _string = str;
        _impostors = s.Title == StringNames.GameNumImpostors;
        if (str != null)
        {
            var v = str.Values;
            _values = new StringNames[v != null ? v.Length : 0];
            for (int k = 0; k < _values.Length; k++) _values[k] = v[k];
            _count = _values.Length;
        }
        else if (f != null)
        {
            _min = f.ValidRange.min;
            _step = f.Increment > 0f ? f.Increment : 1f;
            _count = (int)Math.Round((f.ValidRange.max - _min) / _step) + 1;
        }
        else if (i != null)
        {
            _min = i.ValidRange.min;
            _step = Math.Max(i.Increment, 1);
            _count = (int)Math.Round((i.ValidRange.max - _min) / _step) + 1;
        }
        else _count = 2;
        _count = Math.Max(_count, 1);
    }

    // 本編の設定の定義から作る (数値・整数・ON/OFF・文字の選択肢。それ以外は出さない)
    public static VanillaOpt From(BaseGameSetting s)
    {
        if (!s) return null;
        var f = s.TryCast<FloatGameSetting>();
        var i = f == null ? s.TryCast<IntGameSetting>() : null;
        var c = f == null && i == null ? s.TryCast<CheckboxGameSetting>() : null;
        var str = f == null && i == null && c == null ? s.TryCast<StringGameSetting>() : null;
        if (f == null && i == null && c == null && str == null) return null;
        // インポスターの数は最少 (ImpostorRange.Min) と組で最多として使う
        if (s.Title == StringNames.GameNumImpostors) return new VanillaOpt(ImpostorRange.MaxLabel, s, f, i, c, str);
        string label = TranslationController.Instance ? TranslationController.Instance.GetString(s.Title) : s.Title.ToString();
        return new VanillaOpt(Text.Raw(label), s, f, i, c, str);
    }

    internal override int Count
    {
        get
        {
            if (!_impostors) return _count;
            int max = ImpostorCap.MaxFor(ImpostorCap.RoomSize(Options));
            return Math.Max(1, (int)Math.Round((max - _min) / _step) + 1);
        }
    }

    internal override bool IsToggle => _check != null;
    internal override bool HasDefault => false;

    private static IGameOptions Options => GameOptionsManager.Instance?.CurrentGameOptions;

#pragma warning disable CS0618 // TryGet〜 は out 引数が il2cpp を跨ぐので、値を返す方を使う
    private float Read()
    {
        var o = Options;
        if (o == null) return 0f;
        if (_float != null) return o.GetFloat(_float.OptionName);
        if (_int != null) return o.GetInt(_int.OptionName);
        if (_string != null) return o.GetInt(_string.OptionName);
        return o.GetBool(_check.OptionName) ? 1f : 0f;
    }
#pragma warning restore CS0618

    // 本編の設定から番号を読み直す
    internal override void Sync() => Pull();

    public void Pull()
    {
        float v = Read();
        Index = _check != null ? (v != 0f ? 1 : 0)
            : _string != null ? Math.Clamp((int)v, 0, Math.Max(_count - 1, 0))
            : Math.Clamp((int)Math.Round((v - _min) / _step), 0, Count - 1);
    }

    private float ValueOf(int index) => _min + index * _step;

    internal override string Display()
    {
        Pull();
        if (_check != null) return Index != 0 ? "ON" : "OFF";
        if (_string != null)
        {
            if (Index >= _values.Length) return "-";
            return TranslationController.Instance ? TranslationController.Instance.GetString(_values[Index]) : _values[Index].ToString();
        }
        return _setting.GetValueString(ValueOf(Index));
    }

    protected override bool Wraps => _check != null;

    // 押されたら本編の設定へ書く (同期と保存は画面を閉じた時に本編の仕組みで)
    protected override void OnStepped()
    {
        var o = Options;
        if (o == null) return;
        if (_float != null) o.SetFloat(_float.OptionName, ValueOf(Index));
        else if (_int != null) o.SetInt(_int.OptionName, (int)Math.Round(ValueOf(Index)));
        else if (_string != null) o.SetInt(_string.OptionName, Index);
        else o.SetBool(_check.OptionName, Index != 0);
        VanillaRoles.Dirty = true;
    }
}

internal static class VanillaRoles
{
    // 本編の設定を画面で変えた (閉じた時に本編の保存と同期を回す)
    public static bool Dirty;

    private static bool _attached;

    // 本編の役職の細かい設定の行を、役職の見出しの下に足す (本編の役職が読めるようになってから 1 回だけ)
    public static void EnsureSettings()
    {
        if (_attached || !RoleManager.Instance) return;
        _attached = true;
        foreach (var tab in new[] { Tab.Crew, Tab.Impostor })
        {
            foreach (var sec in Registry.SectionsOf(tab))
            {
                if (sec.Role is not VanillaRole v) continue;
                var b = v.Behaviour();
                if (!b || b.AllGameSettings == null) continue;
                foreach (var s in b.AllGameSettings)
                {
                    var opt = VanillaOpt.From(s);
                    if (opt == null) continue;
                    opt.Key = v.Id + "." + s.Title; // 見分け用 (保存・同期の名前の表には入れない)
                    sec.Opts.Add(opt);
                }
            }
        }
    }

    // ホストが配る直前: 本編の配り方には役職を配らせない (MRP が配る)。幽霊になってから付く役職は MRP の出現率と人数を写す
    public static void PrepareVanillaSelection()
    {
        var opts = GameOptionsManager.Instance?.CurrentGameOptions;
        if (opts == null) return;
        var roleOptions = opts.RoleOptions;
        foreach (var r in Registry.Roles)
        {
            if (r is not VanillaRole v || v.IsPlain) continue;
            if (v.AssignedOnDeath) roleOptions.SetRoleRate(v.Type, v.Count.Value, v.Chance.Value);
            else roleOptions.SetRoleRate(v.Type, 0, 0);
        }
    }

    // 設定画面を閉じた時 (ホスト): 本編の設定を変えていれば本編の保存と同期を回す
    public static void OnMenuClosed()
    {
        if (!Dirty) return;
        Dirty = false;
        try
        {
            GameOptionsManager.Instance.SaveNormalHostOptions();
            if (GameManager.Instance) GameManager.Instance.LogicOptions.SyncOptions();
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"vanilla options sync: {e.Message}"); }
    }
}
