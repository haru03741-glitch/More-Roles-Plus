#include "mrp_native.h"

// Android 版のマイク。ゲームに入っている Android 版の Unity には Microphone が無いので、端末の AAudio を直接開く。
// libaaudio.so は API 26 からあり (アプリの minSdk は 27)、ここは API 24 向けに組むので dlopen で関数を引く。
// 読みは呼ぶ側の毎フレームの Tick から待たずに吸う (timeout 0)。AAudio 自身の溜めを輪の代わりにし、
// 溜めは 1 秒ぶん頼んで、フレームが詰まった時も取りこぼさないようにする。
// PC では Unity の Microphone を使うので、ここは失敗を返すだけ。

#if defined(__ANDROID__)
#include <dlfcn.h>

typedef struct AAudioStreamBuilderStruct AAudioStreamBuilder;
typedef struct AAudioStreamStruct AAudioStream;

static const int32_t DirectionInput = 1;
static const int32_t FormatI16 = 1;
static const int32_t FormatFloat = 2;
static const int32_t SharingShared = 1;
static const int32_t PerfNone = 10;

static int32_t (*CreateBuilder)(AAudioStreamBuilder**);
static void (*SetDirection)(AAudioStreamBuilder*, int32_t);
static void (*SetSampleRate)(AAudioStreamBuilder*, int32_t);
static void (*SetChannelCount)(AAudioStreamBuilder*, int32_t);
static void (*SetFormat)(AAudioStreamBuilder*, int32_t);
static void (*SetSharingMode)(AAudioStreamBuilder*, int32_t);
static void (*SetPerformanceMode)(AAudioStreamBuilder*, int32_t);
static void (*SetBufferCapacity)(AAudioStreamBuilder*, int32_t);
static void (*SetInputPreset)(AAudioStreamBuilder*, int32_t); // API 28 から。無い端末では頼まない
static int32_t (*OpenStream)(AAudioStreamBuilder*, AAudioStream**);
static int32_t (*DeleteBuilder)(AAudioStreamBuilder*);
static int32_t (*RequestStart)(AAudioStream*);
static int32_t (*RequestStop)(AAudioStream*);
static int32_t (*CloseStream)(AAudioStream*);
static int32_t (*GetSampleRate)(AAudioStream*);
static int32_t (*GetChannelCount)(AAudioStream*);
static int32_t (*GetFormat)(AAudioStream*);
static int32_t (*ReadStream)(AAudioStream*, void*, int32_t, int64_t);

static bool _bound, _bindOk;
static AAudioStream* _stream;
static int32_t _rate, _channels, _format;

static const int ScratchFrames = 1024;
static float _scratchF[ScratchFrames * 2];
static int16_t _scratchS[ScratchFrames * 2];

template <typename T> static bool Sym(void* lib, const char* name, T& fn)
{
    fn = (T)dlsym(lib, name);
    return fn != nullptr;
}

static bool Bind()
{
    if (_bound) return _bindOk;
    _bound = true;
    void* lib = dlopen("libaaudio.so", RTLD_NOW);
    if (!lib) return false;
    bool ok = Sym(lib, "AAudio_createStreamBuilder", CreateBuilder)
        && Sym(lib, "AAudioStreamBuilder_setDirection", SetDirection)
        && Sym(lib, "AAudioStreamBuilder_setSampleRate", SetSampleRate)
        && Sym(lib, "AAudioStreamBuilder_setChannelCount", SetChannelCount)
        && Sym(lib, "AAudioStreamBuilder_setFormat", SetFormat)
        && Sym(lib, "AAudioStreamBuilder_setSharingMode", SetSharingMode)
        && Sym(lib, "AAudioStreamBuilder_setPerformanceMode", SetPerformanceMode)
        && Sym(lib, "AAudioStreamBuilder_setBufferCapacityInFrames", SetBufferCapacity)
        && Sym(lib, "AAudioStreamBuilder_openStream", OpenStream)
        && Sym(lib, "AAudioStreamBuilder_delete", DeleteBuilder)
        && Sym(lib, "AAudioStream_requestStart", RequestStart)
        && Sym(lib, "AAudioStream_requestStop", RequestStop)
        && Sym(lib, "AAudioStream_close", CloseStream)
        && Sym(lib, "AAudioStream_getSampleRate", GetSampleRate)
        && Sym(lib, "AAudioStream_getChannelCount", GetChannelCount)
        && Sym(lib, "AAudioStream_getFormat", GetFormat)
        && Sym(lib, "AAudioStream_read", ReadStream);
    SetInputPreset = (void (*)(AAudioStreamBuilder*, int32_t))dlsym(lib, "AAudioStreamBuilder_setInputPreset");
    _bindOk = ok;
    return ok;
}

