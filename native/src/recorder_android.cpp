#include "mrp_native.h"

// 録画の Android 版 (関数の形は recorder.cpp と同じ)。ゲームの画面とゲームの音を MP4 (H.264 + AAC) に書く。
// - 画面: 描画のスレッドで呼ばれる mrp_rec_render_event が、C# の RT (OpenGL ES のテクスチャ) を FBO に付けて
//   glReadPixels で PBO へ読み (待たない)、2 回後の呼び出しで読み終えた PBO を写し取る。決めたフレームの速さより速く呼ばれた分は読まない
// - 符号化と書き出し: 端末の MediaCodec (H.264 / AAC) と MediaMuxer。専用のスレッドで RGBA を NV12 (だめなら I420) にして渡す
// - libGLESv3 と libmediandk は録画を始める時に dlopen で引く (読めない端末でも部品ごと止まらない)
// Vulkan で動いている時は C# が録画を始めない

#if defined(__ANDROID__)
#include <dlfcn.h>
#include <fcntl.h>
#include <unistd.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <time.h>
#include <vector>

namespace
{
    // ---- OpenGL ES 3 ----
    typedef unsigned int GLuint;
    typedef int GLint;
    typedef unsigned int GLenum;
    typedef int GLsizei;
    typedef unsigned int GLbitfield;
    typedef long GLsizeiptr;
    typedef long GLintptr;
    typedef struct __GLsync* GLsync;
    typedef uint64_t GLuint64;

    const GLenum GL_FRAMEBUFFER = 0x8D40, GL_READ_FRAMEBUFFER = 0x8CA8, GL_READ_FRAMEBUFFER_BINDING = 0x8CAA;
    const GLenum GL_COLOR_ATTACHMENT0 = 0x8CE0, GL_TEXTURE_2D = 0x0DE1, GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
    const GLenum GL_PIXEL_PACK_BUFFER = 0x88EB, GL_PIXEL_PACK_BUFFER_BINDING = 0x88ED, GL_STREAM_READ = 0x88E1;
    const GLenum GL_RGBA = 0x1908, GL_UNSIGNED_BYTE = 0x1401, GL_PACK_ALIGNMENT = 0x0D05;
    const GLbitfield GL_MAP_READ_BIT = 0x0001;
    const GLenum GL_SYNC_GPU_COMMANDS_COMPLETE = 0x9117, GL_TIMEOUT_EXPIRED = 0x911B, GL_WAIT_FAILED = 0x911D;

    struct Gl
    {
        void (*GenFramebuffers)(GLsizei, GLuint*);
        void (*DeleteFramebuffers)(GLsizei, const GLuint*);
        void (*BindFramebuffer)(GLenum, GLuint);
        void (*FramebufferTexture2D)(GLenum, GLenum, GLenum, GLuint, GLint);
        GLenum (*CheckFramebufferStatus)(GLenum);
        void (*GenBuffers)(GLsizei, GLuint*);
        void (*DeleteBuffers)(GLsizei, const GLuint*);
        void (*BindBuffer)(GLenum, GLuint);
        void (*BufferData)(GLenum, GLsizeiptr, const void*, GLenum);
        void (*ReadPixels)(GLint, GLint, GLsizei, GLsizei, GLenum, GLenum, void*);
        void* (*MapBufferRange)(GLenum, GLintptr, GLsizeiptr, GLbitfield);
        unsigned char (*UnmapBuffer)(GLenum);
        void (*GetIntegerv)(GLenum, GLint*);
        void (*PixelStorei)(GLenum, GLint);
        GLsync (*FenceSync)(GLenum, GLbitfield);
        GLenum (*ClientWaitSync)(GLsync, GLbitfield, GLuint64);
        void (*DeleteSync)(GLsync);
    } gl;
    bool g_glTried, g_glOk;

    template <class T> bool Sym(void* lib, const char* name, T& out)
    {
        out = (T)dlsym(lib, name);
        return out != nullptr;
    }

