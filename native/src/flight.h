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
const int MaxSpheres = 160;
const int MaxMarkers = 32;
const int MaxEvents = 128;
const int MaxSnaps = 12;
const int MaxParams = 96;
const int MaxParticles = 2048;
const int MaxDust = 320;
const int MaxParts = 48;   // 船 1 隻の動く部品 (翼・舵・噴射口の首振り・姿勢制御の噴射など)
const int PartShips = 2;   // 部品を動かす船の数 (番号 0..1)。シェーダの配列の長さ = PartShips * MaxParts * 2

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
    PUTurnTime, PUTurnCool, PConverge,
    PDustCell, PDustStreak, PDustAlpha,
    PChargeTime, PChargeDelay, PChargeDamage, PChargeScale, PChargeRecover, PGrazeDist, PTailDist, PTailCone,
    PFlameLen, PFlameWidth, PFlameGlow, PExhRate, PExhSpeed, PExhLife, PIgniteShake, PRcsRate, PRcsSpeed,
    PBankDamp, PPitchLean, PIdleSway, PCamTurn, PDodgeYaw, PDodgeEase, PJoltHit, PJoltFreq, PJoltDamp, PKickBoost, PKickCharge, PUTurnCamBack, PKnock,
    ParamCount
};

// 印 (Blender の空のオブジェクトから)。kind = 砲口・噴射口・区画の中心
// 砲台の砲口 (id = 砲身の番号・本編の武器庫の砲台と同じく 1 発ずつ交互に撃つ)・ゴミを捨てる口 (向き = 捨てる向き)・姿勢制御の噴射口 (乗る部品の値 = 噴く強さ)
enum MarkerKind { MkMuzzle = 1, MkEngine = 2, MkSector = 3, MkTurret = 4, MkHatch = 5, MkRcs = 6 };

// 出来事 (C# へ返す)
// EvFire の c = 1 は溜め撃ち・EvGraze = 回避の最中に敵の弾がすぐ横を抜けた (溜めが満ちる)
// EvIgnite = 加速の点火 (a = 船)
enum EventKind { EvFire = 1, EvHit = 2, EvBoltEnd = 3, EvDodge = 4, EvUTurn = 5, EvGraze = 6, EvIgnite = 7 };

struct Sphere { float c[3]; float r; int32_t sector; };
struct Marker { int32_t kind, id; float p[3]; float d[3]; };

// 動く部品の信号 (Part.w の並び)。値は -1..1 (加速・減速・撃った揺れは 0..1)
// 砲台の向き (横・縦)・砲身ごとの撃った揺れ (0..1)・ゴミの口の開き (0..1) は船の中の出来事 (本編の船のタスク) から C# が決める (Ship.aux)
enum Signal { SgPitch, SgYaw, SgRoll, SgBank, SgBoost, SgBrake, SgFire, SgDodge, SgOne, SgAimYaw, SgAimPitch, SgShotA, SgShotB, SgHatch, SgCount };
// Ship.aux の並び (mrp_flt_aux の which)
enum Aux { AxAimYaw, AxAimPitch, AxHatch, AuxCount };
// 動き方: 軸の周りに回す (度)・軸の点を中心に大きさを変える (0..1)・軸の向きへずらす
enum PartMode { PmRotate = 0, PmScale = 1, PmSlide = 2 };

// 動く部品 (Blender の書き出しから)。値 = 信号の重み付きの和を lo..hi に切り、resp の速さで追う (0 = すぐ)。
// spin は値に足す一定の回り (度/秒・回り続けるアンテナなど)。parent は先に並ぶ部品の番号 (-1 = 船体) で、親の動きに乗る
struct Part
{
    int32_t mode, parent;
    float pivot[3], axis[3];
    float w[SgCount];
    float lo, hi, resp, spin;
};

