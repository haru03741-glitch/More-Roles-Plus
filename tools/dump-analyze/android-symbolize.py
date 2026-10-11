# Android ランチャーが保存する tombstone テキスト (exit_info_*.txt) を読み、フレームに名前を付けて
# 人が読めるスタックを出す。tombstone 本体は protobuf の Java toString() 形式 (数値は 10 進)。
#
# 使い方:
#   python android-symbolize.py <exit_info.txt>                        # クラッシュしたスレッドだけ
#   python android-symbolize.py <exit_info.txt> --all-threads          # 全スレッド
#   python android-symbolize.py <exit_info.txt> --thread UnityMain     # 名前 (同名は全部) または tid
#   python android-symbolize.py <exit_info.txt> --il2cpp-map <path> --perf-map <path>
#   python android-symbolize.py <exit_info.txt> --il2cpp-map <path> --il2cpp-key fileoff
#   python android-symbolize.py <exit_info.txt> --blackbox <blackbox-pid.bin> [--crumbs 40]   # tombstone の後ろに続けて出す
#   python android-symbolize.py --blackbox <blackbox-pid.bin> [--perf-map <path>]            # tombstone 無しでも可
#   python android-symbolize.py <ログ共有.zip> [--crumbs 40] [--all-threads]                  # zip をそのまま読む
#
# zip: ランチャーの「ログ共有」が作る zip。中の exit_info_<ts>_<pid>.txt / blackbox-<pid>.bin / perf-<pid>.map を
#   pid ごとに束ね、新しい順 (blackbox の開始時刻、無ければ exit_info の ts) に出す。il2cpp-methods.map は全体で共有。
#   bundle-info.txt があれば先頭に出す。--il2cpp-map / --perf-map を付けた場合は zip の中身よりそちらを使う。
#
# --blackbox: ランチャーのネイティブ層が残す blackbox-<pid>.bin (リトルエンディアン)。
#   ヘッダ (header_size バイト・v1 は 4096 / v2 は 65536、初期の v2 は 16384): 0 magic "EKBB0001" か "EKBB0002" / 8 u32 header_size /
#     12 u32 ring_size / 16 u32 pid /
#     24 u64 start_realtime_ms / 32 u64 write_pos (書いた総バイト数) / 64 u32 stamped, u32 signo, i32 si_code, u32 tid,
#     u64 fault_addr, pc, sp, lr, fp, realtime_ms, u32 frame_count, u32 予約 (v2 は walk_stop), u64 frames[64]
#   v2 だけにある区画 (ヘッダ先頭からの位置):
#     1024 メモリ: u32 sample_count, i32 oom_score_adj (INT32_MIN = 読めず), u64 last_realtime_ms, rss_kb, rss_shared_kb,
#       mem_available_kb (0 = 読めず), peak_rss_kb, peak_realtime_ms
#     2048 ハング記録: u32 total_count, u32 signo, u64 beat_count, u64 last_progress_realtime_ms, u32 paused, +32 から slot[4]。
#       slot (320 バイト): u32 seq (0 = 空), u32 tid, u64 realtime_ms, stalled_ms, pc, sp, lr, fp, u32 frame_count,
#       u32 walk_stop, u64 frames[32]
#     4096 モジュール表: u32 count, u32 truncated, +8 から entry[] (書く側の上限は 65536 なら 950 件、
#       16384 なら 190 件。読む側は count を header_size に収まる範囲で打ち切る)。entry (64 バイト): u64 begin, end, file_offset, load_base (0 = 不明),
#       char name[32] (NUL 詰め・basename から " (deleted)" を外した物・最大 31 バイト)。
#       載るのはパスが '/' で始まる実行可能 mapping すべて (.so のほか dll・boot.oat・memfd:jit-cache 等)。
#       同じファイルの隣り合う断片は 1 entry にまとめられている。
#   walk_stop (フレームポインタを辿るのを止めた理由): 0 辿っていない / 1 fp が 8 の倍数でない / 2 フレームレコードが読めない /
#     3 戻りアドレスが 0 / 4 次の fp が今の fp 以下 (代替シグナルスタックから元のスタックへ移る段は 2 回まで辿る) / 5 配列が満杯
#   本体: ring_size バイトのリング。レコードは "<起動からの ms> <tid> <本文>" + 改行。write_pos > ring_size なら
#     折り返し済みで、最古の行は途中から始まりうるので先頭の 1 行は捨てる。形が崩れた行は "~" を付けてそのまま出す。
#   刻印の pc/lr/frames は、同じ pid の tombstone があればその mappings・フレーム名・il2cpp 表・perf map で、
#   無ければ perf map だけで名前を引く。pid が食い違う tombstone の mappings は使わない (別プロセスの配置なので誤解決する)。
#   pid が同じでも使い回しの別プロセスのことがあるので、exit_info のファイル名の ts (ミリ秒の epoch) でも確かめる:
#   ts が blackbox の開始より前、または刻印があって ts と刻印の時刻が 60 秒を超えて離れていれば別プロセスとして扱う。
#   v2 で tombstone の mappings が使えない時はモジュール表で「モジュール+0x」に直し、libil2cpp なら il2cpp 表も引く
#   (RVA = アドレス - load_base。load_base が 0 なら file_offset + mapping 内の距離。--il2cpp-key fileoff なら常に後者)。
#   この時の鎖の終点は「どのモジュールにも perf map にも入らないアドレスが出た所」。
#
# 名前表 (どちらも省略可。無ければその種のフレームは未解決 "?" のまま):
#   --perf-map    CoreCLR の perf map。1 行 "<start16進> <size16進> <名前>"。絶対アドレスなので
#                 フレームの絶対 pc で引く (start <= pc < start+size)。
#   --il2cpp-map  1 行目 "# il2cpp-map v1 size=<10進> methods=<件数>"、以降 "<rva16進> <名前>" が昇順。
#                 サイズ情報が無いので「引くアドレス以下で最大の RVA」のメソッド + 差分を出す。
#                 同一 RVA に複数名があれば先頭 + "(+N aliases)"。ヘッダの size 以上のアドレスは未解決。
#
# 名前の優先順: tombstone 自身の function_name → il2cpp 表 → perf map → "?"。
# オフセットの表記: tombstone 由来は Android の流儀で 10 進 (name+196)、表由来は 16 進 (name+0x1c)。
#
# libil2cpp.so を引くアドレスについて (重要):
#   tombstone の rel_pc はそのままでは使えない。libil2cpp.so はアンワインダから読めず
#   (unreadable_elf_files)、しかも実行領域がフックで数百個の小さな mapping に割れているため、
#   rel_pc は「その断片 mapping の先頭からの距離」になっていて RVA ではない。
#   そこで RVA は pc - (同名で offset 0 の、pc 以下で最も近い mapping の先頭) で求める。
#   ELF が読めている場合はこの値が rel_pc と一致するので、常にこの求め方で引いてよい。
#   表がファイルオフセットで作られている場合は --il2cpp-key fileoff (= mapping.offset + 断片内の距離)、
#   tombstone の rel_pc そのままで引きたい場合は --il2cpp-key relpc。
#   同じ理由でずれている他モジュールのフレームは、モジュール+0x の値を補正し行末に元の rel_pc を添える。
# 依存: 標準ライブラリのみ