    bool LoadGl()
    {
        if (g_glTried) return g_glOk;
        g_glTried = true;
        void* lib = dlopen("libGLESv3.so", RTLD_NOW);
        if (!lib) return false;
        g_glOk = Sym(lib, "glGenFramebuffers", gl.GenFramebuffers) && Sym(lib, "glDeleteFramebuffers", gl.DeleteFramebuffers)
            && Sym(lib, "glBindFramebuffer", gl.BindFramebuffer) && Sym(lib, "glFramebufferTexture2D", gl.FramebufferTexture2D)
            && Sym(lib, "glCheckFramebufferStatus", gl.CheckFramebufferStatus) && Sym(lib, "glGenBuffers", gl.GenBuffers)
            && Sym(lib, "glDeleteBuffers", gl.DeleteBuffers) && Sym(lib, "glBindBuffer", gl.BindBuffer)
            && Sym(lib, "glBufferData", gl.BufferData) && Sym(lib, "glReadPixels", gl.ReadPixels)
            && Sym(lib, "glMapBufferRange", gl.MapBufferRange) && Sym(lib, "glUnmapBuffer", gl.UnmapBuffer)
            && Sym(lib, "glGetIntegerv", gl.GetIntegerv) && Sym(lib, "glPixelStorei", gl.PixelStorei)
            && Sym(lib, "glFenceSync", gl.FenceSync) && Sym(lib, "glClientWaitSync", gl.ClientWaitSync)
            && Sym(lib, "glDeleteSync", gl.DeleteSync);
        return g_glOk;
    }

    // ---- MediaCodec / MediaMuxer (libmediandk・API 21 から) ----
    struct AMediaCodec;
    struct AMediaFormat;
    struct AMediaMuxer;
    struct AMediaCodecBufferInfo
    {
        int32_t offset, size;
        int64_t presentationTimeUs;
        uint32_t flags;
    };
    const int32_t BUFFER_FLAG_CODEC_CONFIG = 2, BUFFER_FLAG_END_OF_STREAM = 4;
    const int32_t INFO_TRY_AGAIN_LATER = -1, INFO_OUTPUT_FORMAT_CHANGED = -2, INFO_OUTPUT_BUFFERS_CHANGED = -3;
    const int32_t CONFIGURE_FLAG_ENCODE = 1;

    struct Nd
    {
        AMediaFormat* (*FormatNew)();
        int (*FormatDelete)(AMediaFormat*);
        void (*FormatSetString)(AMediaFormat*, const char*, const char*);
        void (*FormatSetInt32)(AMediaFormat*, const char*, int32_t);
        AMediaCodec* (*CreateEncoder)(const char*);
        int (*Configure)(AMediaCodec*, const AMediaFormat*, void*, void*, uint32_t);
        int (*Start)(AMediaCodec*);
        int (*Stop)(AMediaCodec*);
        int (*Delete)(AMediaCodec*);
        ssize_t (*DequeueInput)(AMediaCodec*, int64_t);
        uint8_t* (*GetInput)(AMediaCodec*, size_t, size_t*);
        int (*QueueInput)(AMediaCodec*, size_t, long, size_t, uint64_t, uint32_t);
        ssize_t (*DequeueOutput)(AMediaCodec*, AMediaCodecBufferInfo*, int64_t);
        uint8_t* (*GetOutput)(AMediaCodec*, size_t, size_t*);
        int (*ReleaseOutput)(AMediaCodec*, size_t, bool);
        AMediaFormat* (*GetOutputFormat)(AMediaCodec*);
        AMediaMuxer* (*MuxerNew)(int, int);
        int (*MuxerDelete)(AMediaMuxer*);
        ssize_t (*MuxerAddTrack)(AMediaMuxer*, const AMediaFormat*);
        int (*MuxerStart)(AMediaMuxer*);
        int (*MuxerStop)(AMediaMuxer*);
        int (*MuxerWrite)(AMediaMuxer*, size_t, const uint8_t*, const AMediaCodecBufferInfo*);
    } nd;
    bool g_ndTried, g_ndOk;

