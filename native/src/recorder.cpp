#include "mrp_native.h"

// 録画 (PC だけ)。ゲームの画面とゲームの音を MP4 (H.264 + AAC) に書く。Windows の Media Foundation の Sink Writer を使う。
// - 画面: C# がカメラの最後に「画面を RT へ写す」+「この関数を呼ぶ」を積み、描画のスレッドで呼ばれる mrp_rec_render_event が
//   RT を読み出し用のテクスチャ (3 枚の輪) へ写す。読み出しは 2 回後の呼び出しで待たずに行う (GPU を待たせない)。
//   決めたフレームの速さより速く呼ばれた分は写さない
// - 音: C# がゲームの音の出口 (AudioListener) で受けた音を 16bit にして渡す
// - 書き込み (符号化) は専用のスレッドで行い、溜まりすぎたら画面の方を捨てる
// Android の作りは recorder_android.cpp (同じ関数の形)

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <algorithm>
#include <atomic>
#include <cstdlib>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "ole32.lib")

namespace
{
    const size_t MaxQueued = 400u * 1024u * 1024u; // 書き込み待ちの上限 (超えたら画面を捨てる)
    const int Ring = 3;

    struct Item
    {
        int Kind; // 0 = 画面 / 1 = 音
        std::vector<uint8_t> Data;
        int64_t Time, Dur;
    };

    struct Rec
    {
        std::wstring Path;
        int W, H, Fps, VideoBitrate, Rate, Channels;

        std::mutex M;
        std::condition_variable Cv;
        std::deque<Item> Q;
        size_t Bytes = 0;
        bool Stop = false, AutoDelete = false;
        std::atomic<int> State{0}; // 0 = 準備中 / 1 = 書いている / 2 = 書き終えた / -1 = 失敗
        std::atomic<int> Frames{0}, Dropped{0}, AudioPeak{0};
        std::atomic<int64_t> AudioFrames{0};
        std::thread Th;

        // 描画のスレッドだけが触る
        ID3D11Texture2D* Src = nullptr; // C# の RT (借り物)
        ID3D11Texture2D* Stage[Ring] = {};
        bool Pending[Ring] = {};
        int64_t StageTime[Ring] = {};
        int Head = 0;
        bool Swap = false;
        int64_t NextFrame = 0, FrameDur = 0;

        LARGE_INTEGER T0{}, Freq{};
        int64_t AudioBase = -1; // 最初の音の時刻

        std::mutex PeekM;
        std::vector<uint8_t> Peek; // 確かめ用: 最後に読んだ画面 (BGRA・上から)
        bool Flip = false;
        HRESULT Error = S_OK;
    };

    std::mutex g_m;
    Rec* g_active = nullptr;

    int64_t Now100ns(Rec* r)
    {
        LARGE_INTEGER t;
        QueryPerformanceCounter(&t);
        return (int64_t)((double)(t.QuadPart - r->T0.QuadPart) * 1e7 / (double)r->Freq.QuadPart);
    }

    void Push(Rec* r, Item&& it, bool video)
    {
        {
            std::lock_guard<std::mutex> lk(r->M);
            if (r->Stop) return;
            if (video && r->Bytes + it.Data.size() > MaxQueued)
            {
                r->Dropped++;
                return;
            }
            r->Bytes += it.Data.size();
            r->Q.push_back(std::move(it));
        }
        r->Cv.notify_one();
    }

    HRESULT Setup(Rec* r, IMFSinkWriter** out, DWORD* vs, DWORD* as)
    {
        IMFAttributes* attr = nullptr;
        HRESULT hr = MFCreateAttributes(&attr, 3);
        if (SUCCEEDED(hr)) attr->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
        if (SUCCEEDED(hr)) attr->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE);
        IMFSinkWriter* w = nullptr;
        if (SUCCEEDED(hr)) hr = MFCreateSinkWriterFromURL(r->Path.c_str(), nullptr, attr, &w);
        if (attr) attr->Release();

