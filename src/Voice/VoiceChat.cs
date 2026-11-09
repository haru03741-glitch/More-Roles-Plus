using System;
using System.Collections.Generic;
using Hazel;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Voice;

// ボイスチャット (ホストの設定で 近く / 会議だけ / いつも全員 を選ぶ)。
// - 話す: マイク (各自の MRP の設定で ON にした人だけ) を 48kHz のまま、声のある間だけ 0.06 秒ごとに Opus (32kbps・全帯域) で全員へ送る。
//   公式サーバーは同じ種類の電文を毎秒十数本出すと切るので、MRP のサーバーと LAN の部屋でだけ使う。送りは信頼性なし (遅れた声は要らない)
// - 聞く: 話し手ごとに輪になった音の入れ物を鳴らし続け、届いた声を少し溜めてから再生位置の少し先へ書き足す。
//   音の大きさは 聞こえるか × 距離 (VoiceRules) × 自分で決めたその人の音量 (VoiceSettings)
// - 生きている人の声は死んだ人にも届く・死んだ人の声は死んだ人にだけ届く
[Settings(Tab.General, "VoiceChat.Title", order: -50)]
public static class VoiceChat
{
    public static readonly ChoiceOpt Mode = new("VoiceChat.Mode", 0,
        new Text("VoiceChat.Off"), new Text("VoiceChat.Proximity"), new Text("VoiceChat.MeetingOnly"), new Text("VoiceChat.Everyone"));
    public static readonly IntOpt Range = new("VoiceChat.Range", 6, 2, 20, 1);
    public static readonly BoolOpt InVent = new("VoiceChat.InVent", true); // ベントの中の人も話せる・聞こえる
    // 陣営の無線: キー (既定 Y) で切り替えると、自分の声は同じ陣営の仲間 (インポスター同士) にだけ、どこにいても届く。
    // 聞く側には誰の声か出さない (「仲間の無線」とだけ出す)
    public static readonly BoolOpt TeamRadio = new("VoiceChat.TeamRadio", false);
    // 話している人を見せる (頭の上の ♪ と左下の名前の一覧)。切ると誰の声か分からない
    public static readonly BoolOpt ShowSpeaker = new("VoiceChat.ShowSpeaker", true);

    internal const int Rate = 48000;
    internal const int PacketSamples = 2880; // 0.06 秒
    private const byte Format = 5;           // 声の形 (版が違って形の違う声は鳴らさない)
    private const byte VoiceRpc = 212;

    // 使える部屋か (MRP のサーバーか LAN)
    internal static bool Allowed
    {
        get
        {
            var c = AmongUsClient.Instance;
            if (!c || Mode.Value == 0 || !PlayerControl.LocalPlayer) return false;
            if (c.GameState != InnerNet.InnerNetClient.GameStates.Joined && c.GameState != InnerNet.InnerNetClient.GameStates.Started) return false;
            return c.NetworkMode == NetworkModes.LocalGame || c.NetworkMode == NetworkModes.OnlineGame && MrpRegion.IsCurrent();
        }
    }

    // FixedUpdate から
    internal static void Tick()
    {
        try
        {
            bool on = Allowed;
            Mic.Tick(on && Menu.ClientOptions.VoiceMic.Value && !Muted && (RadioOn || VoiceRules.CanTalk(PlayerControl.LocalPlayer)));
            Speakers.Tick(on && Menu.ClientOptions.VoiceHear.Value);
            VoiceHud.Tick(on);
            VoiceMarks.Tick(on);
        }
        catch (Exception e) { Plugin.Logger.LogError($"voice: {e}"); }
    }

    // ミュート (キーで切り替え・試合をまたいで続く)
    internal static bool Muted;

    // 陣営の無線を使っているか (キーで切り替え・試合が終わると切れる)
    internal static bool Radio;
    internal static bool RadioOn => Radio && VoiceRules.RadioTeam(PlayerControl.LocalPlayer);

    internal static void ToggleRadio()
    {
        if (!TeamRadio.Value || !VoiceRules.RadioTeam(PlayerControl.LocalPlayer)) { Radio = false; return; }
        Radio = !Radio;
        Plugin.Logger.LogInfo($"voice: team radio {(Radio ? "on" : "off")}");
    }