    bool LoadNd()
    {
        if (g_ndTried) return g_ndOk;
        g_ndTried = true;
        void* lib = dlopen("libmediandk.so", RTLD_NOW);
        if (!lib) return false;
        g_ndOk = Sym(lib, "AMediaFormat_new", nd.FormatNew) && Sym(lib, "AMediaFormat_delete", nd.FormatDelete)
            && Sym(lib, "AMediaFormat_setString", nd.FormatSetString) && Sym(lib, "AMediaFormat_setInt32", nd.FormatSetInt32)
            && Sym(lib, "AMediaCodec_createEncoderByType", nd.CreateEncoder) && Sym(lib, "AMediaCodec_configure", nd.Configure)
            && Sym(lib, "AMediaCodec_start", nd.Start) && Sym(lib, "AMediaCodec_stop", nd.Stop) && Sym(lib, "AMediaCodec_delete", nd.Delete)
            && Sym(lib, "AMediaCodec_dequeueInputBuffer", nd.DequeueInput) && Sym(lib, "AMediaCodec_getInputBuffer", nd.GetInput)
            && Sym(lib, "AMediaCodec_queueInputBuffer", nd.QueueInput) && Sym(lib, "AMediaCodec_dequeueOutputBuffer", nd.DequeueOutput)
            && Sym(lib, "AMediaCodec_getOutputBuffer", nd.GetOutput) && Sym(lib, "AMediaCodec_releaseOutputBuffer", nd.ReleaseOutput)
            && Sym(lib, "AMediaCodec_getOutputFormat", nd.GetOutputFormat) && Sym(lib, "AMediaMuxer_new", nd.MuxerNew)
            && Sym(lib, "AMediaMuxer_delete", nd.MuxerDelete) && Sym(lib, "AMediaMuxer_addTrack", nd.MuxerAddTrack)
            && Sym(lib, "AMediaMuxer_start", nd.MuxerStart) && Sym(lib, "AMediaMuxer_stop", nd.MuxerStop)
            && Sym(lib, "AMediaMuxer_writeSampleData", nd.MuxerWrite);
        return g_ndOk;
    }

    const size_t MaxQueued = 200u * 1024u * 1024u; // 書き込み待ちの上限 (超えたら画面を捨てる)
    const int Ring = 3;

    struct Item
    {
        int Kind; // 0 = 画面 (RGBA・GL の並び = 下から) / 1 = 音 (16bit)
        std::vector<uint8_t> Data;
        int64_t Time; // 100ns
        size_t Done = 0; // 音: 渡し終えたバイト数
    };

    struct Rec
    {
        std::string Path;
        int W, H, Fps, VideoBitrate, Rate, Channels;

        std::mutex M;
        std::condition_variable Cv;
        std::deque<Item> Q;
        size_t Bytes = 0;
        bool Stop = false, AutoDelete = false;
        std::atomic<int> State{0}; // 0 = 準備中 / 1 = 書いている / 2 = 書き終えた / -1 = 失敗
        std::atomic<int> Frames{0}, Dropped{0}, AudioPeak{0};
        std::atomic<int64_t> AudioFrames{0};
        int Error = 0;
        std::thread Th;

        // 描画のスレッドだけが触る
        GLuint Tex = 0, Fbo = 0;
        GLuint Pbo[Ring] = {};
        GLsync Fence[Ring] = {};
        bool Pending[Ring] = {};
        int64_t PboTime[Ring] = {};
        int Head = 0;
        bool Ready = false;
        int64_t NextFrame = 0, FrameDur = 0;

        int64_t T0 = 0;
        std::atomic<int64_t> AudioBase{-1};
        bool Flip = true;

        std::mutex PeekM;
        std::vector<uint8_t> Peek;
        std::atomic<bool> PeekWant{false};
    };

    std::mutex g_m;
    Rec* g_active = nullptr;
    // 止めた録画の GL の物 (描画のスレッドでしか消せないので、次の描画のスレッドの呼び出しで消す)
    std::vector<GLuint> g_deadFbo, g_deadPbo;
    std::vector<GLsync> g_deadSync;

