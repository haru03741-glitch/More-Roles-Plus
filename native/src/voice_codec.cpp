#include "mrp_native.h"
#include "../opus/include/opus.h"

// ボイスチャットの声の圧縮 (Opus・48kHz・1ch)。中身は native/opus (libopus 1.5.2・BSD) をそのまま組んだ物。
// 送り手は 1 つの符号器を使い続け、受け手は話し手ごとに 1 つの復号器を持つ (前の包みの続きとして読むので、話し手ごとに分ける)。
// 包みが抜けた時は data = null で呼ぶと、前の声から続きを作って埋める (fec = 1 なら次の包みに入っている前の分の控えから戻す)

MRP_API void* mrp_opus_enc_open(int32_t rate, int32_t bitrate)
{
    int err = 0;
    OpusEncoder* e = opus_encoder_create(rate, 1, OPUS_APPLICATION_AUDIO, &err);
    if (err != OPUS_OK || !e) return nullptr;
    opus_encoder_ctl(e, OPUS_SET_BITRATE(bitrate));
    opus_encoder_ctl(e, OPUS_SET_VBR(1));
    opus_encoder_ctl(e, OPUS_SET_VBR_CONSTRAINT(0));
    opus_encoder_ctl(e, OPUS_SET_COMPLEXITY(10));
    opus_encoder_ctl(e, OPUS_SET_BANDWIDTH(OPUS_BANDWIDTH_FULLBAND));
    opus_encoder_ctl(e, OPUS_SET_SIGNAL(OPUS_SIGNAL_VOICE));
    opus_encoder_ctl(e, OPUS_SET_INBAND_FEC(1));
    opus_encoder_ctl(e, OPUS_SET_PACKET_LOSS_PERC(8));
    opus_encoder_ctl(e, OPUS_SET_LSB_DEPTH(24));
    opus_encoder_ctl(e, OPUS_SET_DTX(0));
    return e;
}

// 戻り = 包みのバイト数 (負 = 失敗)
MRP_API int32_t mrp_opus_enc(void* enc, const float* pcm, int32_t samples, uint8_t* out, int32_t max)
{
    if (!enc || !pcm || !out) return -1;
    return opus_encode_float((OpusEncoder*)enc, pcm, samples, out, max);
}

MRP_API void mrp_opus_enc_close(void* enc)
{
    if (enc) opus_encoder_destroy((OpusEncoder*)enc);
}

MRP_API void* mrp_opus_dec_open(int32_t rate)
{
    int err = 0;
    OpusDecoder* d = opus_decoder_create(rate, 1, &err);
    if (err != OPUS_OK || !d) return nullptr;
    return d;
}

// 戻り = 作った点の数 (負 = 失敗)。data = null (len 0) で抜けた分を埋める。その時 max は埋める長さ (包み 1 つ分)
MRP_API int32_t mrp_opus_dec(void* dec, const uint8_t* data, int32_t len, float* out, int32_t max, int32_t fec)
{
    if (!dec || !out) return -1;
    return opus_decode_float((OpusDecoder*)dec, len > 0 ? data : nullptr, len > 0 ? len : 0, out, max, fec);
}

MRP_API void mrp_opus_dec_close(void* dec)
{
    if (dec) opus_decoder_destroy((OpusDecoder*)dec);
}
