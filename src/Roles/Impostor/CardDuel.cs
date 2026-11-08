using System.Collections.Generic;
using Hazel;

namespace MoreRolesPlus.Roles.Impostor;

// カードゲーマーのカードバトルの決まり (画面と通信から切り離した計算だけ)。
//
// 二人が同時に 1 枚ずつ伏せて出し、一斉に開く。カードを出す代わりに「ためる」でエナジーを 1 増やせる。
// 強いカードほどエナジーを使う。HP が 0 になった方の負け。両方 0 なら引き分け。決めたラウンド数で終われば HP の多い方の勝ち。
//
//   攻撃 (ジャブ 1 / ストレート 2 / 渾身の一撃 3)  … その数だけ相手に当てる
//   守り (受け流し 1 / 鉄壁 2)                     … 受ける攻撃をその数だけ減らす
//   カウンター                                     … 相手の攻撃をそっくり跳ね返す (攻撃されなければ空振り)
//   貫きの矢 (2)                                   … 守りを無視して当たる (カウンターには跳ね返される)
//   応急手当                                       … HP を 1 戻す (最大の HP まで)
//   エナジー吸収                                   … 相手のエナジーを 1 奪う
internal enum CardKind : byte { Strike, Guard, Counter, Pierce, Heal, Drain }

internal readonly struct CardDef
{
    public readonly CardKind Kind;
    public readonly int Power, Cost;
    public readonly string Key; // 言葉の鍵 (CardGamer.Card.<Key>)

    public CardDef(CardKind kind, int power, int cost, string key)
    {
        Kind = kind; Power = power; Cost = cost; Key = key;
    }
}

internal static class CardBook
{
    // カードの番号 → 中身。山札は番号の並び
    internal static readonly CardDef[] All =
    {
        new(CardKind.Strike, 1, 0, "Jab"),
        new(CardKind.Strike, 2, 1, "Straight"),
        new(CardKind.Strike, 3, 2, "Haymaker"),
        new(CardKind.Guard, 1, 0, "Parry"),
        new(CardKind.Guard, 2, 1, "Wall"),
        new(CardKind.Counter, 0, 1, "Counter"),
        new(CardKind.Pierce, 2, 2, "Pierce"),
        new(CardKind.Heal, 1, 2, "Heal"),
        new(CardKind.Drain, 1, 0, "Drain"),
    };

    // 山札 1 つ分の枚数 (番号ごと)。合わせて 20 枚
    private static readonly int[] DeckCounts = { 4, 3, 2, 3, 2, 2, 1, 1, 2 };

    internal const byte Charge = 254; // カードを出さずに「ためる」
    internal const byte None = 255;   // まだ選んでいない

    internal static CardDef Get(byte id) => All[id < All.Length ? id : 0];

    internal static List<byte> NewDeck(System.Random rng)
    {
        var deck = new List<byte>();
        for (byte i = 0; i < DeckCounts.Length; i++)
            for (int k = 0; k < DeckCounts[i]; k++) deck.Add(i);
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
        return deck;
    }
}

// 片方の席
internal sealed class DuelSide
{
    public byte Id;
    public int Hp, Energy;
    public readonly List<byte> Hand = new();
    public List<byte> Deck = new();
    public byte Pick = CardBook.None;   // ホストだけが中身を持つ (開くまで相手と観戦者には「選んだ」だけ伝える)
    public bool Picked;

    // 直前のラウンドの結果 (開いた時に配る)
    public byte Shown = CardBook.None;
    public int Took, Healed, Gained, Lost;
}

internal enum DuelPhase : byte { Pick, Reveal, End }

// 試合の状態。ホストが持って全員へ配る (手札は持ち主と、設定によっては観戦者だけが見る)
internal sealed class DuelState
{
    public byte Gamer, Target;
    public DuelPhase Phase;
    public int Round, MaxRounds, MaxHp, MaxEnergy;
    public float Left;          // 今の場面の残り秒 (受け取った時の値)
    public byte Winner = 255;   // End: 勝った人 (255 = 引き分け)
    public bool Anonymous, ShowHands;
    public readonly DuelSide A = new(), B = new(); // A = カードゲーマー・B = 相手