    internal static void ToggleMute()
    {
        Muted = !Muted;
        Plugin.Logger.LogInfo($"voice: {(Muted ? "muted" : "unmuted")}");
    }

    // ---- 送る ----

    internal static void Send(byte[] data, int length, ushort seq)
    {
        var lp = PlayerControl.LocalPlayer;
        var c = AmongUsClient.Instance;
        if (!lp || !c) return;
        // (long) 必須: net10 (Android) では int がポインタ受けのコンストラクタに解決される
        var payload = new Il2CppStructArray<byte>((long)length);
        for (int i = 0; i < length; i++) payload[i] = data[i];
        var w = c.StartRpcImmediately(lp.NetId, VoiceRpc, SendOption.None, -1);
        w.Write(seq);
        w.Write(Format);
        w.Write(RadioOn);
        w.WriteBytesAndSize(payload);
        c.FinishRpcImmediately(w);
        Sent++;
    }

    internal static int Sent, Received;
    internal static int LoopTo = -1; // 試験用: 自分の声をこの番号の人の声として手元で鳴らす (-1 = しない)

    // 試験用 (ブリッジ): voice [loop <番号>|noloop|mute|mic on|off|tone <番号>]
    internal static void RegisterBridge()
    {
        Bridge.TestBridge.Register("voice", "[loop <番号> | noloop | mute | tone <番号>] ボイスチャットの状態 / loop = 自分の声をその人の声として手元で鳴らす / tone = その人から 440Hz が届いたことにする", (args, reply) =>
        {
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 2 && a[0] == "loop" && int.TryParse(a[1], out int id)) LoopTo = id;
            else if (a.Length == 1 && a[0] == "noloop") LoopTo = -1;
            else if (a.Length == 1 && a[0] == "mute") ToggleMute();
            else if (a.Length == 2 && a[0] == "mic") Menu.ClientOptions.VoiceMic.Value = a[1] == "on";
#if ANDROID
            else if (a.Length == 2 && a[0] == "preset" && int.TryParse(a[1], out int preset)) Mic.Reopen(preset);
#endif
            else if (a.Length == 2 && a[0] == "tone" && byte.TryParse(a[1], out byte pid))
            {
                var pcm = new float[PacketSamples];
                for (int i = 0; i < pcm.Length; i++) pcm[i] = 0.3f * Mathf.Sin(2f * Mathf.PI * 440f * i / Rate);
                var pkt = new byte[OpusCodec.MaxPacket];
                int n = OpusCodec.Encode(pcm, pkt);
                var exact = new byte[n];
                Array.Copy(pkt, exact, n);
                for (int k = 0; k < 5; k++) Speakers.Push(pid, (ushort)(_toneSeq++), exact);
            }
            reply("OK " + Describe());
        });
        Bridge.TestBridge.Register("voicepanel", "[open | close | fx <0-4> | gain <%> | vol <番号> <%>] ボイスチャットの窓・自分の設定",
            (args, reply) => reply("OK " + VoicePanel.Command(args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))));
        Bridge.TestBridge.Register("rec", "[start | stop [wait] | info | peek | flip | voice] 録画",
            (args, reply) => reply(Menu.Recorder.Command(args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))));
        Bridge.TestBridge.Register("joinlists", "[open | close | tab <0-3> | del <i> | code <フレンドコード> [名前]] BAN・ホワイトリスト・VIP・モデレーターの一覧の窓",
            (args, reply) => reply("OK " + Menu.JoinListPanel.Command(args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))));
    }

    private static int _toneSeq = 1;

    internal static bool IsVoice(byte callId) => callId == VoiceRpc;

    internal static void Receive(PlayerControl sender, MessageReader r)
    {
        if (!sender || sender.AmOwner) return;
        ushort seq = r.ReadUInt16();
        if (r.ReadByte() != Format) return;
        bool radio = r.ReadBoolean();
        byte[] bytes = r.ReadBytesAndSize();
        Received++;
        if (!Allowed || !Menu.ClientOptions.VoiceHear.Value) return;
        Speakers.Push(sender.PlayerId, seq, bytes, radio);
    }

    internal static string Describe() =>
        $"voice mode={Mode.Value} allowed={Allowed} mic={Mic.Describe()} muted={Muted} sent={Sent} recv={Received} speakers={Speakers.Describe()}";
}