import sys, os, re, bisect, struct, datetime, zipfile, tempfile, shutil

IL2CPP_NAME = "libil2cpp.so"
TRACE_MARK = "Tombstone / Trace Data:"
MAX_BASE_DISTANCE = 1 << 32
EXIT_INFO_FILE = re.compile(r"^exit_info_(\d+)_(\d+)\.txt$")


def parse_args(argv):
    opts = {"perf": None, "il2cpp": None, "all": False, "thread": None, "key": "rva", "blackbox": None, "crumbs": 40}
    args = []
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--perf-map":
            opts["perf"] = argv[i + 1]; i += 2
        elif a == "--il2cpp-map":
            opts["il2cpp"] = argv[i + 1]; i += 2
        elif a == "--il2cpp-key":
            opts["key"] = argv[i + 1]; i += 2
        elif a == "--thread":
            opts["thread"] = argv[i + 1]; i += 2
        elif a == "--blackbox":
            opts["blackbox"] = argv[i + 1]; i += 2
        elif a == "--crumbs":
            opts["crumbs"] = int(argv[i + 1]); i += 2
        elif a == "--all-threads":
            opts["all"] = True; i += 1
        else:
            args.append(a); i += 1
    return args, opts


# ---------- tombstone (protobuf テキスト形式) ----------

_ESC = {"n": 10, "t": 9, "r": 13, '"': 34, "'": 39, "\\": 92, "a": 7, "b": 8, "f": 12, "v": 11}


def unescape(s):
    # Java の TextFormat は非 ASCII を \ooo (8 進・UTF-8 バイト列) で出す
    if "\\" not in s:
        return s
    out = bytearray()
    i, n = 0, len(s)
    while i < n:
        c = s[i]
        if c != "\\" or i + 1 >= n:
            out += c.encode("utf-8"); i += 1
            continue
        d = s[i + 1]
        if d in "01234567":
            j = i + 1
            while j < n and j < i + 4 and s[j] in "01234567":
                j += 1
            out.append(int(s[i + 1:j], 8) & 0xFF); i = j
        elif d == "x":
            j = i + 2
            while j < n and j < i + 4 and s[j] in "0123456789abcdefABCDEF":
                j += 1
            if j > i + 2:
                out.append(int(s[i + 2:j], 16))
            i = j
        else:
            out.append(_ESC.get(d, ord(d) & 0xFF)); i += 2
    return out.decode("utf-8", errors="replace")


def parse_value(v):
    if v.startswith('"') and v.endswith('"') and len(v) >= 2:
        return unescape(v[1:-1])
    if v == "true":
        return True
    if v == "false":
        return False
    try:
        return int(v)
    except ValueError:
        return v


def parse_proto_text(lines):
    # 行の形は 3 種類だけ: "name {" / "}" / "name: value"。同じキーの繰り返しに備えて値は常にリスト。
    root = {}
    stack = [root]
    for ln in lines:
        s = ln.strip()
        if not s or s.startswith("#"):
            continue
        if s == "}":
            if len(stack) > 1:
                stack.pop()
            continue
        if s.endswith("{"):
            child = {}
            stack[-1].setdefault(s[:-1].strip(), []).append(child)
            stack.append(child)
            continue
        k, sep, v = s.partition(": ")
        if not sep:
            continue
        stack[-1].setdefault(k, []).append(parse_value(v))
    return root


def one(d, key, default=None):
    v = d.get(key)
    return v[0] if v else default


def load_tombstone(path):
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        lines = f.read().split("\n")
    cut = None
    for i, ln in enumerate(lines):
        if ln.startswith(TRACE_MARK):
            cut = i
            break
    header = {}
    exit_line = None
    head_lines = lines if cut is None else lines[:cut]
    for ln in head_lines:
        if ln.startswith("ApplicationExitInfo("):
            exit_line = ln.strip()
        elif ": " in ln and not ln.startswith(" "):
            k, _, v = ln.partition(": ")
            header.setdefault(k.strip(), v.strip())
    proto = parse_proto_text(lines[cut + 1:]) if cut is not None else {}
    return header, exit_line, proto


# ---------- memory mappings ----------

class Mappings:
    def __init__(self, raw):
        ms = []
        for m in raw:
            ms.append({
                "begin": one(m, "begin_address", 0), "end": one(m, "end_address", 0),
                "offset": one(m, "offset", 0), "load_bias": one(m, "load_bias", 0),
                "name": one(m, "mapping_name"), "build_id": one(m, "build_id"),
                "perms": ("r" if one(m, "read") else "-") + ("w" if one(m, "write") else "-")
                         + ("x" if one(m, "execute") else "-"),
            })
        ms.sort(key=lambda m: m["begin"])
        self.maps = ms
        self.begins = [m["begin"] for m in ms]

    def index_at_or_below(self, addr):
        return bisect.bisect_right(self.begins, addr) - 1

    def find(self, addr):
        i = self.index_at_or_below(addr)
        if i >= 0 and self.maps[i]["begin"] <= addr < self.maps[i]["end"]:
            return i
        return -1

    def image_base(self, idx):
        # 同名で offset 0 の、idx 以下で最も近い mapping の先頭 = ロードされたイメージの先頭
        name = self.maps[idx]["name"]
        if not name:
            return None
        top = self.maps[idx]["begin"]
        j = idx
        while j >= 0 and top - self.maps[j]["begin"] < MAX_BASE_DISTANCE:
            m = self.maps[j]
            if m["name"] == name and m["offset"] == 0:
                return m["begin"]
            j -= 1
        return None


def fmt_mapping(m):
    return "%012x-%012x %s %s" % (m["begin"], m["end"], m["perms"], m["name"] or "<anon>")


# ---------- 名前表 ----------

class PerfMap:
    def __init__(self, path):
        ents = []
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            for ln in f:
                p = ln.rstrip("\r\n").split(None, 2)
                if len(p) < 2:
                    continue
                try:
                    start, size = int(p[0], 16), int(p[1], 16)
                except ValueError:
                    continue
                ents.append((start, size, p[2] if len(p) > 2 else "?"))
        ents.sort(key=lambda e: e[0])
        self.ents = ents
        self.starts = [e[0] for e in ents]

    def lookup(self, pc):
        i = bisect.bisect_right(self.starts, pc) - 1
        if i < 0:
            return None
        start, size, name = self.ents[i]
        if start <= pc < start + size:
            return name if pc == start else "%s+0x%x" % (name, pc - start)
        return None