    public DuelSide Of(byte id) => id == A.Id ? A : id == B.Id ? B : null;
    public DuelSide Other(DuelSide s) => s == A ? B : A;

    // ── 計算 (ホスト) ──

    // 引けるだけ引いて手札を size 枚に。山札が尽きたら新しく混ぜる
    internal static void Fill(DuelSide s, int size, System.Random rng)
    {
        while (s.Hand.Count < size)
        {
            if (s.Deck.Count == 0) s.Deck = CardBook.NewDeck(rng);
            s.Hand.Add(s.Deck[s.Deck.Count - 1]);
            s.Deck.RemoveAt(s.Deck.Count - 1);
        }
    }

    // 出せるか (手札の位置・ためる)
    internal static bool CanPlay(DuelSide s, byte slot) =>
        slot == CardBook.Charge || slot < s.Hand.Count && CardBook.Get(s.Hand[slot]).Cost <= s.Energy;

    // 二人の出したものを開いて HP とエナジーを動かす。手札から出したカードを捨てる
    internal void Resolve()
    {
        var a = A; var b = B;
        foreach (var s in new[] { a, b })
        {
            s.Took = s.Healed = s.Gained = s.Lost = 0;
            byte slot = s.Pick;
            if (slot == CardBook.None || !CanPlay(s, slot)) slot = CardBook.Charge;
            if (slot == CardBook.Charge) s.Shown = CardBook.Charge;
            else
            {
                s.Shown = s.Hand[slot];
                s.Hand.RemoveAt(slot);
                s.Energy -= CardBook.Get(s.Shown).Cost;
            }
        }
        // ためる・吸収・手当 (攻撃の前)
        foreach (var s in new[] { a, b })
        {
            var o = Other(s);
            if (s.Shown == CardBook.Charge) { s.Energy++; s.Gained++; continue; }
            var c = CardBook.Get(s.Shown);
            if (c.Kind == CardKind.Drain && o.Energy > 0) { o.Energy--; o.Lost++; s.Energy++; s.Gained++; }
            if (c.Kind == CardKind.Heal && s.Hp < MaxHp) { s.Hp++; s.Healed++; }
        }
        // 攻撃
        int toA = Hit(b, a), toB = Hit(a, b);
        a.Hp -= toA; a.Took += toA;
        b.Hp -= toB; b.Took += toB;
        foreach (var s in new[] { a, b })
        {
            if (s.Energy > MaxEnergy) s.Energy = MaxEnergy;
            if (s.Energy < 0) s.Energy = 0;
            if (s.Hp < 0) s.Hp = 0;
            s.Pick = CardBook.None;
            s.Picked = false;
        }
    }

    // atk が出したものが def にどれだけ当たるか (カウンターで跳ね返った分は atk 自身に当たる → 負の値で返さず、呼ぶ側で入れ替える)
    private int Hit(DuelSide atk, DuelSide def)
    {
        int dmg = 0;
        // def に当たる atk の攻撃
        if (atk.Shown != CardBook.Charge)
        {
            var c = CardBook.Get(atk.Shown);
            if (c.Kind is CardKind.Strike or CardKind.Pierce)
            {
                var d = def.Shown == CardBook.Charge ? (CardDef?)null : CardBook.Get(def.Shown);
                if (d is { Kind: CardKind.Counter }) { }
                else if (d is { Kind: CardKind.Guard } g && c.Kind == CardKind.Strike) dmg += System.Math.Max(0, c.Power - g.Power);
                else dmg += c.Power;
            }
        }
        // def のカウンターに跳ね返された def 自身の攻撃は無い。逆に、atk がカウンターで def の攻撃を跳ね返した分を def に足す
        if (atk.Shown != CardBook.Charge && CardBook.Get(atk.Shown).Kind == CardKind.Counter && def.Shown != CardBook.Charge)
        {
            var dc = CardBook.Get(def.Shown);
            if (dc.Kind is CardKind.Strike or CardKind.Pierce) dmg += dc.Power;
        }
        return dmg;
    }