        IMFMediaType* t = nullptr;
        // 画面: 出口 H.264 / 入口 RGB32 (上から下へ並んだ BGRA)
        if (SUCCEEDED(hr)) hr = MFCreateMediaType(&t);
        if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AVG_BITRATE, (UINT32)r->VideoBitrate);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_MPEG2_PROFILE, 100); // High
        if (SUCCEEDED(hr)) hr = MFSetAttributeSize(t, MF_MT_FRAME_SIZE, (UINT32)r->W, (UINT32)r->H);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(t, MF_MT_FRAME_RATE, (UINT32)r->Fps, 1);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(t, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        if (SUCCEEDED(hr)) hr = w->AddStream(t, vs);
        if (t) { t->Release(); t = nullptr; }
        if (SUCCEEDED(hr)) hr = MFCreateMediaType(&t);
        if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_DEFAULT_STRIDE, (UINT32)(r->W * 4));
        if (SUCCEEDED(hr)) hr = MFSetAttributeSize(t, MF_MT_FRAME_SIZE, (UINT32)r->W, (UINT32)r->H);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(t, MF_MT_FRAME_RATE, (UINT32)r->Fps, 1);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(t, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        if (SUCCEEDED(hr)) hr = w->SetInputMediaType(*vs, t, nullptr);
        if (t) { t->Release(); t = nullptr; }

        // 音: 出口 AAC 192kbps / 入口 16bit PCM
        if (SUCCEEDED(hr)) hr = MFCreateMediaType(&t);
        if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, (UINT32)r->Rate);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, (UINT32)r->Channels);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000);
        if (SUCCEEDED(hr)) hr = w->AddStream(t, as);
        if (t) { t->Release(); t = nullptr; }
        if (SUCCEEDED(hr)) hr = MFCreateMediaType(&t);
        if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, (UINT32)r->Rate);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, (UINT32)r->Channels);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, (UINT32)(r->Channels * 2));
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (UINT32)(r->Rate * r->Channels * 2));
        if (SUCCEEDED(hr)) hr = w->SetInputMediaType(*as, t, nullptr);
        if (t) t->Release();

        if (SUCCEEDED(hr)) hr = w->BeginWriting();
        if (FAILED(hr))
        {
            if (w) w->Release();
            return hr;
        }
        *out = w;
        return S_OK;
    }

    HRESULT Write(IMFSinkWriter* w, DWORD stream, const Item& it)
    {
        IMFMediaBuffer* buf = nullptr;
        IMFSample* s = nullptr;
        HRESULT hr = MFCreateMemoryBuffer((DWORD)it.Data.size(), &buf);
        BYTE* p = nullptr;
        if (SUCCEEDED(hr)) hr = buf->Lock(&p, nullptr, nullptr);
        if (SUCCEEDED(hr))
        {
            memcpy(p, it.Data.data(), it.Data.size());
            buf->Unlock();
        }
        if (SUCCEEDED(hr)) hr = buf->SetCurrentLength((DWORD)it.Data.size());
        if (SUCCEEDED(hr)) hr = MFCreateSample(&s);
        if (SUCCEEDED(hr)) hr = s->AddBuffer(buf);
        if (SUCCEEDED(hr)) hr = s->SetSampleTime(it.Time);
        if (SUCCEEDED(hr)) hr = s->SetSampleDuration(it.Dur);
        if (SUCCEEDED(hr)) hr = w->WriteSample(stream, s);
        if (s) s->Release();
        if (buf) buf->Release();
        return hr;
    }

    void Worker(Rec* r)
    {
        bool com = SUCCEEDED(CoInitializeEx(nullptr, COINIT_MULTITHREADED));
        bool mf = SUCCEEDED(MFStartup(MF_VERSION));
        IMFSinkWriter* w = nullptr;
        DWORD vs = 0, as = 0;
        HRESULT hr = mf ? Setup(r, &w, &vs, &as) : E_FAIL;
        r->Error = hr;
        r->State = SUCCEEDED(hr) ? 1 : -1;
        int64_t lastVideo = -1;
        while (true)
        {
            Item it;
            {
                std::unique_lock<std::mutex> lk(r->M);
                r->Cv.wait(lk, [r] { return r->Stop || !r->Q.empty(); });
                if (r->Q.empty()) break; // 止める時は溜まった分を書き切ってから
                it = std::move(r->Q.front());
                r->Q.pop_front();
                r->Bytes -= it.Data.size();
            }
            if (!w) continue;
            if (it.Kind == 0)
            {
                if (it.Time <= lastVideo) continue; // 時刻が戻る物は書けない
                lastVideo = it.Time;
            }
            HRESULT whr = Write(w, it.Kind == 0 ? vs : as, it);
            if (FAILED(whr) && SUCCEEDED(r->Error)) r->Error = whr;
        }
        if (w)
        {
            HRESULT fhr = w->Finalize();
            if (FAILED(fhr) && SUCCEEDED(r->Error)) r->Error = fhr;
            w->Release();
            if (r->State == 1) r->State = 2;
        }
        if (mf) MFShutdown();
        if (com) CoUninitialize();
        bool del;
        {
            std::lock_guard<std::mutex> lk(r->M);
            del = r->AutoDelete;
        }
        if (del) delete r;
    }

    void ReleaseStages(Rec* r)
    {
        for (int i = 0; i < Ring; i++)
        {
            if (r->Stage[i]) r->Stage[i]->Release();
            r->Stage[i] = nullptr;
            r->Pending[i] = false;
        }
    }

    // 読み出し用のテクスチャから、待たずに読めた物を取り出して書き込みへ回す
    void Drain(Rec* r, ID3D11DeviceContext* ctx, bool wait)
    {
        for (int k = 0; k < Ring; k++)
        {
            int i = (r->Head + k) % Ring; // 古い順
            if (!r->Pending[i]) continue;
            D3D11_MAPPED_SUBRESOURCE m{};
            HRESULT hr = ctx->Map(r->Stage[i], 0, D3D11_MAP_READ, wait ? 0 : D3D11_MAP_FLAG_DO_NOT_WAIT, &m);
            if (hr == DXGI_ERROR_WAS_STILL_DRAWING) return; // 古い物がまだなら新しい物もまだ
            r->Pending[i] = false;
            if (FAILED(hr)) continue;
            Item it;
            it.Kind = 0;
            it.Time = r->StageTime[i];
            it.Dur = r->FrameDur;
            size_t row = (size_t)r->W * 4;
            it.Data.resize(row * r->H);
            const uint8_t* src = (const uint8_t*)m.pData;
            for (int y = 0; y < r->H; y++)
            {
                const uint8_t* s = src + (size_t)m.RowPitch * (r->Flip ? (r->H - 1 - y) : y);
                uint8_t* d = it.Data.data() + row * y;
                if (!r->Swap) memcpy(d, s, row);
                else
                    for (int x = 0; x < r->W; x++)
                    {
                        d[x * 4 + 0] = s[x * 4 + 2];
                        d[x * 4 + 1] = s[x * 4 + 1];
                        d[x * 4 + 2] = s[x * 4 + 0];
                        d[x * 4 + 3] = 255;
                    }
            }
            ctx->Unmap(r->Stage[i], 0);
            {
                std::lock_guard<std::mutex> lk(r->PeekM);
                if (r->Peek.size() != it.Data.size()) r->Peek.resize(it.Data.size());
                memcpy(r->Peek.data(), it.Data.data(), it.Data.size());
            }
            r->Frames++;
            Push(r, std::move(it), true);
        }
    }

    void Capture(Rec* r)
    {
        ID3D11Device* dev = nullptr;
        r->Src->GetDevice(&dev);
        if (!dev) return;
        ID3D11DeviceContext* ctx = nullptr;
        dev->GetImmediateContext(&ctx);
        if (!ctx) { dev->Release(); return; }
        if (!r->Stage[0])
        {
            D3D11_TEXTURE2D_DESC d{};
            r->Src->GetDesc(&d);
            if ((int)d.Width != r->W || (int)d.Height != r->H) { ctx->Release(); dev->Release(); return; }
            switch (d.Format)
            {
            case DXGI_FORMAT_R8G8B8A8_TYPELESS:
            case DXGI_FORMAT_R8G8B8A8_UNORM:
            case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
                r->Swap = true;
                break;
            default:
                r->Swap = false;
                break;
            }
            d.Usage = D3D11_USAGE_STAGING;
            d.BindFlags = 0;
            d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            d.MiscFlags = 0;
            d.MipLevels = 1;
            d.ArraySize = 1;
            d.SampleDesc.Count = 1;
            d.SampleDesc.Quality = 0;
            for (int i = 0; i < Ring; i++)
                if (FAILED(dev->CreateTexture2D(&d, nullptr, &r->Stage[i])))
                {
                    ReleaseStages(r);
                    r->State = -1;
                    ctx->Release();
                    dev->Release();
                    return;
                }
        }
        Drain(r, ctx, false);
        int64_t now = Now100ns(r);
        if (now >= r->NextFrame)
        {
            r->NextFrame += r->FrameDur;
            if (r->NextFrame < now) r->NextFrame = now + r->FrameDur; // 遅れた時は追いかけない
            int i = r->Head;
            if (r->Pending[i]) Drain(r, ctx, true); // 輪が埋まっている (GPU が遅い): 一番古い物を待って読む
            if (!r->Pending[i])
            {
                ctx->CopyResource(r->Stage[i], r->Src);
                r->Pending[i] = true;
                // 時刻はフレームの格子にそろえる (書き出しの再生がなめらかになる)
                r->StageTime[i] = (now / r->FrameDur) * r->FrameDur;
                r->Head = (r->Head + 1) % Ring;
            }
            else r->Dropped++;
        }
        ctx->Release();
        dev->Release();
    }
}