class Il2CppMap:
    MAX_METHOD_SPAN = 0x10000

    def __init__(self, path):
        self.size = None
        self.image = None
        self.declared = None
        rvas, names, extra = [], [], []
        unsorted = False
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            for ln in f:
                ln = ln.rstrip("\r\n")
                if not ln:
                    continue
                if ln.startswith("#"):
                    m = re.search(r"size=(\d+)", ln)
                    if m and self.size is None:
                        self.size = int(m.group(1))
                    m = re.search(r"image=([0-9a-fA-F]+)", ln)
                    if m and self.image is None:
                        self.image = int(m.group(1), 16)
                    m = re.search(r"methods=(\d+)", ln)
                    if m and self.declared is None:
                        self.declared = int(m.group(1))
                    continue
                p = ln.split(None, 1)
                try:
                    rva = int(p[0], 16)
                except ValueError:
                    continue
                name = p[1] if len(p) > 1 else "?"
                if rvas and rva == rvas[-1]:
                    extra[-1] += 1
                    continue
                if rvas and rva < rvas[-1]:
                    unsorted = True
                rvas.append(rva); names.append(name); extra.append(0)
        if unsorted:
            # 昇順でない表でも引けるように並べ直す (同一 RVA は先頭の名前を残す)
            merged = {}
            for r, n, e in zip(rvas, names, extra):
                if r in merged:
                    merged[r][1] += e + 1
                else:
                    merged[r] = [n, e]
            rvas = sorted(merged)
            names = [merged[r][0] for r in rvas]
            extra = [merged[r][1] for r in rvas]
        self.rvas, self.names, self.extra = rvas, names, extra
        self.count = len(rvas) + sum(extra)

    def lookup(self, rva):
        # 上限はロード済みイメージの末尾 (image=) を優先。無ければファイルサイズ (size=)。
        bound = self.image if self.image is not None else self.size
        if rva < 0 or (bound is not None and rva >= bound):
            return None
        i = bisect.bisect_right(self.rvas, rva) - 1
        if i < 0:
            return None
        # 表にサイズが無いので「直下のメソッド」を返すが、離れすぎは il2cpp ランタイム自身の
        # コード (表に載らない) を誤って最寄りのメソッド名にしてしまう。未解決として扱う。
        if rva - self.rvas[i] > self.MAX_METHOD_SPAN:
            return None
        s = self.names[i]
        if rva != self.rvas[i]:
            s += "+0x%x" % (rva - self.rvas[i])
        if self.extra[i]:
            s += " (+%d aliases)" % self.extra[i]
        return s


# ---------- 名前の整形 ----------

_OPERATORS = {"_Znwm": "operator new", "_Znam": "operator new[]", "_ZdlPv": "operator delete", "_ZdaPv": "operator delete[]"}


def demangle_simple(name):
    # リンカ内部シンボルの "__dl_" を外し、引数を捨てて名前だけにする。読むのは単純な関数名 (_Z<長さ><名前>) と
    # テンプレート無しの入れ子名 (_ZN<長さ><名前>...E) と new/delete だけ。
    # 読み切れない物は元の文字列のまま返す。
    s = name[5:] if name.startswith("__dl_") else name
    if s in _OPERATORS:
        return _OPERATORS[s]
    m = re.match(r"_Z(\d+)", s)
    if m:
        n = int(m.group(1))
        return s[m.end():m.end() + n] if m.end() + n <= len(s) else s
    if not s.startswith("_ZN"):
        return s
    i = 3
    while i < len(s) and s[i] in "KVrO":
        i += 1
    parts = []
    while i < len(s) and s[i].isdigit():
        j = i
        while j < len(s) and s[j].isdigit():
            j += 1
        n = int(s[i:j])
        if j + n > len(s):
            return s
        parts.append(s[j:j + n])
        i = j + n
    if not parts or i >= len(s) or s[i] != "E":
        return s
    return "::".join(parts)


# ---------- フレーム解決 ----------

def resolve_frame(fr, maps, il2, perf, key, unreadable):
    pc = one(fr, "pc", 0)
    rel = one(fr, "rel_pc", 0)
    fname = one(fr, "file_name", "") or ""
    func = one(fr, "function_name")
    mi = maps.find(pc)
    m = maps.maps[mi] if mi >= 0 else None
    anon = (not fname) or fname.startswith("<anonymous")
    note = ""
    if anon:
        module = (m["name"] if m and m["name"] else "<anon>")
        off = pc - m["begin"] if m else rel
    else:
        module = os.path.basename(fname.replace(" (deleted)", "")) or fname
        off = rel
    is_il2 = (not anon) and module == IL2CPP_NAME
    lookup_addr = rel
    if m is not None and not anon:
        frag = pc - m["begin"]
        base = maps.image_base(mi)
        # アンワインダが ELF を読めなかったフレームは rel_pc が断片 mapping 基準の値になり RVA ではない
        fragment_relative = (func is None and fname in unreadable)
        if base is not None and (is_il2 or fragment_relative):
            rva = pc - base
            if rva != rel:
                note = "  (tombstone rel_pc 0x%x)" % rel
            off = rva
            lookup_addr = rva
        if is_il2 and key == "fileoff":
            lookup_addr = m["offset"] + frag
        elif is_il2 and key == "relpc":
            lookup_addr = rel

    if func:
        fo = one(fr, "function_offset", 0)
        name = demangle_simple(func) + ("+%d" % fo if fo else "")
        src = "tombstone"
    else:
        name, src = None, None
        if is_il2 and il2 is not None:
            name = il2.lookup(lookup_addr)
            if name:
                src = "il2cpp"
                if key != "rva":
                    note += "  (il2cpp key %s 0x%x)" % (key, lookup_addr)
        if name is None and perf is not None:
            name = perf.lookup(pc)
            if name:
                src = "perfmap"
        if name is None:
            name, src = "?", "unresolved"
    return pc, module, off, name, src, note


def print_thread(tid, th, maps, il2, perf, key, unreadable, crashed, tally):
    frames = th.get("current_backtrace", [])
    print("")
    print('--- thread %s "%s"%s  (%d frames) ---' % (tid, one(th, "name", "?"), "  [CRASHED]" if crashed else "", len(frames)))
    for n, fr in enumerate(frames):
        pc, module, off, name, src, note = resolve_frame(fr, maps, il2, perf, key, unreadable)
        tally[src] = tally.get(src, 0) + 1
        print("#%02d pc %012x %s+0x%x  %s%s" % (n, pc, module, off, name, note))
    unread = sorted(set(th.get("unreadable_elf_files", [])))
    if unread:
        print("    unreadable ELF (names/rel_pc from the unwinder are unreliable): " + ", ".join(os.path.basename(u) for u in unread))


