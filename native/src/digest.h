#pragma once
#include "mrp_native.h"
#include <stddef.h>

// 部品ごとの指紋 (同じ手順で回した時に C++ の結果が変わっていないかを見る)。
// 各 .cpp の末尾の mrp_dg_* が元の関数を包み、呼ぶ前に入力・呼んだ後に出力を畳む。
// C# は指紋を取る間だけ関数の番地を mrp_dg_* へ差し替えるので、普段の道には何も足さない。
// 1 回の呼び出しの指紋を足し算で集めるので、作業スレッドとメインのどちらが先に呼んでも合計は同じ

enum DigestId
{
    DgFloorRaw, DgFloorBlur, DgFloorLips, DgWaterPixels, DgWaterSpreadFace, DgWaterStep,
    DgDamageStamp, DgSolidCarve, DgWaterFace, DgPieceCells, DgFallRay, DgWaterCells, DgLinkScan,
    DgWaterRaw, DgHullClip, DgFoamDraw, DgFoamCull, DgDecompRegions, DgDecompField, DgFloorFlood,
    DgFloorPatch, DgFloorCutMoved, DgLiftShape, DgWaterFill, DgFaceHeights, DgFaceCutHoles,
    DgFrontFill, DgFallDraw, DgFireStep, DgSolidFlood, DgSolidOutside, DgWetFilter, DgIslandCells,
    DgWaterSpray,
    DgCount
};

struct Dg
{
    int id;
    uint64_t in, out;
    explicit Dg(int i);
    void In(const void* p, int64_t bytes);
    void Out(const void* p, int64_t bytes);
    // 行ごとに並んだ地図の範囲 [x0, x1] × [y0, y1] だけ (elem = 1 升のバイト数・地図の外は切る)
    void In2D(const void* p, int stride, int rows, int elem, int x0, int y0, int x1, int y1) { Fold2D(in, p, stride, rows, elem, x0, y0, x1, y1); }
    void Out2D(const void* p, int stride, int rows, int elem, int x0, int y0, int x1, int y1) { Fold2D(out, p, stride, rows, elem, x0, y0, x1, y1); }
    // 1 ビット = 1 画素の地図の範囲 (範囲の端を含むバイト単位)
    void InBits(const uint8_t* p, int stride, int x0, int y0, int x1, int y1) { FoldBits(in, p, stride, x0, y0, x1, y1); }
    void OutBits(const uint8_t* p, int stride, int x0, int y0, int x1, int y1) { FoldBits(out, p, stride, x0, y0, x1, y1); }
    template <class T> void InV(const T& v) { In(&v, sizeof(T)); }
    template <class T> void OutV(const T& v) { Out(&v, sizeof(T)); }
    void Commit();
private:
    static void Fold2D(uint64_t& h, const void* p, int stride, int rows, int elem, int x0, int y0, int x1, int y1);
    static void FoldBits(uint64_t& h, const uint8_t* p, int stride, int x0, int y0, int x1, int y1);
};