// 誰が誰に聞こえるか・どれくらいの大きさか
internal static class VoiceRules
{
    private static bool InMeeting => MeetingHud.Instance || ExileController.Instance;
    private static bool InLobby => !ShipStatus.Instance;

    private static bool Dead(PlayerControl p) => p && p.Data != null && p.Data.IsDead;

    // 話せるか (誰にも聞こえない時はマイクを開けない)
    internal static bool CanTalk(PlayerControl me)
    {
        if (!me || me.Data == null) return false;
        if (InLobby || InMeeting || Dead(me)) return true;
        return VoiceChat.Mode.Value != 2; // 会議だけ: 試合中の生きている人は話せない
    }

    // 陣営の無線に入れる人: 試合中・会議の外で生きているインポスター (本編の役の陣営は全員の端末が知っている)
    internal static bool RadioTeam(PlayerControl p) =>
        VoiceChat.TeamRadio.Value && p && p.Data != null && !p.Data.IsDead && p.Data.Role && p.Data.Role.IsImpostor && !InLobby && !InMeeting;

    // 無線の声: 仲間にはどこでも同じ大きさで、仲間でない人には聞こえない
    internal static float RadioVolume(PlayerControl listener, PlayerControl speaker) =>
        RadioTeam(listener) && RadioTeam(speaker) ? 1f : 0f;

    // 0〜1 (0 = 聞こえない)
    internal static float Volume(PlayerControl listener, PlayerControl speaker)
    {
        if (!listener || !speaker || speaker.Data == null || listener.Data == null) return 0f;
        if (InLobby) return 1f;
        bool deadSpeaker = Dead(speaker), deadListener = Dead(listener);
        if (deadSpeaker && !deadListener) return 0f;   // 死んだ人の声は生きている人に届かない
        if (InMeeting || deadSpeaker) return 1f;
        // ベントの中の人は話せない・聞こえない (設定で切った時)
        if (!VoiceChat.InVent && (listener.inVent || speaker.inVent)) return 0f;
        switch (VoiceChat.Mode.Value)
        {
            case 2: return 0f; // 会議だけ
            case 3: return 1f; // いつも全員
        }
        // 近く: 範囲の半分までは同じ大きさ、そこから範囲の端で 0 へ
        float range = Math.Max(1, VoiceChat.Range.Value);
        float d = Vector2.Distance(listener.GetTruePosition(), speaker.GetTruePosition());
        return Mathf.Clamp01(1f - (d - range * 0.5f) / (range * 0.5f));
    }
}

// マイクから読み、声のある間だけ送る。
// 48kHz で開いてそのまま使い (48kHz で開けなかった時だけ速さをそろえる。落とす時は窓付き sinc で高い所を切って折り返しの雑音を出さない)、低い唸りを切り、自分で決めた大きさ・雑音消し・声の変え方をかけ、
// 大きすぎる所は柔らかく抑える。声が始まった時は直前の 1 包みも送る (言い始めが切れない)
internal static class Mic
{
    private const int ClipSeconds = 1;
    internal const float VoiceLevel = 0.012f; // これより大きい 20ms があれば声とみなす (RMS)
    private const int HangoverPackets = 5;    // 声が止んでもこの数だけ送り続ける (語尾を切らない)
    private const int Window = VoiceChat.Rate / 50; // 声かを見る長さ (20ms)

#if ANDROID
    // Android 版の Unity には Microphone が無いので、C++ の部品から端末の AAudio を直接読む
    private static bool _native;
    private static bool _failed;   // 開けなかった (許可が無い等)。マイクを一度切って入れ直すまで開き直さない
    private static float _retryAt; // 録音が途中で切れた時は少し待ってから開き直す
    private static readonly float[] _buf = new float[2048];
    private static long _readTotal; // 読めた数 (テストの状態表示)
    private static float _rawPeak;  // 加工前の一番大きな値 (テストの状態表示)
    private static float _levelMax; // 20ms の大きさの一番大きな値 (テストの状態表示)
    // 録音の種類 (AAudio の input preset)。1 = 一般。通話用 (7) は端末によって声が 1/10 ほどに小さくなり送る大きさに届かず、
    // 音声認識用 (6) は近くで話すと割れる (Xperia 10 IV 実測)
    private static int _preset = 1;