// 録画を始める。path は UTF-16。戻り = 録画の手 (失敗は null)
MRP_API void* mrp_rec_start(const wchar_t* path, int32_t w, int32_t h, int32_t fps, int32_t videoBitrate, int32_t rate, int32_t channels)
{
    if (!path || w <= 0 || h <= 0 || fps <= 0 || (rate != 44100 && rate != 48000) || channels < 1 || channels > 2) return nullptr;
    auto* r = new Rec();
    r->Path = path;
    r->W = w;
    r->H = h;
    r->Fps = fps;
    r->VideoBitrate = videoBitrate;
    r->Rate = rate;
    r->Channels = channels;
    r->FrameDur = 10000000LL / fps;
    QueryPerformanceFrequency(&r->Freq);
    QueryPerformanceCounter(&r->T0);
    r->Th = std::thread(Worker, r);
    {
        std::lock_guard<std::mutex> lk(g_m);
        g_active = r;
    }
    return r;
}

// 画面を写す元 (C# の RT の ID3D11Texture2D)。flip = 上下を入れ替えて読む
MRP_API void mrp_rec_texture(void* h, void* tex, int32_t flip)
{
    auto* r = (Rec*)h;
    if (!r) return;
    std::lock_guard<std::mutex> lk(g_m);
    if (r->Src != tex) ReleaseStages(r);
    r->Src = (ID3D11Texture2D*)tex;
    r->Flip = flip != 0;
}