def print_fault_neighbourhood(maps, addr):
    print("")
    print("fault address 0x%x:" % addr)
    i = maps.find(addr)
    if i >= 0:
        lo, hi = i - 1, i + 1
        if lo >= 0:
            print("  prev : " + fmt_mapping(maps.maps[lo]))
        print("  IN   : " + fmt_mapping(maps.maps[i]) + "   (+0x%x)" % (addr - maps.maps[i]["begin"]))
        if hi < len(maps.maps):
            print("  next : " + fmt_mapping(maps.maps[hi]))
        return
    print("  (not inside any mapping)")
    lo = maps.index_at_or_below(addr)
    if lo >= 0:
        print("  below: " + fmt_mapping(maps.maps[lo]) + "   (ends 0x%x before)" % (addr - maps.maps[lo]["end"]))
    if lo + 1 < len(maps.maps):
        print("  above: " + fmt_mapping(maps.maps[lo + 1]) + "   (starts 0x%x after)" % (maps.maps[lo + 1]["begin"] - addr))


def exit_info_summary(line):
    if not line:
        return None
    keys = ("timestamp", "pid", "process", "reason", "subreason", "status", "importance", "description")
    out = []
    for k in keys:
        m = re.search(r"\b%s=(.*?)(?= \w+=|\)?$)" % k, line)
        if m:
            out.append("%s=%s" % (k, m.group(1)))
    return " ".join(out) if out else line


def show_tombstone(path, opts, il2, perf):
    header, exit_line, proto = load_tombstone(path)
    ctx = {"maps": None, "pid": None, "known": {}}
    m = EXIT_INFO_FILE.match(os.path.basename(path))
    if m:
        ctx["ts"] = int(m.group(1))

    print("device  : %s %s (Android %s, API %s)" % (header.get("Manufacturer", "?"), header.get("Device Model", "?"),
                                                 header.get("Android Version", "?"), header.get("API Level", "?")))
    print("package : %s %s (code %s)" % (header.get("Package Name", "?"), header.get("Version Name", "?"), header.get("Version Code", "?")))
    print("exitinfo: %s" % (exit_info_summary(exit_line) or "(no ApplicationExitInfo line)"))
    if not proto:
        print("no tombstone body found (marker '%s' missing or empty)" % TRACE_MARK)
        return 1, ctx

    sig = one(proto, "signal_info", {})
    fault = one(sig, "fault_address") if one(sig, "has_fault_address") else None
    print("signal  : %s (%s)  code %s (%s)  fault addr %s" % (
        one(sig, "number", "?"), one(sig, "name", "?"), one(sig, "code", "?"), one(sig, "code_name", "?"),
        "0x%x" % fault if fault is not None else "(none)"))
    print("process : pid %s tid %s  uptime %ss  %s" % (one(proto, "pid", "?"), one(proto, "tid", "?"),
                                                    one(proto, "process_uptime", "?"), one(proto, "timestamp", "")))
    for am in proto.get("abort_message", []):
        if am:
            print("abort   : %s" % am)
    for c in proto.get("causes", []):
        hr = one(c, "human_readable")
        if hr:
            print("cause   : %s" % hr)
    if il2 is not None:
        print("il2cpp map: %d methods (%d distinct addresses)%s, size %s, key=%s" % (
            il2.count, len(il2.rvas),
            "" if il2.declared in (None, il2.count) else " [header says %d]" % il2.declared,
            "0x%x" % il2.size if il2.size is not None else "unknown", opts["key"]))
    if perf is not None:
        print("perf map  : %d entries" % len(perf.ents))

    maps = Mappings(proto.get("memory_mappings", []))
    ctx["maps"] = maps
    ctx["pid"] = one(proto, "pid")
    if fault is not None:
        print_fault_neighbourhood(maps, fault)

    threads = []
    for t in proto.get("threads", []):
        v = one(t, "value", {})
        threads.append((one(t, "key", one(v, "id", -1)), v))
    crash_tid = one(proto, "tid")
    for _, th in threads:
        for fr in th.get("current_backtrace", []):
            fn = one(fr, "function_name")
            if fn:
                fo = one(fr, "function_offset", 0)
                ctx["known"].setdefault(one(fr, "pc", 0), demangle_simple(fn) + ("+%d" % fo if fo else ""))

    if opts["all"]:
        sel = sorted(threads, key=lambda kv: (kv[0] != crash_tid, kv[0]))
    elif opts["thread"] is not None:
        want = opts["thread"]
        sel = [kv for kv in threads if str(kv[0]) == want or str(one(kv[1], "id")) == want or one(kv[1], "name") == want]
        if not sel:
            print("")
            print("thread '%s' not found. names: %s" % (want, ", ".join(sorted(set(str(one(v, "name", "?")) for _, v in threads)))))
            return 1, ctx
    else:
        sel = [kv for kv in threads if kv[0] == crash_tid]
        if not sel:
            print("")
            print("warning: crashing tid %s has no thread entry; showing the first thread instead" % crash_tid)
            sel = threads[:1]

    unreadable = set()
    for _, th in threads:
        unreadable.update(th.get("unreadable_elf_files", []))
    tally = {}
    for tid, th in sel:
        print_thread(tid, th, maps, il2, perf, opts["key"], unreadable, tid == crash_tid, tally)

    total = sum(tally.values())
    print("")
    print("summary: %d threads shown (of %d), %d frames: tombstone %d / il2cpp %d / perfmap %d / unresolved %d" % (
        len(sel), len(threads), total, tally.get("tombstone", 0), tally.get("il2cpp", 0),
        tally.get("perfmap", 0), tally.get("unresolved", 0)))
    return 0, ctx


# ---------- ブラックボックス (blackbox-<pid>.bin) ----------