MRP_API void mrp_mic_close()
{
    if (!_stream) return;
    RequestStop(_stream);
    CloseStream(_stream);
    _stream = nullptr;
}

// 開いて録り始める。返り値 = 実際の速さ (Hz)。0 以下 = 開けない (端末に無い・マイクの許可が無い)。
// preset = 端末の録音の種類 (AAudio の input preset・0 以下 = 端末の既定)。
// 頼んだ形 (1 チャンネル・小数) にならない端末があるので、開いた後の形を読み直して read で直す
MRP_API int32_t mrp_mic_open(int32_t wantRate, int32_t preset)
{
    mrp_mic_close();
    if (!Bind()) return -1;
    AAudioStreamBuilder* b = nullptr;
    if (CreateBuilder(&b) != 0 || !b) return -2;
    SetDirection(b, DirectionInput);
    SetSampleRate(b, wantRate);
    SetChannelCount(b, 1);
    SetFormat(b, FormatFloat);
    SetSharingMode(b, SharingShared);
    SetPerformanceMode(b, PerfNone);
    SetBufferCapacity(b, wantRate);
    if (SetInputPreset && preset > 0) SetInputPreset(b, preset);
    AAudioStream* s = nullptr;
    int32_t r = OpenStream(b, &s);
    DeleteBuilder(b);
    if (r != 0 || !s) return -3;
    _rate = GetSampleRate(s);
    _channels = GetChannelCount(s);
    _format = GetFormat(s);
    if (_rate <= 0 || _channels < 1 || _channels > 2 || (_format != FormatFloat && _format != FormatI16) || RequestStart(s) != 0)
    {
        CloseStream(s);
        return -4;
    }
    _stream = s;
    return _rate;
}

// 溜まっている分を待たずに 1 チャンネルの小数で dst へ。返り値 = 読んだ数・負 = 流れが切れた (閉じて開き直す)
MRP_API int32_t mrp_mic_read(float* dst, int32_t max)
{
    if (!_stream) return -1;
    int32_t total = 0;
    while (total < max)
    {
        int32_t want = max - total;
        if (want > ScratchFrames) want = ScratchFrames;
        int32_t got;
        if (_format == FormatFloat)
        {
            got = ReadStream(_stream, _channels == 1 ? (void*)(dst + total) : (void*)_scratchF, want, 0);
            if (got > 0 && _channels == 2)
                for (int32_t i = 0; i < got; i++) dst[total + i] = (_scratchF[i * 2] + _scratchF[i * 2 + 1]) * 0.5f;
        }
        else
        {
            got = ReadStream(_stream, _scratchS, want, 0);
            for (int32_t i = 0; i < got; i++)
            {
                float v = _scratchS[i * _channels];
                if (_channels == 2) v = (v + _scratchS[i * 2 + 1]) * 0.5f;
                dst[total + i] = v * (1.0f / 32768.0f);
            }
        }
        if (got < 0) return total > 0 ? total : got;
        total += got;
        if (got < want) break;
    }
    return total;
}

#else

MRP_API int32_t mrp_mic_open(int32_t, int32_t) { return -1; }
MRP_API int32_t mrp_mic_read(float*, int32_t) { return -1; }
MRP_API void mrp_mic_close() {}

#endif