    // 録音の種類を変えて開き直す (テスト用)
    internal static void Reopen(int preset)
    {
        _preset = preset;
        _readTotal = 0;
        _rawPeak = _levelMax = 0f;
        Stop();
        _failed = false;
    }
    internal static bool Open => _native;
    internal static bool Available => !_failed;
#else
    private static AudioClip _clip;
    private static int _readPos;
    private static Il2CppStructArray<float> _buf;
    internal static bool Open => _clip;
    internal static bool Available => Microphone.devices != null && Microphone.devices.Length > 0;
#endif
    private static int _rate, _chunk;
    private const int CaptureRate = 48000;
    private const int Taps = 47;                  // 落とす前の低域の濾し (窓付き sinc)
    private static float[] _fir;
    private static readonly float[] Hist = new float[Taps];
    private static int _histPos;
    private static float _phase, _prev;
    private static float _hpX, _hpY;             // 低い唸りを切る (直流よけ) の前の値
    private static readonly float[] Frame = new float[VoiceChat.PacketSamples];
    private static readonly float[] Last = new float[VoiceChat.PacketSamples]; // 送らなかった直前の包み
    private static bool _lastKept;
    private static int _frameLen, _hang, _winN;
    private static double _winSum;
    private static bool _voiced;
    private static ushort _seq;
    private static readonly byte[] Packet = new byte[OpusCodec.MaxPacket];
    private static readonly float HpCoef = VoiceFx.OnePoleHp(60f);
    private static float _lastLevel;
    internal static bool Talking { get; private set; } // 今送っているか (画面の印)
    internal static float Level => Open ? _lastLevel : 0f; // 直近の 20ms の大きさ (RMS・画面の音量のバー)

#if ANDROID
    internal static unsafe void Tick(bool want)
    {
        if (!want)
        {
            Stop();
            _failed = false;
            _retryAt = 0f;
            return;
        }
        if (!_native) Start();
        if (!_native) return;
        while (true)
        {
            int n;
            fixed (float* p = _buf) n = Native.MrpNative.MicRead(p, _buf.Length);
            if (n < 0)
            {
                Stop();
                _retryAt = Time.realtimeSinceStartup + 2f;
                return;
            }
            if (n > 0)
            {
                _readTotal += n;
                for (int i = 0; i < n; i++) _rawPeak = Math.Max(_rawPeak, Math.Abs(_buf[i]));
                Feed(n);
            }
            if (n < _buf.Length) return;
        }
    }

    private static unsafe void Start()
    {
        if (_failed || (_retryAt > 0f && Time.realtimeSinceStartup < _retryAt)) return;
        int rate = Native.MrpNative.MicOpen(CaptureRate, _preset);
        if (rate <= 0)
        {
            _failed = true;
            Plugin.Logger.LogInfo($"voice: mic unavailable ({rate})");
            return;
        }
        _native = true;
        _rate = rate;
        Begin();
        Plugin.Logger.LogInfo($"voice: mic started ({_rate}Hz, AAudio)");
    }

    private static unsafe void Stop()
    {
        Talking = false;
        if (!_native) return;
        Native.MrpNative.MicClose();
        _native = false;
        Plugin.Logger.LogInfo("voice: mic stopped");
    }
#else
    internal static void Tick(bool want)
    {
        if (!want)
        {
            Stop();
            return;
        }
        if (!_clip) Start();
        if (!_clip) return;
        int pos = Microphone.GetPosition(null);
        int len = _clip.samples;
        int avail = (pos - _readPos + len) % len;
        while (avail >= _chunk)
        {
            _clip.GetData(_buf, _readPos);
            _readPos = (_readPos + _chunk) % len;
            avail -= _chunk;
            Feed(_chunk);
        }
    }

    private static void Start()
    {
        if (!Available) return;
        _clip = Microphone.Start(null, true, ClipSeconds, CaptureRate);
        if (!_clip) return;
        _rate = _clip.frequency > 0 ? _clip.frequency : CaptureRate;
        _chunk = _rate / 50; // 20ms
        // 輪の長さが 20ms で割り切れない時は読む位置が端をまたがないように、割り切れる大きさにそろえる
        while (_clip.samples % _chunk != 0 && _chunk > 1) _chunk--;
        _buf = new Il2CppStructArray<float>((long)_chunk);
        _readPos = Microphone.GetPosition(null);
        Begin();
        Plugin.Logger.LogInfo($"voice: mic started ({_rate}Hz, device '{Microphone.devices[0]}')");
    }

