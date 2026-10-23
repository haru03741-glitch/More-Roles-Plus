using System;
using System.Collections.Generic;
using Hazel;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 世界を数秒前に戻す (時戻し)。各端末が StepMs ごとに輪の中へ今の状態を書いておき、ホストの合図で全員が同じ秒数だけ戻る。
// 持ち主ごとに分けて書く:
//   自分の端末だけ = 自分の体の位置・タスクの進み・キルの待ち時間・本編の役職の待ち時間・MRP のボタンの待ち時間と効果・役職の手元の値
//   全員の端末     = 能力の残り回数 (Player の能力は全員の端末で同じ数を持つ)
//   ホストだけ     = サボタージュの系統・生き死に・客の依頼を確かめる時刻
// 位置は本編の SnapTo を使わず Teleport.Self (自分の位置の電文) で戻す。生き返りは誰がそうかをホストが決めて合図に入れる。
// 会議は越えない (会議の始まりで輪を捨てる)。記録は試合に時戻しがいる時だけ書く。輪の配列は最初に 1 回だけ確保して使い回す
internal static class TimeRewind
{
    internal const int StepMs = 250;
    private const int Slots = 100;         // 25 秒分 (戻す秒数の上限 15 + 押してから依頼までの上限 8 + 余裕)
    internal const int MaxElapsedMs = 8000; // 押してから依頼を出すまで (発動者の演出の間) として数える上限
    private const int MaxTasks = 24, MaxData = 48;
    private const int MaxAbilities = 64, MaxRoleFloats = 64, MaxTracked = 128;
    private const int MaxSystems = 16, SysBytes = 1024;
    private const int RoleFloatsPer = 3;   // 本編の役職の待ち時間などの欄

    private static bool _on;
    private static int _head, _count;      // 次に書く所・書いてある数
    private static long _nextMs, _poseNextMs;
    private static int _shipGen = -1;

    private static readonly long[] SlotMs = new long[Slots];
    // ---- 自分 ----
    private static readonly float[] PosX = new float[Slots], PosY = new float[Slots], Kill = new float[Slots];
    private static readonly bool[] Alive = new bool[Slots];
    private static readonly bool[] Hold = new bool[Slots];      // はしご・ジップライン・昇降機の途中 (位置が床ではない)
    private static readonly int[] InVent = new int[Slots];      // 入っていたベントの番号 (-1 = 外)
    private static readonly float[] VRole = new float[Slots * RoleFloatsPer];
    private static readonly IntPtr[] VRolePtr = new IntPtr[Slots];
    private static readonly int[] TaskN = new int[Slots];
    private static readonly uint[] TaskId = new uint[Slots * MaxTasks];
    private static readonly int[] TaskStep = new int[Slots * MaxTasks];
    private static readonly byte[] TaskTimerState = new byte[Slots * MaxTasks];
    private static readonly float[] TaskTimer = new float[Slots * MaxTasks];
    private static readonly bool[] TaskDone = new bool[Slots * MaxTasks];
    private static readonly byte[] TaskDataLen = new byte[Slots * MaxTasks];
    private static readonly byte[] TaskData = new byte[Slots * MaxTasks * MaxData];
    // ---- 全員 (能力と役職の値。並びは RoleState.All の順。人の数が変わったら古い記録は使わない) ----
    private static readonly int[] Layout = new int[Slots];
    private static int _layout;
    private static int _layoutCount = -1;
    private static readonly int[] AbUses = new int[Slots * MaxAbilities];
    private static readonly float[] AbLeft = new float[Slots * MaxAbilities], AbEffect = new float[Slots * MaxAbilities];
    private static readonly long[] AbHostMs = new long[Slots * MaxAbilities];
    private static readonly float[] RoleFloats = new float[Slots * MaxRoleFloats];
    private static readonly long[] TrackedBits = new long[Slots * MaxTracked];
    // 生き死に (ホストが抜けても次のホストが戻せるよう全員の端末で)
    private static readonly ulong[] DeadBits = new ulong[Slots * 4];
    // ---- ホスト ----
    private static readonly long[] KillMs = new long[Slots * 256];
    private static readonly SystemTypes[] SysTypes = new SystemTypes[MaxSystems];
    private static readonly ISystemType[] Systems = new ISystemType[MaxSystems];
    private static int _sysN;
    private static readonly byte[] SysData = new byte[Slots * SysBytes];
    private static readonly short[] SysEnd = new short[Slots * MaxSystems];
    private static readonly bool[] SysOk = new bool[Slots];
    private static Il2CppStructArray<byte> _sysScratch;

    // 戻すサボタージュの系統 (部屋の中の装置の使用中の印 = 監視カメラ・医務室・ベントは戻さない)
    private static readonly SystemTypes[] Restorable =
    {
        SystemTypes.Reactor, SystemTypes.Laboratory, SystemTypes.LifeSupp, SystemTypes.Electrical, SystemTypes.Comms,
        SystemTypes.Sabotage, SystemTypes.Doors, SystemTypes.HeliSabotage, SystemTypes.MushroomMixupSabotage,
    };

    private static readonly List<NormalPlayerTask> Tasks = new();
    private static readonly List<NetworkedPlayerInfo.TaskInfo> TaskInfos = new(); // 完了の印 (数えるのはこちら)
    private static int _tasksCount = -1;
    private static IntPtr _tasksOwner, _tasksLast;
    private static readonly List<uint> Undone = new();

    // 確かめ用
    internal static int Rewinds, LastSlotAgeMs, LastRevived, LastTasksUndone, LastSystems, LastGone;
    internal static string LastNote = "-";

    // 合図 (ホスト → 全員): 戻す秒数 (ms)・生き返らせる人・倒れた体を片付ける抜けた人 (戻し先では生きていた)
    private readonly struct Go
    {
        public readonly ushort BackMs;
        public readonly byte[] Revive, Gone;
        public Go(ushort backMs, byte[] revive, byte[] gone) { BackMs = backMs; Revive = revive; Gone = gone; }
    }