    int64_t Mono100ns()
    {
        timespec t;
        clock_gettime(CLOCK_MONOTONIC, &t);
        return (int64_t)t.tv_sec * 10000000LL + t.tv_nsec / 100;
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

    // GL の物を捨てる側へ回す (g_m を持って呼ぶ)
    void Retire(Rec* r)
    {
        if (r->Fbo) g_deadFbo.push_back(r->Fbo);
        for (int i = 0; i < Ring; i++)
        {
            if (r->Pbo[i]) g_deadPbo.push_back(r->Pbo[i]);
            if (r->Fence[i]) g_deadSync.push_back(r->Fence[i]);
            r->Pbo[i] = 0;
            r->Fence[i] = nullptr;
            r->Pending[i] = false;
        }
        r->Fbo = 0;
        r->Ready = false;
    }

    void FreeDead()
    {
        if (!g_glOk) return;
        for (GLuint f : g_deadFbo) gl.DeleteFramebuffers(1, &f);
        for (GLuint b : g_deadPbo) gl.DeleteBuffers(1, &b);
        for (GLsync s : g_deadSync) gl.DeleteSync(s);
        g_deadFbo.clear();
        g_deadPbo.clear();
        g_deadSync.clear();
    }

    // ---- 描画のスレッド ----

    // 読み終えた PBO を古い順に写し取る (wait = 一番古い物は待ってでも)
    void Drain(Rec* r, bool wait)
    {
        size_t bytes = (size_t)r->W * r->H * 4;
        for (int k = 0; k < Ring; k++)
        {
            int i = (r->Head + k) % Ring;
            if (!r->Pending[i]) continue;
            GLenum st = gl.ClientWaitSync(r->Fence[i], 0, wait && k == 0 ? 50000000ULL : 0);
            if (st == GL_TIMEOUT_EXPIRED) return;
            gl.DeleteSync(r->Fence[i]);
            r->Fence[i] = nullptr;
            r->Pending[i] = false;
            if (st == GL_WAIT_FAILED) continue;
            gl.BindBuffer(GL_PIXEL_PACK_BUFFER, r->Pbo[i]);
            void* p = gl.MapBufferRange(GL_PIXEL_PACK_BUFFER, 0, (GLsizeiptr)bytes, GL_MAP_READ_BIT);
            if (p)
            {
                Item it;
                it.Kind = 0;
                it.Time = r->PboTime[i];
                it.Data.resize(bytes);
                memcpy(it.Data.data(), p, bytes);
                gl.UnmapBuffer(GL_PIXEL_PACK_BUFFER);
                r->Frames++;
                Push(r, std::move(it), true);
            }
        }
    }

    void Capture(Rec* r)
    {
        GLint prevRead = 0, prevPack = 0;
        gl.GetIntegerv(GL_READ_FRAMEBUFFER_BINDING, &prevRead);
        gl.GetIntegerv(GL_PIXEL_PACK_BUFFER_BINDING, &prevPack);
        if (!r->Ready)
        {
            gl.GenFramebuffers(1, &r->Fbo);
            gl.BindFramebuffer(GL_READ_FRAMEBUFFER, r->Fbo);
            gl.FramebufferTexture2D(GL_READ_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, r->Tex, 0);
            if (gl.CheckFramebufferStatus(GL_READ_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE)
            {
                r->Error = 1;
                r->State = -1;
                gl.BindFramebuffer(GL_READ_FRAMEBUFFER, (GLuint)prevRead);
                return;
            }
            gl.GenBuffers(Ring, r->Pbo);
            for (int i = 0; i < Ring; i++)
            {
                gl.BindBuffer(GL_PIXEL_PACK_BUFFER, r->Pbo[i]);
                gl.BufferData(GL_PIXEL_PACK_BUFFER, (GLsizeiptr)r->W * r->H * 4, nullptr, GL_STREAM_READ);
            }
            r->Ready = true;
        }
        Drain(r, false);
        int64_t now = Mono100ns() - r->T0;
        if (now >= r->NextFrame)
        {
            r->NextFrame += r->FrameDur;
            if (r->NextFrame < now) r->NextFrame = now + r->FrameDur; // 遅れた時は追いかけない
            int i = r->Head;
            if (r->Pending[i]) Drain(r, true); // 輪が埋まっている (GPU が遅い): 一番古い物を待って読む
            if (!r->Pending[i])
            {
                gl.BindFramebuffer(GL_READ_FRAMEBUFFER, r->Fbo);
                gl.BindBuffer(GL_PIXEL_PACK_BUFFER, r->Pbo[i]);
                gl.PixelStorei(GL_PACK_ALIGNMENT, 4);
                gl.ReadPixels(0, 0, r->W, r->H, GL_RGBA, GL_UNSIGNED_BYTE, nullptr);
                r->Fence[i] = gl.FenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
                r->Pending[i] = true;
                r->PboTime[i] = (now / r->FrameDur) * r->FrameDur;
                r->Head = (r->Head + 1) % Ring;
            }
            else r->Dropped++;
        }
        gl.BindBuffer(GL_PIXEL_PACK_BUFFER, (GLuint)prevPack);
        gl.BindFramebuffer(GL_READ_FRAMEBUFFER, (GLuint)prevRead);
    }

    // ---- 書き込みのスレッド ----

    // RGBA (GL の並び) を NV12 / I420 (上から) にする。BT.601 の狭い範囲
    void ToYuv(const Rec* r, const uint8_t* rgba, uint8_t* out, bool nv12)
    {
        int w = r->W, h = r->H;
        uint8_t* y0 = out;
        uint8_t* u0 = out + (size_t)w * h;
        uint8_t* v0 = u0 + (size_t)(w / 2) * (h / 2);
        for (int y = 0; y < h; y++)
        {
            const uint8_t* s = rgba + (size_t)w * 4 * (r->Flip ? (h - 1 - y) : y);
            uint8_t* d = y0 + (size_t)w * y;
            for (int x = 0; x < w; x++)
            {
                int R = s[x * 4], G = s[x * 4 + 1], B = s[x * 4 + 2];
                d[x] = (uint8_t)(((66 * R + 129 * G + 25 * B + 128) >> 8) + 16);
            }
            if (y & 1) continue;
            for (int x = 0; x < w; x += 2)
            {
                int R = s[x * 4], G = s[x * 4 + 1], B = s[x * 4 + 2];
                int U = ((-38 * R - 74 * G + 112 * B + 128) >> 8) + 128;
                int V = ((112 * R - 94 * G - 18 * B + 128) >> 8) + 128;
                size_t c = (size_t)(y / 2) * (w / 2) + x / 2;
                if (nv12)
                {
                    u0[c * 2] = (uint8_t)U;
                    u0[c * 2 + 1] = (uint8_t)V;
                }
                else
                {
                    u0[c] = (uint8_t)U;
                    v0[c] = (uint8_t)V;
                }
            }
        }
    }

    struct Out
    {
        AMediaCodec* Codec = nullptr;
        ssize_t Track = -1;
        bool Eos = false;
        int64_t LastUs = -1;
    };

    AMediaCodec* MakeVideo(Rec* r, bool* nv12)
    {
        const int32_t colors[] = {21, 19}; // COLOR_FormatYUV420SemiPlanar (NV12) / Planar (I420)
        for (int32_t color : colors)
        {
            AMediaCodec* c = nd.CreateEncoder("video/avc");
            if (!c) return nullptr;
            AMediaFormat* f = nd.FormatNew();
            nd.FormatSetString(f, "mime", "video/avc");
            nd.FormatSetInt32(f, "width", r->W);
            nd.FormatSetInt32(f, "height", r->H);
            nd.FormatSetInt32(f, "bitrate", r->VideoBitrate);
            nd.FormatSetInt32(f, "frame-rate", r->Fps);
            nd.FormatSetInt32(f, "i-frame-interval", 2);
            nd.FormatSetInt32(f, "color-format", color);
            int err = nd.Configure(c, f, nullptr, nullptr, CONFIGURE_FLAG_ENCODE);
            nd.FormatDelete(f);
            if (err == 0 && nd.Start(c) == 0)
            {
                *nv12 = color == 21;
                return c;
            }
            nd.Delete(c);
        }
        return nullptr;
    }

    AMediaCodec* MakeAudio(Rec* r)
    {
        AMediaCodec* c = nd.CreateEncoder("audio/mp4a-latm");
        if (!c) return nullptr;
        AMediaFormat* f = nd.FormatNew();
        nd.FormatSetString(f, "mime", "audio/mp4a-latm");
        nd.FormatSetInt32(f, "sample-rate", r->Rate);
        nd.FormatSetInt32(f, "channel-count", r->Channels);
        nd.FormatSetInt32(f, "bitrate", 192000);
        nd.FormatSetInt32(f, "aac-profile", 2); // AAC LC
        nd.FormatSetInt32(f, "max-input-size", 16384);
        int err = nd.Configure(c, f, nullptr, nullptr, CONFIGURE_FLAG_ENCODE);
        nd.FormatDelete(f);
        if (err == 0 && nd.Start(c) == 0) return c;
        nd.Delete(c);
        return nullptr;
    }

    // 符号器から出てきた物を MP4 へ (両方の形が決まるまで書き出しは始められない)
    void DrainOut(Out& o, Out& other, AMediaMuxer* mux, bool& started)
    {
        if (!o.Codec || o.Eos) return;
        while (true)
        {
            AMediaCodecBufferInfo info{};
            ssize_t idx = nd.DequeueOutput(o.Codec, &info, 0);
            if (idx == INFO_TRY_AGAIN_LATER) return;
            if (idx == INFO_OUTPUT_FORMAT_CHANGED)
            {
                AMediaFormat* f = nd.GetOutputFormat(o.Codec);
                if (f && !started) o.Track = nd.MuxerAddTrack(mux, f);
                if (f) nd.FormatDelete(f);
                if (o.Track >= 0 && other.Track >= 0 && !started) started = nd.MuxerStart(mux) == 0;
                continue;
            }
            if (idx == INFO_OUTPUT_BUFFERS_CHANGED) continue;
            if (idx < 0) return;
            size_t cap = 0;
            uint8_t* p = nd.GetOutput(o.Codec, (size_t)idx, &cap);
            bool config = (info.flags & BUFFER_FLAG_CODEC_CONFIG) != 0;
            // 時刻が戻る物・書き出しを始める前の物は書けない
            if (p && started && !config && info.size > 0 && info.presentationTimeUs > o.LastUs)
            {
                nd.MuxerWrite(mux, (size_t)o.Track, p, &info);
                o.LastUs = info.presentationTimeUs;
            }
            nd.ReleaseOutput(o.Codec, (size_t)idx, false);
            if (info.flags & BUFFER_FLAG_END_OF_STREAM)
            {
                o.Eos = true;
                return;
            }
        }
    }

    // 1 つを符号器へ渡す。渡し切れなければ false (入口が空くまで待つ)
    bool Feed(Rec* r, Out& video, Out& audio, Item& it, bool nv12)
    {
        Out& o = it.Kind == 0 ? video : audio;
        if (!o.Codec) return true;
        ssize_t idx = nd.DequeueInput(o.Codec, 5000);
        if (idx < 0) return false;
        size_t cap = 0;
        uint8_t* p = nd.GetInput(o.Codec, (size_t)idx, &cap);
        if (!p)
        {
            nd.QueueInput(o.Codec, (size_t)idx, 0, 0, 0, 0);
            return true;
        }
        if (it.Kind == 0)
        {
            size_t need = (size_t)r->W * r->H * 3 / 2;
            if (cap < need)
            {
                nd.QueueInput(o.Codec, (size_t)idx, 0, 0, (uint64_t)(it.Time / 10), 0);
                return true;
            }
            ToYuv(r, it.Data.data(), p, nv12);
            if (r->PeekWant.exchange(false))
            {
                // 確かめ用: BGRA の上から下にして控える
                std::lock_guard<std::mutex> lk(r->PeekM);
                size_t row = (size_t)r->W * 4;
                r->Peek.resize(row * r->H);
                for (int y = 0; y < r->H; y++)
                {
                    const uint8_t* s = it.Data.data() + row * (r->Flip ? (r->H - 1 - y) : y);
                    uint8_t* d = r->Peek.data() + row * y;
                    for (int x = 0; x < r->W; x++)
                    {
                        d[x * 4] = s[x * 4 + 2];
                        d[x * 4 + 1] = s[x * 4 + 1];
                        d[x * 4 + 2] = s[x * 4];
                        d[x * 4 + 3] = 255;
                    }
                }
            }
            nd.QueueInput(o.Codec, (size_t)idx, 0, need, (uint64_t)(it.Time / 10), 0);
            return true;
        }
        size_t frame = (size_t)r->Channels * 2;
        size_t left = it.Data.size() - it.Done;
        size_t n = std::min(left, cap / frame * frame);
        memcpy(p, it.Data.data() + it.Done, n);
        int64_t t = it.Time + (int64_t)(it.Done / frame) * 10000000LL / r->Rate;
        nd.QueueInput(o.Codec, (size_t)idx, 0, n, (uint64_t)(t / 10), 0);
        it.Done += n;
        return it.Done >= it.Data.size();
    }

    void Worker(Rec* r)
    {
        int fd = -1;
        AMediaMuxer* mux = nullptr;
        Out video, audio;
        bool nv12 = true, started = false;
        if (LoadNd())
        {
            fd = open(r->Path.c_str(), O_CREAT | O_TRUNC | O_RDWR, 0644);
            if (fd >= 0) mux = nd.MuxerNew(fd, 0); // 0 = MPEG_4
            if (mux)
            {
                video.Codec = MakeVideo(r, &nv12);
                audio.Codec = MakeAudio(r);
            }
        }
        if (!mux || !video.Codec || !audio.Codec)
        {
            r->Error = !LoadNd() ? 2 : fd < 0 ? 3 : !mux ? 4 : !video.Codec ? 5 : 6;
            r->State = -1;
        }
        else r->State = 1;

        bool sentEos = false;
        while (true)
        {
            Item it;
            bool have = false, stopping;
            {
                std::unique_lock<std::mutex> lk(r->M);
                r->Cv.wait_for(lk, std::chrono::milliseconds(10), [r] { return r->Stop || !r->Q.empty(); });
                stopping = r->Stop;
                if (!r->Q.empty())
                {
                    it = std::move(r->Q.front());
                    r->Q.pop_front();
                    have = true;
                }
            }
            if (r->State < 0)
            {
                if (have) { std::lock_guard<std::mutex> lk(r->M); r->Bytes -= it.Data.size(); }
                if (stopping && !have) break;
                continue;
            }
            if (have)
            {
                size_t size = it.Data.size();
                // 入口が空くまで出口を流しながら渡す (止める時に溜まった分も書き切る)
                for (int tries = 0; tries < 400; tries++)
                {
                    if (Feed(r, video, audio, it, nv12)) break;
                    DrainOut(video, audio, mux, started);
                    DrainOut(audio, video, mux, started);
                }
                std::lock_guard<std::mutex> lk(r->M);
                r->Bytes -= size;
            }
            DrainOut(video, audio, mux, started);
            DrainOut(audio, video, mux, started);
            if (stopping && !have)
            {
                if (!sentEos)
                {
                    sentEos = true;
                    for (Out* o : {&video, &audio})
                    {
                        ssize_t idx = -1;
                        for (int k = 0; k < 100 && idx < 0; k++) idx = nd.DequeueInput(o->Codec, 10000);
                        if (idx >= 0) nd.QueueInput(o->Codec, (size_t)idx, 0, 0, 0, BUFFER_FLAG_END_OF_STREAM);
                        else o->Eos = true;
                    }
                }
                // 終わりの印が両方から出るまで流す (止まらない符号器のために 3 秒で切る)
                for (int k = 0; k < 300 && !(video.Eos && audio.Eos); k++)
                {
                    DrainOut(video, audio, mux, started);
                    DrainOut(audio, video, mux, started);
                    if (!(video.Eos && audio.Eos)) usleep(10000);
                }
                break;
            }
        }
        for (Out* o : {&video, &audio})
            if (o->Codec)
            {
                nd.Stop(o->Codec);
                nd.Delete(o->Codec);
            }
        if (mux)
        {
            if (started) nd.MuxerStop(mux);
            nd.MuxerDelete(mux);
        }
        if (fd >= 0) close(fd);
        if (r->State == 1) r->State = started ? 2 : -1;
        bool del;
        {
            std::lock_guard<std::mutex> lk(r->M);
            del = r->AutoDelete;
        }
        if (del) delete r;
    }

    std::string Utf8(const char16_t* s)
    {
        std::string out;
        for (; *s; s++)
        {
            uint32_t c = *s;
            if (c >= 0xD800 && c < 0xDC00 && s[1] >= 0xDC00 && s[1] < 0xE000)
            {
                c = 0x10000 + ((c - 0xD800) << 10) + (s[1] - 0xDC00);
                s++;
            }
            if (c < 0x80) out += (char)c;
            else if (c < 0x800) { out += (char)(0xC0 | c >> 6); out += (char)(0x80 | (c & 63)); }
            else if (c < 0x10000) { out += (char)(0xE0 | c >> 12); out += (char)(0x80 | (c >> 6 & 63)); out += (char)(0x80 | (c & 63)); }
            else { out += (char)(0xF0 | c >> 18); out += (char)(0x80 | (c >> 12 & 63)); out += (char)(0x80 | (c >> 6 & 63)); out += (char)(0x80 | (c & 63)); }
        }
        return out;
    }
}

MRP_API void* mrp_rec_start(const char16_t* path, int32_t w, int32_t h, int32_t fps, int32_t videoBitrate, int32_t rate, int32_t channels)
{
    if (!path || w <= 0 || h <= 0 || (w & 1) || (h & 1) || fps <= 0 || (rate != 44100 && rate != 48000) || channels < 1 || channels > 2) return nullptr;
    if (!LoadNd()) return nullptr;
    auto* r = new Rec();
    r->Path = Utf8(path);
    r->W = w;
    r->H = h;
    r->Fps = fps;
    r->VideoBitrate = videoBitrate;
    r->Rate = rate;
    r->Channels = channels;
    r->FrameDur = 10000000LL / fps;
    r->T0 = Mono100ns();
    r->Th = std::thread(Worker, r);
    {
        std::lock_guard<std::mutex> lk(g_m);
        g_active = r;
    }
    return r;
}

// 画面を写す元 (C# の RT の GL のテクスチャの番号)。flip = 上下を入れ替えて読む (GL は下から並ぶので普通は 1)
MRP_API void mrp_rec_texture(void* h, void* tex, int32_t flip)
{
    auto* r = (Rec*)h;
    if (!r) return;
    std::lock_guard<std::mutex> lk(g_m);
    if (r->Tex != (GLuint)(uintptr_t)tex) Retire(r);
    r->Tex = (GLuint)(uintptr_t)tex;
    r->Flip = flip != 0;
}

// 描画のスレッドから。1 = 画面を読む / 2 = 止めた録画の GL の物を片付ける
static void RenderEvent(int id)
{
    std::lock_guard<std::mutex> lk(g_m);
    if (!LoadGl()) return;
    FreeDead();
    if (id != 1) return;
    Rec* r = g_active;
    if (!r || !r->Tex || r->State < 0) return;
    Capture(r);
}

MRP_API void* mrp_rec_render_event() { return (void*)&RenderEvent; }

MRP_API void mrp_rec_audio(void* h, const int16_t* pcm, int32_t frames)
{
    auto* r = (Rec*)h;
    if (!r || !pcm || frames <= 0 || r->State < 0) return;
    int64_t base = r->AudioBase.load();
    if (base < 0)
    {
        base = Mono100ns() - r->T0;
        r->AudioBase = base;
    }
    int64_t done = r->AudioFrames.fetch_add(frames);
    Item it;
    it.Kind = 1;
    it.Time = base + done * 10000000LL / r->Rate;
    it.Data.resize((size_t)frames * r->Channels * 2);
    memcpy(it.Data.data(), pcm, it.Data.size());
    int peak = 0;
    for (int i = 0; i < frames * r->Channels; i++) peak = std::max(peak, std::abs((int)pcm[i]));
    if (peak > r->AudioPeak) r->AudioPeak = peak;
    Push(r, std::move(it), false);
}

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
    out[5] = r->Error;
}

// 確かめ用: 最後に読んだ画面 (BGRA・上から下)。まだ無い時は次の画面を控えるよう頼んで 0
MRP_API int32_t mrp_rec_peek(void* h, uint8_t* out, int32_t size)
{
    auto* r = (Rec*)h;
    if (!r || !out) return 0;
    std::lock_guard<std::mutex> lk(r->PeekM);
    if (r->Peek.empty() || (int32_t)r->Peek.size() > size)
    {
        r->PeekWant = true;
        return 0;
    }
    int32_t n = (int32_t)r->Peek.size();
    memcpy(out, r->Peek.data(), n);
    r->Peek.clear();
    return n;
}

MRP_API int32_t mrp_rec_stop(void* h, int32_t wait)
{
    auto* r = (Rec*)h;
    if (!r) return -1;
    {
        std::lock_guard<std::mutex> lk(g_m);
        if (g_active == r) g_active = nullptr;
        Retire(r);
        r->Tex = 0;
    }
    std::thread th = std::move(r->Th);
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
    int32_t res = r->State == 2 ? 0 : (r->Error ? r->Error : -1);
    delete r;
    return res;
}

#endif