    private static void Stop()
    {
        Talking = false;
        if (!_clip) return;
        Microphone.End(null);
        Object.Destroy(_clip);
        _clip = null;
        Plugin.Logger.LogInfo("voice: mic stopped");
    }
#endif

    // 開いた直後に、速さに合わせた濾しを作り、前の録音の名残を消す
    private static void Begin()
    {
        _fir = _rate > VoiceChat.Rate ? Lowpass(0.45f * VoiceChat.Rate / _rate) : null;
        Array.Clear(Hist, 0, Hist.Length);
        _histPos = 0;
        _frameLen = 0;
        _hang = 0;
        _phase = _prev = 0f;
        _lastKept = false;
        VoiceFx.Reset();
        VoiceDenoise.Reset();
        OpusCodec.ResetEncoder();
    }

    // 窓付き sinc (Blackman) の低域の濾し。cutoff は入りの速さに対する割合
    private static float[] Lowpass(float cutoff)
    {
        var h = new float[Taps];
        int m = Taps - 1;
        float sum = 0f;
        for (int i = 0; i < Taps; i++)
        {
            float n = i - m / 2f;
            float sinc = n == 0f ? 2f * cutoff : MathF.Sin(2f * MathF.PI * cutoff * n) / (MathF.PI * n);
            float w = 0.42f - 0.5f * MathF.Cos(2f * MathF.PI * i / m) + 0.08f * MathF.Cos(4f * MathF.PI * i / m);
            h[i] = sinc * w;
            sum += h[i];
        }
        for (int i = 0; i < Taps; i++) h[i] /= sum;
        return h;
    }

    // 20ms 分を送る速さ (48kHz) にそろえる (落とす時は濾してから、どちらも前の値との間を直線でつなぐ)
    private static void Feed(int count)
    {
        float step = (float)_rate / VoiceChat.Rate;
        for (int i = 0; i < count; i++)
        {
            float s = _buf[i];
            if (_fir != null)
            {
                Hist[_histPos] = s;
                _histPos = (_histPos + 1) % Taps;
                float acc = 0f;
                int k = _histPos;
                for (int j = 0; j < Taps; j++)
                {
                    acc += _fir[j] * Hist[k];
                    if (++k == Taps) k = 0;
                }
                s = acc;
            }
            while (_phase < 1f)
            {
                Process(_prev + (s - _prev) * _phase);
                _phase += step;
            }
            _phase -= 1f;
            _prev = s;
        }
    }

    private static void Process(float x)
    {
        // 直流と低い唸りを切る (約 60Hz より下)
        float y = x - _hpX + HpCoef * _hpY;
        _hpX = x;
        _hpY = y;
        y *= VoiceSettings.MicGain;
        if (VoiceSettings.Denoise) y = VoiceDenoise.Next(y);
        y = VoiceFx.Apply(y);
        // 柔らかく抑える (割れた音にしない)
        y = y * (27f + y * y) / (27f + 9f * y * y);
        y = Math.Clamp(y, -1f, 1f);

        _winSum += y * y;
        if (++_winN >= Window)
        {
            float level = (float)Math.Sqrt(_winSum / _winN);
            _lastLevel = level;
#if ANDROID
            if (level > _levelMax) _levelMax = level;
#endif
            if (level >= VoiceLevel) _voiced = true;
            _winSum = 0;
            _winN = 0;
        }
        Frame[_frameLen++] = y;
        if (_frameLen == Frame.Length) Flush();
    }

    private static void Flush()
    {
        bool start = _voiced && _hang <= 0;
        if (_voiced) _hang = HangoverPackets;
        bool send = _voiced || _hang-- > 0;
        _voiced = false;
        _frameLen = 0;
        Talking = send;
        if (!send)
        {
            _hang = Math.Max(_hang, 0);
            Array.Copy(Frame, Last, Frame.Length);
            _lastKept = true;
            return;
        }
        if (start && _lastKept) SendFrame(Last);
        _lastKept = false;
        SendFrame(Frame);
    }

