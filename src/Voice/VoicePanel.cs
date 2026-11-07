using System;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Voice;

// ボイスチャットの窓 (マイクの印を押す (PC は左下・スマホは右上) / キー): マイク・ミュート・聞く・マイクの大きさ・ボイスチェンジャー・人ごとの聞こえる大きさ。
// どれも自分だけの設定
internal static class VoicePanel
{
    private const string Name = "Voice";
    private const float WinW = 7.4f, WinH = 5.2f, InnerW = 6.8f, SliderW = 2.8f;
    private static readonly Color TitleColor = new(1f, 0.85f, 0.35f);
    private static readonly Color SubText = new(0.78f, 0.8f, 0.86f, 1f);
    private static readonly Color TabColor = new(0.5f, 0.85f, 0.75f);
    private static readonly string[] ChangerKeys = { "VoicePanel.FxNone", "VoicePanel.FxHigh", "VoicePanel.FxLow", "VoicePanel.FxRobot", "VoicePanel.FxRadio" };

    public static bool IsOpen => UiWindow.Current != null && UiWindow.Current.Root && UiWindow.Current.Root.name == "MrpWindow_" + Name;

    internal static void Toggle()
    {
        if (IsOpen) UiWindow.Current.Close();
        else Open();
    }

    internal static void Open()
    {
        var w = UiWindow.Open(Name, new Vector2(WinW, WinH), VoiceSettings.Save);
        if (w == null) return;
        Build();
    }

    private static void Build()
    {
        var w = UiWindow.Current;
        if (w == null || !IsOpen) return;
        var col = new Stack(false) { Gap = 0.12f, Width = InnerW };
        string key = Menu.KeyHints.SlotName(Menu.Keybinds.Slot.VoicePanel);
        col.Add(new Stack(true,
            new Label(new Text("VoicePanel.Title"), 2.4f) { MenuFont = true, Bold = true, Color = TitleColor },
            new Spacer(0.2f, 0.1f),
#if ANDROID
            new Label(new Text("VoicePanel.SubAndroid"), 1.3f) { Color = SubText }) { Gap = 0.1f, Align = Align.Start });
#else
            new Label(new Text("VoicePanel.Sub").With("key", key), 1.3f) { Color = SubText }) { Gap = 0.1f, Align = Align.Start });