BB_MAGIC = b"EKBB0001"
BB_FIXED = struct.Struct("<8sIIIIQQ")          # magic, header_size, ring_size, pid, 予約, start_realtime_ms, write_pos
BB_STAMP = struct.Struct("<IIiI6QII64Q")       # offset 64 から
BB_STAMP_OFFSET = 64
BB_MIN_SIZE = BB_STAMP_OFFSET + BB_STAMP.size
# v2 で増えた区画 (どれもヘッダ先頭からの位置)。v1 との違いは magic とこれらと、刻印の予約欄が walk_stop になった事だけ
BB_MAGIC_V2 = b"EKBB0002"
BB_MEM = struct.Struct("<Ii6Q")                # sample_count, oom_score_adj, last_realtime_ms, rss, shared, avail, peak_rss, peak_realtime_ms
BB_MEM_OFFSET = 1024
BB_HANG = struct.Struct("<IIQQI")              # total_count, signo, beat_count, last_progress_realtime_ms, paused
BB_HANG_OFFSET = 2048
BB_HANG_SLOT = struct.Struct("<IIQQ4QII32Q")   # seq, tid, realtime_ms, stalled_ms, pc, sp, lr, fp, frame_count, walk_stop, frames[32]
BB_HANG_SLOTS_OFFSET = BB_HANG_OFFSET + 32
BB_HANG_SLOT_COUNT = 4
BB_MOD_HEAD = struct.Struct("<II")             # count, truncated
BB_MOD_ENTRY = struct.Struct("<4Q32s")         # begin, end, file_offset, load_base, name
BB_MOD_OFFSET = 4096
SIGRTMIN_KERNEL, SIGRTMAX = 32, 64
INT32_MIN = -(1 << 31)
PID_REUSE_WINDOW_MS = 60000
WALK_STOP = {0: "not walked", 1: "frame pointer is not a multiple of 8", 2: "frame record is unreadable",
             3: "return address is 0", 4: "next frame pointer is not above the current one (after the allowed stack switches)", 5: "frame array is full"}

SIGNAL_NAMES = {1: "SIGHUP", 2: "SIGINT", 3: "SIGQUIT", 4: "SIGILL", 5: "SIGTRAP", 6: "SIGABRT", 7: "SIGBUS", 8: "SIGFPE",
                9: "SIGKILL", 10: "SIGUSR1", 11: "SIGSEGV", 12: "SIGUSR2", 13: "SIGPIPE", 14: "SIGALRM", 15: "SIGTERM",
                16: "SIGSTKFLT", 31: "SIGSYS"}
SI_CODE_COMMON = {0: "SI_USER", 0x80: "SI_KERNEL", -1: "SI_QUEUE", -2: "SI_TIMER", -6: "SI_TKILL"}
SI_CODE_BY_SIGNAL = {
    11: {1: "SEGV_MAPERR", 2: "SEGV_ACCERR", 3: "SEGV_BNDERR", 4: "SEGV_PKUERR", 8: "SEGV_MTEAERR", 9: "SEGV_MTESERR"},
    7: {1: "BUS_ADRALN", 2: "BUS_ADRERR", 3: "BUS_OBJERR"},
    4: {1: "ILL_ILLOPC", 2: "ILL_ILLOPN", 3: "ILL_ILLADR", 4: "ILL_ILLTRP", 5: "ILL_PRVOPC", 6: "ILL_PRVREG", 7: "ILL_COPROC", 8: "ILL_BADSTK"},
    8: {1: "FPE_INTDIV", 2: "FPE_INTOVF", 3: "FPE_FLTDIV", 4: "FPE_FLTOVF", 5: "FPE_FLTUND", 6: "FPE_FLTRES", 7: "FPE_FLTINV", 8: "FPE_FLTSUB"},
    5: {1: "TRAP_BRKPT", 2: "TRAP_TRACE", 3: "TRAP_BRANCH", 4: "TRAP_HWBKPT"},
}


def fmt_realtime_ms(ms):
    try:
        t = datetime.datetime.fromtimestamp(ms / 1000.0)
        return t.strftime("%Y-%m-%d %H:%M:%S.") + "%03d" % (ms % 1000)
    except (OverflowError, OSError, ValueError):
        return "(invalid time %d)" % ms