    private static void SendFrame(float[] pcm)
    {
        int len = OpusCodec.Encode(pcm, Packet);
        if (len <= 0) return;
        VoiceChat.Send(Packet, len, _seq);
        if (VoiceChat.LoopTo >= 0)
        {
            var exact = new byte[len];
            Array.Copy(Packet, exact, len);
            Speakers.Push((byte)VoiceChat.LoopTo, _seq, exact);
        }
        _seq++;
    }

#if ANDROID
    internal static string Describe() => (_native ? $"on {_rate}Hz level={_lastLevel:0.000} talking={Talking}" : "off") + $" aaudio={(_failed ? "failed" : "ok")} preset={_preset} read={_readTotal} peak={_rawPeak:0.0000} lvmax={_levelMax:0.0000}";
#else
    internal static string Describe() => (_clip ? $"on {_rate}Hz level={_lastLevel:0.000} talking={Talking}" : "off") + $" devices={Microphone.devices?.Length ?? 0}";
#endif
}

// 話し手ごとの鳴らし手。届いた声は話し手ごとの Opus の復号器で戻し、少し溜めてから鳴らし始め (届く間隔の揺れで途切れない)、
// 溜めが尽きたら滑らかに消して、また溜まるのを待つ。包みが抜けた時は、次の包みの控え (FEC) と前の声の続き (PLC) で埋める
internal static class Speakers
{
    private const int ClipSamples = VoiceChat.Rate * 2;       // 輪は 2 秒
    private const int Lead = VoiceChat.Rate * 8 / 100;        // 再生位置の 0.08 秒先まで書いておく
    private const int Prefill = VoiceChat.Rate * 15 / 100;    // 0.15 秒溜まってから鳴らし始める
    private const long PrefillWaitMs = 220;                    // 溜まらなくてもこれだけ待ったら鳴らす (短い一言)
    private const int MaxQueue = VoiceChat.Rate * 45 / 100;   // 溜めすぎ (遅れ) はこの長さまで
    private const int TrimTo = VoiceChat.Rate * 25 / 100;     // 溜めすぎた時はここまで古い方を捨てる
    private const int Chunk = VoiceChat.Rate / 50;             // 20ms ずつ書く

    private sealed class Speaker
    {
        public byte PlayerId;
        public GameObject Go;
        public AudioSource Source;
        public AudioClip Clip;
        public int Write;
        public readonly float[] Buf = new float[VoiceChat.Rate]; // 溜め (輪)
        public int Head, Count;
        public bool Playing;
        public long WaitSince;
        public float Tail; // 最後に書いた値 (尽きた時はここから 0 へ下げる)
        public long LastMs;
        public ushort LastSeq;
        public OpusCodec.Decoder Dec;
        public bool Hidden; // 録画に入れないように混ぜ口を外しているか
        public bool Radio;  // 直前の声は陣営の無線か
    }

    private static readonly Dictionary<byte, Speaker> All = new();
    private static readonly float[] Decoded = new float[VoiceChat.PacketSamples];
    private static Il2CppStructArray<float> _chunk;
    private static GameObject _root;

    internal static void Push(byte playerId, ushort seq, byte[] bytes, bool radio = false)
    {
        if (!All.TryGetValue(playerId, out var s) || !s.Go) s = Make(playerId);
        if (s == null) return;
        s.Radio = radio;
        // 古い順番の物 (遅れて届いた物) は捨てる。16bit の番号の一回りは考える
        if (s.LastMs != 0 && (short)(seq - s.LastSeq) <= 0 && Environment.TickCount64 - s.LastMs < 3000) return;
        int gap = s.LastMs != 0 && Environment.TickCount64 - s.LastMs < 1000 ? (short)(seq - s.LastSeq) : 1;
        s.LastSeq = seq;
        s.LastMs = Environment.TickCount64;
        if (s.Count == 0 && !s.Playing) s.WaitSince = s.LastMs;
        // 抜けた包み (3 つまで): 古い方は前の声の続きで、直前の 1 つはこの包みに入っている控えから戻す
        if (gap > 1 && gap <= 4)
        {
            for (int k = 0; k < gap - 2; k++) Append(s, s.Dec.Conceal(Decoded));
            Append(s, s.Dec.Recover(bytes, Decoded));
        }
        Append(s, s.Dec.Decode(bytes, Decoded));
        if (s.Count > MaxQueue)
        {
            int drop = s.Count - TrimTo;
            s.Head = (s.Head + drop) % s.Buf.Length;
            s.Count -= drop;
        }
    }