#endif

        col.Add(new Stack(true,
            Toggle(new Text("VoicePanel.Mic"), () => Menu.ClientOptions.VoiceMic.Value, v => Menu.ClientOptions.VoiceMic.Value = v),
            new Spacer(0.2f, 0.1f),
            Toggle(new Text("VoicePanel.Mute"), () => VoiceChat.Muted, v => { if (VoiceChat.Muted != v) VoiceChat.ToggleMute(); }),
            new Spacer(0.2f, 0.1f),
            Toggle(new Text("VoicePanel.Hear"), () => Menu.ClientOptions.VoiceHear.Value, v => Menu.ClientOptions.VoiceHear.Value = v),
            new Spacer(0.2f, 0.1f),
            Toggle(new Text("VoicePanel.Denoise"), () => VoiceSettings.Denoise, v => VoiceSettings.Denoise = v)) { Gap = 0.1f, Align = Align.Start });

        // マイクの大きさ (50〜300%・10% 刻み)
        var gainText = new Label(VoiceSettings.MicGainPercent + "%", 1.4f) { Width = 0.7f };
        float span = VoiceSettings.MaxGain - VoiceSettings.MinGain;
        col.Add(new Stack(true,
            new Label(new Text("VoicePanel.MicGain"), 1.4f) { Bold = true, Width = 1.6f },
            new Slider(SliderW, (VoiceSettings.MicGainPercent - VoiceSettings.MinGain) / span, v =>
            {
                int pct = VoiceSettings.MinGain + Mathf.RoundToInt(v * span / 10f) * 10;
                VoiceSettings.MicGainPercent = pct;
                if (gainText.Built) gainText.Built.text = pct + "%";
                return (pct - VoiceSettings.MinGain) / span;
            }),
            gainText) { Gap = 0.15f, Align = Align.Start });

        var fx = new Stack(true, new Label(new Text("VoicePanel.Changer"), 1.4f) { Bold = true, Width = 1.6f }) { Gap = 0.08f, Align = Align.Start };
        for (int i = 0; i < ChangerKeys.Length; i++) fx.Add(FxTab(i));
        col.Add(fx);

        col.Add(new Label(new Text("VoicePanel.Players"), 1.4f) { Bold = true });
        var rows = new Stack(false) { Gap = 0.06f, Width = InnerW - 0.3f };
        int n = 0;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.AmOwner || p.Data == null || p.Data.Disconnected) continue;
            n++;
            var pc = p;
            var pctText = new Label(VoiceSettings.PercentOf(pc) + "%", 1.4f) { Width = 0.7f };
            rows.Add(new Plate(new Stack(true,
                new Label(pc.Data.PlayerName, 1.45f) { Bold = true, Width = 2.2f, NoWrap = true, MinFontSize = 1f },
                new Slider(SliderW, VoiceSettings.PercentOf(pc) / (float)VoiceSettings.MaxVolume, v =>
                {
                    int pct = Mathf.RoundToInt(v * VoiceSettings.MaxVolume / 10f) * 10;
                    VoiceSettings.SetPercent(pc, pct);
                    if (pctText.Built) pctText.Built.text = pct + "%";
                    return pct / (float)VoiceSettings.MaxVolume;
                }),
                pctText) { Gap = 0.15f, Align = Align.Start }) { Width = InnerW - 0.3f });
        }
        if (n == 0) rows.Add(new Label(new Text("VoicePanel.NoPlayers"), 1.4f) { Color = SubText });
        col.Add(new ScrollView(new Vector2(InnerW, 1.9f), rows) { Tag = "voice:players" });
        w.Body.Set(col);
    }

    private static Widget Toggle(string label, Func<bool> get, Action<bool> set)
    {
        Switch sw = null;
        sw = new Switch(get(), () =>
        {
            set(!get());
            sw.SetOn(get());
        });
        return new Stack(true, new Label(label, 1.4f) { Bold = true }, sw) { Gap = 0.12f, Align = Align.Center };
    }

    private static Widget FxTab(int i)
    {
        bool on = VoiceSettings.Changer == i;
        var label = new Label(new Text(ChangerKeys[i]), 1.3f) { Bold = true, TextAlign = TextAlignmentOptions.Center, Color = on ? new Color(0.08f, 0.08f, 0.1f) : TabColor };
        var b = new Button(label, () => { VoiceSettings.Changer = i; Build(); })
            { Size = new Vector2(0f, 0.4f), MinSize = new Vector2(0.9f, 0.4f), Padding = new Vector2(0.1f, 0.04f), Color = TabColor };
        if (on) b.Fill = TabColor;
        return b;
    }

    // 確かめ用 (bridge `voicepanel [open|close|fx N|vol <id> <pct>|gain <pct>]`)
    internal static string Command(string[] a)
    {
        if (a.Length >= 1 && a[0] == "open") Open();
        else if (a.Length >= 1 && a[0] == "close") { if (IsOpen) UiWindow.Current.Close(); }
        else if (a.Length == 2 && a[0] == "fx" && int.TryParse(a[1], out int fx)) { VoiceSettings.Changer = fx; if (IsOpen) Build(); }
        else if (a.Length == 2 && a[0] == "gain" && int.TryParse(a[1], out int g)) VoiceSettings.MicGainPercent = g;
        else if (a.Length == 3 && a[0] == "vol" && byte.TryParse(a[1], out byte id) && int.TryParse(a[2], out int pct))
        {
            foreach (var p in PlayerControl.AllPlayerControls)
                if (p && p.PlayerId == id) VoiceSettings.SetPercent(p, pct);
            VoiceSettings.Save();
        }
        else if (a.Length == 2 && a[0] == "denoise") VoiceSettings.Denoise = a[1] == "on";
        return $"open={IsOpen} fx={VoiceSettings.Changer} gain={VoiceSettings.MicGainPercent} denoise={VoiceSettings.Denoise}";
    }
}