struct Model
{
    Sphere sph[MaxSpheres];
    Marker mk[MaxMarkers];
    Part part[MaxParts];
    int32_t mkPart[MaxMarkers]; // 印が乗る部品 (-1 = 船体)。翼の先の砲口は翼と一緒に動く
    int32_t sphN, mkN, partN;
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
    float charge, chargeWait;    // 溜め撃ち (撃たずにいると 0→1 に溜まる・撃つと 0 から待ち直し)
    int32_t firePrev, grazed, tailed; // 撃つの前の刻みの押下・この回避で溜めが満ちたか・後ろに敵が付いているか
    int32_t chargeBurst;         // 次の刻みで溜めが満ちた光をはじけさせる
    float hp, hpMax;
    int32_t alive, model, team, remote, muzzleNext;
    Snap snap[MaxSnaps]; int32_t snapN;
    float drawP[3], drawQ[4];
    float sig[SgCount];          // 部品を動かす信号 (刻みごと)
    float partV[MaxParts];       // 部品の今の値 (信号を追った後)
    float firePulse;             // 撃った揺れ (撃つと 1・すぐ減る)
    float aux[AuxCount];         // 船の中から決める部品の目標 (砲台の向き・ゴミの口)
    float shotPulse[2];          // 砲台の砲身ごとの撃った揺れ
    float prevQ[4]; int32_t prevInit; // 遠くの船の回る速さを向きの差から出す
    float thr, ignite, exhAcc, boostPrev; // 噴射の出力 (0..1・目で見る値)・加速の点火の光 (1→0)・噴射の粒の端数・前の刻みの加速の信号
    float bankV, lean, leanV;    // 描く姿の傾きの速さ・機首の上下の寄せ (機体の慣性をばねで追う)
    float jolt[3], joltV[3];     // 被弾・溜め撃ち・点火で受けた回りの揺れ (船の中の軸 x/y/z・描く姿だけ)
    float idleT, brakePrev;
    float knock[3], knockV[3];   // 被弾で押された描く位置のずれ (世界の向き・描く姿だけ)
};

// kind 0 = 戦いの弾・1 = 砲台が小惑星を撃つ光線 (船に当たらず、届いた所で小惑星が砕ける)
// size = 見た目と当たりの太さの倍率 (溜め撃ちは大きい)
struct Bolt { float p[3], v[3]; float age, life; int32_t owner, alive, mine; float dmg; int32_t kind; float size; };

struct Event { int32_t kind, a, b, c; float x, y, z, f; };

// 火花・炎・煙・欠片。kind 0 = 丸いにじみ・1 = 速さの向きに伸びる筋・2 = 岩・3 = ゴミ・4 = 噴射のガス (丸いにじみ・カメラのすぐ前で薄くする)。
// 色は乗算済み (rgb = 光・a = 後ろを隠す量)
struct Particle { float p[3], v[3]; float age, life, s0, s1, drag; uint8_t c0[4], c1[4]; int32_t kind, alive; };

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
    float camF[3], camU[3];         // カメラの台の向き (船の向きを遅れて追う・曲がると船の横顔が見える)
    uint32_t rng;
    int32_t camInit;
    int32_t latchDodge, latchUTurn; // 押した瞬間の入力 (刻みが回るまで持つ)
    float view[6];                  // 確かめ用の視点 (yaw・pitch 度・距離・見る点 xyz = 船の中)。距離 0 = 普段の追うカメラ
    Particle part[MaxParticles];
    int32_t partNext;
    float dust[MaxDust][3];
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
    float* hud;             // 速さ・加速の残り・耐久・回避の待ち・宙返りの待ち・揺れ・溜め・後ろに付かれた・噴射の出力・姿勢制御の噴射の強さ (10 個)
    int32_t steps;          // 回した固定刻みの数
    uint8_t* fxVerts;       // 火花・炎・煙・塵の頂点 (弾と同じ形・1 枚 4 頂点)
    int32_t fxCap, fxPrev, fxN;
    float* partOut;         // 部品の動き (PartShips 隻 × MaxParts 個 × 8 = 四元数 4・ずれ 3・大きさ 1)。シェーダの配列へそのまま
};
#pragma pack(pop)

const int BoltStride = 24; // 位置 float3 + 色 unorm8x4 + uv float2
}