    private static void Append(Speaker s, int n)
    {
        var buf = s.Buf;
        for (int i = 0; i < n; i++)
        {
            if (s.Count == buf.Length) { s.Head = (s.Head + 1) % buf.Length; s.Count--; }
            buf[(s.Head + s.Count) % buf.Length] = Decoded[i];
            s.Count++;
        }
    }

    internal static bool Speaking(byte playerId) =>
        All.TryGetValue(playerId, out var s) && Environment.TickCount64 - s.LastMs < 300;

    internal static bool SpeakingRadio(byte playerId) =>
        All.TryGetValue(playerId, out var s) && s.Radio && Environment.TickCount64 - s.LastMs < 300;

    internal static void Tick(bool on)
    {
        if (!on)
        {
            if (All.Count > 0) Clear();
            return;
        }
        var me = PlayerControl.LocalPlayer;
        _chunk ??= new Il2CppStructArray<float>((long)Chunk);
        long now = Environment.TickCount64;
        List<byte> gone = null;
        bool hide = Menu.Recorder.HideVoice;
        float sfx = hide && AmongUs.Data.DataManager.Settings?.Audio != null ? AmongUs.Data.DataManager.Settings.Audio.SfxVolume : 1f;
        foreach (var s in All.Values)
        {
            var p = Player(s.PlayerId);
            if (!s.Go || !p)
            {
                (gone ??= new List<byte>()).Add(s.PlayerId);
                continue;
            }
            // 聞こえ方 (距離など) × 自分で決めたその人の音量。1 を超える分は音そのものを大きくする
            float vol = (s.Radio ? VoiceRules.RadioVolume(me, p) : VoiceRules.Volume(me, p)) * VoiceSettings.VolumeOf(p);
            // 録画に声を入れない時は、本編の効果音の混ぜ口を通さず (混ぜ口の出口は録画の部品の手前を通る)、録画の部品も飛ばして鳴らす。
            // 混ぜ口の効果音の大きさは自分で掛ける
            if (hide != s.Hidden)
            {
                s.Hidden = hide;
                s.Source.outputAudioMixerGroup = hide ? null : SfxGroup();
                s.Source.bypassListenerEffects = hide;
            }
            s.Source.volume = Math.Min(vol, 1f) * (hide ? sfx : 1f);
            float boost = Math.Max(vol, 1f);
            if (!s.Playing && s.Count > 0 && (s.Count >= Prefill || now - s.WaitSince >= PrefillWaitMs)) s.Playing = true;
            int play = s.Source.timeSamples;
            int ahead = (s.Write - play + ClipSamples) % ClipSamples;
            // 書いた所を追い越された (止まっていた時など) ら、再生位置のすぐ先から書き直す
            if (ahead > ClipSamples - Lead * 2) { s.Write = play; ahead = 0; }
            while (ahead < Lead)
            {
                for (int i = 0; i < Chunk; i++)
                {
                    float v;
                    if (s.Playing && s.Count > 0)
                    {
                        v = Math.Clamp(s.Buf[s.Head] * boost, -1f, 1f);
                        s.Head = (s.Head + 1) % s.Buf.Length;
                        s.Count--;
                        s.Tail = v;
                    }
                    else
                    {
                        // 尽きた: 最後の値から滑らかに 0 へ (ぷつっと鳴らさない)。次はまた溜まるのを待つ
                        if (s.Playing) { s.Playing = false; s.WaitSince = now; }
                        s.Tail *= 0.92f;
                        v = s.Tail;
                    }
                    _chunk[i] = v;
                }
                s.Clip.SetData(_chunk, s.Write);
                s.Write = (s.Write + Chunk) % ClipSamples;
                ahead += Chunk;
            }
        }
        if (gone != null) foreach (byte id in gone) Remove(id);
    }

