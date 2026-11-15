#pragma once
#include <stdint.h>

// 3D の艦隊戦のシム (src/Flight の C# と形を揃える)。Unity を呼ばない。座標は Unity と同じ左手系 (y が上・z が前)。
// 四元数は (x, y, z, w)・掛け算と回し方は Unity の Quaternion と同じ式。
// C# は試合ごとに mrp_flt_create で世界を 1 つ作り、フレームに 1 回 mrp_flt_frame を呼ぶ。確保は create の 1 回だけ

namespace flt
{
const int MaxShips = 8;
const int MaxBolts = 1024;
const int MaxModels = 8;
const int MaxSpheres = 96;
const int MaxMarkers = 32;
const int MaxEvents = 128;
const int MaxSnaps = 12;
const int MaxParams = 64;

// 調整の値 (C# の FlightParams.Names と同じ並び)。名前の付いた数をここに直書きしない
enum Param
{
    PCruise, PBoost, PBrake, PSpeedResp,
    PPitchRate, PYawRate, PRollRate, PTurnResp, PBankMax, PBankResp,
    PBoostMax, PBoostDrain, PBoostRegen, PBoostDelay,
    PBoltSpeed, PBoltLife, PBoltInterval, PBoltLen, PBoltWidth, PBoltDamage,
    PCamDist, PCamHeight, PCamLag, PCamLook, PCamSteer, PFov, PFovBoost, PFovResp,
    PArena, PArenaTurn,
    PDodgeTime, PDodgeSide, PDodgeCool,
    PShakeHit, PShakeHurt, PShakeDecay, PInterpDelay, PExtrapMax,
    PUTurnTime, PUTurnCool,
    ParamCount
};

// 印 (Blender の空のオブジェクトから)。kind = 砲口・噴射口・区画の中心
enum MarkerKind { MkMuzzle = 1, MkEngine = 2, MkSector = 3 };

// 出来事 (C# へ返す)
enum EventKind { EvFire = 1, EvHit = 2, EvBoltEnd = 3, EvDodge = 4, EvUTurn = 5 };

struct Sphere { float c[3]; float r; int32_t sector; };
struct Marker { int32_t kind, id; float p[3]; float d[3]; };

struct Model
{
    Sphere sph[MaxSpheres];
    Marker mk[MaxMarkers];
    int32_t sphN, mkN;
    float bound; // 全部の球を包む球の半径 (粗い当たりの前に見る)
};

struct Snap { double t; float p[3], v[3], q[4]; };

struct Ship
{
    float p[3], v[3], q[4];
    float speed, bank, pitchV, yawV, rollV;
    float boost, boostWait, fireCd;
    float dodgeT, dodgeCd; int32_t dodgeDir;
    float uturnT, uturnCd; float uturnQ0[4];
    float hp, hpMax;
    int32_t alive, model, team, remote, muzzleNext;
    Snap snap[MaxSnaps]; int32_t snapN;
    float drawP[3], drawQ[4];
};

struct Bolt { float p[3], v[3]; float age, life; int32_t owner, alive, mine; float dmg; };

struct Event { int32_t kind, a, b, c; float x, y, z, f; };

struct World
{
    float prm[MaxParams];
    Ship ship[MaxShips];
    Bolt bolt[MaxBolts];
    Model model[MaxModels];
    Event ev[MaxEvents];
    int32_t evN, boltNext, shipN;
    double time;
    float acc;
    float camP[3], camQ[4], camFov, shake, shakeT;
    uint32_t rng;
    int32_t camInit;
};

// 自分の船の入力 (-1..1)
struct Input
{
    float pitch, yaw, roll;
    int32_t fire, boost, brake, dodge, uturn;
};

#pragma pack(push, 4)
struct FrameArgs
{
    World* w;
    float dt;
    int32_t local;          // 自分が操る船の番号 (-1 = 見ているだけ)
    Input in;
    double now;             // 試合の時刻 (遠くの船の補間に使う)
    // 出力 (C# の固定の配列)
    float* shipOut;         // 船ごとに 8 個 = 位置 3・四元数 4・見えるか 1
    float* camOut;          // 位置 3・四元数 4・視野角 1
    uint8_t* boltVerts;     // 弾の頂点 (BoltStride バイトずつ・1 発 4 頂点)
    int32_t boltCap;        // 書ける発数
    int32_t boltPrev;       // 前のフレームに書いた発数 (それより後ろを潰す)
    int32_t boltN;          // 書いた発数
    Event* evOut;           // 出来事
    int32_t evCap, evN;
    float* hud;             // 速さ・加速の残り・耐久・回避の待ち・宙返りの待ち・揺れ
    int32_t steps;          // 回した固定刻みの数
};
#pragma pack(pop)

const int BoltStride = 24; // 位置 float3 + 色 unorm8x4 + uv float2
}