// 描画のスレッドから (CommandBuffer.IssuePluginEvent)
static void __stdcall RenderEvent(int)
{
    std::lock_guard<std::mutex> lk(g_m);
    Rec* r = g_active;
    if (!r || !r->Src || r->State < 0) return;
    Capture(r);
}

MRP_API void* mrp_rec_render_event() { return (void*)&RenderEvent; }

// ゲームの音 (16bit・channels 本を交互に)。最初に届いた時刻を音の始まりにし、あとは数で進める
MRP_API void mrp_rec_audio(void* h, const int16_t* pcm, int32_t frames)
{
    auto* r = (Rec*)h;
    if (!r || !pcm || frames <= 0 || r->State < 0) return;
    if (r->AudioBase < 0) r->AudioBase = Now100ns(r);
    int64_t done = r->AudioFrames.fetch_add(frames);
    Item it;
    it.Kind = 1;
    it.Time = r->AudioBase + done * 10000000LL / r->Rate;
    it.Dur = (int64_t)frames * 10000000LL / r->Rate;
    it.Data.resize((size_t)frames * r->Channels * 2);
    memcpy(it.Data.data(), pcm, it.Data.size());
    int peak = 0;
    for (int i = 0; i < frames * r->Channels; i++) peak = std::max(peak, std::abs((int)pcm[i]));
    if (peak > r->AudioPeak) r->AudioPeak = peak;
    Push(r, std::move(it), false);
}