    private static PlayerControl Player(byte id)
    {
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p && p.PlayerId == id) return p;
        return null;
    }

    private static Speaker Make(byte playerId)
    {
        if (!_root)
        {
            _root = new GameObject("MrpVoice");
            Object.DontDestroyOnLoad(_root);
        }
        var go = new GameObject("Speaker" + playerId);
        go.transform.SetParent(_root.transform, false);
        var src = go.AddComponent<AudioSource>();
        var clip = AudioClip.Create("MrpVoice" + playerId, ClipSamples, 1, VoiceChat.Rate, false);
        src.clip = clip;
        src.loop = true;
        src.spatialBlend = 0f;
        src.volume = 0f;
        src.outputAudioMixerGroup = SfxGroup(); // 本編の効果音の大きさに従う
        src.Play();
        var s = new Speaker { PlayerId = playerId, Go = go, Source = src, Clip = clip, Write = 0, Dec = new OpusCodec.Decoder() };
        All[playerId] = s;
        return s;
    }

    private static UnityEngine.Audio.AudioMixerGroup SfxGroup()
    {
        var sm = SoundManager.Instance;
        return sm && sm.sfxMixer ? sm.sfxMixer : null;
    }

    private static void Remove(byte id)
    {
        if (!All.TryGetValue(id, out var s)) return;
        All.Remove(id);
        if (s.Go) Object.Destroy(s.Go);
        if (s.Clip) Object.Destroy(s.Clip);
        s.Dec?.Dispose();
    }

    internal static void Clear()
    {
        foreach (byte id in new List<byte>(All.Keys)) Remove(id);
    }

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in All.Values) sb.Append($" {s.PlayerId}:vol={(s.Source ? s.Source.volume : 0):0.00},q={s.Count},play={s.Playing}");
        return All.Count + sb.ToString();
    }
}

// Opus (C++ の部品の libopus)。送り手は符号器を 1 つ、受け手は話し手ごとに復号器を 1 つ
internal static unsafe class OpusCodec
{
    // 1ch の話し声なら Opus はこれで十分聞き分けられる。声はサーバーが部屋の全員へ配るので、
    // サーバーの出口は 話し手 × (人数 - 1) 倍になる (96kbps だと 15 人で数人話すだけで数 Mbps)
    internal const int Bitrate = 32000;
    internal const int MaxPacket = 1000; // 包みの上限 (送りの 1 通に収める。超えそうな時は Opus がこの中に収める)

    private static void* _enc;

    internal static void ResetEncoder()
    {
        if (_enc != null) Native.MrpNative.OpusEncClose(_enc);
        _enc = null;
    }

    // 戻り = 包みのバイト数 (0 = 作れなかった)
    internal static int Encode(float[] pcm, byte[] dst)
    {
        if (_enc == null) _enc = Native.MrpNative.OpusEncOpen(VoiceChat.Rate, Bitrate);
        if (_enc == null) return 0;
        int n;
        fixed (float* p = pcm)
        fixed (byte* d = dst)
            n = Native.MrpNative.OpusEnc(_enc, p, pcm.Length, d, Math.Min(dst.Length, MaxPacket));
        return n > 0 ? n : 0;
    }

    internal sealed class Decoder : IDisposable
    {
        private void* _dec = Native.MrpNative.OpusDecOpen(VoiceChat.Rate);

        // 届いた包みを戻す。戻り = 点の数
        internal int Decode(byte[] src, float[] pcm) => Run(src, pcm, pcm.Length, 0);

        // 抜けた包みの分を前の声の続きで作る
        internal int Conceal(float[] pcm) => Run(null, pcm, VoiceChat.PacketSamples, 0);

        // 直前に抜けた包みの分を、この包みに入っている控えから戻す (控えが無ければ続きで作る)
        internal int Recover(byte[] next, float[] pcm) => Run(next, pcm, VoiceChat.PacketSamples, 1);

        private int Run(byte[] src, float[] pcm, int max, int fec)
        {
            if (_dec == null) return 0;
            int n;
            fixed (byte* s = src)
            fixed (float* p = pcm)
                n = Native.MrpNative.OpusDec(_dec, s, src?.Length ?? 0, p, Math.Min(max, pcm.Length), fec);
            return n > 0 ? n : 0;
        }

        public void Dispose()
        {
            if (_dec != null) Native.MrpNative.OpusDecClose(_dec);
            _dec = null;
        }
    }
}