    private static readonly RemoteCall<Go> GoCall = new("TimeRewind.Go2", Route.HostToAll,
        (w, g) =>
        {
            w.Write(g.BackMs);
            WriteIds(w, g.Revive);
            WriteIds(w, g.Gone);
        },
        r => new Go(r.ReadUInt16(), ReadIds(r), ReadIds(r)),
        (_, g) => Apply(g));

    private static void WriteIds(MessageWriter w, byte[] ids)
    {
        w.Write((byte)ids.Length);
        foreach (var b in ids) w.Write(b);
    }

    private static byte[] ReadIds(MessageReader r)
    {
        var ids = new byte[r.ReadByte()];
        for (int i = 0; i < ids.Length; i++) ids[i] = r.ReadByte();
        return ids;
    }

    // 戻したタスクの完了の印を外す (持ち主 → 全員)
    private static readonly RemoteCall<uint[]> UndoCall = new("TimeRewind.TaskUndo", Route.Anyone,
        (w, ids) => { w.Write((byte)ids.Length); foreach (var id in ids) w.Write(id); },
        r =>
        {
            var ids = new uint[r.ReadByte()];
            for (int i = 0; i < ids.Length; i++) ids[i] = r.ReadUInt32();
            return ids;
        },
        (sender, ids) => UndoTasks(sender, ids));

    // 時戻しがいる試合で、役職の寿命が切れるまで記録する (全員の端末で)
    internal static void Enable(Lifespan life)
    {
        RewindWorldFx.Prewarm();
        if (_on) return;
        _on = true;
        Clear();
        Rewinds = LastSlotAgeMs = LastRevived = LastTasksUndone = LastSystems = LastGone = 0;
        LastNote = "-";
        Events<MeetingStartEvent>.Subscribe(_ => Clear(), life, owner: "TimeRewind");
        Terrain.TerrainRewind.Enable(life, Crew.Rewinder.Seconds + MaxElapsedMs / 1000f);
        life.OnRelease(() =>
        {
            _on = false;
            if (_playing) Finish();
            Clear();
        });
    }

    internal static void Clear()
    {
        _pend = PendNone;
        RewindPlayback.Clear();
        RewindJournal.Clear();
        _head = _count = 0;
        _poseNextMs = 0;
        _nextMs = 0;
        _tasksCount = -1;
        _layoutCount = -1;
        Tasks.Clear();
        TaskInfos.Clear();
        for (int i = 0; i < MaxSystems; i++) Systems[i] = null;
        _sysN = 0;
        _shipGen = -1;
    }

    internal static bool On => _on;

    // 今から戻せる一番古い所までの秒数
    internal static float Span => _count == 0 ? 0f : (Environment.TickCount64 - SlotMs[Oldest]) / 1000f;

    private static int Oldest => (_head - _count + Slots) % Slots;

    // ---- 記録 ----