def fmt_rel_ms(ms):
    return "%s%d.%03d" % ("-" if ms < 0 else "+", abs(ms) // 1000, abs(ms) % 1000)


def printable(s):
    return "".join(c if (c >= " " or c == "\t") else "\\x%02x" % ord(c) for c in s)


def signal_label(signo):
    # リアルタイムシグナルは SIGRTMAX からの距離で書く (bionic の SIGRTMIN は版によって違うので基準にしない)
    if signo in SIGNAL_NAMES:
        return " (%s)" % SIGNAL_NAMES[signo]
    if SIGRTMIN_KERNEL <= signo <= SIGRTMAX:
        return " (SIGRTMAX%s)" % ("-%d" % (SIGRTMAX - signo) if signo != SIGRTMAX else "")
    return ""


def fmt_mb(kb):
    return "%.1f MB" % (kb / 1024.0)


class ModuleTable:
    # blackbox v2 のモジュール表。パスが '/' で始まる実行可能 mapping が載っている (CoreCLR の JIT コード等の
    # 無名領域は載らない)。data はヘッダ部分だけを渡す: count がヘッダに収まらない分は読まない。
    def __init__(self, data):
        self.count, self.truncated = 0, 0
        self.capacity = max(0, (len(data) - BB_MOD_OFFSET - BB_MOD_HEAD.size) // BB_MOD_ENTRY.size)
        ents = []
        if len(data) >= BB_MOD_OFFSET + BB_MOD_HEAD.size:
            self.count, self.truncated = BB_MOD_HEAD.unpack_from(data, BB_MOD_OFFSET)
            for i in range(min(self.count, self.capacity)):
                pos = BB_MOD_OFFSET + BB_MOD_HEAD.size + i * BB_MOD_ENTRY.size
                begin, end, file_offset, load_base, raw = BB_MOD_ENTRY.unpack_from(data, pos)
                if end <= begin:
                    continue
                name = raw.split(b"\0", 1)[0].decode("utf-8", errors="replace")
                ents.append({"begin": begin, "end": end, "offset": file_offset, "load_base": load_base, "name": name})
        ents.sort(key=lambda e: e["begin"])
        self.ents = ents
        self.begins = [e["begin"] for e in ents]

    def find(self, addr):
        i = bisect.bisect_right(self.begins, addr) - 1
        if i >= 0 and self.ents[i]["begin"] <= addr < self.ents[i]["end"]:
            return self.ents[i]
        return None

    def names(self):
        return sorted(set(e["name"] for e in self.ents))


def resolve_addr(addr, maps, il2, perf, key, known, mods=None):
    # 絶対アドレス 1 個に「モジュール+0x」と名前を付ける。maps が無ければ perf map だけで引く。
    # maps が無く mods (blackbox v2 のモジュール表) がある時は、そちらで「モジュール+0x」と il2cpp 表を引く。
    name = known.get(addr) if known else None
    loc = ""
    if maps is None and mods is not None:
        e = mods.find(addr)
        if e is not None:
            frag = addr - e["begin"]
            module = os.path.basename(e["name"].replace(" (deleted)", "")) or e["name"] or "<anon>"
            # load_base が分からない entry はファイルオフセットで代用する (RVA と一致するのは先頭のセグメントだけ)
            off = addr - e["load_base"] if e["load_base"] else e["offset"] + frag
            if name is None and module == IL2CPP_NAME and il2 is not None:
                name = il2.lookup(e["offset"] + frag if key == "fileoff" else off)
            loc = "%s+0x%x" % (module, off)
    if maps is not None:
        mi = maps.find(addr)
        if mi < 0:
            loc = "<unmapped>"
        else:
            m = maps.maps[mi]
            frag = addr - m["begin"]
            if m["name"] and m["name"].startswith("/"):
                module = os.path.basename(m["name"].replace(" (deleted)", "")) or m["name"]
                base = maps.image_base(mi)
                off = addr - base if base is not None else m["offset"] + frag
                if name is None and module == IL2CPP_NAME and il2 is not None:
                    name = il2.lookup(m["offset"] + frag if key == "fileoff" else off)
            else:
                module = m["name"] or "<anon>"
                off = frag
            loc = "%s+0x%x" % (module, off)
    if name is None and perf is not None:
        name = perf.lookup(addr)
    return loc, name or "?"


def read_ring(data, header_size, ring_size, write_pos):
    # 返り値: (古い順のレコード行 (bytes) のリスト, 折り返したか, 注意書きのリスト)
    notes = []
    ring = data[header_size:header_size + ring_size]
    if len(ring) < ring_size:
        notes.append("file is truncated: ring has %d of %d bytes" % (len(ring), ring_size))
    wrapped = ring_size > 0 and write_pos > ring_size
    if ring_size <= 0 or write_pos == 0:
        return [], False, notes
    if not wrapped:
        buf = ring[:write_pos]
    else:
        pos = write_pos % ring_size
        buf = ring[pos:] + ring[:pos]
        # 最古の行は上書きで頭が欠けているので捨てる
        nl = buf.find(b"\n")
        buf = buf[nl + 1:] if nl >= 0 else b""
    lines = [ln for ln in buf.split(b"\n") if ln.strip(b"\0\r")]
    return lines, wrapped, notes


_CRUMB = re.compile(r"^(\d{1,15}) (\d{1,10}) (.*)$", re.S)


def show_blackbox(path, opts, il2, perf, ctx):
    print("")
    print("=== blackbox: %s ===" % os.path.basename(path))
    with open(path, "rb") as f:
        data = f.read()
    if len(data) < BB_MIN_SIZE:
        print("not a blackbox file: %d bytes (a header needs at least %d)" % (len(data), BB_MIN_SIZE))
        return 1
    magic, header_size, ring_size, pid, _, start_ms, write_pos = BB_FIXED.unpack_from(data, 0)
    if magic not in (BB_MAGIC, BB_MAGIC_V2):
        print("not a blackbox file: magic %r (expected %r or %r)" % (magic, BB_MAGIC, BB_MAGIC_V2))
        return 1
    v2 = magic == BB_MAGIC_V2
    if header_size < BB_MIN_SIZE or header_size > len(data):
        print("blackbox header is damaged: header_size %d (file is %d bytes, minimum %d)" % (header_size, len(data), BB_MIN_SIZE))
        return 1
    stamp = BB_STAMP.unpack_from(data, BB_STAMP_OFFSET)
    stamped, signo, si_code, tid, fault, pc, sp, lr, fp, stamp_ms, frame_count = stamp[:11]
    walk_stop = stamp[11] if v2 else None
    frames = stamp[12:]
    lines, wrapped, notes = read_ring(data, header_size, ring_size, write_pos)

    print("process : pid %d  started %s" % (pid, fmt_realtime_ms(start_ms)))
    print("ring    : %d bytes written in total, ring %d bytes, %s" % (
        write_pos, ring_size,
        "WRAPPED (oldest %d bytes overwritten, first partial line dropped)" % (write_pos - ring_size) if wrapped else "not wrapped"))
    for n in notes:
        print("warning : " + n)

    maps, known = ctx.get("maps"), ctx.get("known")
    if ctx.get("pid") is not None and ctx["pid"] != pid:
        print("warning : tombstone is pid %s but blackbox is pid %d -- different process; tombstone mappings are NOT used below" % (ctx["pid"], pid))
        maps, known = None, None
    elif ctx.get("ts") is not None:
        # pid は使い回される。exit_info のファイル名の ts (プロセスが死んだ時刻) がこの blackbox の開始より前か、
        # 刻印の時刻から離れすぎていれば、同じ pid の別プロセスの記録。
        ts, why = ctx["ts"], None
        if ts < start_ms:
            why = "is before this blackbox started"
        elif stamped == 1 and abs(ts - stamp_ms) > PID_REUSE_WINDOW_MS:
            why = "is %ds away from the stamp (%s)" % (abs(ts - stamp_ms) // 1000, fmt_realtime_ms(stamp_ms))
        if why:
            print("warning : exit_info time %s %s -- pid %d was reused by a different process; tombstone mappings are NOT used below" % (
                fmt_realtime_ms(ts), why, pid))
            maps, known = None, None
    # モジュール表は tombstone の mappings が使えない時だけ使う
    table = ModuleTable(data[:header_size]) if v2 else None
    mods = table if (table is not None and maps is None and table.ents) else None
    if perf is not None:
        m = re.search(r"perf-(\d+)\.map$", os.path.basename(opts["perf"]))
        if m and int(m.group(1)) != pid:
            print("warning : perf map file name says pid %s but blackbox is pid %d -- names from it may be wrong" % (m.group(1), pid))
    if not ctx.get("tombstone_shown"):
        if il2 is not None and mods is not None:
            print("il2cpp map: %d methods (%d distinct addresses), key=%s (looked up through the blackbox module table)" % (
                il2.count, len(il2.rvas), "fileoff" if opts["key"] == "fileoff" else "rva"))
        elif il2 is not None:
            print("il2cpp map: %d methods (not usable without the tombstone's mappings)" % il2.count)
        if perf is not None:
            print("perf map  : %d entries" % len(perf.ents))
    if table is not None:
        print("modules   : %d executable mappings in %d files%s -- %s" % (
            len(table.ents), len(table.names()),
            " [TRUNCATED: the table was full; frames in the mappings that did not fit end the chain early]"
            if table.truncated or table.count > table.capacity else "",
            "used to resolve the addresses below" if mods is not None else
            "not used (the tombstone's mappings are)" if maps is not None else "empty"))

    def addr_line(label, addr):
        loc, name = resolve_addr(addr, maps, il2, perf, opts["key"], known, mods)
        return "%s %012x %s  %s" % (label, addr, loc, name) if loc else "%s %012x  %s" % (label, addr, name)

    def frame_lines(frame_count, frames, walk_stop):
        shown = min(frame_count, len(frames))
        for n in range(shown):
            # フレームポインタを保たないコード (libunity 等) に入ると、読んだ値は戻りアドレスでなく
            # ただのスタック上のデータになる。実行可能でないアドレスが出た所で鎖は切れている。
            if maps is not None:
                mi = maps.find(frames[n])
                if mi < 0 or "x" not in maps.maps[mi]["perms"]:
                    print("(chain ends here: %012x is not executable code; %d more stored value(s) dropped)" % (frames[n], shown - n))
                    break
            elif mods is not None:
                # モジュール表には perms が無いので、どのモジュールにも perf map にも入らない所を終点とする
                if mods.find(frames[n]) is None and (perf is None or perf.lookup(frames[n]) is None):
                    print("(chain ends here: %012x is in no module and not in the perf map; %d more stored value(s) dropped)" % (frames[n], shown - n))
                    break
            print(addr_line("#%02d pc" % n, frames[n]))
        if walk_stop is not None:
            print("(walk stopped: %s)" % WALK_STOP.get(walk_stop, "unknown reason %d" % walk_stop))

    if v2 and header_size >= BB_MEM_OFFSET + BB_MEM.size:
        samples, oom_adj, last_ms, rss_kb, shared_kb, avail_kb, peak_kb, peak_ms = BB_MEM.unpack_from(data, BB_MEM_OFFSET)
        if samples > 0:
            print("")
            print("memory  : %d samples, last at %s (%ss after start), oom_score_adj %s" % (
                samples, fmt_realtime_ms(last_ms), fmt_rel_ms(last_ms - start_ms),
                "(unreadable)" if oom_adj == INT32_MIN else "%d" % oom_adj))
            print("          rss %s (shared %s), system available %s" % (
                fmt_mb(rss_kb), fmt_mb(shared_kb), fmt_mb(avail_kb) if avail_kb else "(unreadable)"))
            print("          peak rss %s at %s (%ss after start)" % (fmt_mb(peak_kb), fmt_realtime_ms(peak_ms), fmt_rel_ms(peak_ms - start_ms)))

    print("")
    if stamped == 1:
        code_name = SI_CODE_BY_SIGNAL.get(signo, {}).get(si_code) or SI_CODE_COMMON.get(si_code, "?")
        print("stamp   : signal %d (%s)  code %d (%s)  fault addr 0x%x  tid %d" % (
            signo, SIGNAL_NAMES.get(signo, "?"), si_code, code_name, fault, tid))
        print("          at %s (%ss after start)" % (fmt_realtime_ms(stamp_ms), fmt_rel_ms(stamp_ms - start_ms)))
        print("  " + addr_line("pc", pc))
        print("  " + addr_line("lr", lr))
        print("  sp %012x  fp %012x" % (sp, fp))
    elif stamped == 0:
        print("stamp   : シグナルの刻印なし (強制終了・メモリ不足 kill・正常終了のいずれか)")
    else:
        print("stamp   : stamped=%d (unexpected value; header may be damaged)" % stamped)

    if frame_count > 0 or (stamped == 1 and walk_stop is not None):
        shown = min(frame_count, len(frames))
        print("")
        print("--- blackbox frames (%d%s) ---" % (frame_count, "" if shown == frame_count else ", only %d stored" % shown))
        frame_lines(frame_count, frames, walk_stop)

    if v2 and header_size >= BB_HANG_SLOTS_OFFSET + BB_HANG_SLOT_COUNT * BB_HANG_SLOT.size:
        total, hang_signo, beats, progress_ms, paused = BB_HANG.unpack_from(data, BB_HANG_OFFSET)
        slots = [BB_HANG_SLOT.unpack_from(data, BB_HANG_SLOTS_OFFSET + i * BB_HANG_SLOT.size) for i in range(BB_HANG_SLOT_COUNT)]
        slots = sorted((s for s in slots if s[0] != 0), key=lambda s: s[0])
        print("")
        if total == 0 and hang_signo == 0 and beats == 0 and progress_ms == 0 and not slots:
            print("hang    : nothing recorded (no heartbeat, no signal number: the hang watch never started in this process)")
        else:
            print("hang    : %d stall(s) recorded%s, signal %d%s, %d heartbeats%s, main thread last progressed %s" % (
                total, "" if total == len(slots) else " (%d kept)" % len(slots), hang_signo, signal_label(hang_signo),
                beats, " [PAUSED]" if paused else "",
                "%s (%ss after start)" % (fmt_realtime_ms(progress_ms), fmt_rel_ms(progress_ms - start_ms)) if progress_ms else "(never)"))
        for s in slots:
            seq, h_tid, h_ms, stalled_ms, h_pc, h_sp, h_lr, h_fp, h_count, h_stop = s[:10]
            h_frames = s[10:]
            print("")
            print("--- hang #%d: tid %d stalled %d ms, at %s (%ss after start)  (%d frames%s) ---" % (
                seq, h_tid, stalled_ms, fmt_realtime_ms(h_ms), fmt_rel_ms(h_ms - start_ms), h_count,
                "" if h_count <= len(h_frames) else ", only %d stored" % len(h_frames)))
            print("  " + addr_line("pc", h_pc))
            print("  " + addr_line("lr", h_lr))
            print("  sp %012x  fp %012x" % (h_sp, h_fp))
            frame_lines(h_count, h_frames, h_stop)

    n_show = max(0, opts["crumbs"])
    print("")
    print("--- crumbs: last %d of %d records ---" % (min(n_show, len(lines)), len(lines)))
    for raw in (lines[-n_show:] if n_show else []):
        text = raw.decode("utf-8", errors="replace").rstrip("\r")
        m = _CRUMB.match(text)
        if m:
            print("%s [%s] %s" % (fmt_rel_ms(int(m.group(1))), m.group(2), printable(m.group(3))))
        else:
            print("~ " + printable(text))
    return 0


# ---------- ログ共有 zip ----------

_BUNDLE_BLACKBOX = re.compile(r"^blackbox-(\d+)\.bin$")
_BUNDLE_PERF = re.compile(r"^perf-(\d+)\.map$")
BUNDLE_INFO = "bundle-info.txt"
BUNDLE_IL2CPP = "il2cpp-methods.map"


def blackbox_start_ms(path):
    # 並べ替え用にヘッダの開始時刻だけ読む。blackbox でなければ None。
    try:
        with open(path, "rb") as f:
            head = f.read(BB_FIXED.size)
    except OSError:
        return None
    if len(head) < BB_FIXED.size or head[:8] not in (BB_MAGIC, BB_MAGIC_V2):
        return None
    return BB_FIXED.unpack_from(head, 0)[5]


def extract_bundle(zip_path, dest):
    # 使う物だけを basename で取り出す (zip 内のディレクトリ構成には頼らない)。
    # 返り値: ({basename: 展開先のパス}, 取り出さなかった物の名前のリスト)
    got, others = {}, []
    with zipfile.ZipFile(zip_path) as z:
        for info in z.infolist():
            if info.is_dir():
                continue
            base = os.path.basename(info.filename.replace("\\", "/"))
            wanted = (EXIT_INFO_FILE.match(base) or _BUNDLE_BLACKBOX.match(base) or _BUNDLE_PERF.match(base)
                      or base in (BUNDLE_INFO, BUNDLE_IL2CPP))
            if not wanted or base in got:
                others.append(info.filename)
                continue
            out = os.path.join(dest, base)
            with z.open(info) as src, open(out, "wb") as dst:
                shutil.copyfileobj(src, dst)
            got[base] = out
    return got, others


def group_bundle(files):
    # pid ごとに束ねる。返り値: 新しい順の [{"pid", "exit", "ts", "blackbox", "perf", "order"}]
    exits, boxes, perfs = {}, {}, {}
    for base, path in files.items():
        m = EXIT_INFO_FILE.match(base)
        if m:
            exits.setdefault(int(m.group(2)), []).append((int(m.group(1)), path))
            continue
        m = _BUNDLE_BLACKBOX.match(base)
        if m:
            boxes[int(m.group(1))] = path
            continue
        m = _BUNDLE_PERF.match(base)
        if m:
            perfs[int(m.group(1))] = path
    groups = []
    for pid in sorted(set(exits) | set(boxes)):
        found = sorted(exits.get(pid, []), reverse=True)
        box = boxes.get(pid)
        start = blackbox_start_ms(box) if box else None
        # 同じ pid の exit_info が複数ある時 (pid の使い回し)、blackbox と組むのは開始より後で最も古い 1 本。
        # 残りは tombstone だけで出す。perf map は blackbox と同じプロセスの物なので、組んだ側にだけ当てる。
        mate = None
        if box and found:
            later = [e for e in found if start is None or e[0] >= start]
            mate = later[-1] if later else found[0]
        if box:
            groups.append({"pid": pid, "exit": mate[1] if mate else None, "ts": mate[0] if mate else None,
                           "blackbox": box, "perf": perfs.get(pid), "order": start if start is not None else (mate[0] if mate else 0)})
        for n, e in enumerate(found):
            if e is mate:
                continue
            groups.append({"pid": pid, "exit": e[1], "ts": e[0], "blackbox": None,
                           "perf": perfs.get(pid) if (not box and n == 0) else None, "order": e[0]})
    groups.sort(key=lambda g: g["order"], reverse=True)
    return groups


def show_bundle(zip_path, opts):
    tmp = tempfile.mkdtemp(prefix="android-symbolize-")
    try:
        files, others = extract_bundle(zip_path, tmp)
        print("=== bundle: %s ===" % os.path.basename(zip_path))
        if BUNDLE_INFO in files:
            with open(files[BUNDLE_INFO], "r", encoding="utf-8", errors="replace") as f:
                for ln in f.read().splitlines():
                    print(printable(ln))
        else:
            print("(no %s)" % BUNDLE_INFO)
        if others:
            print("not read: " + ", ".join(sorted(others)))
        if opts["blackbox"]:
            print("note    : --blackbox is ignored for a zip (the blackbox files inside it are used)")

        il2_path = opts["il2cpp"] or files.get(BUNDLE_IL2CPP)
        il2 = Il2CppMap(il2_path) if il2_path else None
        groups = group_bundle(files)
        if not groups:
            print("")
            print("no exit_info_<ts>_<pid>.txt or blackbox-<pid>.bin in this zip")
            return 1
        rc = 0
        for g in groups:
            parts = [os.path.basename(p) for p in (g["exit"], g["blackbox"], g["perf"]) if p]
            print("")
            print("")
            print("######## pid %d: %s ########" % (g["pid"], " + ".join(parts)))
            sub = dict(opts)
            sub["perf"] = opts["perf"] or g["perf"]
            perf = PerfMap(sub["perf"]) if sub["perf"] else None
            ctx = {"maps": None, "pid": None, "known": {}}
            if g["exit"]:
                trc, ctx = show_tombstone(g["exit"], sub, il2, perf)
                # tombstone 本体の無い exit_info (メモリ不足 kill 等) は zip では普通にあるので失敗に数えない
                if ctx["maps"] is not None:
                    rc = max(rc, trc)
                    ctx["tombstone_shown"] = True
            if g["blackbox"]:
                rc = max(rc, show_blackbox(g["blackbox"], sub, il2, perf, ctx))
        return rc
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main():
    args, opts = parse_args(sys.argv[1:])
    if (not args and not opts["blackbox"]) or opts["key"] not in ("rva", "fileoff", "relpc"):
        print("usage: android-symbolize.py <exit_info.txt> [--perf-map <path>] [--il2cpp-map <path>]")
        print("       [--il2cpp-key rva|fileoff|relpc] [--all-threads] [--thread <name|tid>]")
        print("       [--blackbox <blackbox-pid.bin>] [--crumbs <N>]     (<exit_info.txt> may be omitted with --blackbox)")
        print("       android-symbolize.py <log-share.zip> [--crumbs <N>] [--all-threads] [--thread <name|tid>]")
        sys.exit(2)
    try:
        sys.stdout.reconfigure(errors="backslashreplace")
    except Exception:
        pass

    if args and zipfile.is_zipfile(args[0]):
        sys.exit(show_bundle(args[0], opts))

    il2 = Il2CppMap(opts["il2cpp"]) if opts["il2cpp"] else None
    perf = PerfMap(opts["perf"]) if opts["perf"] else None
    rc, ctx = 0, {"maps": None, "pid": None, "known": {}}
    if args:
        rc, ctx = show_tombstone(args[0], opts, il2, perf)
        ctx["tombstone_shown"] = True
    if opts["blackbox"]:
        rc = max(rc, show_blackbox(opts["blackbox"], opts, il2, perf, ctx))
    sys.exit(rc)


if __name__ == "__main__":
    try:
        main()
    except (BrokenPipeError, OSError) as e:
        # "| head" 等で読み手が先に閉じた場合 (Windows では EINVAL になる) は黙って終わる
        if isinstance(e, BrokenPipeError) or (getattr(e, "errno", None) == 22 and e.filename is None):
            try:
                sys.stdout = open(os.devnull, "w")
            except Exception:
                pass
            sys.exit(0)
        raise
