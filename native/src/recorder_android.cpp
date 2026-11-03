#include "mrp_native.h"

// 録画の Android 版 (関数の形は recorder.cpp と同じ)。ゲームの画面とゲームの音を MP4 (H.264 + AAC) に書く。
// - 画面: ゲームの Unity は描いた画面を読む口が削られていて、描画も別のスレッド (Vulkan の端末も多い) なので、端末の画面録画
//   (MediaProjection) を使う。H.264 の符号器の入口を Surface にして、ランチャーの ScreenRecorder (Java) に渡すと、
//   ランチャーが許可の画面を出し、画面を仮想の画面としてその Surface へ映す。符号器は届いた画面を自分で符号にする
// - 音: C# がゲームの音の出口で受けた音を mrp_rec_audio で渡す (ボイスチャットの声を入れない設定も効く)
// - 符号化と書き出し: 端末の MediaCodec (H.264 / AAC) と MediaMuxer を専用のスレッドで回す
// - libmediandk・libandroid・Java は録画を始める時に引く (無い端末・古いランチャーでも部品ごと止まらない)

#if defined(__ANDROID__)
#include <jni.h>
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
    template <class T> bool Sym(void* lib, const char* name, T& out)
    {
        out = (T)dlsym(lib, name);
        return out != nullptr;
    }

    // ---- MediaCodec / MediaMuxer (libmediandk) ----
    struct AMediaCodec;
    struct AMediaFormat;
    struct AMediaMuxer;
    struct ANativeWindow;
    struct AMediaCodecBufferInfo
    {
        int32_t offset, size;
        int64_t presentationTimeUs;
        uint32_t flags;
    };
    const int32_t BUFFER_FLAG_CODEC_CONFIG = 2, BUFFER_FLAG_END_OF_STREAM = 4;
    const int32_t INFO_TRY_AGAIN_LATER = -1, INFO_OUTPUT_FORMAT_CHANGED = -2, INFO_OUTPUT_BUFFERS_CHANGED = -3;
    const int32_t CONFIGURE_FLAG_ENCODE = 1;
    const int32_t COLOR_FormatSurface = 0x7F000789;

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
        int (*CreateInputSurface)(AMediaCodec*, ANativeWindow**);
        int (*SignalEndOfInput)(AMediaCodec*);
        AMediaMuxer* (*MuxerNew)(int, int);
        int (*MuxerDelete)(AMediaMuxer*);
        ssize_t (*MuxerAddTrack)(AMediaMuxer*, const AMediaFormat*);
        int (*MuxerStart)(AMediaMuxer*);
        int (*MuxerStop)(AMediaMuxer*);
        int (*MuxerWrite)(AMediaMuxer*, size_t, const uint8_t*, const AMediaCodecBufferInfo*);
        jobject (*WindowToSurface)(JNIEnv*, ANativeWindow*);
        void (*WindowRelease)(ANativeWindow*);
    } nd;
    bool g_ndTried, g_ndOk;

    bool LoadNd()
    {
        if (g_ndTried) return g_ndOk;
        g_ndTried = true;
        void* lib = dlopen("libmediandk.so", RTLD_NOW);
        void* and_ = dlopen("libandroid.so", RTLD_NOW);
        if (!lib || !and_) return false;
        g_ndOk = Sym(lib, "AMediaFormat_new", nd.FormatNew) && Sym(lib, "AMediaFormat_delete", nd.FormatDelete)
            && Sym(lib, "AMediaFormat_setString", nd.FormatSetString) && Sym(lib, "AMediaFormat_setInt32", nd.FormatSetInt32)
            && Sym(lib, "AMediaCodec_createEncoderByType", nd.CreateEncoder) && Sym(lib, "AMediaCodec_configure", nd.Configure)
            && Sym(lib, "AMediaCodec_start", nd.Start) && Sym(lib, "AMediaCodec_stop", nd.Stop) && Sym(lib, "AMediaCodec_delete", nd.Delete)
            && Sym(lib, "AMediaCodec_dequeueInputBuffer", nd.DequeueInput) && Sym(lib, "AMediaCodec_getInputBuffer", nd.GetInput)
            && Sym(lib, "AMediaCodec_queueInputBuffer", nd.QueueInput) && Sym(lib, "AMediaCodec_dequeueOutputBuffer", nd.DequeueOutput)
            && Sym(lib, "AMediaCodec_getOutputBuffer", nd.GetOutput) && Sym(lib, "AMediaCodec_releaseOutputBuffer", nd.ReleaseOutput)
            && Sym(lib, "AMediaCodec_getOutputFormat", nd.GetOutputFormat)
            && Sym(lib, "AMediaCodec_createInputSurface", nd.CreateInputSurface)
            && Sym(lib, "AMediaCodec_signalEndOfInputStream", nd.SignalEndOfInput) && Sym(lib, "AMediaMuxer_new", nd.MuxerNew)
            && Sym(lib, "AMediaMuxer_delete", nd.MuxerDelete) && Sym(lib, "AMediaMuxer_addTrack", nd.MuxerAddTrack)
            && Sym(lib, "AMediaMuxer_start", nd.MuxerStart) && Sym(lib, "AMediaMuxer_stop", nd.MuxerStop)
            && Sym(lib, "AMediaMuxer_writeSampleData", nd.MuxerWrite)
            && Sym(and_, "ANativeWindow_toSurface", nd.WindowToSurface) && Sym(and_, "ANativeWindow_release", nd.WindowRelease);
        return g_ndOk;
    }

    // ---- Java (ランチャーの ScreenRecorder) ----
    // JavaVM は Android 12 からは libnativehelper が渡す。それより前はランチャーの部品 (libfusion) から借りる
    JavaVM* g_vm;
    // ランチャーの関数で VM を借りた時、そのスレッドは関数が Java につないでいる (終わる時に外す)
    thread_local bool t_attachedByLauncher;

    JavaVM* Vm()
    {
        if (g_vm) return g_vm;
        if (void* lib = dlopen("libnativehelper.so", RTLD_NOW))
        {
            jint (*getVms)(JavaVM**, jsize, jsize*) = nullptr;
            if (Sym(lib, "JNI_GetCreatedJavaVMs", getVms))
            {
                JavaVM* vm = nullptr;
                jsize n = 0;
                if (getVms(&vm, 1, &n) == JNI_OK && n > 0) g_vm = vm;
            }
        }
        if (!g_vm)
            if (void* lib = dlopen("libfusion.so", RTLD_NOW | RTLD_NOLOAD))
            {
                JNIEnv* (*getEnv)() = nullptr;
                if (Sym(lib, "_Z9getJNIEnvv", getEnv))
                    if (JNIEnv* env = getEnv())
                        if (env->GetJavaVM(&g_vm) == JNI_OK && g_vm) t_attachedByLauncher = true;
            }
        return g_vm;
    }

    struct Java
    {
        JNIEnv* Env = nullptr;
        jclass Cls = nullptr;
        jmethodID Start = nullptr, Stop = nullptr, State = nullptr, Publish = nullptr;
        bool Attached = false;
    };

    bool Clear(JNIEnv* env)
    {
        if (!env->ExceptionCheck()) return false;
        env->ExceptionClear();
        return true;
    }

    // このスレッドを Java につないで ScreenRecorder を引く (ランチャーの物なのでアプリのクラスローダーから)
    bool OpenJava(Java& j)
    {
        JavaVM* vm = Vm();
        if (!vm) return false;
        if (vm->GetEnv((void**)&j.Env, JNI_VERSION_1_6) != JNI_OK)
        {
            if (vm->AttachCurrentThread(&j.Env, nullptr) != JNI_OK) return false;
            j.Attached = true;
        }
        else if (t_attachedByLauncher) j.Attached = true; // このスレッドはランチャーの関数がつないだ
        t_attachedByLauncher = false;
        JNIEnv* e = j.Env;
        jclass at = e->FindClass("android/app/ActivityThread");
        if (Clear(e) || !at) return false;
        jmethodID cur = e->GetStaticMethodID(at, "currentApplication", "()Landroid/app/Application;");
        jobject app = cur ? e->CallStaticObjectMethod(at, cur) : nullptr;
        if (Clear(e) || !app) return false;
        jclass ctx = e->FindClass("android/content/Context");
        jmethodID getCl = ctx ? e->GetMethodID(ctx, "getClassLoader", "()Ljava/lang/ClassLoader;") : nullptr;
        if (Clear(e) || !getCl) return false;
        jobject cl = e->CallObjectMethod(app, getCl);
        if (Clear(e) || !cl) return false;
        jclass clc = e->FindClass("java/lang/ClassLoader");
        jmethodID load = clc ? e->GetMethodID(clc, "loadClass", "(Ljava/lang/String;)Ljava/lang/Class;") : nullptr;
        if (Clear(e) || !load) return false;
        jstring name = e->NewStringUTF("dev.allofus.fusioncore.ScreenRecorder");
        auto cls = (jclass)e->CallObjectMethod(cl, load, name);
        e->DeleteLocalRef(name);
        e->DeleteLocalRef(at);
        e->DeleteLocalRef(app);
        e->DeleteLocalRef(ctx);
        e->DeleteLocalRef(cl);
        e->DeleteLocalRef(clc);
        if (Clear(e) || !cls) return false; // 古いランチャー
        j.Cls = (jclass)e->NewGlobalRef(cls);
        e->DeleteLocalRef(cls);
        j.Start = e->GetStaticMethodID(j.Cls, "start", "(Landroid/view/Surface;II)V");
        j.Stop = e->GetStaticMethodID(j.Cls, "stop", "()V");
        j.State = e->GetStaticMethodID(j.Cls, "state", "()I");
        j.Publish = e->GetStaticMethodID(j.Cls, "publish", "(Ljava/lang/String;)Z");
        if (Clear(e) || !j.Start || !j.Stop || !j.State) return false;
        return true;
    }

    void CloseJava(Java& j)
    {
        if (j.Env && j.Cls) j.Env->DeleteGlobalRef(j.Cls);
        j.Cls = nullptr;
        if (j.Attached && g_vm) g_vm->DetachCurrentThread();
        j.Env = nullptr;
        j.Attached = false;
    }

    const size_t MaxQueued = 64u * 1024u * 1024u; // 書き込み待ちの上限 (音だけなので小さい)

    struct Item
    {
        std::vector<uint8_t> Data; // 音 (16bit)
        int64_t Time; // 100ns
        size_t Done = 0; // 渡し終えたバイト数
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
        // 0 = 準備中 / 1 = 書いている / 2 = 書き終えた / 3 = 端末の側で画面録画が止められた (C# が止めて保存する) / -1 = 失敗
        std::atomic<int> State{0};
        std::atomic<int> Frames{0}, Dropped{0}, AudioPeak{0};
        std::atomic<int64_t> AudioFrames{0};
        // 失敗の種類: 2 = libmediandk / 3 = ファイル / 4 = MP4 / 5 = 画面の符号器 / 6 = 音の符号器 /
        // 7 = 画面の録画を断られた / 8 = ランチャーが画面の録画を始められない / 9 = 古いランチャー (Java の部品が無い)
        int Error = 0;
        std::thread Th;

        int64_t T0 = 0;
        std::atomic<int64_t> AudioBase{-1};
    };

    int64_t Mono100ns()
    {
        timespec t;
        clock_gettime(CLOCK_MONOTONIC, &t);
        return (int64_t)t.tv_sec * 10000000LL + t.tv_nsec / 100;
    }

    void Push(Rec* r, Item&& it)
    {
        {
            std::lock_guard<std::mutex> lk(r->M);
            if (r->Stop) return;
            if (r->Bytes + it.Data.size() > MaxQueued)
            {
                r->Dropped++;
                return;
            }
            r->Bytes += it.Data.size();
            r->Q.push_back(std::move(it));
        }
        r->Cv.notify_one();
    }

    // ---- 書き込みのスレッド ----

    struct Out
    {
        AMediaCodec* Codec = nullptr;
        ssize_t Track = -1;
        bool Eos = false;
        int64_t LastUs = -1;
        int64_t BaseUs = 0; // 出てきた時刻から引く値 (画面は端末の時計そのままで届くので録画の始まりを引く)
    };

    // 入口が Surface の H.264 の符号器 (画面は Surface へ映った物を符号器が自分で読む)
    AMediaCodec* MakeVideo(Rec* r, ANativeWindow** win)
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
        nd.FormatSetInt32(f, "color-format", COLOR_FormatSurface);
        // 画面が止まっていても一定の間隔で画を出させる (静かな場面で動画が途切れないように)
        nd.FormatSetInt32(f, "repeat-previous-frame-after", 1000000 / r->Fps);
        int err = nd.Configure(c, f, nullptr, nullptr, CONFIGURE_FLAG_ENCODE);
        nd.FormatDelete(f);
        if (err == 0 && nd.CreateInputSurface(c, win) == 0 && *win && nd.Start(c) == 0) return c;
        if (*win)
        {
            nd.WindowRelease(*win);
            *win = nullptr;
        }
        nd.Delete(c);
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

    // 符号器から出てきた物を MP4 へ (両方の形が決まるまで書き出しは始められない)。frames = 画面の数を数える先
    void DrainOut(Out& o, Out& other, AMediaMuxer* mux, bool& started, std::atomic<int>* frames)
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
            info.presentationTimeUs -= o.BaseUs;
            // 時刻が戻る物・録画の前の物・書き出しを始める前の物は書けない
            if (p && started && !config && info.size > 0 && info.presentationTimeUs >= 0 && info.presentationTimeUs > o.LastUs)
            {
                nd.MuxerWrite(mux, (size_t)o.Track, p, &info);
                o.LastUs = info.presentationTimeUs;
                if (frames) (*frames)++;
            }
            nd.ReleaseOutput(o.Codec, (size_t)idx, false);
            if (info.flags & BUFFER_FLAG_END_OF_STREAM)
            {
                o.Eos = true;
                return;
            }
        }
    }

    // 音を 1 つ符号器へ渡す。渡し切れなければ false (入口が空くまで待つ)
    bool Feed(Rec* r, Out& audio, Item& it)
    {
        if (!audio.Codec) return true;
        ssize_t idx = nd.DequeueInput(audio.Codec, 5000);
        if (idx < 0) return false;
        size_t cap = 0;
        uint8_t* p = nd.GetInput(audio.Codec, (size_t)idx, &cap);
        if (!p)
        {
            nd.QueueInput(audio.Codec, (size_t)idx, 0, 0, 0, 0);
            return true;
        }
        size_t frame = (size_t)r->Channels * 2;
        size_t left = it.Data.size() - it.Done;
        size_t n = std::min(left, cap / frame * frame);
        memcpy(p, it.Data.data() + it.Done, n);
        int64_t t = it.Time + (int64_t)(it.Done / frame) * 10000000LL / r->Rate;
        nd.QueueInput(audio.Codec, (size_t)idx, 0, n, (uint64_t)(t / 10), 0);
        it.Done += n;
        return it.Done >= it.Data.size();
    }

    void Fail(Rec* r, int error)
    {
        r->Error = error;
        r->State = -1;
    }

    void Worker(Rec* r)
    {
        int fd = -1;
        AMediaMuxer* mux = nullptr;
        Out video, audio;
        ANativeWindow* win = nullptr;
        jobject surface = nullptr;
        Java java;
        bool started = false, javaStarted = false;
        if (LoadNd())
        {
            fd = open(r->Path.c_str(), O_CREAT | O_TRUNC | O_RDWR, 0644);
            if (fd >= 0) mux = nd.MuxerNew(fd, 0); // 0 = MPEG_4
            if (mux)
            {
                video.Codec = MakeVideo(r, &win);
                audio.Codec = MakeAudio(r);
            }
        }
        if (!mux || !video.Codec || !audio.Codec) Fail(r, !LoadNd() ? 2 : fd < 0 ? 3 : !mux ? 4 : !video.Codec ? 5 : 6);
        else if (!OpenJava(java)) Fail(r, 9);
        else
        {
            JNIEnv* e = java.Env;
            jobject s = nd.WindowToSurface(e, win);
            if (Clear(e) || !s) Fail(r, 8);
            else
            {
                surface = e->NewGlobalRef(s);
                e->DeleteLocalRef(s);
                video.BaseUs = r->T0 / 10; // 画面の時刻は端末の CLOCK_MONOTONIC (ns) なので録画の始まりを引く
                e->CallStaticVoidMethod(java.Cls, java.Start, surface, r->W, r->H);
                if (Clear(e)) Fail(r, 8);
                else
                {
                    javaStarted = true;
                    r->State = 1;
                }
            }
        }

        bool sentEos = false;
        auto nextPoll = std::chrono::steady_clock::now();
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
            // ランチャーの側の様子 (断られた・端末の側で止められた) を時々見る
            if (javaStarted && r->State == 1 && std::chrono::steady_clock::now() >= nextPoll)
            {
                nextPoll = std::chrono::steady_clock::now() + std::chrono::milliseconds(200);
                int st = java.Env->CallStaticIntMethod(java.Cls, java.State);
                if (Clear(java.Env)) st = -2;
                if (st == -1) Fail(r, 7);
                else if (st == -2 || st == 0) Fail(r, 8); // 0 = ランチャーの側で録画が片付いた (別の録画に入れ替わった)
                else if (st == -3) r->State = 3;
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
                    if (Feed(r, audio, it)) break;
                    DrainOut(video, audio, mux, started, &r->Frames);
                    DrainOut(audio, video, mux, started, nullptr);
                }
                std::lock_guard<std::mutex> lk(r->M);
                r->Bytes -= size;
            }
            DrainOut(video, audio, mux, started, &r->Frames);
            DrainOut(audio, video, mux, started, nullptr);
            if (stopping && !have)
            {
                if (!sentEos)
                {
                    sentEos = true;
                    if (javaStarted)
                    {
                        java.Env->CallStaticVoidMethod(java.Cls, java.Stop);
                        Clear(java.Env);
                        javaStarted = false;
                    }
                    if (nd.SignalEndOfInput(video.Codec) != 0) video.Eos = true;
                    ssize_t idx = -1;
                    for (int k = 0; k < 100 && idx < 0; k++) idx = nd.DequeueInput(audio.Codec, 10000);
                    if (idx >= 0) nd.QueueInput(audio.Codec, (size_t)idx, 0, 0, 0, BUFFER_FLAG_END_OF_STREAM);
                    else audio.Eos = true;
                }
                // 終わりの印が両方から出るまで流す (止まらない符号器のために 3 秒で切る)
                for (int k = 0; k < 300 && !(video.Eos && audio.Eos); k++)
                {
                    DrainOut(video, audio, mux, started, &r->Frames);
                    DrainOut(audio, video, mux, started, nullptr);
                    if (!(video.Eos && audio.Eos)) usleep(10000);
                }
                break;
            }
        }
        if (javaStarted)
        {
            java.Env->CallStaticVoidMethod(java.Cls, java.Stop);
            Clear(java.Env);
        }
        for (Out* o : {&video, &audio})
            if (o->Codec)
            {
                nd.Stop(o->Codec);
                nd.Delete(o->Codec);
            }
        if (surface && java.Env) java.Env->DeleteGlobalRef(surface);
        if (win) nd.WindowRelease(win);
        if (mux)
        {
            if (started) nd.MuxerStop(mux);
            nd.MuxerDelete(mux);
        }
        if (fd >= 0) close(fd);
        bool ok = (r->State == 1 || r->State == 3) && started;
        // 書き終えた動画は端末の「動画」(Movies/MoreRolesPlus) へ移す (ギャラリーやファイルのアプリから見られるように)
        if (ok && java.Cls && java.Publish)
        {
            jstring path = java.Env->NewStringUTF(r->Path.c_str());
            java.Env->CallStaticBooleanMethod(java.Cls, java.Publish, path);
            Clear(java.Env);
            java.Env->DeleteLocalRef(path);
        }
        CloseJava(java);
        if (r->State == 1 || r->State == 3) r->State = ok ? 2 : -1;
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
    // 音の速さは AAC が受ける物ならよい (端末によっては 24000 などで鳴っている)
    static const int32_t rates[] = {8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000};
    bool okRate = false;
    for (int32_t v : rates) okRate |= v == rate;
    if (!path || w <= 0 || h <= 0 || (w & 1) || (h & 1) || fps <= 0 || !okRate || channels < 1 || channels > 2) return nullptr;
    if (!LoadNd()) return nullptr;
    auto* r = new Rec();
    r->Path = Utf8(path);
    r->W = w;
    r->H = h;
    r->Fps = fps;
    r->VideoBitrate = videoBitrate;
    r->Rate = rate;
    r->Channels = channels;
    r->T0 = Mono100ns();
    r->Th = std::thread(Worker, r);
    return r;
}

// Android は画面を端末の画面録画から受けるので、PC 版の「写す元のテクスチャ」と「描画のスレッドの呼び出し」は使わない
MRP_API void mrp_rec_texture(void*, void*, int32_t) {}

static void RenderEvent(int) {}

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
    it.Time = base + done * 10000000LL / r->Rate;
    it.Data.resize((size_t)frames * r->Channels * 2);
    memcpy(it.Data.data(), pcm, it.Data.size());
    int peak = 0;
    for (int i = 0; i < frames * r->Channels; i++) peak = std::max(peak, std::abs((int)pcm[i]));
    if (peak > r->AudioPeak) r->AudioPeak = peak;
    Push(r, std::move(it));
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

// 確かめ用の「最後に読んだ画面」は Android には無い (画面は符号器が直接読む)
MRP_API int32_t mrp_rec_peek(void*, uint8_t*, int32_t) { return 0; }

MRP_API int32_t mrp_rec_stop(void* h, int32_t wait)
{
    auto* r = (Rec*)h;
    if (!r) return -1;
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