    // 毎 FixedUpdate
    internal static void Tick()
    {
        if (!_on) return;
        long now = Environment.TickCount64;
        if (_playing) PlayStep(now);
        else if (now >= _poseNextMs)
        {
            _poseNextMs = now + RewindPlayback.StepMs;
            if (Recording())
            {
                try { RewindPlayback.Capture(now); }
                catch (Exception e) { Plugin.Logger.LogError($"TimeRewind pose: {e}"); }
            }
        }
        if (_pend != PendNone && !_playing)
        {
            try { PendStep(now); }
            catch (Exception e) { _pend = PendNone; Plugin.Logger.LogError($"TimeRewind pending: {e}"); }
        }
        if (now < _nextMs) return;
        _nextMs = now + StepMs;
        if (!Recording()) return;
        try { Capture(_head, now); }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"TimeRewind capture: {e}");
            return;
        }
        _head = (_head + 1) % Slots;
        if (_count < Slots) _count++;
    }

    private static bool Recording()
    {
        if (!Terrain.GameClock.ShipAlive || MeetingHud.Instance || ExileController.Instance) return false;
        var client = AmongUsClient.Instance;
        // フリープレイは本編の試合の状態が「始まった」にならない
        if (!client || !(client.IsGameStarted || client.NetworkMode == NetworkModes.FreePlay)) return false;
        if (_shipGen != Terrain.GameClock.ShipGen)
        {
            Clear();
            _shipGen = Terrain.GameClock.ShipGen;
        }
        return PlayerControl.LocalPlayer;
    }

    private static void Capture(int s, long now)
    {
        SlotMs[s] = now;
        var lp = PlayerControl.LocalPlayer;
        var pos = lp.transform.position;
        PosX[s] = pos.x;
        PosY[s] = pos.y;
        Kill[s] = lp.killTimer;
        Alive[s] = lp.Data != null && !lp.Data.IsDead;
        Hold[s] = lp.onLadder || lp.inMovingPlat;
        InVent[s] = VentOf(lp);
        CaptureVanillaRole(s, lp);
        CaptureTasks(s, lp);
        CaptureRoles(s);
        CaptureDead(s);
        if (AmongUsClient.Instance.AmHost) CaptureHost(s);
    }

    // 本編の役職の待ち時間 (本編の役職ごとに欄の名前が違う)
    private static void CaptureVanillaRole(int s, PlayerControl lp)
    {
        var rb = lp.Data != null ? lp.Data.Role : null;
        VRolePtr[s] = rb ? rb.Pointer : IntPtr.Zero;
        if (!rb) return;
        int o = s * RoleFloatsPer;
        switch (rb.Role)
        {
            case AmongUs.GameOptions.RoleTypes.Shapeshifter:
                VRole[o] = Typed<ShapeshifterRole>(rb).cooldownSecondsRemaining; break;
            case AmongUs.GameOptions.RoleTypes.Engineer:
                var en = Typed<EngineerRole>(rb);
                VRole[o] = en.cooldownSecondsRemaining;
                VRole[o + 1] = en.inVentTimeRemaining;
                break;
            case AmongUs.GameOptions.RoleTypes.Phantom:
                VRole[o] = Typed<PhantomRole>(rb).cooldownSecondsRemaining; break;
            case AmongUs.GameOptions.RoleTypes.Scientist:
                var sc = Typed<ScientistRole>(rb);
                VRole[o] = sc.currentCooldown;
                VRole[o + 1] = sc.currentCharge;
                break;
            case AmongUs.GameOptions.RoleTypes.Tracker:
                VRole[o] = Typed<TrackerRole>(rb).cooldownSecondsRemaining; break;
            case AmongUs.GameOptions.RoleTypes.Detective:
                VRole[o] = Typed<DetectiveRole>(rb).cooldownSecondsRemaining; break;
        }
    }

    // 本編の役職の型付きの包み (役職が替わった時だけ作り直す)
    private static IntPtr _typedPtr;
    private static object _typed;

    private static T Typed<T>(RoleBehaviour rb) where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
    {
        if (rb.Pointer != _typedPtr || _typed is not T)
        {
            _typedPtr = rb.Pointer;
            _typed = rb.Cast<T>();
        }
        return (T)_typed;
    }

    private static void RestoreVanillaRole(int s, PlayerControl lp)
    {
        var rb = lp.Data != null ? lp.Data.Role : null;
        if (!rb || rb.Pointer != VRolePtr[s]) return;
        int o = s * RoleFloatsPer;
        switch (rb.Role)
        {
            case AmongUs.GameOptions.RoleTypes.Shapeshifter:
                Typed<ShapeshifterRole>(rb).cooldownSecondsRemaining = VRole[o]; break;
            case AmongUs.GameOptions.RoleTypes.Engineer:
                var en = Typed<EngineerRole>(rb);
                en.cooldownSecondsRemaining = VRole[o];
                en.inVentTimeRemaining = VRole[o + 1];
                break;
            case AmongUs.GameOptions.RoleTypes.Phantom:
                Typed<PhantomRole>(rb).cooldownSecondsRemaining = VRole[o]; break;
            case AmongUs.GameOptions.RoleTypes.Scientist:
                var sc = Typed<ScientistRole>(rb);
                sc.currentCooldown = VRole[o];
                sc.currentCharge = VRole[o + 1];
                break;
            case AmongUs.GameOptions.RoleTypes.Tracker:
                Typed<TrackerRole>(rb).cooldownSecondsRemaining = VRole[o]; break;
            case AmongUs.GameOptions.RoleTypes.Detective:
                Typed<DetectiveRole>(rb).cooldownSecondsRemaining = VRole[o]; break;
        }
    }

    // 自分のタスクの進み。タスクの一覧はサボタージュの修理のタスクが出入りした時だけ作り直す
    private static void CaptureTasks(int s, PlayerControl lp)
    {
        var list = lp.myTasks;
        int n = list != null ? list.Count : 0;
        // 数が同じでも中身が入れ替わることがある (サボタージュの修理のタスクの出入り) ので、最後の要素も比べる
        IntPtr last = n > 0 && list[n - 1] ? list[n - 1].Pointer : IntPtr.Zero;
        if (n != _tasksCount || lp.Pointer != _tasksOwner || last != _tasksLast)
        {
            _tasksCount = n;
            _tasksOwner = lp.Pointer;
            _tasksLast = last;
            Tasks.Clear();
            TaskInfos.Clear();
            for (int i = 0; i < n; i++)
            {
                var t = list[i] ? list[i].TryCast<NormalPlayerTask>() : null;
                if (t == null || Tasks.Count >= MaxTasks) continue;
                Tasks.Add(t);
                TaskInfos.Add(lp.Data != null ? lp.Data.FindTaskById(t.Id) : null);
            }
        }
        int k = 0;
        for (int i = 0; i < Tasks.Count; i++)
        {
            var t = Tasks[i];
            if (!t) continue;
            int o = s * MaxTasks + k;
            TaskId[o] = t.Id;
            TaskStep[o] = t.taskStep;
            TaskTimerState[o] = (byte)t.TimerStarted;
            TaskTimer[o] = t.TaskTimer;
            var info = TaskInfos[i];
            TaskDone[o] = info != null ? info.Complete : t.IsComplete;
            TaskDataLen[o] = CopyOut(t.Data, TaskData, o * MaxData);
            k++;
        }
        TaskN[s] = k;
    }

    private static unsafe byte CopyOut(Il2CppStructArray<byte> src, byte[] dst, int at)
    {
        if (src == null) return 0;
        int n = Math.Min(src.Length, MaxData);
        byte* p = (byte*)Il2Array.Data(src);
        for (int i = 0; i < n; i++) dst[at + i] = p[i];
        return (byte)n;
    }

    private static unsafe void CopyIn(byte[] src, int at, int n, Il2CppStructArray<byte> dst)
    {
        if (dst == null) return;
        n = Math.Min(n, dst.Length);
        byte* p = (byte*)Il2Array.Data(dst);
        for (int i = 0; i < n; i++) p[i] = src[at + i];
    }

    // 能力の回数・ボタンの待ち時間・役職の手元の値 (全員の端末で全員の分)
    private static void CaptureRoles(int s)
    {
        var all = RoleState.All;
        var addons = RoleState.AllAddons;
        // 並びが変わったら (人数だけでなく役職の型も) 古い記録の欄は使わない
        int key = all.Count + addons.Count;
        foreach (var x in all) key = key * 31 + x.GetType().GetHashCode();
        foreach (var x in addons) key = key * 31 + x.GetType().GetHashCode();
        if (key != _layoutCount)
        {
            _layoutCount = key;
            _layout++;
        }
        Layout[s] = _layout;
        int a = 0, f = 0;
        foreach (var role in all)
        {
            foreach (var ab in role.Abilities)
            {
                if (a >= MaxAbilities) break;
                int o = s * MaxAbilities + a++;
                AbUses[o] = ab.UsesLeft;
                AbHostMs[o] = ab.HostLastMs;
                if (ab.Button != null)
                {
                    AbLeft[o] = ab.Button.Left;
                    AbEffect[o] = ab.Button.EffectLeft;
                }
            }
            int n = role.RewindFloats;
            if (n <= 0 || f + n > MaxRoleFloats) continue;
            role.SaveRewind(RoleFloats.AsSpan(s * MaxRoleFloats + f, n));
            f += n;
        }
        int k = 0;
        foreach (var role in all) SaveTracked(role, s, ref k);
        foreach (var addon in addons) SaveTracked(addon, s, ref k);
    }

    private static void SaveTracked(Assignable a, int s, ref int k)
    {
        foreach (var v in a.TrackedValues)
        {
            if (k >= MaxTracked) return;
            TrackedBits[s * MaxTracked + k++] = v.Save();
        }
    }

    private static void LoadTracked(Assignable a, int s, ref int k)
    {
        foreach (var v in a.TrackedValues)
        {
            if (k >= MaxTracked) return;
            long bits = TrackedBits[s * MaxTracked + k++];
            if (!v.Keep) v.Load(bits);
        }
    }

    // 戻し終わった後に役職とアドオンへ知らせる
    private static void NotifyRewound()
    {
        foreach (var role in RoleState.All) Notify(role);
        foreach (var addon in RoleState.AllAddons) Notify(addon);
    }

    private static void Notify(Assignable a)
    {
        try { a.OnRewound(); }
        catch (Exception e) { Plugin.Logger.LogError($"{a.Id}.OnRewound: {e}"); }
    }

    private static void CaptureDead(int s)
    {
        var gd = GameData.Instance;
        int d = s * 4;
        DeadBits[d] = DeadBits[d + 1] = DeadBits[d + 2] = DeadBits[d + 3] = 0;
        if (gd)
        {
            var all = gd.AllPlayers;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || !p.IsDead) continue;
                int id = p.PlayerId;
                DeadBits[d + (id >> 6)] |= 1UL << (id & 63);
            }
        }
    }

    private static void CaptureHost(int s)
    {
        KillAbility.CopyHostKillMs(KillMs.AsSpan(s * 256, 256), false);
        CaptureSystems(s);
    }

    private static void CaptureSystems(int s)
    {
        SysOk[s] = false;
        var ship = ShipStatus.Instance;
        if (!ship) return;
        if (_sysN == 0) FindSystems(ship);
        if (_sysN == 0) return;
        var w = MessageWriter.Get(SendOption.None);
        try
        {
            int start = w.Length;
            for (int i = 0; i < _sysN; i++)
            {
                // 記録で本編の同期の印 (IsDirty) を変えない (書き出しが印を下ろす系統があっても、客へ送る前に消さない)
                uint dirty = ReadDirty(Systems[i]);
                Systems[i].Serialize(w, true);
                WriteDirty(Systems[i], dirty);
                int len = w.Length - start;
                if (len > SysBytes) return; // 収まらない時はこの回の系統を戻さない
                SysEnd[s * MaxSystems + i] = (short)len;
            }
            CopyWriter(w, start, w.Length - start, SysData, s * SysBytes);
            SysOk[s] = true;
        }
        finally { w.Recycle(); }
    }

    private static unsafe void CopyWriter(MessageWriter w, int from, int n, byte[] dst, int at)
    {
        byte* p = (byte*)Il2Array.Data(w.Buffer) + from;
        for (int i = 0; i < n; i++) dst[at + i] = p[i];
    }

    private static void FindSystems(ShipStatus ship)
    {
        _sysN = 0;
        foreach (var t in Restorable)
        {
            if (_sysN >= MaxSystems) break;
            if (!ship.Systems.TryGetValue(t, out var sys) || sys == null) continue;
            SysTypes[_sysN] = t;
            Systems[_sysN] = sys;
            _sysN++;
        }
    }

    // ---- 戻す ----

    // 時戻しが押した (押した人の端末)。true = 依頼を出した
    internal static bool Ask() => Ask(0);

    // elapsedMs = 押してから今までの時間 (発動者の演出の間)。戻す区間は押した時刻から数える (上限 MaxElapsedMs)
    internal static bool Ask(int elapsedMs)
    {
        if (!_on || _count == 0) return false;
        AskCall.Send((ushort)Math.Clamp(elapsedMs, 0, MaxElapsedMs));
        return true;
    }

    // 依頼 (押した人 → ホスト)。押した人が生きた時戻しかはホストが確かめる
    private static readonly RemoteCall<ushort> AskCall = new("TimeRewind.Ask2", Route.ToHost,
        (w, v) => w.Write(v), r => r.ReadUInt16(), (sender, elapsed) => HostAsk(sender, elapsed));

    private static long _hostLastGo = long.MinValue / 2;
    private const long MinGapMs = 1500; // 続けて届いた依頼を 2 回効かせない

    private static void HostAsk(PlayerControl sender, ushort elapsedMs)
    {
        if (!sender || sender.Data == null || sender.Data.IsDead) return;
        if (RoleState.Of(sender) is not Crew.Rewinder rw) return;
        long now = Environment.TickCount64;
        // 回数と待ち時間を数えるのは、戻すと決まった時だけ
        if (now - _hostLastGo < MinGapMs || _playing || MeetingHud.Instance || _count == 0 || !rw.HostAllows()) return;
        _hostLastGo = now;
        HostGo(Crew.Rewinder.Seconds + Math.Min((int)elapsedMs, MaxElapsedMs) / 1000f);
    }

    private static void HostGo(float seconds)
    {
        ushort back = (ushort)Math.Clamp((int)(seconds * 1000f), 0, 60000);
        HostLists(Environment.TickCount64 - back, out var revive, out var gone);
        var go = new Go(back, revive, gone);
        GoCall.Send(go);
        Apply(go); // 配る電文は送った本人 (ホスト) の手元では動かない
        // 水・火・減圧・物は壊した結果と同じ列で配り、全員が同じ刻みで戻す
        Terrain.TerrainSync.HostRewind(back * Terrain.GameClock.Hz / 1000);
    }

    // 確かめ用 (ブリッジ rewind force): ホストが役職と待ち時間を見ずに戻す
    internal static bool HostForce(float seconds)
    {
        if (!_on || _count == 0 || _playing || !AmongUsClient.Instance.AmHost) return false;
        HostGo(seconds);
        return true;
    }

    // revive = 戻す時刻に生きていて今は死んでいる人。吸い出されて飛んでいる途中の人も入れる
    //   (飛び終わりの死は各端末で起きるので、合図より先に飛び終わった端末では生き返らせる)
    // gone = 戻す時刻に生きていたのに倒れた体が残っていて、生き返らせられない人 (抜けた人は本編の人の一覧から消える)。
    //   体は戻せない (本編は試合中に人を足せない) ので、倒れた体だけを片付ける
    private static void HostLists(long targetMs, out byte[] revive, out byte[] gone)
    {
        revive = gone = Array.Empty<byte>();
        int s = Find(targetMs);
        var gd = GameData.Instance;
        if (s < 0 || !gd) return;
        List<byte> rv = null, gn = null;
        int d = s * 4;
        foreach (var p in gd.AllPlayers)
        {
            if (p == null || p.Disconnected || !p.Object) continue;
            int id = p.PlayerId;
            if ((DeadBits[d + (id >> 6)] & (1UL << (id & 63))) != 0) continue;
            if (p.IsDead || Terrain.CrewGrip.Flying(p.PlayerId)) (rv ??= new List<byte>()).Add(p.PlayerId);
        }
        foreach (var body in UnityEngine.Object.FindObjectsOfType<DeadBody>())
        {
            if (!body) continue;
            byte id = body.ParentId;
            if ((DeadBits[d + (id >> 6)] & (1UL << (id & 63))) != 0 || (rv != null && rv.Contains(id))) continue;
            var p = gd.GetPlayerById(id);
            if (p != null && !p.Disconnected && p.Object) continue;
            gn ??= new List<byte>();
            if (!gn.Contains(id)) gn.Add(id);
        }
        if (rv != null) revive = rv.ToArray();
        if (gn != null) gone = gn.ToArray();
    }

    // targetMs 以前で一番新しい記録 (無ければ一番古い記録・何も無ければ -1)
    private static int Find(long targetMs)
    {
        if (_count == 0) return -1;
        for (int k = 1; k <= _count; k++)
        {
            int s = (_head - k + Slots) % Slots;
            if (SlotMs[s] <= targetMs) return s;
        }
        return Oldest;
    }

    // 逆再生の間 (合図を受けてから、全員の姿を控えに沿って逆にたどり終えるまで)。全員が止まり、終わりに今の戻しを当てる
    private static bool _playing, _froze;
    private static Go _play;
    private static long _playStart, _playEnd, _playSlotMs;
    private static int _playSlot;
    private static readonly ulong[] Revived = new ulong[4]; // 逆再生の途中で生き返らせた人
    private static readonly ulong[] Cleared = new ulong[4]; // 逆再生の途中で倒れた体を片付けた抜けた人

    internal static bool Playing => _playing;

    private static void Apply(Go g)
    {
        if (!_on || MeetingHud.Instance) return;
        if (_playing) Finish();
        long now = Environment.TickCount64;
        // 吸い出されて飛んでいる途中は、逆再生の間に飛び終わって死なないよう先に取り消す
        Step("flights", Terrain.CrewGrip.Rewound);
        _play = g;
        _playStart = now;
        int playMs = Terrain.TerrainRewind.PlayMs(g.BackMs);
        _playEnd = now + playMs;
        _playSlot = Find(now - g.BackMs);
        _playSlotMs = _playSlot >= 0 ? SlotMs[_playSlot] : 0;
        _playing = true;
        Revived[0] = Revived[1] = Revived[2] = Revived[3] = 0;
        Cleared[0] = Cleared[1] = Cleared[2] = Cleared[3] = 0;
        Step("pose", () => RewindPlayback.Begin(now));
        RewindJournal.Begin(_playEnd - (_playSlot >= 0 ? _playSlotMs : now - g.BackMs));
        var lp = PlayerControl.LocalPlayer;
        if (lp)
        {
            if (Minigame.Instance) Minigame.Instance.Close();
            // 本編の「動ける」を切ると、移動・キル・使う・通報・能力のボタンがまとめて止まる
            if (lp.moveable) { lp.moveable = false; _froze = true; }
        }
        RewindWorldFx.Play(false, playMs / 1000f);
    }

    // 毎 FixedUpdate (逆再生の間): 全員の体を控えの姿へ置き、死んだ時刻を越えた人をその場で生き返らせる
    private static void PlayStep(long now)
    {
        try { PlayStepCore(now); }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"TimeRewind play: {e}");
            Finish();
        }
    }

    private static void PlayStepCore(long now)
    {
        if (now >= _playEnd || MeetingHud.Instance || !Terrain.GameClock.ShipAlive) { Finish(); return; }
        if (_playSlot < 0) return;
        float u = (now - _playStart) / (float)(_playEnd - _playStart);
        u = RewindClock.World(u);
        long t = _playStart - (long)(u * (_playStart - _playSlotMs));
        RewindPlayback.Drive(t);
        RewindJournal.UndoAfter(t);
        foreach (var pid in _play.Gone)
        {
            ulong bit = 1UL << (pid & 63);
            if ((Cleared[pid >> 6] & bit) != 0 || RewindPlayback.DeadAt(pid, t) != false) continue;
            Cleared[pid >> 6] |= bit;
            ClearBody(pid);
        }
        foreach (var pid in _play.Revive)
        {
            ulong bit = 1UL << (pid & 63);
            if ((Revived[pid >> 6] & bit) != 0 || RewindPlayback.DeadAt(pid, t) != false) continue;
            Revived[pid >> 6] |= bit;
            try { Dev.DevCommands.ReviveLocal(pid); }
            catch (Exception e) { Plugin.Logger.LogError($"TimeRewind revive {pid}: {e}"); }
        }
    }

    // 逆再生の終わり: 止めていたのを戻し、生き返り・位置・タスク・待ち時間・役職の値を戻す時刻へ揃える
    private static void Finish()
    {
        _playing = false;
        _pend = PendNone;
        Step("pose", RewindPlayback.Stop);
        var lp = PlayerControl.LocalPlayer;
        if (_froze) { _froze = false; if (lp) lp.moveable = true; }
        // 会議が始まった (記録は会議の始まりで捨てる)・試合が終わった時は人の側は戻さない
        if (!_on || MeetingHud.Instance || !Terrain.GameClock.ShipAlive) return;
        var g = _play;
        long now = Environment.TickCount64;
        foreach (var pid in g.Revive)
        {
            try { Dev.DevCommands.ReviveLocal(pid); }
            catch (Exception e) { Plugin.Logger.LogError($"TimeRewind revive {pid}: {e}"); }
        }
        LastRevived = g.Revive.Length;
        foreach (var pid in g.Gone) ClearBody(pid);
        LastGone = g.Gone.Length;
        int s = _playSlot;
        if (s < 0 || SlotMs[s] != _playSlotMs) s = Find(_playStart - g.BackMs); // 逆再生の間の記録に枠を使われた
        if (s < 0) return;
        LastSlotAgeMs = (int)(_playStart - SlotMs[s]);
        Rewinds++;
        RewindJournal.UndoAfter(SlotMs[s]);
        Step("effects", () => StatusEffects.Rewound(now - SlotMs[s]));
        Step("self", () => RestoreSelf(s));
        Step("roles", () => RestoreRoles(s, now - SlotMs[s]));
        if (AmongUsClient.Instance.AmHost) Step("host", () => RestoreHost(s, now - SlotMs[s]));
        NotifyRewound();
        // 戻した所より後の記録は無かったことになる
        RewindPlayback.Forget(SlotMs[s]);
        _count -= (_head - s - 1 + Slots) % Slots;
        _head = (s + 1) % Slots;
        _nextMs = now + StepMs;
        Plugin.Logger.LogInfo($"TimeRewind: back {g.BackMs}ms -> slot age {LastSlotAgeMs}ms revive={g.Revive.Length} tasks={LastTasksUndone} systems={LastSystems} {LastNote}");
    }

    // 抜けた人の倒れた体を片付ける (戻し先ではまだ生きていたので、体が残っていると通報できてしまう)
    private static void ClearBody(byte pid)
    {
        try
        {
            foreach (var body in UnityEngine.Object.FindObjectsOfType<DeadBody>())
                if (body && body.ParentId == pid) UnityEngine.Object.Destroy(body.gameObject);
        }
        catch (Exception e) { Plugin.Logger.LogError($"TimeRewind gone {pid}: {e}"); }
    }

    private static void Step(string what, Action fn)
    {
        try { fn(); }
        catch (Exception e) { Plugin.Logger.LogError($"TimeRewind {what}: {e}"); }
    }

    private static void RestoreSelf(int s)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null) return;
        LastNote = "-";
        if (Minigame.Instance) Minigame.Instance.Close();
        // はしご・昇降機の途中の控えは床の上ではないので、乗る前に降りていた控えの位置へ戻す
        int g = Grounded(s);
        // 死んでいた時刻の体へは戻さない (幽霊のまま)。生きていた人は生きていた時の位置へ
        _pendX = PosX[g];
        _pendY = PosY[g];
        _pendMove = Alive[s] || lp.Data.IsDead;
        _pendVent = Alive[s] && !lp.Data.IsDead ? InVent[g] : -1;
        _pendUntil = Environment.TickCount64 + PendMaxMs;
        _pendShip = Terrain.GameClock.ShipGen;
        // はしご・ジップライン・昇降機の途中は本編の動きが位置を持っているので、降りてから置く
        _pend = PendLand;
        if (lp.onLadder || lp.inMovingPlat) LastNote = "moving";
        else PendStep(Environment.TickCount64);
        if (lp.Data.Role && lp.Data.Role.IsImpostor) lp.SetKillTimer(Kill[s]);
        else lp.killTimer = Kill[s];
        RestoreVanillaRole(s, lp);
        RestoreTasks(s, lp);
    }

    // 入っているベントの番号 (-1 = 外)。入った直後は Vent.currentVent がまだ空のことがあるので、足元のベントで引く
    internal static int VentOf(PlayerControl pc)
    {
        if (!pc.inVent) return -1;
        if (pc.AmOwner && Vent.currentVent) return Vent.currentVent.Id;
        var ship = ShipStatus.Instance;
        if (!ship) return -1;
        var p = pc.transform.position;
        int best = -1;
        float bd = 1f; // ベントの中の体はベントの上にある (0.4 程度ずれる)
        foreach (var v in ship.AllVents)
        {
            if (!v) continue;
            var q = v.transform.position;
            float dx = p.x - q.x, dy = p.y - q.y, d = dx * dx + dy * dy;
            if (d < bd) { bd = d; best = v.Id; }
        }
        return best;
    }

    // s がはしご・昇降機の途中なら、それより前で一番近い降りていた控え (無ければ後ろ側で一番近い物)
    private static int Grounded(int s)
    {
        if (!Hold[s]) return s;
        int oldest = (_head - _count + Slots) % Slots;
        for (int i = s; i != oldest;)
        {
            i = (i - 1 + Slots) % Slots;
            if (!Hold[i]) return i;
        }
        int newest = (_head - 1 + Slots) % Slots;
        for (int i = s; i != newest;)
        {
            i = (i + 1) % Slots;
            if (!Hold[i]) return i;
        }
        return s;
    }

    // 戻した後の自分の置き直し (降りるのを待つ → 位置 → ベントの出入りを順に)
    private const byte PendNone = 0, PendLand = 1, PendEnter = 2;
    private const long PendMaxMs = 20000;   // ジップラインの端から端より長く
    private const long VentExitMs = 700;    // ベントから出る絵が終わるまで (入り直すのはその後)
    private static byte _pend;
    private static float _pendX, _pendY;
    private static bool _pendMove;
    private static int _pendVent;
    private static long _pendUntil, _pendEnterAt;
    private static int _pendShip;

    private static void PendStep(long now)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || MeetingHud.Instance || !Terrain.GameClock.ShipAlive || Terrain.GameClock.ShipGen != _pendShip || now > _pendUntil)
        {
            _pend = PendNone;
            return;
        }
        if (_pend == PendLand)
        {
            if (lp.onLadder || lp.inMovingPlat) return;
            int cur = VentOf(lp);
            if (cur >= 0 && cur == _pendVent) { _pend = PendNone; return; } // 同じベントの中にいる
            if (cur >= 0) lp.MyPhysics.RpcExitVent(cur);
            if (_pendMove) Net.Teleport.Self(new Vector2(_pendX, _pendY));
            if (_pendVent < 0) { _pend = PendNone; return; }
            _pend = PendEnter;
            _pendEnterAt = now + (cur >= 0 ? VentExitMs : 0);
            return;
        }
        if (_pend == PendEnter)
        {
            // 位置が予算待ちで飛んでいない間は入らない (入る位置がずれる)
            if (now < _pendEnterAt || Net.Teleport.Pending) return;
            _pend = PendNone;
            if (lp.inVent || lp.Data.IsDead) return;
            lp.MyPhysics.RpcEnterVent(_pendVent);
            LastNote = $"vent{_pendVent}";
        }
    }

    private static void RestoreTasks(int s, PlayerControl lp)
    {
        Undone.Clear();
        int n = TaskN[s];
        for (int i = 0; i < Tasks.Count; i++)
        {
            var t = Tasks[i];
            if (!t) continue;
            uint id = t.Id;
            int o = -1;
            for (int k = 0; k < n; k++)
                if (TaskId[s * MaxTasks + k] == id) { o = s * MaxTasks + k; break; }
            if (o < 0) continue;
            var info = TaskInfos[i];
            bool wasDone = info != null ? info.Complete : t.IsComplete;
            bool stepChanged = t.taskStep != TaskStep[o];
            t.taskStep = TaskStep[o];
            t.TimerStarted = (NormalPlayerTask.TimerState)TaskTimerState[o];
            t.TaskTimer = TaskTimer[o];
            CopyIn(TaskData, o * MaxData, TaskDataLen[o], t.Data);
            if (wasDone && !TaskDone[o])
            {
                if (info != null) info.Complete = false;
                Undone.Add(id);
                if (t.Arrow) t.Arrow.gameObject.SetActive(true);
                stepChanged = true;
            }
            if (!stepChanged) continue;
            // 段で行き先の変わるタスクの矢印を引き直す。本編はタスクによって (行き先の端末が無いなど) 例外を投げるので 1 件ずつ受け止める
            try { t.UpdateArrowAndLocation(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"TimeRewind task arrow {t.TaskType}: {e.Message}"); }
        }
        LastTasksUndone = Undone.Count;
        if (Undone.Count == 0) return;
        UndoCall.Send(Undone.ToArray());
        if (GameData.Instance) GameData.Instance.RecomputeTaskCounts();
    }

    private static void UndoTasks(PlayerControl sender, uint[] ids)
    {
        if (!sender || sender.Data == null) return;
        foreach (var id in ids)
        {
            var info = sender.Data.FindTaskById(id);
            if (info != null) info.Complete = false;
        }
        if (GameData.Instance) GameData.Instance.RecomputeTaskCounts();
    }

    // 能力の回数・ボタン・役職の手元の値。ホストの確かめの時刻は戻した秒数ぶん後へずらす (経った時間を記録の時のままにする)
    private static void RestoreRoles(int s, long shiftMs)
    {
        if (Layout[s] != _layout) return;
        int a = 0, f = 0;
        foreach (var role in RoleState.All)
        {
            foreach (var ab in role.Abilities)
            {
                if (a >= MaxAbilities) break;
                int o = s * MaxAbilities + a++;
                if (ab.KeepOnRewind) continue;
                ab.RestoreUses(AbUses[o]);
                if (AbHostMs[o] > long.MinValue / 4) ab.HostLastMs = AbHostMs[o] + shiftMs;
                ab.Button?.RewindTo(AbLeft[o], AbEffect[o]);
            }
            int n = role.RewindFloats;
            if (n <= 0 || f + n > MaxRoleFloats) continue;
            role.LoadRewind(RoleFloats.AsSpan(s * MaxRoleFloats + f, n));
            f += n;
        }
        int k = 0;
        foreach (var role in RoleState.All) LoadTracked(role, s, ref k);
        foreach (var addon in RoleState.AllAddons) LoadTracked(addon, s, ref k);
    }

    private static void RestoreHost(int s, long shiftMs)
    {
        Span<long> kill = stackalloc long[256];
        KillMs.AsSpan(s * 256, 256).CopyTo(kill);
        for (int i = 0; i < 256; i++) kill[i] += shiftMs;
        KillAbility.CopyHostKillMs(kill, true);
        LastSystems = RestoreSystems(s);
    }

    private static int RestoreSystems(int s)
    {
        if (!SysOk[s] || _sysN == 0) return 0;
        _sysScratch ??= new Il2CppStructArray<byte>((long)SysBytes);
        int done = 0, prev = 0;
        for (int i = 0; i < _sysN; i++)
        {
            int end = SysEnd[s * MaxSystems + i];
            var sys = Systems[i];
            int len = end - prev;
            int from = s * SysBytes + prev;
            prev = end;
            if (sys == null) continue;
            try
            {
                CopyIn(SysData, from, len, _sysScratch);
                // キノコカオスは本編の「治る」流れを通らないと入れ替えた見た目とタスク欄の行が残る。
                // 戻り先で出ていない時は今の状態のまま治るまでの秒を 0 にして本編に治させる (戻り先の中身を読み込むと治る処理が使う中身も変わる)
                var mix = SysTypes[i] == SystemTypes.MushroomMixupSabotage ? sys.TryCast<MushroomMixupSabotageSystem>() : null;
                if (mix != null && mix.IsActive)
                {
                    if (!MixActiveIn(sys))
                    {
                        mix.currentState = MushroomMixupSabotageSystem.State.IdleButMixedUp;
                        mix.currentSecondsUntilHeal = 0.01f;
                        MarkDirty(sys);
                        done++;
                        continue;
                    }
                }
                var r = MessageReader.Get(_sysScratch);
                try { sys.Deserialize(r, true); }
                finally { r.Recycle(); }
                MarkDirty(sys);
                done++;
            }
            catch (Exception e) { Plugin.Logger.LogError($"TimeRewind system {SysTypes[i]}: {e.Message}"); }
        }
        return done;
    }

    // _sysScratch に写した記録の中でキノコカオスが出ているか。今の中身を控えて記録を読み込み、確かめてから控えを読み戻す
    private static Il2CppStructArray<byte> _mixKeep;

    private static bool MixActiveIn(ISystemType sys)
    {
        var mix = sys.Cast<MushroomMixupSabotageSystem>();
        var w = MessageWriter.Get(SendOption.None);
        try
        {
            int start = w.Length;
            sys.Serialize(w, true);
            int len = Math.Min(w.Length - start, SysBytes);
            _mixKeep ??= new Il2CppStructArray<byte>((long)SysBytes);
            CopyBetween(w, start, len, _mixKeep);
            var r = MessageReader.Get(_sysScratch);
            try { sys.Deserialize(r, true); }
            finally { r.Recycle(); }
            bool active = mix.IsActive;
            var back = MessageReader.Get(_mixKeep);
            try { sys.Deserialize(back, true); }
            finally { back.Recycle(); }
            return active;
        }
        finally { w.Recycle(); }
    }

    private static unsafe void CopyBetween(MessageWriter w, int from, int n, Il2CppStructArray<byte> dst)
    {
        byte* src = (byte*)Il2Array.Data(w.Buffer) + from;
        byte* d = (byte*)Il2Array.Data(dst);
        for (int i = 0; i < n; i++) d[i] = src[i];
    }

    // 本編の系統の IsDirty は書き込み口が隠れているので欄を直に書く (扉の系統は扉ごとの印 dirtyBits)。欄の位置は型ごとに 1 回だけ引く
    private static readonly Dictionary<IntPtr, (int at, bool bits)> DirtyField = new();

    private static void MarkDirty(ISystemType sys) => WriteDirty(sys, uint.MaxValue);

    private static unsafe uint ReadDirty(ISystemType sys)
    {
        var f = DirtyOf(sys);
        if (f.at < 0) return 0;
        byte* p = (byte*)sys.Pointer + f.at;
        return f.bits ? *(uint*)p : *(bool*)p ? 1u : 0u;
    }

    private static unsafe void WriteDirty(ISystemType sys, uint v)
    {
        var f = DirtyOf(sys);
        if (f.at < 0) return;
        byte* p = (byte*)sys.Pointer + f.at;
        if (f.bits) *(uint*)p = v;
        else *(bool*)p = v != 0;
    }

    private static (int at, bool bits) DirtyOf(ISystemType sys)
    {
        IntPtr klass = IL2CPP.il2cpp_object_get_class(sys.Pointer);
        if (!DirtyField.TryGetValue(klass, out var f))
        {
            f = (-1, false);
            for (IntPtr k = klass; k != IntPtr.Zero && f.at < 0; k = IL2CPP.il2cpp_class_get_parent(k))
            {
                IntPtr fi = IL2CPP.il2cpp_class_get_field_from_name(k, "<IsDirty>k__BackingField");
                if (fi != IntPtr.Zero) { f = ((int)IL2CPP.il2cpp_field_get_offset(fi), false); break; }
                fi = IL2CPP.il2cpp_class_get_field_from_name(k, "dirtyBits");
                if (fi != IntPtr.Zero) f = ((int)IL2CPP.il2cpp_field_get_offset(fi), true);
            }
            DirtyField[klass] = f;
            if (f.at < 0) Plugin.Logger.LogWarning($"TimeRewind: no dirty field on {sys.GetType().Name}");
        }
        return f;
    }

    internal static string Probe()
    {
        long now = Environment.TickCount64;
        return $"on={_on} playing={_playing} slots={_count} span={Span:0.00}s rewinds={Rewinds} lastAge={LastSlotAgeMs}ms revived={LastRevived} gone={LastGone} journal={RewindJournal.Count}/{RewindJournal.Undone} tasksUndone={LastTasksUndone} systems={LastSystems}/{_sysN} note={LastNote} next={_nextMs - now}ms";
    }
}
