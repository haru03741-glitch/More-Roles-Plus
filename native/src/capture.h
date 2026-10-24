#pragma once
#include "mrp_native.h"
#include "digest.h"
#include <stddef.h>

// 部品ごとの呼び出しの記録 (入力と出力のバイト列を丸ごと書き出し、ゲームの外で C++ に食わせ直して比べる)。
// 書くのは指紋を取る版 (mrp_dg_*) の中だけで、mrp_cap_open の間だけ動く。普段の道には何も足さない。
// 1 回の呼び出し = 塊の並び。書く順と読む順は各 .cpp の mrp_dg_* と rp_* で揃える

struct Cap
{
    explicit Cap(int id);
    ~Cap();
    bool on;
    // 引数の構造体をそのまま (番地の欄は再生側で差し替える・PC も Android も 64 ビットで並びは同じ)
    void Raw(const void* p, int64_t bytes);
    template <class T> void RawV(const T& v) { Raw(&v, sizeof(T)); }
    // 配列 (無い時は「無い」と書く)
    void Blob(const void* p, int64_t bytes);
    // 行ごとに並んだ地図の範囲 [x0, x1] × [y0, y1] (地図の外は切る)
    void Rect(const void* p, int stride, int rows, int elem, int x0, int y0, int x1, int y1);
    void End();
private:
    int id;
    uint8_t* buf;
    int64_t len, cap;
    int32_t blobs;
    void Put(const void* p, int64_t bytes);
};

#if defined(MRP_REPLAY)
// 再生側 (tools/native-replay)。1 回の呼び出しの塊を前から順に取り出す
struct Rp
{
    int32_t n, k;
    uint8_t** data;
    int64_t* len;
    bool ok;
    char why[256];
    // 次の塊を構造体へ写す (大きさが違えば失敗)
    void Raw(void* into, int64_t bytes);
    template <class T> void RawV(T& v) { Raw(&v, sizeof(T)); }
    // 次の塊の番地 (無い時は null)。bytes に長さ
    const void* Blob(int64_t* bytes = nullptr);
    // 範囲を写した塊を、地図の大きさの領域へ戻す。外側は fill で埋める (外を読んだら結果が変わって見つかる)
    void* RectInto(int stride, int rows, int elem, int x0, int y0, int x1, int y1, uint8_t fill);
    // 範囲の塊と地図を比べる。外側が fill のままかも見る
    void RectSame(const char* what, const void* p, int stride, int rows, int elem, int x0, int y0, int x1, int y1, uint8_t fill);
    void Same(const char* what, const void* a, const void* b, int64_t bytes);
    void Fail(const char* fmt, ...);
};

typedef void (*RpFn)(Rp& r);
RpFn rp_find(int id);
// 各 .cpp の再生の関数を部品の番号で登録する (static RpReg reg(DgXxx, rp_xxx);)
struct RpReg { RpReg(int id, RpFn fn); };
#endif