    // 勝ち負けが付いたか。付いたら Winner を決める
    internal bool Decided()
    {
        bool aOut = A.Hp <= 0, bOut = B.Hp <= 0;
        if (aOut || bOut)
        {
            Winner = aOut && bOut ? (byte)255 : aOut ? B.Id : A.Id;
            return true;
        }
        if (Round >= MaxRounds)
        {
            Winner = A.Hp == B.Hp ? (byte)255 : A.Hp > B.Hp ? A.Id : B.Id;
            return true;
        }
        return false;
    }

    // ── 配る形 ──
    // viewer: 受け取る人。手札はその人の物 (と、観戦者に見せる設定なら両方) だけ中身を入れ、他は枚数だけ。
    // 選んだカードの中身は開くまで誰にも送らない

    internal void Write(MessageWriter w, byte viewer)
    {
        w.Write(Gamer); w.Write(Target);
        w.Write((byte)Phase);
        w.Write((byte)Round); w.Write((byte)MaxRounds); w.Write((byte)MaxHp); w.Write((byte)MaxEnergy);
        w.Write(Left);
        w.Write(Winner);
        w.Write(Anonymous); w.Write(ShowHands);
        bool spectator = viewer != A.Id && viewer != B.Id;
        foreach (var s in new[] { A, B })
        {
            w.Write(s.Id);
            w.Write((byte)s.Hp); w.Write((byte)s.Energy);
            w.Write(s.Picked);
            w.Write(s.Shown);
            w.Write((byte)s.Took); w.Write((byte)s.Healed); w.Write((byte)s.Gained); w.Write((byte)s.Lost);
            bool open = viewer == s.Id || spectator && ShowHands;
            w.Write((byte)s.Hand.Count);
            w.Write(open);
            if (open) foreach (var c in s.Hand) w.Write(c);
        }
    }

    internal static DuelState Read(MessageReader r)
    {
        var d = new DuelState
        {
            Gamer = r.ReadByte(), Target = r.ReadByte(),
            Phase = (DuelPhase)r.ReadByte(),
            Round = r.ReadByte(), MaxRounds = r.ReadByte(), MaxHp = r.ReadByte(), MaxEnergy = r.ReadByte(),
            Left = r.ReadSingle(),
            Winner = r.ReadByte(),
            Anonymous = r.ReadBoolean(), ShowHands = r.ReadBoolean(),
        };
        foreach (var s in new[] { d.A, d.B })
        {
            s.Id = r.ReadByte();
            s.Hp = r.ReadByte(); s.Energy = r.ReadByte();
            s.Picked = r.ReadBoolean();
            s.Shown = r.ReadByte();
            s.Took = r.ReadByte(); s.Healed = r.ReadByte(); s.Gained = r.ReadByte(); s.Lost = r.ReadByte();
            int n = r.ReadByte();
            bool open = r.ReadBoolean();
            for (int i = 0; i < n; i++) s.Hand.Add(open ? r.ReadByte() : CardBook.None);
        }
        return d;
    }

    // ホストの手元で見る写し (配った物と同じく、見せない手札は伏せる)
    internal DuelState CopyFor(byte viewer)
    {
        var d = new DuelState
        {
            Gamer = Gamer, Target = Target, Phase = Phase, Round = Round, MaxRounds = MaxRounds, MaxHp = MaxHp, MaxEnergy = MaxEnergy,
            Left = Left, Winner = Winner, Anonymous = Anonymous, ShowHands = ShowHands,
        };
        bool spectator = viewer != A.Id && viewer != B.Id;
        foreach (var (src, dst) in new[] { (A, d.A), (B, d.B) })
        {
            dst.Id = src.Id; dst.Hp = src.Hp; dst.Energy = src.Energy; dst.Picked = src.Picked; dst.Shown = src.Shown;
            dst.Took = src.Took; dst.Healed = src.Healed; dst.Gained = src.Gained; dst.Lost = src.Lost;
            bool open = viewer == src.Id || spectator && ShowHands;
            foreach (var c in src.Hand) dst.Hand.Add(open ? c : CardBook.None);
        }
        return d;
    }
}