// 状態: out[0] = 状態 (0 準備中 / 1 書いている / 2 書き終えた / -1 失敗) / out[1] = 写したフレーム / out[2] = 捨てたフレーム /
// out[3] = 待ちの MB / out[4] = 音の一番大きな値 (読むと 0 に戻す) / out[5] = 失敗の HRESULT
MRP_API void mrp_rec_info(void* h, int32_t* out)
{
    auto* r = (Rec*)h;
    if (!r || !out) return;
    out[0] = r->State;
    out[1] = r->Frames;
    out[2] = r->Dropped;
    {
        std::lock_guard<std::mutex> lk(r->M);
        out[3] = (int32_t)(r->Bytes / (1024 * 1024));
    }
    out[4] = r->AudioPeak.exchange(0);
    out[5] = (int32_t)r->Error;
}

// 確かめ用: 最後に読んだ画面 (BGRA・上から下) を out へ。戻り = 書いたバイト数
MRP_API int32_t mrp_rec_peek(void* h, uint8_t* out, int32_t size)
{
    auto* r = (Rec*)h;
    if (!r || !out) return 0;
    std::lock_guard<std::mutex> lk(r->PeekM);
    if (r->Peek.empty() || (int32_t)r->Peek.size() > size) return 0;
    memcpy(out, r->Peek.data(), r->Peek.size());
    return (int32_t)r->Peek.size();
}

// 止める。wait = 書き終えるまで待つ (待たない時は書き終えたら自分で片付く)。戻り = 待った時の結果 (0 = 書けた)
MRP_API int32_t mrp_rec_stop(void* h, int32_t wait)
{
    auto* r = (Rec*)h;
    if (!r) return -1;
    {
        std::lock_guard<std::mutex> lk(g_m);
        if (g_active == r) g_active = nullptr;
        ReleaseStages(r);
        r->Src = nullptr;
    }
    std::thread th = std::move(r->Th); // 待たない時は書き終えたスレッドが r を消すので、先に手元へ移す
    {
        std::lock_guard<std::mutex> lk(r->M);
        r->Stop = true;
        if (!wait) r->AutoDelete = true;
    }
    r->Cv.notify_one();
    if (!wait)
    {
        th.detach();
        return 0;
    }
    th.join();
    int32_t res = r->State == 2 && SUCCEEDED(r->Error) ? 0 : (int32_t)r->Error;
    delete r;
    return res;
}

#elif !defined(__ANDROID__) // Android は recorder_android.cpp

MRP_API void* mrp_rec_start(const char16_t*, int32_t, int32_t, int32_t, int32_t, int32_t, int32_t) { return nullptr; }
MRP_API void mrp_rec_texture(void*, void*, int32_t) {}
MRP_API void* mrp_rec_render_event() { return nullptr; }
MRP_API void mrp_rec_audio(void*, const int16_t*, int32_t) {}
MRP_API void mrp_rec_info(void*, int32_t* out) { if (out) out[0] = -1; }
MRP_API int32_t mrp_rec_peek(void*, uint8_t*, int32_t) { return 0; }
MRP_API int32_t mrp_rec_stop(void*, int32_t) { return -1; }

#endif
